using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;
using TextFileProcessor.Models;

namespace TextFileProcessor.Services;

public sealed class IspmanagerAutomationService
{
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(120);

    private static readonly TimeSpan DomainCreationTimeout =
        TimeSpan.FromSeconds(120);

    public async Task<IspmanagerOperationResult> TestConnectionAsync(
        IspmanagerSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        ValidateSettings(settings, password);

        using var client = CreateClient(settings);

        var sessionId = await AuthenticateAsync(
            client,
            settings,
            password,
            cancellationToken);

        var listResult = await CallAsync(
            client,
            settings,
            sessionId,
            "webdomain",
            null,
            cancellationToken);

        EnsureSuccess(
            listResult,
            "Не удалось получить список WWW-доменов");

        return new IspmanagerOperationResult
        {
            Success = true,
            Message =
                "Подключение к ISPmanager API выполнено успешно. " +
                "Список WWW-доменов получен."
        };
    }

        public Task<IspmanagerOperationResult> CreateWebDomainAsync(
        IspmanagerSettings settings,
        string password,
        string domain,
        CancellationToken cancellationToken)
    {
        return DeploymentSchemaFix.EnsureSiteAsync(
            settings, password, domain, cancellationToken);
    }
        internal static HttpClient CreateClient(IspmanagerSettings settings)
    {
        if (!Uri.TryCreate(settings.PanelUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "Для ISPmanager нужен HTTPS URL без реквизитов внутри адреса.");
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        // Проверка TLS обязательна. IgnoreCertificateErrors не применяется.
        var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "TextFileProcessor-DeploymentFix/1.0");
        return client;
    }
    internal static async Task<string> AuthenticateAsync(
        HttpClient client,
        IspmanagerSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["out"] = "xml",
            ["func"] = "auth",
            ["username"] = settings.Login.Trim(),
            ["password"] = password
        };

        var result = await SendFormAsync(
            client,
            NormalizePanelUrl(settings.PanelUrl),
            form,
            cancellationToken);

        EnsureSuccess(
            result,
            "ISPmanager отклонил авторизацию");

        if (result.Document is null)
        {
            throw new InvalidOperationException(
                "ISPmanager не вернул XML при авторизации.");
        }

        var authElement = result.Document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(
                    element.Name.LocalName,
                    "auth",
                    StringComparison.OrdinalIgnoreCase));

        if (authElement is null)
        {
            throw new InvalidOperationException(
                "ISPmanager не вернул номер API-сессии.");
        }

        var sessionId = authElement.Value.Trim();

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId =
                authElement.Attribute("id")?.Value?.Trim() ??
                string.Empty;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(
                "ISPmanager вернул пустой номер API-сессии.");
        }

        return sessionId;
    }

    internal static async Task<ApiResult> CallAsync(
        HttpClient client,
        IspmanagerSettings settings,
        string sessionId,
        string function,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["out"] = "xml",
            ["lang"] = "ru",
            ["auth"] = sessionId,
            ["func"] = function
        };

        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                form[parameter.Key] = parameter.Value;
            }
        }

        return await SendFormAsync(
            client,
            NormalizePanelUrl(settings.PanelUrl),
            form,
            cancellationToken);
    }

    private static async Task<ApiResult> SendFormAsync(
        HttpClient client,
        string url,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content =
            new FormUrlEncodedContent(form);

        HttpResponseMessage response;

        try
        {
            response = await client.PostAsync(
                url,
                content,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Failure(
                "timeout",
                "Истекло время ожидания ответа ISPmanager.");
        }
        catch (HttpRequestException exception)
        {
            return ApiResult.Failure(
                "connection",
                exception.Message);
        }

        using (response)
        {
            var responseText =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return ApiResult.Failure(
                    ((int)response.StatusCode).ToString(
                        CultureInfo.InvariantCulture),
                    $"HTTP {(int)response.StatusCode}: " +
                    response.ReasonPhrase);
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                return ApiResult.Failure(
                    "empty_response",
                    "ISPmanager вернул пустой ответ.");
            }

            XDocument document;

            try
            {
                document = XDocument.Parse(responseText);
            }
            catch
            {
                return ApiResult.Failure(
                    "invalid_xml",
                    "ISPmanager вернул ответ неизвестного формата.");
            }

            var errorElement = document
                .Descendants()
                .FirstOrDefault(element =>
                    string.Equals(
                        element.Name.LocalName,
                        "error",
                        StringComparison.OrdinalIgnoreCase));

            if (errorElement is null)
            {
                return ApiResult.SuccessResult(document);
            }

            var errorCode =
                errorElement.Attribute("code")?.Value ??
                FindElementValue(errorElement, "code") ??
                "ispmanager_error";

            var errorMessage =
                errorElement.Attribute("msg")?.Value ??
                FindElementValue(errorElement, "msg") ??
                FindElementValue(errorElement, "message") ??
                errorElement.Value;

            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage =
                    "ISPmanager вернул ошибку без описания.";
            }

            return ApiResult.Failure(
                errorCode.Trim(),
                errorMessage.Trim(),
                document);
        }
    }

    private static async Task<bool> DomainExistsAsync(
        HttpClient client,
        IspmanagerSettings settings,
        string sessionId,
        string domain,
        CancellationToken cancellationToken)
    {
        var result = await CallAsync(
            client,
            settings,
            sessionId,
            "webdomain",
            null,
            cancellationToken);

        EnsureSuccess(
            result,
            "Не удалось проверить список WWW-доменов");

        if (result.Document is null)
        {
            return false;
        }

        var expectedDomain =
            NormalizeDomain(domain);

        return result.Document
            .Descendants()
            .Where(element =>
                string.Equals(
                    element.Name.LocalName,
                    "name",
                    StringComparison.OrdinalIgnoreCase))
            .Select(element =>
                NormalizeDomainSafe(element.Value))
            .Any(value =>
                string.Equals(
                    value,
                    expectedDomain,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindElementValue(
        XElement parent,
        string localName)
    {
        return parent
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(
                    element.Name.LocalName,
                    localName,
                    StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    internal static void EnsureSuccess(
        ApiResult result,
        string operation)
    {
        if (result.Success)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{operation}. " +
            $"Ошибка {result.ErrorCode}: " +
            result.ErrorMessage);
    }

    private static void ValidateSettings(
        IspmanagerSettings settings,
        string password)
    {
        if (!Uri.TryCreate(
            settings.PanelUrl,
            UriKind.Absolute,
            out var panelUri) ||
            (
                panelUri.Scheme != Uri.UriSchemeHttps &&
                panelUri.Scheme != Uri.UriSchemeHttp
            ))
        {
            throw new InvalidOperationException(
                "Укажите корректный URL панели ISPmanager.");
        }

        if (string.IsNullOrWhiteSpace(settings.Login))
        {
            throw new InvalidOperationException(
                "Укажите логин ISPmanager.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Введите или сохраните пароль ISPmanager.");
        }

        if (string.IsNullOrWhiteSpace(settings.Owner))
        {
            throw new InvalidOperationException(
                "Укажите владельца WWW-домена.");
        }

        if (string.IsNullOrWhiteSpace(settings.PhpVersion))
        {
            throw new InvalidOperationException(
                "Укажите версию PHP.");
        }
    }

    private static string NormalizePanelUrl(
        string panelUrl)
    {
        var value = panelUrl.Trim();

        var fragmentIndex = value.IndexOf('#');

        if (fragmentIndex >= 0)
        {
            value = value[..fragmentIndex];
        }

        value = value.TrimEnd('/');

        if (!value.EndsWith(
            "/ispmgr",
            StringComparison.OrdinalIgnoreCase))
        {
            value += "/ispmgr";
        }

        return value;
    }

    internal static string NormalizeDomain(
        string input)
    {
        var value = input.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Не указан домен для создания.");
        }

        if (!value.Contains(
            "://",
            StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(
            value,
            UriKind.Absolute,
            out var uri))
        {
            throw new InvalidOperationException(
                $"Некорректный домен: {input}");
        }

        var host = uri.IdnHost
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();

        if (host.StartsWith(
            "www.",
            StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        if (string.IsNullOrWhiteSpace(host) ||
            !host.Contains('.'))
        {
            throw new InvalidOperationException(
                $"Некорректный домен: {input}");
        }

        return host;
    }

    private static string NormalizeDomainSafe(
        string input)
    {
        try
        {
            return NormalizeDomain(input);
        }
        catch
        {
            return input
                .Trim()
                .TrimEnd('.')
                .ToLowerInvariant();
        }
    }

    internal sealed class ApiResult
    {
        public bool Success { get; init; }

        public string ErrorCode { get; init; } =
            string.Empty;

        public string ErrorMessage { get; init; } =
            string.Empty;

        public XDocument? Document { get; init; }

        public static ApiResult SuccessResult(
            XDocument document)
        {
            return new ApiResult
            {
                Success = true,
                Document = document
            };
        }

        public static ApiResult Failure(
            string code,
            string message,
            XDocument? document = null)
        {
            return new ApiResult
            {
                Success = false,
                ErrorCode = code,
                ErrorMessage = message,
                Document = document
            };
        }
    }
}