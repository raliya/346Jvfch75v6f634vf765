using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using TextFileProcessor.Models;
using static TextFileProcessor.Services.IspmanagerAutomationService;

namespace TextFileProcessor.Services;

internal static class DeploymentSchemaFix
{
    private static readonly SemaphoreSlim SiteGate = new(1, 1);

    private sealed class Api : IDisposable
    {
        private readonly HttpClient client;
        private readonly IspmanagerSettings settings;
        private readonly string session;
        private readonly string password;

        private Api(
            HttpClient client, IspmanagerSettings settings,
            string session, string password)
        {
            this.client = client;
            this.settings = settings;
            this.session = session;
            this.password = password;
        }

        internal static async Task<Api> Open(
            IspmanagerSettings settings, string password,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(settings.Login) ||
                string.IsNullOrWhiteSpace(settings.Owner) ||
                string.IsNullOrEmpty(password))
                throw new InvalidOperationException(
                    "Заполните логин, владельца и пароль ISPmanager.");

            var client = CreateClient(settings);
            try
            {
                var session = await AuthenticateAsync(
                    client, settings, password, ct);
                return new Api(client, settings, session, password);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        internal async Task<XDocument> Call(
            string function, Dictionary<string, string>? parameters,
            CancellationToken ct)
        {
            var result = await CallAsync(
                client, settings, session, function, parameters, ct);

            if (!result.Success)
            {
                // Не включаем XML или значения параметров в исключение.
                var error = result.Document?.Root?.Element("error");
                var type = error?.Attribute("type")?.Value ?? result.ErrorCode;
                var field = error?.Attribute("object")?.Value ?? "";
                var text = $"ISPmanager: {function}; ошибка {type}; поле {field}.";

                foreach (var secret in new[] { password, session })
                    if (!string.IsNullOrEmpty(secret))
                        text = text.Replace(secret, "[СКРЫТО]", StringComparison.Ordinal);

                throw new InvalidOperationException(text);
            }

            if (result.Document?.Root?.Name.LocalName != "doc")
                throw new InvalidOperationException(
                    "ISPmanager не вернул ожидаемый XML-документ doc.");

            return result.Document;
        }

        public void Dispose() => client.Dispose();
    }

    private static string Value(XDocument doc, string name) =>
        doc.Root?.Element(name)?.Value ?? "";

    private static HashSet<string> Options(XDocument doc, string name) =>
        (doc.Root?.Elements("slist")
            .Where(x => (string?)x.Attribute("name") == name)
            .Elements("val")
            .Select(x => (string?)x.Attribute("key") ?? "")
            ?? Enumerable.Empty<string>())
        .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> FormValues(XDocument doc)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var controls = doc.Root?.Element("metadata")?.Element("form")?.Descendants()
            .Where(e => e.Name.LocalName is "input" or "select" or "textarea")
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct() ?? Enumerable.Empty<string?>();

        foreach (var name in controls)
        {
            if (name is null ||
                name.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("confirm", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = doc.Root?.Element(name);
            if (value is not null)
                result[name] = value.Value;
        }

        return result;
    }

    internal static async Task<IspmanagerOperationResult> EnsureSiteAsync(
        IspmanagerSettings settings, string password,
        string domain, CancellationToken ct)
    {
        domain = NormalizeDomain(domain);
        await SiteGate.WaitAsync(ct);

        try
        {
            using var api = await Api.Open(settings, password, ct);
            var list = await api.Call("webdomain", null, ct);

            var exists = list.Root!.Elements("elem").Any(e =>
                string.Equals(e.Element("name")?.Value?.TrimEnd('.'),
                    domain, StringComparison.OrdinalIgnoreCase));

            var request = new Dictionary<string, string>
            {
                ["out"] = "devel",
                ["tconvert"] = "punycode"
            };
            if (exists)
                request["elid"] = domain;
            else
                request["plid"] = "";

            var form = await api.Call("site.edit", request, ct);
            var fields = FormValues(form);
            var expectedOwner = settings.Owner.Trim();

            if (exists &&
                !string.Equals(Value(form, "site_owner"), expectedOwner,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Владелец существующего сайта не совпадает с настройками. " +
                    "Автоматическая смена владельца запрещена.");

            if (!exists && !Options(form, "site_owner").Contains(expectedOwner))
                throw new InvalidOperationException(
                    "Владелец отсутствует в списке site_owner панели.");

            var aliases = Value(form, "site_aliases")
                .Split(new[] { ' ', '\r', '\n', '\t', ',' },
                    StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var changed = aliases.Add("www." + domain);
            changed |= aliases.Add("*." + domain);

            fields["out"] = "xml";
            fields["sok"] = "ok";
            fields["tconvert"] = "punycode";
            fields["site_name"] = domain;
            fields["site_owner"] = expectedOwner;
            fields["site_aliases"] = string.Join(" ", aliases);

            if (exists)
            {
                fields["elid"] = domain;
            }
            else
            {
                fields["plid"] = "";
                fields["site_ssl_cert"] = "letsencrypt";
                fields["site_srv_cache"] = "off";
                fields["site_script_selector"] = "*";
                fields["lp_db_source"] = "db_not_use";
                fields["site_email"] = "webmaster@" + domain;
            }

            if (!exists || changed)
                await api.Call("site.edit", fields, ct);

            var check = await api.Call("site.edit",
                new Dictionary<string, string>
                {
                    ["out"] = "devel",
                    ["elid"] = domain,
                    ["tconvert"] = "punycode"
                }, ct);

            var savedAliases = Value(check, "site_aliases")
                .Split(new[] { ' ', '\r', '\n', '\t', ',' },
                    StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!string.Equals(Value(check, "site_name"), domain,
                    StringComparison.OrdinalIgnoreCase) ||
                Value(check, "site_owner") != expectedOwner ||
                !savedAliases.Contains("www." + domain) ||
                !savedAliases.Contains("*." + domain))
                throw new InvalidOperationException(
                    "Не подтверждено сохранение имени, владельца или псевдонимов сайта.");

            var stateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TextFileProcessor", "deployment-certificate-requests");

            Directory.CreateDirectory(stateDirectory);

            var panel = new UriBuilder(settings.PanelUrl)
            {
                Query = "",
                Fragment = ""
            }.Uri.AbsoluteUri.TrimEnd('/');

            var stateKey = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(panel + "\n" + expectedOwner + "\n" + domain)));

            var stateFile = Path.Combine(stateDirectory, stateKey + ".state");

            if (File.Exists(stateFile))
            {
                var state = await File.ReadAllTextAsync(stateFile, ct);

                if (state == "submitted")
                    return new IspmanagerOperationResult
                    {
                        Success = true,
                        Message = $"Сайт {domain} и псевдонимы проверены. " +
                            "Запрос Wildcard ранее отправлен этим модулем. " +
                            "Повторный выпуск не запускался; проверьте статус сертификата в панели."
                    };

                throw new InvalidOperationException(
                    "Для этого домена сохранён незавершённый запрос сертификата. " +
                    "Его исход неизвестен: проверьте ISPmanager перед повторным выпуском. " +
                    "Файл состояния: " + stateFile);
            }

            var certificateParameters = new Dictionary<string, string>
            {
                ["out"] = "devel",
                ["username"] = expectedOwner,
                ["domain_name"] = domain,
                ["aliases"] = $"www.{domain} *.{domain}",
                ["crtname"] = domain + "_le1",
                ["email"] = "webmaster@" + domain,
                ["from_webdomain"] = "on",
                ["wildcard"] = "on",
                ["dns_check"] = "on",
                ["enable_cert"] = "on"
            };

            var certificateForm = await api.Call(
                "letsencrypt.generate", certificateParameters, ct);

            var certificateFields = FormValues(certificateForm);
            foreach (var pair in certificateParameters)
                certificateFields[pair.Key] = pair.Value;

            certificateFields["out"] = "xml";
            certificateFields["sok"] = "ok";

            // Записываем намерение до сетевой мутации.
            // При таймауте не отправляем повторный запрос автоматически.
            using (var stream = new FileStream(
                stateFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes("outcome-unknown");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            await api.Call("letsencrypt.generate", certificateFields, ct);
            await File.WriteAllTextAsync(stateFile, "submitted", ct);

            return new IspmanagerOperationResult
            {
                Success = true,
                Message = $"Сайт {domain}, владелец и псевдонимы проверены. " +
                    "Запрос Wildcard отправлен в ISPmanager. " +
                    "Выпуск сертификата ещё не подтверждён: проверьте DNS-01/TXT и статус панели."
            };
        }
        finally
        {
            SiteGate.Release();
        }
    }

    internal static async Task<bool> EnsureDatabaseAsync(
        IspmanagerSettings settings, string panelPassword,
        DeploymentDbConfig db, CancellationToken ct)
    {
        using var api = await Api.Open(settings, panelPassword, ct);

        // В полученной схеме site.edit список lp_db_source содержит
        // реальные ключи вида database->MySQL.
        var siteForm = await api.Call("site.edit",
            new Dictionary<string, string>
            {
                ["out"] = "devel",
                ["plid"] = "",
                ["site_owner"] = settings.Owner.Trim()
            }, ct);

        var databases = Options(siteForm, "lp_db_source");

        if (!databases.Contains("db_not_use"))
            throw new InvalidOperationException(
                "Панель не вернула ожидаемый список баз lp_db_source.");

        if (databases.Contains(db.Name + "->MySQL"))
            return false;

        var form = await api.Call("db.edit",
            new Dictionary<string, string>
            {
                ["out"] = "devel",
                ["plid"] = "",
                ["owner"] = settings.Owner.Trim(),
                ["server"] = "MySQL"
            }, ct);

        if (!Options(form, "server").Contains("MySQL") ||
            !Options(form, "owner").Contains(settings.Owner.Trim()) ||
            !Options(form, "charset").Contains("utf8mb4"))
            throw new InvalidOperationException(
                "Схема db.edit не подтвердила сервер, владельца или utf8mb4.");

        var users = Options(form, "user");
        if (!users.Contains("*"))
            throw new InvalidOperationException(
                "Панель не вернула ожидаемый список пользователей БД.");

        var parameters = new Dictionary<string, string>
        {
            ["sok"] = "ok",
            ["name"] = db.Name,
            ["owner"] = settings.Owner.Trim(),
            ["server"] = "MySQL",
            ["charset"] = "utf8mb4",
            ["remote_access"] = "off"
        };

        if (users.Contains(db.User))
        {
            parameters["user"] = db.User;
        }
        else
        {
            parameters["user"] = "*";
            parameters["username"] = db.User;
            parameters["password"] = db.Password;
            parameters["confirm"] = db.Password;
        }

        try
        {
            await api.Call("db.edit", parameters, ct);
        }
        finally
        {
            parameters.Clear();
        }

        var check = await api.Call("site.edit",
            new Dictionary<string, string>
            {
                ["out"] = "devel",
                ["plid"] = "",
                ["site_owner"] = settings.Owner.Trim()
            }, ct);

        if (!Options(check, "lp_db_source").Contains(db.Name + "->MySQL"))
            throw new InvalidOperationException(
                "Команда создания БД отправлена, но база не найдена в списке панели. " +
                "Повторное создание автоматически не выполняется.");

        return true;
    }
}