using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using TextFileProcessor.Data;
using TextFileProcessor.Models;
using TextFileProcessor.Services;

namespace TextFileProcessor;

public partial class MainWindow : Window
{
    private readonly AppDatabase _database = new();
    private readonly DomainService _domainService = new();
    private readonly FileProcessingService _processor = new();

    private readonly ObservableCollection<DomainJob> _jobs = new();
    private readonly ObservableCollection<LogEntry> _logs = new();

    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
  InitializeBuild6();
        InitializeBuild7();
        InitializeBuild10();

        JobsGrid.ItemsSource = _jobs;
        LogsGrid.ItemsSource = _logs;

        try
        {
            _database.Initialize();

            foreach (var job in _database.LoadJobs())
            {
                _jobs.Add(job);
            }

            foreach (var log in _database.LoadLogs())
            {
                _logs.Add(log);
            }

            AddLog(
                "INFO",
                string.Empty,
                $"Сборка 1 запущена. SQLite: {_database.DatabasePath}");
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Ошибка инициализации",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BrowseSource_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите корень исходного сайта",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            SourceFolderTextBox.Text =
                dialog.FolderName;
        }
    }

    private void BrowseOutput_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку результата",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            OutputFolderTextBox.Text =
                dialog.FolderName;
        }
    }

    private void Preview_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var firstDomain = ParseRows()
                .Select(row => row.Domain)
                .FirstOrDefault() ?? string.Empty;

            PreviewTextBox.Text =
                _processor.CreatePreview(
                    SourceFolderTextBox.Text.Trim(),
                    OutputFolderTextBox.Text.Trim(),
                    firstDomain);

            AddLog(
                "INFO",
                firstDomain,
                "Предварительная проверка структуры выполнена.");
        }
        catch (Exception exception)
        {
            var message =
                SensitiveDataRedactor.Redact(
                    exception.Message);

            PreviewTextBox.Text =
                "ОШИБКА:\n" + message;

            AddLog(
                "ERROR",
                string.Empty,
                message);
        }
    }

    private async void Start_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        try
        {
            var rows = ParseRows();

            if (rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "Введите хотя бы один домен.");
            }

            if (string.IsNullOrWhiteSpace(
                    SearchText1TextBox.Text))
            {
                throw new InvalidOperationException(
                    "Заполните искомый текст №1.");
            }

            var searchText2 =
                SearchText2TextBox.Text;

            if (!string.IsNullOrWhiteSpace(searchText2) &&
                rows.Any(row =>
                    string.IsNullOrWhiteSpace(
                        row.SecondValue)))
            {
                throw new InvalidOperationException(
                    "Для замены №2 необходимо указать " +
                    "второе значение для каждого домена.");
            }

            var options = new ProcessingOptions
            {
                SourceFolder =
                    SourceFolderTextBox.Text.Trim(),
                OutputFolder =
                    OutputFolderTextBox.Text.Trim(),
                SearchText1 =
                    SearchText1TextBox.Text,
                SearchText2 =
                    searchText2,
                IncludeAdditionalExtensions =
                    AdditionalExtensionsCheckBox.IsChecked == true,
                ReplaceExistingFolders =
                    ReplaceExistingCheckBox.IsChecked == true
            };

            _isRunning = true;

            StartButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            CancelButton.IsEnabled = true;

            _cancellationTokenSource =
                new CancellationTokenSource();

            var newJobs = rows
                .Select(row => new DomainJob
                {
                    Domain = row.Domain,
                    SecondValue = row.SecondValue,
                    Status = JobStatus.Pending,
                    Progress = 0,
                    Message = "Ожидает запуска."
                })
                .ToList();

            foreach (var job in newJobs)
            {
                _jobs.Insert(0, job);
                _database.SaveJob(job);
            }

            var usedDatabaseNames = new HashSet<string>(
                _jobs
                    .Where(job =>
                        job.Status == JobStatus.Completed &&
                        !string.IsNullOrWhiteSpace(
                            job.DatabaseName))
                    .Select(job => job.DatabaseName),
                StringComparer.OrdinalIgnoreCase);

            var usedDatabaseUsers = new HashSet<string>(
                _jobs
                    .Where(job =>
                        job.Status == JobStatus.Completed &&
                        !string.IsNullOrWhiteSpace(
                            job.DatabaseUser))
                    .Select(job => job.DatabaseUser),
                StringComparer.OrdinalIgnoreCase);

            foreach (var job in newJobs)
            {
                if (_cancellationTokenSource
                    .IsCancellationRequested)
                {
                    job.Status = JobStatus.Skipped;
                    job.Message =
                        "Пропущено из-за остановки очереди.";

                    _database.SaveJob(job);

                    AddLog(
                        "WARN",
                        job.Domain,
                        job.Message);

                    continue;
                }

                job.Status = JobStatus.Running;
                job.Progress = 0;
                job.Message = "Запуск обработки.";

                _database.SaveJob(job);

                SetStatus(
                    $"Обработка: {job.Domain}");

                AddLog(
                    "INFO",
                    job.Domain,
                    "Начата локальная обработка.");

                try
                {
                    var result =
                        await _processor.ProcessAsync(
                            job,
                            options,
                            (databaseName, databaseUser) =>
                                !usedDatabaseNames.Contains(
                                    databaseName) &&
                                !usedDatabaseUsers.Contains(
                                    databaseUser),
                            (progress, message) =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    job.Progress = progress;
                                    job.Message = message;

                                    _database.SaveJob(job);

                                    SetStatus(
                                        $"{job.Domain}: " +
                                        $"{progress}% — {message}");
                                });
                            },
                            _cancellationTokenSource.Token);

                    job.Status = JobStatus.Completed;
                    job.Progress = 100;
                    job.OutputPath = result.FinalFolder;
                    job.ConfigPath = result.ConfigPath;
                    job.SqlPath = result.SqlPath;
                    job.DatabaseName =
                        result.Credentials.Name;
                    job.DatabaseUser =
                        result.Credentials.User;

                    job.Message =
                        $"Готово. Файлов: {result.FilesProcessed}; " +
                        $"замен №1: {result.ReplacementCount1}; " +
                        $"замен №2: {result.ReplacementCount2}.";

                    usedDatabaseNames.Add(
                        result.Credentials.Name);

                    usedDatabaseUsers.Add(
                        result.Credentials.User);

                    _database.SaveJob(job);

                    AddLog(
                        "INFO",
                        job.Domain,
                        $"Обработка завершена. " +
                        $"Результат: {result.FinalFolder}");
                }
                catch (OperationCanceledException)
                {
                    job.Status = JobStatus.Cancelled;
                    job.Message =
                        "Обработка отменена пользователем.";

                    _database.SaveJob(job);

                    AddLog(
                        "WARN",
                        job.Domain,
                        job.Message);
                }
                catch (Exception exception)
                {
                    job.Status = JobStatus.Failed;
                    job.Message =
                        SensitiveDataRedactor.Redact(
                            exception.Message);

                    _database.SaveJob(job);

                    AddLog(
                        "ERROR",
                        job.Domain,
                        job.Message);
                }
            }

            var completedCount = newJobs.Count(
                job =>
                    job.Status == JobStatus.Completed);

            var failedCount = newJobs.Count(
                job =>
                    job.Status == JobStatus.Failed);

            var cancelledCount = newJobs.Count(
                job =>
                    job.Status is
                        JobStatus.Cancelled or
                        JobStatus.Skipped);

            SetStatus(
                $"Очередь завершена. " +
                $"Успешно: {completedCount}; " +
                $"ошибок: {failedCount}; " +
                $"отменено/пропущено: {cancelledCount}.");

            AddLog(
                "INFO",
                string.Empty,
                StatusTextBlock.Text);
            if (failedCount > 0)
            {
                CaptureBuild7Error(
                    new InvalidOperationException(
                        "Локальная обработка завершилась с ошибками. " +
                        $"Ошибок: {failedCount}."));
            }
            else if (cancelledCount > 0)
            {
                CaptureBuild7Error(
                    new OperationCanceledException(
                        "Локальная обработка отменена или пропущена."));
            }
            else
            {
                CompleteBuild7LegacyOperation();
            }
        }
        catch (Exception exception)
        {
            CaptureBuild7Error(exception);
            var message =
                SensitiveDataRedactor.Redact(
                    exception.Message);

            SetStatus(message);

            AddLog(
                "ERROR",
                string.Empty,
                message);

            MessageBox.Show(
                this,
                message,
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            _isRunning = false;

            StartButton.IsEnabled = true;
            PreviewButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void Cancel_Click(
        object sender,
        RoutedEventArgs e)
    {
        CancelProcessing();
    }

    private void ClearJobs_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        _jobs.Clear();

        AddLog(
            "INFO",
            string.Empty,
            "Таблица очищена. История SQLite сохранена.");
    }

    private void CancelProcessing()
    {
        if (!_isRunning)
        {
            return;
        }

        _cancellationTokenSource?.Cancel();

        SetStatus(
            "Запрошена остановка текущего задания.");

        AddLog(
            "WARN",
            string.Empty,
            "Пользователь запросил остановку очереди.");
    }

    private List<InputRow> ParseRows()
    {
        var domainLines = SplitLines(
            DomainsTextBox.Text,
            removeEmptyLines: true);

        var secondValueLines = SplitLines(
            SecondValuesTextBox.Text,
            removeEmptyLines: false);

        while (secondValueLines.Count > 0 &&
               string.IsNullOrWhiteSpace(
                   secondValueLines[^1]))
        {
            secondValueLines.RemoveAt(
                secondValueLines.Count - 1);
        }

        if (!string.IsNullOrWhiteSpace(
                SearchText2TextBox.Text) &&
            secondValueLines.Count !=
            domainLines.Count)
        {
            throw new InvalidOperationException(
                "Количество значений №2 должно совпадать " +
                "с количеством доменов.");
        }

        var rows = new List<InputRow>();

        var uniqueDomains = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        for (var index = 0;
             index < domainLines.Count;
             index++)
        {
            var domain = _domainService.Normalize(
                domainLines[index]);

            if (!uniqueDomains.Add(domain))
            {
                throw new InvalidOperationException(
                    $"Домен повторяется во входном списке: {domain}");
            }

            var secondValue =
                index < secondValueLines.Count
                    ? secondValueLines[index].Trim()
                    : string.Empty;

            rows.Add(
                new InputRow(
                    domain,
                    secondValue));
        }

        return rows;
    }

    private static List<string> SplitLines(
        string text,
        bool removeEmptyLines)
    {
        var normalized = text
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (removeEmptyLines)
        {
            return normalized
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .ToList();
        }

        return normalized
            .Split(
                '\n',
                StringSplitOptions.None)
            .ToList();
    }

    private void AddLog(
        string level,
        string domain,
        string message)
    {
        var entry = new LogEntry
        {
            Level = level,
            Domain = domain,
            Message = SensitiveDataRedactor.Redact(
                message)
        };

        _logs.Insert(0, entry);

        while (_logs.Count > 1000)
        {
            _logs.RemoveAt(
                _logs.Count - 1);
        }

        try
        {
            _database.AddLog(entry);
        }
        catch
        {
            // Ошибка записи журнала не останавливает обработку.
        }
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private void Window_Closing(
        object? sender,
        CancelEventArgs eventArgs)
    {
        if (!_isRunning)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Идёт обработка. Остановить её и закрыть программу?",
            "Подтверждение",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            eventArgs.Cancel = true;
            return;
        }

        CancelProcessing();
    }

    private sealed record InputRow(
        string Domain,
        string SecondValue);
}

