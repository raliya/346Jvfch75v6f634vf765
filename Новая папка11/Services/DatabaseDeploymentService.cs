using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using TextFileProcessor.Models;

namespace TextFileProcessor.Services;

public sealed record DatabaseDeploymentProgress(
    int Percent,
    string Message);

public sealed class DatabaseDeploymentResult
{
    public string DatabaseName { get; init; } = string.Empty;

    public string DatabaseUser { get; init; } = string.Empty;

    public string SqlFile { get; init; } = string.Empty;

    public bool DatabaseWasCreated { get; init; }
}

public sealed class DatabaseDeploymentService
{
    private static readonly Regex DatabaseNameRegex = new(
        @"^[A-Za-z0-9_]{1,64}$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant);

    private static readonly Regex DatabaseUserRegex = new(
        @"^[A-Za-z0-9_]{1,64}$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant);

        public async Task<DatabaseDeploymentResult> DeployAsync(
        SshDeploymentSettings settings,
        string sshPassword,
        string localSiteDirectory,
        IProgress<DatabaseDeploymentProgress>? progress,
        CancellationToken cancellationToken,
        IspmanagerSettings ispSettings,
        string ispPassword)
    {
        settings.Validate(sshPassword);

        if (string.IsNullOrWhiteSpace(localSiteDirectory))
            throw new InvalidOperationException("Не указана локальная папка сайта.");

        var siteDirectory = Path.GetFullPath(localSiteDirectory);
        if (!Directory.Exists(siteDirectory))
            throw new DirectoryNotFoundException("Не найдена папка результата.");

        var configPath = Path.Combine(siteDirectory, "config", "config.php");
        if (!File.Exists(configPath))
            throw new FileNotFoundException("Не найден config/config.php.", configPath);

        var config = DeploymentDbConfig.Read(
            await File.ReadAllTextAsync(configPath, cancellationToken));

        var sqlFile = FindSqlFile(siteDirectory);
        if (new FileInfo(sqlFile).Length == 0)
            throw new InvalidOperationException("SQL-дамп пуст.");

        if (!string.Equals(settings.Owner.Trim(), ispSettings.Owner.Trim(),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Владельцы в настройках SSH и ISPmanager должны совпадать.");

        progress?.Report(new DatabaseDeploymentProgress(
            10, "Проверка БД через ISPmanager API. Пароли не изменяются."));

        var created = await DeploymentSchemaFix.EnsureDatabaseAsync(
            ispSettings, ispPassword, config, cancellationToken);

        var deployed = await Task.Run(
            () => DeployCore(
                settings, sshPassword,
                config.Name, config.User, config.Password, config.Host,
                sqlFile, progress, cancellationToken),
            cancellationToken);

        return new DatabaseDeploymentResult
        {
            DatabaseName = deployed.DatabaseName,
            DatabaseUser = deployed.DatabaseUser,
            SqlFile = deployed.SqlFile,
            DatabaseWasCreated = created
        };
    }
    private static DatabaseDeploymentResult DeployCore(
        SshDeploymentSettings settings,
        string sshPassword,
        string databaseName,
        string databaseUser,
        string databasePassword,
        string databaseHost,
        string sqlFile,
        IProgress<DatabaseDeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var operationId =
            Guid.NewGuid().ToString("N");

        var remoteDirectory =
            $"/tmp/text-file-processor-db-{operationId}";

        var remoteSql =
            $"{remoteDirectory}/database.sql";

        var remoteSecret =
            $"{remoteDirectory}/database-password";

        var remoteScript =
            $"{remoteDirectory}/run.sh";

        using var ssh = CreateSshClient(
            settings,
            sshPassword);

        using var sftp = CreateSftpClient(
            settings,
            sshPassword);

        var temporaryDirectoryCreated = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(
                new DatabaseDeploymentProgress(
                    15,
                    "Подключение к SSH/SFTP."));

            ssh.Connect();
            sftp.Connect();

            cancellationToken.ThrowIfCancellationRequested();

            RunChecked(
                ssh,
                "umask 077; mkdir -- " +
                ShellQuote(remoteDirectory));

            temporaryDirectoryCreated = true;

            progress?.Report(
                new DatabaseDeploymentProgress(
                    25,
                    "Загрузка SQL во временный закрытый каталог."));

            using (var sqlStream = new FileStream(
                       sqlFile,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       1024 * 1024,
                       FileOptions.SequentialScan))
            {
                var sqlLength = sqlStream.Length;

                sftp.UploadFile(
                    sqlStream,
                    remoteSql,
                    true,
                    uploaded =>
                    {
                        var percent = sqlLength > 0
                            ? 25 + (int)Math.Min(
                                35UL,
                                uploaded * 35UL /
                                (ulong)sqlLength)
                            : 60;

                        progress?.Report(
                            new DatabaseDeploymentProgress(
                                percent,
                                "Загрузка SQL: " +
                                uploaded +
                                " из " +
                                sqlLength +
                                " байт."));
                    });
            }

            cancellationToken.ThrowIfCancellationRequested();

            using (var secretStream = new MemoryStream(
                       new UTF8Encoding(false).GetBytes(DeploymentDbConfig.ClientOptions(databaseHost, databaseUser, databasePassword))))
            {
                sftp.UploadFile(
                    secretStream,
                    remoteSecret,
                    true);
            }

            var scriptText = CreateRemoteScript(
                databaseName,
                databaseUser,
                settings.Owner,
                remoteSql,
                remoteSecret);

            var normalizedScriptText = scriptText
                .Replace(
                    "\r\n",
                    "\n",
                    StringComparison.Ordinal)
                .Replace(
                    "\r",
                    "\n",
                    StringComparison.Ordinal);

            using (var scriptStream = new MemoryStream(
                       new UTF8Encoding(false).GetBytes(
                           normalizedScriptText)))
            {
                sftp.UploadFile(
                    scriptStream,
                    remoteScript,
                    true);
            }

            RunChecked(
                ssh,
                "chmod 700 -- " +
                ShellQuote(remoteDirectory) +
                " && chmod 600 -- " +
                ShellQuote(remoteSql) +
                " " +
                ShellQuote(remoteSecret) +
                " " +
                ShellQuote(remoteScript));

            progress?.Report(
                new DatabaseDeploymentProgress(
                    65,
                    "Проверка доступа, пустоты БД и импорт SQL."));

            cancellationToken.ThrowIfCancellationRequested();

            var output = RunChecked(
                ssh,
                "/bin/sh " +
                ShellQuote(remoteScript));

            var databaseWasCreated =
                output.Contains(
                    "DATABASE_CREATED",
                    StringComparison.Ordinal);

            if (!output.Contains(
                    "SQL_IMPORT_OK",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Сервер не подтвердил успешный импорт SQL.");
            }

            progress?.Report(
                new DatabaseDeploymentProgress(
                    100,
                    "База данных готова, SQL импортирован."));

            return new DatabaseDeploymentResult
            {
                DatabaseName = databaseName,
                DatabaseUser = databaseUser,
                SqlFile = sqlFile,
                DatabaseWasCreated = databaseWasCreated
            };
        }
        catch (Exception exception)
        {
            var safeMessage = exception.Message.Replace(
                databasePassword,
                "[СКРЫТО]",
                StringComparison.Ordinal);

            throw new InvalidOperationException(safeMessage);
        }
        finally
        {
            if (temporaryDirectoryCreated &&
                ssh.IsConnected)
            {
                TryRun(
                    ssh,
                    "rm -rf -- " +
                    ShellQuote(remoteDirectory));
            }

            if (sftp.IsConnected)
            {
                sftp.Disconnect();
            }

            if (ssh.IsConnected)
            {
                ssh.Disconnect();
            }
        }
    }

        private static string CreateRemoteScript(
        string databaseName,
        string databaseUser,
        string owner,
        string remoteSql,
        string remoteSecret)
    {
        return
$$"""
#!/bin/sh
set -eu
umask 077
DATABASE_NAME={{ShellQuote(databaseName)}}
REMOTE_SQL={{ShellQuote(remoteSql)}}
OPTIONS_FILE={{ShellQuote(remoteSecret)}}
ERROR_FILE="${OPTIONS_FILE}.error"
unset MYSQL_PWD MYSQL_HOST MYSQL_TCP_PORT MYSQL_UNIX_PORT

if command -v mysql >/dev/null 2>&1; then
    DB_CLIENT=mysql
elif command -v mariadb >/dev/null 2>&1; then
    DB_CLIENT=mariadb
else
    echo "MYSQL_CLIENT_NOT_FOUND" >&2
    exit 34
fi

if ! command -v flock >/dev/null 2>&1; then
    echo "FLOCK_NOT_FOUND: нужен flock для защиты от параллельного импорта." >&2
    exit 35
fi

test -s "$REMOTE_SQL"
test -s "$OPTIONS_FILE"

STATE="$HOME/.local/state/text-file-processor-db"
mkdir -p -- "$STATE"
chmod 700 -- "$STATE"
exec 9>"$STATE/$DATABASE_NAME.lock"

if ! flock -n 9; then
    echo "DATABASE_BUSY: для этой БД уже выполняется импорт." >&2
    exit 36
fi

mysql_error()
{
     code="$(sed -n 's/.*ERROR \([0-9][0-9]*\).*/\1/p' "$ERROR_FILE" | head -n 1)"
    echo "MYSQL_ERROR ${code:-unknown}: операция $1." >&2
    case "$code" in
        1045) echo "Доступ отклонён. Проверьте пароль и учётную запись; пароль не сбрасывался." >&2 ;;
        1044) echo "У пользователя недостаточно прав на указанную БД." >&2 ;;
        1049) echo "MySQL не нашёл указанную БД на выбранном сервере." >&2 ;;
        2002|2003) echo "Не удалось подключиться к MySQL на db.host." >&2 ;;
    esac
}

QUERY="SELECT
(SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()) +
(SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema=DATABASE()) +
(SELECT COUNT(*) FROM information_schema.events WHERE event_schema=DATABASE())"

if ! OBJECTS="$(
    "$DB_CLIENT" --defaults-file="$OPTIONS_FILE" \
        --connect-timeout=10 --batch --skip-column-names \
        --database="$DATABASE_NAME" \
        --execute="$QUERY" 2>"$ERROR_FILE"
)"; then
    mysql_error "проверка подключения и объектов БД"
    exit 41
fi

case "$OBJECTS" in
    ''|*[!0-9]*)
        echo "INVALID_OBJECT_COUNT: проверка пустоты БД не подтверждена." >&2
        exit 42
        ;;
esac

if [ "$OBJECTS" -ne 0 ]; then
    echo "DATABASE_NOT_EMPTY: импорт остановлен, существующие объекты не изменены." >&2
    exit 43
fi

if ! "$DB_CLIENT" --defaults-file="$OPTIONS_FILE" \
    --connect-timeout=10 --default-character-set=utf8mb4 \
    --database="$DATABASE_NAME" \
    < "$REMOTE_SQL" >"${OPTIONS_FILE}.output" 2>"$ERROR_FILE"; then
    mysql_error "импорт SQL"
    echo "Возможен частичный импорт. Автоматический повтор запрещён." >&2
    exit 44
fi

if ! TABLES="$(
    "$DB_CLIENT" --defaults-file="$OPTIONS_FILE" \
        --connect-timeout=10 --batch --skip-column-names \
        --database="$DATABASE_NAME" \
        --execute="SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()" \
        2>"$ERROR_FILE"
)"; then
    mysql_error "проверка после импорта"
    exit 45
fi

case "$TABLES" in
    ''|*[!0-9]*|0)
        echo "IMPORT_NOT_VERIFIED: после импорта не подтверждено наличие таблиц." >&2
        exit 46
        ;;
esac

echo "SQL_IMPORT_OK"
""";
    }
    private static string ReadPhpValue(
        string configText,
        string key)
    {
        var pattern =
            @"[""']" +
            Regex.Escape(key) +
            @"[""']\s*=>\s*" +
            @"(?<quote>[""'])" +
            @"(?<value>(?:\\.|(?!\k<quote>).)*)" +
            @"\k<quote>";

        var match = Regex.Match(
            configText,
            pattern,
            RegexOptions.CultureInvariant |
            RegexOptions.Singleline);

        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"В config.php не найден параметр '{key}'.");
        }

        var value = match
            .Groups["value"]
            .Value;

        value = Regex.Replace(
            value,
            @"\\(['""\\])",
            "$1");

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Параметр '{key}' в config.php пуст.");
        }

        return value;
    }

    private static void ValidateDatabaseCredentials(
        string databaseName,
        string databaseUser,
        string databasePassword)
    {
        if (!DatabaseNameRegex.IsMatch(databaseName))
        {
            throw new InvalidOperationException(
                $"Некорректное имя БД: {databaseName}");
        }

        if (!DatabaseUserRegex.IsMatch(databaseUser))
        {
            throw new InvalidOperationException(
                $"Некорректный пользователь БД: {databaseUser}");
        }

        if (databasePassword.Length > 1024 ||
            databasePassword.Contains('\0') ||
            databasePassword.Contains('\r') ||
            databasePassword.Contains('\n'))
        {
            throw new InvalidOperationException(
                "Пароль БД имеет недопустимый формат.");
        }
    }

    private static string FindSqlFile(
        string siteDirectory)
    {
        var files = Directory
            .EnumerateFiles(
                siteDirectory,
                "*.sql",
                SearchOption.AllDirectories)
            .OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "В папке результата не найден SQL-файл.");
        }

        if (files.Count == 1)
        {
            return files[0];
        }

        var rootFiles = files
            .Where(path =>
                string.Equals(
                    Path.GetDirectoryName(
                        Path.GetFullPath(path)),
                    Path.GetFullPath(siteDirectory)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rootFiles.Count == 1)
        {
            return rootFiles[0];
        }

        throw new InvalidOperationException(
            "Найдено несколько SQL-файлов. " +
            "Оставьте один SQL-файл в папке результата.");
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
            OperationTimeout = TimeSpan.FromMinutes(30),
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

        eventArgs.CanTrust =
            expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                actualBytes);
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

    private static string RunChecked(
        SshClient ssh,
        string commandText)
    {
        using var command =
            ssh.CreateCommand(commandText);

        command.CommandTimeout =
            TimeSpan.FromMinutes(60);

        var output = command.Execute();

        if (command.ExitStatus == 0)
        {
            return output ?? string.Empty;
        }

        var error = string.IsNullOrWhiteSpace(command.Error)
            ? output
            : command.Error;

        error = string.IsNullOrWhiteSpace(error)
            ? "сервер не вернул описание ошибки"
            : error
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

        if (error.Length > 2000)
        {
            error = error[..2000];
        }

        throw new InvalidOperationException(
            "Серверная команда завершилась с кодом " +
            command.ExitStatus +
            ": " +
            error);
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
            // Ошибка очистки не заменяет основную ошибку.
        }
    }

    private static string ShellQuote(
        string value)
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
}
