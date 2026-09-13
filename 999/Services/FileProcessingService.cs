using System.Text;
using System.Text.RegularExpressions;
using TextFileProcessor.Models;

namespace TextFileProcessor.Services;

public sealed class FileProcessingService
{
    private static readonly HashSet<string> BaseExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".php",
            ".html",
            ".htm",
            ".txt",
            ".sql"
        };

    private static readonly HashSet<string> AdditionalExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".css",
            ".js",
            ".json",
            ".xml",
            ".env",
            ".ini",
            ".conf"
        };

    public string CreatePreview(
        string sourceFolder,
        string outputFolder,
        string domain)
    {
        ValidatePaths(sourceFolder, outputFolder);

        var rootFiles = Directory
            .EnumerateFiles(
                sourceFolder,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToList();

        var rootDirectories = Directory
            .EnumerateDirectories(
                sourceFolder,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToList();

        var startFile = FindOptionalStartFile(sourceFolder);

        var configFiles = Directory
            .EnumerateFiles(
                sourceFolder,
                "config.php",
                SearchOption.AllDirectories)
            .ToList();

        var sqlFiles = Directory
            .EnumerateFiles(
                sourceFolder,
                "*.sql",
                SearchOption.AllDirectories)
            .ToList();

        var finalDomain = string.IsNullOrWhiteSpace(domain)
            ? "<домен>"
            : domain;

        var warning = string.Empty;

        if (startFile is null &&
            rootFiles.Count == 0 &&
            rootDirectories.Count == 1)
        {
            warning =
                "\n\nВНИМАНИЕ:\n" +
                "В выбранном корне находится только одна вложенная папка. " +
                "Возможно, выбран внешний уровень сайта.";
        }

        return
            $"Исходный корень:\n{sourceFolder}\n\n" +
            $"Конечный корень:\n" +
            $"{Path.Combine(outputFolder, finalDomain)}\n\n" +
            $"Стартовый файл:\n" +
            $"{startFile ?? "НЕ НАЙДЕН В КОРНЕ"}\n\n" +
            $"config.php найдено: {configFiles.Count}\n" +
            $"SQL-файлов найдено: {sqlFiles.Count}\n" +
            $"Файлов в корне: {rootFiles.Count}\n" +
            $"Папок в корне: {rootDirectories.Count}" +
            warning;
    }

    public async Task<ProcessingResult> ProcessAsync(
        DomainJob job,
        ProcessingOptions options,
        Func<string, string, bool> credentialsAreUnique,
        Action<int, string> report,
        CancellationToken cancellationToken)
    {
        ValidatePaths(
            options.SourceFolder,
            options.OutputFolder);

        if (string.IsNullOrWhiteSpace(options.SearchText1))
        {
            throw new InvalidOperationException(
                "Искомый текст №1 не заполнен.");
        }

        Directory.CreateDirectory(options.OutputFolder);

        var shortId = job.Id.Length >= 8
            ? job.Id[..8]
            : job.Id;

        var temporaryFolder = Path.Combine(
            options.OutputFolder,
            $".processing-{job.Domain}-{shortId}");

        var finalFolder = Path.Combine(
            options.OutputFolder,
            job.Domain);

        var backupFolder = Path.Combine(
            options.OutputFolder,
            $".backup-{job.Domain}-{shortId}");

        DeleteDirectoryIfExists(temporaryFolder);

        Directory.CreateDirectory(temporaryFolder);

        try
        {
            report(
                5,
                "Копирование содержимого исходного корня.");

            await Task.Run(
                () => CopyDirectoryContents(
                    options.SourceFolder,
                    temporaryFolder,
                    cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            report(
                30,
                "Выполнение текстовых замен.");

            var replacementResult = await Task.Run(
                () => ReplaceText(
                    temporaryFolder,
                    job,
                    options,
                    cancellationToken),
                cancellationToken);

            if (replacementResult.ReplacementCount1 == 0)
            {
                throw new InvalidOperationException(
                    $"Искомый текст №1 «{options.SearchText1}» " +
                    "не найден в разрешённых текстовых файлах.");
            }

            report(
                65,
                "Проверка структуры и стартового файла.");

            var startFile = FindRequiredStartFile(
                temporaryFolder);

            EnsureNoExtraDomainLevel(
                temporaryFolder,
                job.Domain);

            report(
                72,
                "Чтение обработанного config.php.");

            var configResult = ParseConfigPhp(
                temporaryFolder);

            if (!credentialsAreUnique(
                    configResult.Credentials.Name,
                    configResult.Credentials.User))
            {
                throw new InvalidOperationException(
                    "Значения name или user из config.php " +
                    "повторяются в другом завершённом задании.");
            }

            report(
                78,
                "Поиск обработанного SQL-файла.");

            var sqlPath = FindSqlFile(
                temporaryFolder);

            cancellationToken.ThrowIfCancellationRequested();

            report(
                85,
                "Подготовка конечной папки домена.");

            var backupCreated = false;

            try
            {
                if (Directory.Exists(finalFolder))
                {
                    if (!options.ReplaceExistingFolders)
                    {
                        throw new InvalidOperationException(
                            $"Конечная папка уже существует: {finalFolder}");
                    }

                    DeleteDirectoryIfExists(backupFolder);

                    Directory.Move(
                        finalFolder,
                        backupFolder);

                    backupCreated = true;
                }

                Directory.Move(
                    temporaryFolder,
                    finalFolder);

                if (backupCreated)
                {
                    DeleteDirectoryIfExists(backupFolder);
                }
            }
            catch
            {
                if (Directory.Exists(finalFolder) &&
                    backupCreated)
                {
                    DeleteDirectoryIfExists(finalFolder);
                }

                if (backupCreated &&
                    Directory.Exists(backupFolder) &&
                    !Directory.Exists(finalFolder))
                {
                    Directory.Move(
                        backupFolder,
                        finalFolder);
                }

                throw;
            }

            report(
                100,
                "Локальная обработка завершена.");

            return new ProcessingResult
            {
                FinalFolder = finalFolder,
                StartFile = ChangeRoot(
                    startFile,
                    temporaryFolder,
                    finalFolder),
                ConfigPath = ChangeRoot(
                    configResult.Path,
                    temporaryFolder,
                    finalFolder),
                SqlPath = ChangeRoot(
                    sqlPath,
                    temporaryFolder,
                    finalFolder),
                Credentials = configResult.Credentials,
                FilesProcessed =
                    replacementResult.FilesProcessed,
                ReplacementCount1 =
                    replacementResult.ReplacementCount1,
                ReplacementCount2 =
                    replacementResult.ReplacementCount2
            };
        }
        catch
        {
            try
            {
                DeleteDirectoryIfExists(temporaryFolder);
            }
            catch
            {
                // Папка может быть занята сторонней программой.
            }

            throw;
        }
    }

    private static void ValidatePaths(
        string sourceFolder,
        string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) ||
            !Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException(
                "Исходная папка не существует.");
        }

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            throw new InvalidOperationException(
                "Не выбрана папка результата.");
        }

        var source = NormalizePath(sourceFolder);
        var output = NormalizePath(outputFolder);

        if (string.Equals(
                source,
                output,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Исходная папка и папка результата не могут совпадать.");
        }

        if (output.StartsWith(
                source + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Папка результата не должна находиться " +
                "внутри исходной папки.");
        }

        if (source.StartsWith(
                output + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Исходная папка не должна находиться " +
                "внутри папки результата.");
        }
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void CopyDirectoryContents(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(sourceFile);

            if ((fileInfo.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var destinationFile = Path.Combine(
                destination,
                fileInfo.Name);

            File.Copy(
                sourceFile,
                destinationFile,
                true);
        }

        foreach (var sourceDirectory in
                 Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryInfo =
                new DirectoryInfo(sourceDirectory);

            if ((directoryInfo.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var destinationDirectory = Path.Combine(
                destination,
                directoryInfo.Name);

            Directory.CreateDirectory(
                destinationDirectory);

            CopyDirectoryContents(
                sourceDirectory,
                destinationDirectory,
                cancellationToken);
        }
    }

    private static ReplacementResult ReplaceText(
        string root,
        DomainJob job,
        ProcessingOptions options,
        CancellationToken cancellationToken)
    {
        var extensions = new HashSet<string>(
            BaseExtensions,
            StringComparer.OrdinalIgnoreCase);

        if (options.IncludeAdditionalExtensions)
        {
            extensions.UnionWith(
                AdditionalExtensions);
        }

        var filesProcessed = 0;
        var replacementCount1 = 0;
        var replacementCount2 = 0;

        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(path);

            if (!extensions.Contains(extension))
            {
                continue;
            }

            if (!IsProbablyText(path))
            {
                continue;
            }

            var textDocument = ReadText(path);
            var content = textDocument.Text;
            var changed = false;

            var count1 = CountOccurrences(
                content,
                options.SearchText1);

            if (count1 > 0)
            {
                content = content.Replace(
                    options.SearchText1,
                    job.Domain,
                    StringComparison.Ordinal);

                replacementCount1 += count1;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(
                    options.SearchText2) &&
                !string.IsNullOrWhiteSpace(
                    job.SecondValue))
            {
                var count2 = CountOccurrences(
                    content,
                    options.SearchText2);

                if (count2 > 0)
                {
                    content = content.Replace(
                        options.SearchText2,
                        job.SecondValue,
                        StringComparison.Ordinal);

                    replacementCount2 += count2;
                    changed = true;
                }
            }

            if (changed)
            {
                WriteTextAtomically(
                    path,
                    content,
                    textDocument.Encoding);
            }

            filesProcessed++;
        }

        return new ReplacementResult(
            filesProcessed,
            replacementCount1,
            replacementCount2);
    }

    private static bool IsProbablyText(string path)
    {
        const int sampleSize = 8192;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var size = (int)Math.Min(
            stream.Length,
            sampleSize);

        if (size == 0)
        {
            return true;
        }

        var buffer = new byte[size];
        var read = stream.Read(
            buffer,
            0,
            buffer.Length);

        var controlCharacters = 0;

        for (var index = 0;
             index < read;
             index++)
        {
            var current = buffer[index];

            if (current == 0)
            {
                return false;
            }

            if (current < 9 ||
                current is > 13 and < 32)
            {
                controlCharacters++;
            }
        }

        return controlCharacters <=
               Math.Max(3, read / 50);
    }

    private static TextDocument ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            var encoding = new UTF8Encoding(true);

            return new TextDocument(
                encoding.GetString(
                    bytes,
                    3,
                    bytes.Length - 3),
                encoding);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE)
        {
            return new TextDocument(
                Encoding.Unicode.GetString(
                    bytes,
                    2,
                    bytes.Length - 2),
                Encoding.Unicode);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
        {
            return new TextDocument(
                Encoding.BigEndianUnicode.GetString(
                    bytes,
                    2,
                    bytes.Length - 2),
                Encoding.BigEndianUnicode);
        }

        try
        {
            var utf8 = new UTF8Encoding(
                false,
                true);

            return new TextDocument(
                utf8.GetString(bytes),
                new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            return new TextDocument(
                Encoding.Latin1.GetString(bytes),
                Encoding.Latin1);
        }
    }

    private static void WriteTextAtomically(
        string path,
        string content,
        Encoding encoding)
    {
        var temporaryPath =
            path + ".replace-" +
            Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllText(
                temporaryPath,
                content,
                encoding);

            File.Move(
                temporaryPath,
                path,
                true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static int CountOccurrences(
        string content,
        string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return 0;
        }

        var count = 0;
        var position = 0;

        while (true)
        {
            position = content.IndexOf(
                searchText,
                position,
                StringComparison.Ordinal);

            if (position < 0)
            {
                return count;
            }

            count++;
            position += searchText.Length;
        }
    }

    private static string FindRequiredStartFile(
        string root)
    {
        var result = FindOptionalStartFile(root);

        if (result is null)
        {
            throw new InvalidOperationException(
                "В корне обработанного сайта не найден " +
                "index.php, index.html или index.htm.");
        }

        return result;
    }

    private static string? FindOptionalStartFile(
        string root)
    {
        var candidates = new[]
        {
            "index.php",
            "index.html",
            "index.htm"
        };

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(
                root,
                candidate);

            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static void EnsureNoExtraDomainLevel(
        string root,
        string domain)
    {
        var nestedFolder = Path.Combine(
            root,
            domain);

        if (Directory.Exists(nestedFolder))
        {
            throw new InvalidOperationException(
                $"Обнаружен лишний уровень: {domain}\\{domain}. " +
                "Выберите внутренний корень исходного сайта.");
        }
    }

    private static ConfigResult ParseConfigPhp(
        string root)
    {
        var configFiles = Directory
            .EnumerateFiles(
                root,
                "config.php",
                SearchOption.AllDirectories)
            .ToList();

        if (configFiles.Count == 0)
        {
            throw new InvalidOperationException(
                "В обработанной копии не найден config.php.");
        }

        if (configFiles.Count > 1)
        {
            var preferredFiles = configFiles
                .Where(path =>
                    path.Replace('\\', '/')
                        .EndsWith(
                            "/config/config.php",
                            StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (preferredFiles.Count == 1)
            {
                configFiles = preferredFiles;
            }
            else
            {
                throw new InvalidOperationException(
                    "Найдено несколько config.php. " +
                    "Невозможно однозначно выбрать конфигурацию.");
            }
        }

        var configPath = configFiles[0];
        var content = File.ReadAllText(configPath);

        var name = ReadPhpArrayValue(
            content,
            "name");

        var user = ReadPhpArrayValue(
            content,
            "user");

        var password = ReadPhpArrayValue(
            content,
            "pass");

        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "В обработанном config.php не удалось " +
                "прочитать name, user и pass.");
        }

        return new ConfigResult(
            configPath,
            new DatabaseCredentials
            {
                Name = name,
                User = user,
                Password = password
            });
    }

    private static string ReadPhpArrayValue(
        string content,
        string key)
    {
        var pattern =
            $@"['""]{Regex.Escape(key)}['""]" +
            @"\s*=>\s*(['""])(?<value>.*?)\1";

        var match = Regex.Match(
            content,
            pattern,
            RegexOptions.IgnoreCase |
            RegexOptions.Singleline);

        return match.Success
            ? match.Groups["value"].Value.Trim()
            : string.Empty;
    }

    private static string FindSqlFile(
        string root)
    {
        var sqlFiles = Directory
            .EnumerateFiles(
                root,
                "*.sql",
                SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file =>
                string.Equals(
                    file.Name,
                    "database.sql",
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.Length)
            .ToList();

        if (sqlFiles.Count == 0)
        {
            throw new InvalidOperationException(
                "В обработанной копии не найден SQL-файл.");
        }

        return sqlFiles[0].FullName;
    }

    private static string ChangeRoot(
        string path,
        string oldRoot,
        string newRoot)
    {
        var relativePath = Path.GetRelativePath(
            oldRoot,
            path);

        return Path.Combine(
            newRoot,
            relativePath);
    }

    private static void DeleteDirectoryIfExists(
        string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(
                    file,
                    FileAttributes.Normal);
            }
            catch
            {
                // Продолжаем стандартное удаление.
            }
        }

        Directory.Delete(
            path,
            true);
    }

    private sealed record TextDocument(
        string Text,
        Encoding Encoding);

    private sealed record ReplacementResult(
        int FilesProcessed,
        int ReplacementCount1,
        int ReplacementCount2);

    private sealed record ConfigResult(
        string Path,
        DatabaseCredentials Credentials);
}
