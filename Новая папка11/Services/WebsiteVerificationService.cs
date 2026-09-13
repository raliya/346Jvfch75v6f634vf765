using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TextFileProcessor.Models;

namespace TextFileProcessor.Services
{
    public sealed class WebsiteVerificationService
    {
        private const int MaximumBodyCharacters = 2_000_000;

        public async Task<WebsiteVerificationResult> VerifyAsync(
            string domainOrUrl,
            string? controlText,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var result = new WebsiteVerificationResult();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                string host = GetSafeHost(domainOrUrl);

                result.Domain = host;
                result.ControlText = controlText?.Trim() ?? string.Empty;
                result.ControlTextRequired =
                    !string.IsNullOrWhiteSpace(result.ControlText);

                await VerifyDnsAsync(
                    host,
                    result,
                    cancellationToken);

                if (!result.DnsResolved)
                {
                    return result;
                }

                string httpBody = await VerifyHttpAsync(
                    new Uri($"http://{host}/"),
                    false,
                    result,
                    timeout,
                    cancellationToken);

                string httpsBody = await VerifyHttpAsync(
                    new Uri($"https://{host}/"),
                    true,
                    result,
                    timeout,
                    cancellationToken);

                if (result.ControlTextRequired)
                {
                    string body = !string.IsNullOrEmpty(httpsBody)
                        ? httpsBody
                        : httpBody;

                    result.ControlTextFound = body.IndexOf(
                        result.ControlText,
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }
                else
                {
                    result.ControlTextFound = true;
                }
            }
            catch (OperationCanceledException)
            {
                result.Errors.Add("Проверка отменена или превышен тайм-аут.");
            }
            catch (Exception ex)
            {
                result.Errors.Add(GetSafeError(ex));
            }
            finally
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
            }

            return result;
        }

        private static string GetSafeHost(string value)
        {
            value = value?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Не указан домен.");
            }

            Uri uri;

            if (!Uri.TryCreate(value, UriKind.Absolute, out uri!))
            {
                if (!Uri.TryCreate(
                    "https://" + value,
                    UriKind.Absolute,
                    out uri!))
                {
                    throw new ArgumentException("Некорректный домен или URL.");
                }
            }

            if (uri.Scheme != Uri.UriSchemeHttp &&
                uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException(
                    "Разрешены только HTTP- и HTTPS-адреса.");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException(
                    "URL не должен содержать имя пользователя или пароль.");
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new ArgumentException("Не удалось определить домен.");
            }

            if (uri.IsLoopback)
            {
                throw new ArgumentException(
                    "Локальные адреса для этой проверки не поддерживаются.");
            }

            return new System.Globalization.IdnMapping().GetAscii(uri.Host);
        }

        private static async Task VerifyDnsAsync(
            string host,
            WebsiteVerificationResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(
                    host,
                    cancellationToken);

                result.IpAddresses = addresses
                    .Select(address => address.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                result.DnsResolved = result.IpAddresses.Count > 0;

                if (!result.DnsResolved)
                {
                    result.Errors.Add(
                        "DNS не вернул IP-адреса домена.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.DnsResolved = false;
                result.Errors.Add("Ошибка DNS: " + GetSafeError(ex));
            }
        }

        private static async Task<string> VerifyHttpAsync(
            Uri address,
            bool isHttps,
            WebsiteVerificationResult result,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            X509Certificate2? serverCertificate = null;
            SslPolicyErrors certificateErrors = SslPolicyErrors.None;

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            };

            if (isHttps)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (_, certificate, _, errors) =>
                    {
                        certificateErrors = errors;

                        if (certificate != null)
                        {
                            serverCertificate =
                                new X509Certificate2(certificate);
                        }

                        // Проверка сертификата не отключается.
                        return errors == SslPolicyErrors.None;
                    };
            }

            using var client = new HttpClient(handler)
            {
                Timeout = timeout
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "TextFileProcessor-Build6/1.0");

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    address);

                using HttpResponseMessage response =
                    await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                int statusCode = (int)response.StatusCode;
                string finalUrl =
                    response.RequestMessage?.RequestUri?.ToString() ??
                    address.ToString();

                if (isHttps)
                {
                    result.HttpsAvailable = true;
                    result.HttpsStatusCode = statusCode;
                    result.HttpsFinalUrl = finalUrl;
                    result.CertificatePresent =
                        serverCertificate != null;
                    result.CertificateValid =
                        serverCertificate != null &&
                        certificateErrors == SslPolicyErrors.None;

                    if (serverCertificate != null)
                    {
                        result.CertificateSubject =
                            serverCertificate.Subject;
                        result.CertificateIssuer =
                            serverCertificate.Issuer;
                        result.CertificateExpiresAt =
                            new DateTimeOffset(
                                serverCertificate.NotAfter);
                    }

                    if (certificateErrors != SslPolicyErrors.None)
                    {
                        result.CertificateError =
                            certificateErrors.ToString();
                    }
                }
                else
                {
                    result.HttpAvailable = true;
                    result.HttpStatusCode = statusCode;
                    result.HttpFinalUrl = finalUrl;
                }

                return await ReadLimitedBodyAsync(
                    response,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (isHttps)
                {
                    result.HttpsAvailable = false;
                    result.CertificatePresent =
                        serverCertificate != null;
                    result.CertificateValid = false;

                    if (serverCertificate != null)
                    {
                        result.CertificateSubject =
                            serverCertificate.Subject;
                        result.CertificateIssuer =
                            serverCertificate.Issuer;
                        result.CertificateExpiresAt =
                            new DateTimeOffset(
                                serverCertificate.NotAfter);
                    }

                    if (certificateErrors != SslPolicyErrors.None)
                    {
                        result.CertificateError =
                            certificateErrors.ToString();
                    }

                    result.Errors.Add(
                        "Ошибка HTTPS: " + GetSafeError(ex));
                }
                else
                {
                    result.HttpAvailable = false;
                    result.Errors.Add(
                        "Ошибка HTTP: " + GetSafeError(ex));
                }

                return string.Empty;
            }
            finally
            {
                serverCertificate?.Dispose();
            }
        }

        private static async Task<string> ReadLimitedBodyAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            await using Stream stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken);

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                true,
                8192,
                leaveOpen: false);

            var builder = new StringBuilder();
            var buffer = new char[8192];

            while (builder.Length < MaximumBodyCharacters)
            {
                int requested = Math.Min(
                    buffer.Length,
                    MaximumBodyCharacters - builder.Length);

                int read = await reader.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken);

                if (read <= 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
            }

            return builder.ToString();
        }

        private static string GetSafeError(Exception exception)
        {
            string message = exception.Message
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            return message.Length <= 500
                ? message
                : message.Substring(0, 500);
        }
    }
}