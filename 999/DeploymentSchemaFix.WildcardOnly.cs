using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TextFileProcessor.Models;

namespace TextFileProcessor.Services;

internal static partial class DeploymentSchemaFix
{
    private sealed record Wc2Txt(string Name, string Content);

    private static string Wc2Field(XElement row, string name) =>
        row.Element(name)?.Value.Trim() ?? "";

    private static string Wc2CertKey(XElement row)
    {
        var key = Wc2Field(row, "key");
        return key.Length > 0 ? key : Wc2Field(row, "name");
    }

    private static bool Wc2Covers(XElement row, string domain)
    {
        var names = SplitValues(Wc2Field(row, "info"))
            .Select(x => x.TrimEnd('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return names.Contains(domain) &&
               names.Contains("*." + domain);
    }

    private static bool Wc2Pending(XElement row) =>
        Wc2Field(row, "letsencrypt_verifying") == "on" ||
        Wc2Field(row, "letsencrypt_txt") == "on";

    private static bool Wc2Ready(XElement row, string domain)
    {
        return Wc2Covers(row, domain) &&
               Wc2Field(row, "type") == "ssl_existing" &&
               !Wc2Pending(row) &&
               Wc2Field(row, "letsencrypt_failed") != "on" &&
               DateTime.TryParseExact(
                   Wc2Field(row, "valid_after"),
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var end) &&
               end.Date > DateTime.UtcNow.Date.AddDays(1);
    }

    private static async Task<List<XElement>> Wc2Certificates(
        Api api, string owner, CancellationToken ct)
    {
        var doc = await api.Call(
            "sslcert",
            new Dictionary<string, string>
            {
                ["out"] = "xml",
                ["lang"] = "en",
                ["su"] = owner
            }, ct);

        return doc.Root!.Elements("elem").ToList();
    }

    private static async Task<IspmanagerOperationResult> Wc2Run(
        Api api,
        IspmanagerSettings settings,
        string domain,
        string owner,
        CancellationToken ct)
    {
        var phase = "проверка существующих сертификатов";
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TextFileProcessor",
            "deployment-certificate-requests");

        Directory.CreateDirectory(directory);

        var panel = new UriBuilder(settings.PanelUrl)
        {
            Query = "",
            Fragment = ""
        }.Uri.AbsoluteUri.TrimEnd('/');

        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(panel + "\n" + owner + "\n" + domain)));

        var attemptFile = Path.Combine(directory, key + ".wc2-attempt");

        // Межпроцессная блокировка. Сам файл может оставаться после выхода.
        using var gate = new FileStream(
            Path.Combine(directory, key + ".wc2-lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Wc2Dns? dns = null;

        try
        {
            var certificates = await Wc2Certificates(api, owner, ct);

            var ready = certificates
                .Where(row => Wc2Ready(row, domain))
                .OrderByDescending(row =>
                    Wc2Field(row, "active") == "on")
                .ThenByDescending(row => Wc2Field(row, "valid_after"))
                .FirstOrDefault();

            if (ready is not null)
            {
                phase = "подключение существующего Wildcard";
                await Wc2AttachAndVerify(
                    api, ready, domain, owner, ct);

                return Wc2Success(domain);
            }

            var pending = certificates
                .Where(row => Wc2Covers(row, domain) && Wc2Pending(row))
                .ToList();

            if (pending.Count > 1)
                throw new InvalidOperationException(
                    "Найдено несколько незавершённых Wildcard-заявок. " +
                    "Автоматический выбор остановлен.");

            phase = "чтение сохранённых настроек Spaceship";
            dns = Wc2Dns.Load();

            phase = "проверка DNS перед выпуском";
            await dns.Preflight(domain, ct);

            if (pending.Count == 0)
            {
                // Новый маркер этой версии означает, что запрос уже мог
                // быть принят. Сначала необходимо обнаружить его в панели.
                // Не отправляем повторный POST после неизвестного исхода.
                if (File.Exists(attemptFile))
                    throw new InvalidOperationException(
                        "Эта версия уже отправляла заявку, но сейчас " +
                        "подходящий сертификат или ожидающая заявка " +
                        "не обнаружены. Автоматический повтор остановлен. " +
                        "Проверьте результат заявки в ISPmanager. " +
                        "Маркер не удалялся.");

                var oldState = Path.Combine(directory, key + ".state");
                if (File.Exists(oldState))
                {
                    var state = (await File.ReadAllTextAsync(oldState, ct)).Trim();
                    if (state.Length > 0 && state != "submitted")
                        throw new InvalidOperationException(
                            "У прежней заявки неизвестный исход, а в панели " +
                            "не найдена ожидающая Wildcard-заявка. " +
                            "Новая заявка не отправлена.");

                    // Старое submitted не считается выпущенным сертификатом.
                    // Отсутствие подходящего сертификата уже проверено в панели.
                }

                // WC3_PANEL_CERTIFICATE_NAME
                phase = "получение имени сертификата из формы";

                // Не передаём crtname при открытии формы.
                // Имя сертификата выбирает ISPmanager в контексте
                // текущего владельца и сайта.
                var request = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["out"] = "devel",
                    ["lang"] = "en",
                    ["username"] = owner,
                    ["domain_name"] = domain,
                    ["from_webdomain"] = "on",
                    ["aliases"] = "www." + domain + " *." + domain,
                    ["domain"] = domain + " *." + domain,
                    ["email"] = "webmaster@" + domain,
                    ["keylen"] = "2048",
                    ["wildcard"] = "on",
                    ["dns_check"] = "on",
                    ["skip_check_a_record"] = "off",
                    ["enable_cert"] = "on"
                };

                var form = await api.Call(
                    "letsencrypt.generate", request, ct);

                RequireForm(form, "letsencrypt.generate");
                RequireControls(
                    form,
                    "domain",
                    "crtname",
                    "wildcard",
                    "dns_check");

                var returnedOwner = Value(form, "username").Trim();

                if (!string.Equals(
                    returnedOwner,
                    owner,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Владелец формы выпуска не подтверждён. " +
                        "Заявка не отправлена.");
                }

                var returnedDomain = Value(
                    form,
                    "domain_name").Trim();

                if (!SameDomain(returnedDomain, domain))
                {
                    throw new InvalidOperationException(
                        "Форма выпуска относится к другому сайту " +
                        "или не содержит его имени. " +
                        "Заявка не отправлена.");
                }

                string certificateName = Value(
                    form,
                    "crtname").Trim();

                if (!Regex.IsMatch(
                    certificateName,
                    @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,239}\z"))
                {
                    throw new InvalidOperationException(
                        "Панель не вернула поддерживаемое имя " +
                        "сертификата. Заявка не отправлена.");
                }

                if (certificates.Any(row =>
                    string.Equals(
                        Wc2Field(row, "name"),
                        certificateName,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        Wc2CertKey(row),
                        certificateName,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        Wc2CertKey(row),
                        owner + "%#%" + certificateName,
                        StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        "Предложенное формой имя уже присутствует " +
                        "в прочитанном списке: " +
                        certificateName +
                        ". Новая заявка не отправлена.");
                }

                var csrName = certificateName + "_csr";

                var fields = new Dictionary<string, string>(
                    request,
                    StringComparer.Ordinal)
                {
                    ["out"] = "xml",
                    ["sok"] = "ok",
                    ["crtname"] = certificateName,
                    ["name"] = csrName
                };

                var requestedDomains = SplitValues(fields["domain"])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (requestedDomains.Count != 2 ||
                    !requestedDomains.Contains(domain) ||
                    !requestedDomains.Contains("*." + domain))
                {
                    throw new InvalidOperationException(
                        "Некорректный состав доменов сертификата. " +
                        "Заявка не отправлена.");
                }

                ct.ThrowIfCancellationRequested();

                using (var stateStream = new FileStream(
                    attemptFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(
                        "outcome-unknown\n" + certificateName);
                    stateStream.Write(bytes);
                    stateStream.Flush(true);
                }

                phase = "отправка заявки Wildcard";
                phase =
                    "отправка заявки Wildcard; crtname=" +
                    certificateName;

                await api.Call(
                    "letsencrypt.generate",
                    fields,
                    ct);
            }

            // Все последующие действия продолжают серверную заявку.
            // Повторный letsencrypt.generate в этом цикле не отправляется.
            var finishBy = DateTime.UtcNow.AddMinutes(18);
            string lastTxtSet = "";
            bool retriedForCurrentSet = false;
            string lastState = "ожидание появления заявки";

            while (DateTime.UtcNow < finishBy)
            {
                ct.ThrowIfCancellationRequested();

                phase = "проверка состояния выпуска";
                certificates = await Wc2Certificates(api, owner, ct);

                ready = certificates
                    .Where(row => Wc2Ready(row, domain))
                    .OrderByDescending(row => Wc2Field(row, "valid_after"))
                    .FirstOrDefault();

                if (ready is not null)
                {
                    phase = "назначение Wildcard и проверка HTTPS";
                    await Wc2AttachAndVerify(
                        api, ready, domain, owner, ct);

                    return Wc2Success(domain);
                }

                pending = certificates
                    .Where(row => Wc2Covers(row, domain) && Wc2Pending(row))
                    .ToList();

                if (pending.Count > 1)
                    throw new InvalidOperationException(
                        "Обнаружено несколько ожидающих Wildcard-заявок.");

                if (pending.Count == 0)
                {
                    if (certificates.Any(row =>
                        Wc2Covers(row, domain) &&
                        Wc2Field(row, "letsencrypt_failed") == "on"))
                        throw new InvalidOperationException(
                            "ISPmanager сообщил letsencrypt_failed. " +
                            "Нужен текст ошибки этой заявки в панели. " +
                            "Повторный выпуск автоматически не запускался.");

                    lastState = "заявка пока не появилась в списке";
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    continue;
                }

                var current = pending[0];

                if (Wc2Field(current, "letsencrypt_failed") == "on")
                    throw new InvalidOperationException(
                        "ISPmanager сообщил ошибку текущей заявки.");

                if (Wc2Field(current, "letsencrypt_txt") == "on")
                {
                    phase = "получение актуальных ACME TXT";

                    var form = await api.Call(
                        "webdomain.letsencrypt.txt",
                        new Dictionary<string, string>
                        {
                            ["out"] = "devel",
                            ["lang"] = "en",
                            ["elid"] = domain
                        }, ct);

                    var returnedId = Value(form, "elid").Trim();
                    var certKey = Wc2CertKey(current);
                    var certName = Wc2Field(current, "name");

                    var allowedIds = new HashSet<string>(
                        StringComparer.Ordinal)
                    {
                        certKey,
                        certName,
                        owner + "%#%" + certKey,
                        owner + "%#%" + certName
                    };

                    allowedIds.Remove("");

                    if (!allowedIds.Contains(returnedId))
                        throw new InvalidOperationException(
                            "Форма TXT относится не к ожидаемой заявке. " +
                            "DNS не изменён.");

                    var records = Wc2ReadTxt(form, domain);

                    if (records.Count > 0)
                    {
                        var signature = string.Join("\n",
                            records
                                .Select(r => r.Name + "=" + r.Content)
                                .OrderBy(x => x, StringComparer.Ordinal));

                        if (signature != lastTxtSet)
                        {
                            phase = "публикация ACME TXT в Spaceship";
                            await dns.Ensure(domain, records, ct);

                            lastTxtSet = signature;
                            retriedForCurrentSet = false;
                        }

                        phase = "ожидание TXT в публичном DNS";

                        if (!await dns.PublicTxtPresent(domain, records, ct))
                        {
                            lastState = "TXT сохранены, ожидается публичный DNS";
                            await Task.Delay(TimeSpan.FromSeconds(15), ct);
                            continue;
                        }

                        if (!retriedForCurrentSet)
                        {
                            // Используется тот же обработчик сайта,
                            // из которого была прочитана форма TXT.
                            phase = "повторная DNS-проверка в ISPmanager";
                            retriedForCurrentSet = true;

                            await api.Call(
                                "webdomain.letsencrypt.txt",
                                new Dictionary<string, string>
                                {
                                    ["out"] = "xml",
                                    ["elid"] = returnedId,
                                    ["elname"] = domain,
                                    ["sok"] = "ok"
                                }, ct);
                        }

                        lastState = "TXT видны; ISPmanager проверяет заявку";
                    }
                    else
                    {
                        lastState = "ISPmanager подтвердил TXT; ожидается выпуск";
                    }
                }
                else
                {
                    lastState = "ISPmanager выполняет выпуск сертификата";
                }

                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }

            throw new TimeoutException(
                "За отведённое время выпуск не подтверждён: " +
                lastState + ". Заявка не удалялась; следующий запуск " +
                "сначала проверит её состояние.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Wildcard — " + phase + ": " + ex.Message, ex);
        }
        finally
        {
            dns?.Dispose();
        }
    }

    private static IspmanagerOperationResult Wc2Success(string domain) =>
        new()
        {
            Success = true,
            Message =
                $"Сайт {domain} и псевдонимы проверены. " +
                $"Wildcard для {domain} и *.{domain} назначен сайту. " +
                "TLS-проверка серверного адреса с SNI домена пройдена."
        };

    private static List<Wc2Txt> Wc2ReadTxt(
        XDocument form, string domain)
    {
        var root = form.Root ??
            throw new InvalidOperationException("Пустая форма TXT.");

        var nameFields = root.Elements()
            .Where(e => Regex.IsMatch(
                e.Name.LocalName, @"\Atxt_record_name_[0-9]+\z"))
            .ToList();

        var valueFields = root.Elements()
            .Where(e => Regex.IsMatch(
                e.Name.LocalName, @"\Atxt_record_value_[0-9]+\z"))
            .ToList();

        if (nameFields.Count == 0)
        {
            bool found = root.Elements("metadata")
                .SelectMany(e => e.Descendants("field"))
                .Any(e => (string?)e.Attribute("name") == "txt_found_banner");

            if (found && valueFields.Count == 0)
                return new List<Wc2Txt>();

            throw new InvalidOperationException(
                "ISPmanager не вернул именованные поля ACME TXT.");
        }

        if (nameFields.Count != valueFields.Count ||
            nameFields.Count > 10)
            throw new InvalidOperationException(
                "Неподдерживаемая структура полей ACME TXT.");

        var result = new List<Wc2Txt>();

        foreach (var nameField in nameFields)
        {
            var suffix = nameField.Name.LocalName["txt_record_name_".Length..];
            var values = root.Elements("txt_record_value_" + suffix).ToList();

            if (values.Count != 1)
                throw new InvalidOperationException(
                    "Не найдена однозначная пара имени и значения TXT.");

            var name = nameField.Value.Trim().TrimEnd('.').ToLowerInvariant();
            var value = values[0].Value.Trim();

            string relative;
            if (SameDomain(name, "_acme-challenge." + domain))
                relative = "_acme-challenge";
            else if (SameDomain(name, "_acme-challenge.www." + domain))
                relative = "_acme-challenge.www";
            else
                throw new InvalidOperationException(
                    "ISPmanager запросил TXT вне ожидаемых имён домена.");

            if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{43}\z"))
                throw new InvalidOperationException(
                    "Неподдерживаемый формат значения DNS-01.");

            result.Add(new Wc2Txt(relative, value));
        }

        return result.Distinct().ToList();
    }

    private static async Task Wc2AttachAndVerify(
        Api api,
        XElement certificate,
        string domain,
        string owner,
        CancellationToken ct)
    {
        var site = await FindSite(api, domain, ct) ??
            throw new InvalidOperationException("Сайт не найден.");

        VerifySite(site, domain, owner);

        var form = site.Form;
        var schema = form;

        if (Value(form, "secure") != "on" ||
            Options(form, "ssl_cert").Count == 0)
        {
            schema = await api.Call(
                "webdomain.edit",
                new Dictionary<string, string>
                {
                    ["out"] = "devel",
                    ["elid"] = site.Id,
                    ["secure"] = "on",
                    ["tconvert"] = "punycode"
                }, ct);

            RequireForm(schema, "webdomain.edit");

            if (!SameDomain(Value(schema, "name"), domain))
                throw new InvalidOperationException(
                    "Панель вернула форму другого сайта.");

            var schemaOwner = Value(schema, "owner").Trim();
            if (schemaOwner.Length > 0 && schemaOwner != owner)
                throw new InvalidOperationException(
                    "Владелец формы сайта не совпадает.");
        }

        RequireControls(schema, "secure", "ssl_cert");

        var rowKey = Wc2CertKey(certificate);
        var rowName = Wc2Field(certificate, "name");
        var possible = new HashSet<string>(StringComparer.Ordinal)
        {
            rowKey, rowName,
            owner + "%#%" + rowKey,
            owner + "%#%" + rowName
        };
        possible.Remove("");

        var choices = Options(schema, "ssl_cert")
            .Where(possible.Contains)
            .ToList();

        var current = Value(form, "ssl_cert").Trim();

        string selected;
        if (possible.Contains(current) && Value(form, "secure") == "on")
            selected = current;
        else if (choices.Count == 1)
            selected = choices[0];
        else
            throw new InvalidOperationException(
                "В форме сайта не найден однозначный вариант " +
                "выпущенного Wildcard-сертификата.");

        if (Value(form, "secure") != "on" || current != selected)
        {
            // Остальные настройки берём из исходной формы сайта.
            var fields = FormValues(form, includeHidden: false);
            fields.Remove("owner");
            fields.Remove("home");
            fields.Remove("script_selector");
            fields.Remove("emailcreate");

            foreach (var name in new[] { "currname", "ssl_port" })
            {
                if (form.Root?.Element(name) is not null)
                    fields[name] = Value(form, name);
                else if (schema.Root?.Element(name) is not null)
                    fields[name] = Value(schema, name);
            }

            fields["out"] = "xml";
            fields["sok"] = "ok";
            fields["elid"] = site.Id;
            fields["name"] = Value(form, "name");
            fields["tconvert"] = "punycode";
            fields["secure"] = "on";
            fields["ssl_cert"] = selected;

            // Один запрос сохранения, без повтора при сетевой ошибке.
            await api.Call("webdomain.edit", fields, ct);
        }

        var saved = await FindSite(api, domain, ct) ??
            throw new InvalidOperationException(
                "После назначения сертификата сайт не найден.");

        VerifySite(saved, domain, owner);

        if (Value(saved.Form, "secure") != "on" ||
            Value(saved.Form, "ssl_cert").Trim() != selected)
            throw new InvalidOperationException(
                "Панель не подтвердила назначение выбранного Wildcard.");

        // Контроль: параметры сайта, не относящиеся к SSL, не изменились.
        foreach (var name in new[]
        {
            "home", "php", "php_mode", "php_fpm_version",
            "dirindex", "nginx_proxy"
        })
        {
            if (form.Root?.Element(name) is not null &&
                Value(form, name) != Value(saved.Form, name))
                throw new InvalidOperationException(
                    "После назначения SSL изменилось поле " + name +
                    ". Дальнейшие действия с этим сайтом остановлены.");
        }

        await Wc2VerifyTls(saved.Form, domain, ct);
    }

    private static async Task Wc2VerifyTls(
        XDocument site, string domain, CancellationToken ct)
    {
        var addresses = SplitValues(Value(site, "ipaddrs"))
            .Where(s => IPAddress.TryParse(s, out _))
            .Select(IPAddress.Parse)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();

        if (addresses.Count == 0)
            throw new InvalidOperationException(
                "Wildcard назначен в панели, но IP сайта не получен. " +
                "TLS-проверка не выполнена.");

        int port = 443;
        var portText = Value(site, "ssl_port").Trim();

        if (portText.Length > 0 &&
            (!int.TryParse(portText, out port) || port < 1 || port > 65535))
            throw new InvalidOperationException(
                "Wildcard назначен, но панель вернула некорректный SSL-порт.");

        // Проверяем первый IPv4, либо первый IPv6, если IPv4 нет.
        var address = addresses[0];

        for (int attempt = 0; attempt < 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var probe =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(15));

            try
            {
                using var tcp = new TcpClient(address.AddressFamily);
                await tcp.ConnectAsync(address, port, probe.Token);

                // Стандартная проверка доверия и имени сертификата.
                // Callback, отключающего проверку TLS, нет.
                using var tls = new SslStream(tcp.GetStream(), false);
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = domain,
                        EnabledSslProtocols = SslProtocols.None
                    }, probe.Token);

                if (tls.RemoteCertificate is null)
                    throw new AuthenticationException(
                        "Сервер не предоставил сертификат.");

                using var cert = new X509Certificate2(tls.RemoteCertificate);
                var names = Wc2San(cert);

                if (!names.Contains(domain) ||
                    !names.Contains("*." + domain) ||
                    cert.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
                    cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(1))
                    throw new AuthenticationException(
                        "Сервер пока не отдаёт требуемый действующий Wildcard.");

                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (AuthenticationException)
            {
            }
            catch (SocketException)
            {
            }
            catch (IOException)
            {
            }

            if (attempt < 9)
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        throw new InvalidOperationException(
            $"Wildcard назначен в ISPmanager, но TLS-проверка " +
            $"{address}:{port} с SNI {domain} не прошла. " +
            "Проверьте доступность порта и цепочку сертификата. " +
            "Новый выпуск для этой ошибки не запускался.");
    }

    private static HashSet<string> Wc2San(X509Certificate2 certificate)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extension = certificate.Extensions
            .Cast<X509Extension>()
            .FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");

        if (extension is null)
            return result;

        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var dnsTag = new Asn1Tag(TagClass.ContextSpecific, 2);

        while (sequence.HasData)
        {
            if (sequence.PeekTag().HasSameClassAndValue(dnsTag))
            {
                result.Add(sequence.ReadCharacterString(
                    UniversalTagNumber.IA5String, dnsTag).TrimEnd('.'));
            }
            else
            {
                sequence.ReadEncodedValue();
            }
        }

        reader.ThrowIfNotEmpty();
        return result;
    }

    private sealed class Wc2Dns : IDisposable
    {
        private readonly HttpClient api;
        private readonly HttpClient publicDns;

        private Wc2Dns(string key, string secret)
        {
            api = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            {
                BaseAddress = new Uri("https://spaceship.dev/api/v1/"),
                Timeout = TimeSpan.FromSeconds(45)
            };

            api.DefaultRequestHeaders.Add("X-API-Key", key);
            api.DefaultRequestHeaders.Add("X-API-Secret", secret);
            api.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            // Отдельный клиент: реквизиты Spaceship сюда не попадают.
            publicDns = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
        }

        internal static Wc2Dns Load()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "TextFileProcessor", "spaceship-settings.json");

            if (!File.Exists(path))
                throw new InvalidOperationException(
                    "Не найдены сохранённые настройки Spaceship. " +
                    "В существующей вкладке программы нажмите " +
                    "«Сохранить Spaceship», затем повторите выбранное задание.");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var key = J(root, "ApiKey").Trim();
            var encrypted = J(root, "EncryptedSecret");
            var baseUrl = J(root, "BaseUrl").TrimEnd('/');
            var dnsPath = J(root, "DnsPath").TrimStart('/');

            if (baseUrl != "https://spaceship.dev/api/v1" ||
                dnsPath != "dns/records/{domain}")
                throw new InvalidOperationException(
                    "Сохранён нестандартный адрес Spaceship API. " +
                    "Реквизиты не отправлялись.");

            if (key.Length == 0 || encrypted.Length == 0)
                throw new InvalidOperationException(
                    "В сохранённых настройках отсутствуют API Key " +
                    "или защищённый API Secret Spaceship.");

            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(encrypted),
                    Encoding.UTF8.GetBytes(
                        "TextFileProcessor.Spaceship.Build7.v1"),
                    DataProtectionScope.CurrentUser);
            }
            catch (Exception ex) when (
                ex is CryptographicException || ex is FormatException)
            {
                throw new InvalidOperationException(
                    "Не удалось прочитать сохранённый секрет Spaceship " +
                    "для текущего пользователя Windows. " +
                    "Повторно сохраните его в существующей вкладке.");
            }

            try
            {
                var secret = Encoding.UTF8.GetString(plain);

                if (string.IsNullOrWhiteSpace(secret) ||
                    key.Any(char.IsControl) || secret.Any(char.IsControl))
                    throw new InvalidOperationException(
                        "Сохранённые реквизиты Spaceship некорректны.");

                return new Wc2Dns(key, secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static string J(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private static string Relative(string name, string domain)
        {
            name = name.Trim().TrimEnd('.').ToLowerInvariant();
            var suffix = "." + domain.ToLowerInvariant();

            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name[..^suffix.Length]
                : name;
        }

        private static string TxtValue(string value)
        {
            value = value.Trim();
            return value.Length >= 2 &&
                   value.StartsWith('"') && value.EndsWith('"')
                ? value[1..^1]
                : value;
        }

        private static bool Has(
            IEnumerable<JsonElement> rows, string domain, Wc2Txt txt) =>
            rows.Any(row =>
                J(row, "type").Equals("TXT", StringComparison.OrdinalIgnoreCase) &&
                Relative(J(row, "name"), domain) == txt.Name &&
                TxtValue(J(row, "value")) == txt.Content);

        private static void Check(
            HttpResponseMessage response, string operation)
        {
            if (response.IsSuccessStatusCode)
                return;

            throw new InvalidOperationException(
                $"Spaceship: {operation}, HTTP {(int)response.StatusCode}. " +
                "Проверьте права API на DNS и состояние зоны. " +
                "Изменяющий запрос автоматически не повторялся.");
        }

        private async Task<List<JsonElement>> Read(
            string domain, CancellationToken ct)
        {
            var result = new List<JsonElement>();
            int skip = 0;

            for (int page = 0; page < 1000; page++)
            {
                var path = "dns/records/" + Uri.EscapeDataString(domain) +
                    "?take=100&skip=" +
                    skip.ToString(CultureInfo.InvariantCulture);

                using var response = await api.GetAsync(path, ct);
                Check(response, "чтение DNS");

                using var doc = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(ct));

                if (!doc.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException(
                        "Неподдерживаемая структура списка DNS Spaceship.");

                int count = items.GetArrayLength();
                if (count == 0)
                    return result;

                foreach (var row in items.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object ||
                        J(row, "type").Length == 0)
                        throw new InvalidOperationException(
                            "Неподдерживаемая DNS-запись Spaceship.");

                    result.Add(row.Clone());
                }

                skip = checked(skip + count);

                if (doc.RootElement.TryGetProperty("total", out var total) &&
                    total.TryGetInt32(out int totalCount) &&
                    totalCount >= 0 && skip >= totalCount)
                    return result;
            }

            throw new InvalidOperationException(
                "Превышен предел страниц DNS. Изменение остановлено.");
        }

        internal async Task Preflight(string domain, CancellationToken ct)
        {
            // Проверяем доступ к зоне до создания новой заявки.
            await Read(domain, ct);

            var servers = await Resolve(
                "https://dns.google/resolve",
                domain, 2, ct);

            if (servers.Count == 0 ||
                servers.Any(server =>
                    !server.TrimEnd('.').EndsWith(
                        ".spaceship.net", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Публичная NS-проверка не подтвердила, что DNS домена " +
                    "обслуживается Spaceship. DNS и заявка не изменялись " +
                    "этой проверкой.");
        }

        internal async Task Ensure(
            string domain, List<Wc2Txt> wanted, CancellationToken ct)
        {
            var before = await Read(domain, ct);
            var missing = wanted
                .Where(txt => !Has(before, domain, txt))
                .Distinct()
                .ToList();

            if (missing.Count == 0)
                return;

            var affected = missing.Select(txt => txt.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (before.Any(row =>
                J(row, "type").Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
                affected.Contains(Relative(J(row, "name"), domain))))
                throw new InvalidOperationException(
                    "На имени ACME есть CNAME. " +
                    "Делегированная DNS-01 этим модулем не изменяется.");

            var previous = before
                .Where(row =>
                    J(row, "type").Equals("TXT", StringComparison.OrdinalIgnoreCase) &&
                    affected.Contains(Relative(J(row, "name"), domain)))
                .Select(row => new Wc2Txt(
                    Relative(J(row, "name"), domain),
                    TxtValue(J(row, "value"))))
                .ToList();

            var body = JsonSerializer.Serialize(new
            {
                force = false,
                items = missing.Select(txt => new
                {
                    type = "TXT",
                    name = txt.Name,
                    value = txt.Content,
                    ttl = 300
                }).ToArray()
            });

            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                "dns/records/" + Uri.EscapeDataString(domain));

            request.Content = new StringContent(
                body, Encoding.UTF8, "application/json");

            using var response = await api.SendAsync(request, ct);
            Check(response, "добавление ACME TXT");

            for (int attempt = 0; attempt < 6; attempt++)
            {
                var after = await Read(domain, ct);

                if (wanted.All(txt => Has(after, domain, txt)) &&
                    previous.All(txt => Has(after, domain, txt)))
                    return;

                if (attempt < 5)
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            throw new InvalidOperationException(
                "Контрольное чтение не подтвердило новые TXT " +
                "и сохранность прежних значений. Повторный PUT не отправлен.");
        }

        private async Task<HashSet<string>> Resolve(
            string endpoint, string name, int type, CancellationToken ct)
        {
            var separator = endpoint.Contains('?') ? "&" : "?";
            var url = endpoint + separator +
                "name=" + Uri.EscapeDataString(name) +
                "&type=" + type.ToString(CultureInfo.InvariantCulture);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/dns-json");

            using var response = await publicDns.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "Публичный DNS-сервис вернул HTTP " +
                    (int)response.StatusCode + ".");

            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("Status", out var status))
                throw new InvalidOperationException(
                    "Некорректный ответ публичного DNS.");

            var result = new HashSet<string>(StringComparer.Ordinal);
            int code = status.GetInt32();

            // NXDOMAIN допустим во время распространения TXT.
            if (code == 3)
                return result;

            if (code != 0)
                throw new InvalidOperationException(
                    "Публичный DNS вернул код " + code +
                    ". Проверьте DNS/DNSSEC домена.");

            if (!doc.RootElement.TryGetProperty("Answer", out var answers))
                return result;

            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out var recordType) ||
                    recordType.GetInt32() != type ||
                    !SameDomain(J(answer, "name"), name))
                    continue;

                var data = J(answer, "data");
                result.Add(type == 16 ? TxtValue(data) : data);
            }

            return result;
        }

        internal async Task<bool> PublicTxtPresent(
            string domain, List<Wc2Txt> wanted, CancellationToken ct)
        {
            foreach (var group in wanted.GroupBy(txt => txt.Name))
            {
                var name = group.Key + "." + domain;

                foreach (var endpoint in new[]
                {
                    "https://dns.google/resolve",
                    "https://cloudflare-dns.com/dns-query"
                })
                {
                    var values = await Resolve(endpoint, name, 16, ct);
                    if (!group.All(txt => values.Contains(txt.Content)))
                        return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            api.Dispose();
            publicDns.Dispose();
        }
    }
}