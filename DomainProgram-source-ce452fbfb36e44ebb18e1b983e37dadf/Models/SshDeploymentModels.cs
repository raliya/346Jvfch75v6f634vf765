using System.Text.RegularExpressions;

namespace TextFileProcessor.Models;

public sealed class SshDeploymentSettings
{
    public string Host { get; set; } = "185.115.33.18";

    public int Port { get; set; } = 22;

    public string Username { get; set; } = "root";

    public string EncryptedPassword { get; set; } = string.Empty;

    public string HostKeySha256 { get; set; } = string.Empty;

    public string RemoteSitesRoot { get; set; } =
        "/var/www/www-root/data/www";

    public string Owner { get; set; } = "www-root";

    public string Group { get; set; } = "www-root";

    public void Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException(
                "Не указан SSH-сервер.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "SSH-порт должен находиться в диапазоне 1–65535.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidOperationException(
                "Не указан SSH-пользователь.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Введите или сохраните SSH-пароль.");
        }

        if (string.IsNullOrWhiteSpace(HostKeySha256))
        {
            throw new InvalidOperationException(
                "Не указан SHA-256 fingerprint SSH-ключа сервера.");
        }

        ValidateRemoteRoot(RemoteSitesRoot);
        ValidateLinuxName(Owner, "владельца");
        ValidateLinuxName(Group, "группы");
    }

    private static void ValidateRemoteRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains('\0') ||
            value.Contains('\r') ||
            value.Contains('\n'))
        {
            throw new InvalidOperationException(
                "Корень сайтов должен быть абсолютным Linux-путём.");
        }

        var parts = value
            .Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);

        if (parts.Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException(
                "Корень сайтов содержит недопустимый сегмент.");
        }
    }

    private static void ValidateLinuxName(
        string value,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Regex.IsMatch(
                value,
                @"^[a-z_][a-z0-9_-]{0,31}$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                $"Некорректное имя {description}: {value}");
        }
    }
}

public sealed class SiteDeploymentRequest
{
    public string Domain { get; init; } = string.Empty;

    public string LocalDirectory { get; init; } = string.Empty;
}

public sealed class SiteDeploymentResult
{
    public string Domain { get; init; } = string.Empty;

    public string RemoteDirectory { get; init; } = string.Empty;

    public int UploadedFiles { get; init; }

    public long UploadedBytes { get; init; }
}

public sealed record SftpDeploymentProgress(
    int Percent,
    string Message);
