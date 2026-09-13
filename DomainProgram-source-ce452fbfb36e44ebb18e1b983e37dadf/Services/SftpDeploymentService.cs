using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using TextFileProcessor.Models;

namespace TextFileProcessor.Services;

public sealed class SftpDeploymentService
{
    private static readonly Regex DomainRegex = new(
        @"^(?=.{1,253}$)(?:(?!-)[a-z0-9-]{1,63}(?<!-)\.)+" +
        @"(?!-)[a-z0-9-]{2,63}(?<!-)$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);

    public async Task<string> ReadServerFingerprintAsync(
        SshDeploymentSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new InvalidOperationException(
                "Не указан SSH-сервер.");
        }

        if (settings.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "Некорректный SSH-порт.");
        }

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            throw new InvalidOperationException(
                "Не указан SSH-пользователь.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Введите или сохраните SSH-пароль.");
        }

        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? fingerprint = null;

                using var client = new SshClient(
                    CreateConnectionInfo(settings, password));

                client.HostKeyReceived += (_, eventArgs) =>
                {
                    fingerprint =
                        CreateFingerprint(eventArgs.HostKey);

                    // Это подключение используется только для чтения
                    // fingerprint. Перед рабочим подключением пользователь
                    // должен независимо сверить и сохранить значение.
                    eventArgs.CanTrust = true;
                };

                client.Connect();

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(fingerprint))
                    {
                        throw new InvalidOperationException(
                            "Сервер не предоставил SSH host key.");
                    }

                    return fingerprint;
                }
                finally
                {
                    if (client.IsConnected)
                    {
                        client.Disconnect();
                    }
                }
            },
            cancellationToken);
    }

    public async Task TestConnectionAsync(
        SshDeploymentSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        settings.Validate(password);

        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var client =
                    CreateSshClient(settings, password);

                client.Connect();

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var command =
                        client.CreateCommand("printf 'SSH_OK'");

                    command.CommandTimeout =
                        TimeSpan.FromSeconds(30);

                    var output = command.Execute();

                    if (command.ExitStatus != 0 ||
                        !string.Equals(
                            output,
                            "SSH_OK",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "SSH-проверка завершилась ошибкой.");
                    }
                }
                finally
                {
                    if (client.IsConnected)
                    {
                        client.Disconnect();
                    }
                }
            },
            cancellationToken);
    }

    public async Task<SiteDeploymentResult> DeployAsync(
        SshDeploymentSettings settings,
        string password,
        SiteDeploymentRequest request,
        IProgress<SftpDeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        settings.Validate(password);

        var domain = NormalizeDomain(request.Domain);

        if (string.IsNullOrWhiteSpace(request.LocalDirectory))
        {
            throw new InvalidOperationException(
                "Не указана локальная папка сайта.");
        }

        var localRoot =
            Path.GetFullPath(request.LocalDirectory);

        if (!Directory.Exists(localRoot))
        {
            throw new DirectoryNotFoundException(
                $"Локальная папка не найдена: {localRoot}");
        }

        var manifest = BuildManifest(
            localRoot,
            cancellationToken);

        if (manifest.Files.Count == 0)
        {
            throw new InvalidOperationException(
                "После исключения SQL-файлов нет файлов для загрузки.");
        }

        var startFile = FindStartFile(manifest.Files);

        if (startFile is null)
        {
            throw new InvalidOperationException(
                "В корне сайта не найден index.php, " +
                "index.html или index.htm.");
        }

        var operationId =
            Guid.NewGuid().ToString("N")[..12];

        var sitesRoot =
            NormalizeRemotePath(settings.RemoteSitesRoot);

        var targetDirectory =
            $"{sitesRoot}/{domain}";

        var uploadDirectory =
            $"{sitesRoot}/.upload-{domain}-{operationId}";

        var backupDirectory =
            $"{sitesRoot}/.backup-{domain}-{operationId}";

        progress?.Report(
            new SftpDeploymentProgress(
                0,
                "Подключение к SSH/SFTP."));

        return await Task.Run(
            () =>
            {
                using var ssh =
                    CreateSshClient(settings, password);

                using var sftp =
                    CreateSftpClient(settings, password);

                var targetMoved = false;
                var uploadMoved = false;

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ssh.Connect();
                    sftp.Connect();

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!sftp.Exists(targetDirectory))
                    {
                        throw new InvalidOperationException(
                            "Каталог домена отсутствует на сервере: " +
                            targetDirectory +
                            ". Сначала создайте WWW-домен в ISPmanager.");
                    }

                    RemoveTemporaryDirectory(
                        ssh,
                        uploadDirectory);

                    RemoveTemporaryDirectory(
                        ssh,
                        backupDirectory);

                    RunChecked(
                        ssh,
                        $"mkdir -p -- {ShellQuote(uploadDirectory)}");

                    UploadManifest(
                        sftp,
                        manifest,
                        uploadDirectory,
                        progress,
                        cancellationToken);

                    progress?.Report(
                        new SftpDeploymentProgress(
                            78,
                            "Проверка размеров загруженных файлов."));

                    VerifyManifest(
                        sftp,
                        manifest,
                        uploadDirectory,
                        cancellationToken);

                    var uploadedStartFile =
                        CombineRemote(
                            uploadDirectory,
                            startFile.RelativePath);

                    if (!sftp.Exists(uploadedStartFile))
                    {
                        throw new InvalidOperationException(
                            "Во временной папке отсутствует стартовый файл.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    progress?.Report(
                        new SftpDeploymentProgress(
                            85,
                            "Создание серверной резервной копии."));

                    RunChecked(
                        ssh,
                        $"mv -- {ShellQuote(targetDirectory)} " +
                        $"{ShellQuote(backupDirectory)}");

                    targetMoved = true;

                    progress?.Report(
                        new SftpDeploymentProgress(
                            90,
                            "Переключение каталога сайта."));

                    RunChecked(
                        ssh,
                        $"mv -- {ShellQuote(uploadDirectory)} " +
                        $"{ShellQuote(targetDirectory)}");

                    uploadMoved = true;

                    RunChecked(
                        ssh,
                        $"chown -R -- " +
                        $"{ShellQuote(settings.Owner + ":" + settings.Group)} " +
                        $"{ShellQuote(targetDirectory)}");

                    RunChecked(
                        ssh,
                        $"find {ShellQuote(targetDirectory)} " +
                        "-type d -exec chmod 0755 {} +");

                    RunChecked(
                        ssh,
                        $"find {ShellQuote(targetDirectory)} " +
                        "-type f -exec chmod 0644 {} +");

                    var finalStartFile =
                        CombineRemote(
                            targetDirectory,
                            startFile.RelativePath);

                    RunChecked(
                        ssh,
                        $"test -f {ShellQuote(finalStartFile)}");

                    progress?.Report(
                        new SftpDeploymentProgress(
                            97,
                            "Удаление серверной резервной папки."));

                    RemoveTemporaryDirectory(
                        ssh,
                        backupDirectory);

                    targetMoved = false;

                    progress?.Report(
                        new SftpDeploymentProgress(
                            100,
                            "Загрузка сайта завершена."));

                    return new SiteDeploymentResult
                    {
                        Domain = domain,
                        RemoteDirectory = targetDirectory,
                        UploadedFiles = manifest.Files.Count,
                        UploadedBytes = manifest.TotalBytes
                    };
                }
                catch
                {
                    TryRollback(
                        ssh,
                        targetDirectory,
                        uploadDirectory,
                        backupDirectory,
                        targetMoved,
                        uploadMoved);

                    throw;
                }
                finally
                {
                    if (sftp.IsConnected)
                    {
                        sftp.Disconnect();
                    }

                    if (ssh.IsConnected)
                    {
                        ssh.Disconnect();
                    }
                }
            },
            cancellationToken);
    }

    private static LocalManifest BuildManifest(
        string localRoot,
        CancellationToken cancellationToken)
    {
        var files = new List<LocalFile>();
        var directories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var directory in
                 Directory.EnumerateDirectories(
                     localRoot,
                     "*",
                     options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = NormalizeRelativePath(
                Path.GetRelativePath(
                    localRoot,
                    directory));

            directories.Add(relative);
        }

        foreach (var path in
                 Directory.EnumerateFiles(
                     localRoot,
                     "*",
                     options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = new FileInfo(path);

            if ((file.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (string.Equals(
                    file.Extension,
                    ".sql",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(
                    file.Name,
                    "Thumbs.db",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    file.Name,
                    "desktop.ini",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = NormalizeRelativePath(
                Path.GetRelativePath(
                    localRoot,
                    path));

            files.Add(
                new LocalFile(
                    file.FullName,
                    relative,
                    file.Length));
        }

        var orderedDirectories = directories
            .OrderBy(path =>
                path.Count(character => character == '/'))
            .ThenBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        files.Sort(
            (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(
                    left.RelativePath,
                    right.RelativePath));

        return new LocalManifest(
            files,
            orderedDirectories,
            files.Sum(file => file.Length));
    }

    private static LocalFile? FindStartFile(
        IReadOnlyCollection<LocalFile> files)
    {
        var names = new[]
        {
            "index.php",
            "index.html",
            "index.htm"
        };

        foreach (var name in names)
        {
            var result = files.FirstOrDefault(
                file =>
                    string.Equals(
                        file.RelativePath,
                        name,
                        StringComparison.OrdinalIgnoreCase));

            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static void UploadManifest(
        SftpClient sftp,
        LocalManifest manifest,
        string remoteRoot,
        IProgress<SftpDeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var relativeDirectory in manifest.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EnsureSftpDirectory(
                sftp,
                CombineRemote(
                    remoteRoot,
                    relativeDirectory));
        }

        var uploadedFiles = 0;
        long uploadedBytes = 0;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteFile = CombineRemote(
                remoteRoot,
                file.RelativePath);

            EnsureSftpDirectory(
                sftp,
                GetRemoteDirectoryName(remoteFile));

            using var input = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);

            sftp.UploadFile(
                input,
                remoteFile,
                true);

            uploadedFiles++;
            uploadedBytes += file.Length;

            var percent = manifest.TotalBytes > 0
                ? 5 + (int)Math.Min(
                    70,
                    uploadedBytes * 70 /
                    manifest.TotalBytes)
                : 75;

            progress?.Report(
                new SftpDeploymentProgress(
                    percent,
                    $"Загружено: {uploadedFiles}/" +
                    $"{manifest.Files.Count}; " +
                    file.RelativePath));
        }
    }

    private static void VerifyManifest(
        SftpClient sftp,
        LocalManifest manifest,
        string remoteRoot,
        CancellationToken cancellationToken)
    {
        var verifiedCount = 0;
        long verifiedBytes = 0;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteFile = CombineRemote(
                remoteRoot,
                file.RelativePath);

            if (!sftp.Exists(remoteFile))
            {
                throw new InvalidOperationException(
                    $"На сервере отсутствует файл: {file.RelativePath}");
            }

            var attributes =
                sftp.GetAttributes(remoteFile);

            if (attributes.IsDirectory)
            {
                throw new InvalidOperationException(
                    $"Вместо файла обнаружен каталог: " +
                    file.RelativePath);
            }

            var remoteSize =
                Convert.ToInt64(attributes.Size);

            if (remoteSize != file.Length)
            {
                throw new InvalidOperationException(
                    $"Размер файла не совпадает: " +
                    $"{file.RelativePath}. " +
                    $"Локально: {file.Length}; " +
                    $"сервер: {remoteSize}.");
            }

            verifiedCount++;
            verifiedBytes += remoteSize;
        }

        if (verifiedCount != manifest.Files.Count ||
            verifiedBytes != manifest.TotalBytes)
        {
            throw new InvalidOperationException(
                "Количество или общий размер файлов не совпадают.");
        }
    }

    private static void RemoveTemporaryDirectory(
        SshClient ssh,
        string directory)
    {
        if (!directory.Contains(
                "/.upload-",
                StringComparison.Ordinal) &&
            !directory.Contains(
                "/.backup-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Отказ от удаления неподтверждённой папки.");
        }

        RunChecked(
            ssh,
            $"if test -e {ShellQuote(directory)}; " +
            $"then rm -rf -- {ShellQuote(directory)}; fi");
    }

    private static void TryRollback(
        SshClient ssh,
        string targetDirectory,
        string uploadDirectory,
        string backupDirectory,
        bool targetMoved,
        bool uploadMoved)
    {
        if (!ssh.IsConnected)
        {
            return;
        }

        try
        {
            if (uploadMoved)
            {
                TryRun(
                    ssh,
                    $"if test -e {ShellQuote(targetDirectory)}; " +
                    $"then rm -rf -- {ShellQuote(targetDirectory)}; fi");
            }
            else
            {
                TryRun(
                    ssh,
                    $"if test -e {ShellQuote(uploadDirectory)}; " +
                    $"then rm -rf -- {ShellQuote(uploadDirectory)}; fi");
            }

            if (targetMoved)
            {
                TryRun(
                    ssh,
                    $"if test -d {ShellQuote(backupDirectory)}; " +
                    $"then mv -- {ShellQuote(backupDirectory)} " +
                    $"{ShellQuote(targetDirectory)}; fi");
            }
        }
        catch
        {
            // Исходная ошибка важнее ошибки автоматического отката.
        }
    }

    private static void RunChecked(
        SshClient ssh,
        string commandText)
    {
        using var command =
            ssh.CreateCommand(commandText);

        command.CommandTimeout =
            TimeSpan.FromMinutes(15);

        command.Execute();

        if (command.ExitStatus == 0)
        {
            return;
        }

        var error = string.IsNullOrWhiteSpace(command.Error)
            ? "сервер не вернул описание ошибки"
            : command.Error
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

        if (error.Length > 1000)
        {
            error = error[..1000];
        }

        throw new InvalidOperationException(
            $"Серверная команда завершилась с кодом " +
            $"{command.ExitStatus}: {error}");
    }

    private static void TryRun(
        SshClient ssh,
        string commandText)
    {
        try
        {
            using var command =
                ssh.CreateCommand(commandText);

            command.CommandTimeout =
                TimeSpan.FromMinutes(5);

            command.Execute();
        }
        catch
        {
            // Используется только при автоматическом откате.
        }
    }

    private static void EnsureSftpDirectory(
        SftpClient sftp,
        string directory)
    {
        var normalized =
            NormalizeRemotePath(directory);

        if (normalized == "/")
        {
            return;
        }

        var current = string.Empty;

        foreach (var segment in normalized.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;

            if (!sftp.Exists(current))
            {
                sftp.CreateDirectory(current);
            }
        }
    }

    private static SshClient CreateSshClient(
        SshDeploymentSettings settings,
        string password)
    {
        var client = new SshClient(
            CreateConnectionInfo(settings, password))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };

        client.HostKeyReceived += (_, eventArgs) =>
            ValidateHostKey(settings, eventArgs);

        return client;
    }

    private static SftpClient CreateSftpClient(
        SshDeploymentSettings settings,
        string password)
    {
        var client = new SftpClient(
            CreateConnectionInfo(settings, password))
        {
            OperationTimeout = TimeSpan.FromMinutes(15),
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };

        client.HostKeyReceived += (_, eventArgs) =>
            ValidateHostKey(settings, eventArgs);

        return client;
    }

    private static ConnectionInfo CreateConnectionInfo(
        SshDeploymentSettings settings,
        string password)
    {
        var authentication =
            new PasswordAuthenticationMethod(
                settings.Username.Trim(),
                password);

        return new ConnectionInfo(
            settings.Host.Trim(),
            settings.Port,
            settings.Username.Trim(),
            authentication)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static void ValidateHostKey(
        SshDeploymentSettings settings,
        HostKeyEventArgs eventArgs)
    {
        var expected = NormalizeFingerprint(
            settings.HostKeySha256);

        var actual = NormalizeFingerprint(
            CreateFingerprint(eventArgs.HostKey));

        var expectedBytes =
            Encoding.ASCII.GetBytes(expected);

        var actualBytes =
            Encoding.ASCII.GetBytes(actual);

        var matches =
            expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                actualBytes);

        eventArgs.CanTrust = matches;
    }

    private static string CreateFingerprint(
        byte[] hostKey)
    {
        var hash = SHA256.HashData(hostKey);

        return "SHA256:" +
               Convert.ToBase64String(hash)
                   .TrimEnd('=');
    }

    private static string NormalizeFingerprint(
        string value)
    {
        var normalized = value.Trim();

        if (normalized.StartsWith(
                "SHA256:",
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized
            .Trim()
            .TrimEnd('=');
    }

    private static string NormalizeDomain(
        string value)
    {
        var domain = value
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();

        if (!DomainRegex.IsMatch(domain))
        {
            throw new InvalidOperationException(
                $"Некорректный домен: {value}");
        }

        return domain;
    }

    private static string NormalizeRelativePath(
        string value)
    {
        var normalized =
            value.Replace('\\', '/').Trim('/');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var parts = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException(
                "Относительный путь содержит недопустимый сегмент.");
        }

        return string.Join("/", parts);
    }

    private static string NormalizeRemotePath(
        string value)
    {
        var normalized =
            value.Replace('\\', '/').Trim();

        if (!normalized.StartsWith(
                "/",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Серверный путь должен быть абсолютным.");
        }

        var parts = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException(
                "Серверный путь содержит недопустимый сегмент.");
        }

        return "/" + string.Join("/", parts);
    }

    private static string CombineRemote(
        string root,
        string relativePath)
    {
        var normalizedRoot =
            NormalizeRemotePath(root);

        var normalizedRelative =
            NormalizeRelativePath(relativePath);

        return string.IsNullOrEmpty(normalizedRelative)
            ? normalizedRoot
            : normalizedRoot.TrimEnd('/') +
              "/" +
              normalizedRelative;
    }

    private static string GetRemoteDirectoryName(
        string path)
    {
        var normalized =
            NormalizeRemotePath(path);

        var separator =
            normalized.LastIndexOf('/');

        return separator <= 0
            ? "/"
            : normalized[..separator];
    }

    private static string ShellQuote(string value)
    {
        if (value.Contains('\0') ||
            value.Contains('\r') ||
            value.Contains('\n'))
        {
            throw new InvalidOperationException(
                "Команда содержит недопустимые символы.");
        }

        return "'" +
               value.Replace(
                   "'",
                   "'\"'\"'",
                   StringComparison.Ordinal) +
               "'";
    }

    private sealed record LocalFile(
        string FullPath,
        string RelativePath,
        long Length);

    private sealed record LocalManifest(
        List<LocalFile> Files,
        List<string> Directories,
        long TotalBytes);
}
