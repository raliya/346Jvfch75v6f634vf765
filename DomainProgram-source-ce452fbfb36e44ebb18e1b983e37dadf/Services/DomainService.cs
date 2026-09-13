using System.Globalization;

namespace TextFileProcessor.Services;

public sealed class DomainService
{
    public string Normalize(string input)
    {
        var value = input.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Обнаружена пустая строка домена.");
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

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"Некорректный протокол домена: {input}");
        }

        var unicodeHost = uri.Host
            .Trim()
            .TrimEnd('.');

        if (unicodeHost.StartsWith(
                "www.",
                StringComparison.OrdinalIgnoreCase))
        {
            unicodeHost = unicodeHost[4..];
        }

        string host;

        try
        {
            host = new IdnMapping()
                .GetAscii(unicodeHost)
                .ToLowerInvariant();
        }
        catch
        {
            throw new InvalidOperationException(
                $"Некорректный домен: {input}");
        }

        if (host.Length > 253)
        {
            throw new InvalidOperationException(
                $"Домен слишком длинный: {input}");
        }

        var labels = host.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries);

        if (labels.Length < 2)
        {
            throw new InvalidOperationException(
                $"У домена отсутствует доменная зона: {input}");
        }

        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63 ||
                label.StartsWith('-') ||
                label.EndsWith('-') ||
                label.Any(character =>
                    !char.IsLetterOrDigit(character) &&
                    character != '-'))
            {
                throw new InvalidOperationException(
                    $"Некорректный домен: {input}");
            }
        }

        return host;
    }
}
