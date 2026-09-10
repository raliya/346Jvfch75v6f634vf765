using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TextFileProcessor.Models;
using TextFileProcessor.Services;

namespace TextFileProcessor;

public partial class MainWindow
{
    private TextBox? _spaceshipBaseUrlTextBox;
    private TextBox? _spaceshipKeyTextBox;
    private PasswordBox? _spaceshipSecretPasswordBox;
    private TextBox? _spaceshipDomainTextBox;
    private TextBox? _spaceshipDnsPathTextBox;
    private TextBlock? _spaceshipSecretStatus;
    private TextBlock? _build7Status;
    private ProgressBar? _build7Progress;

    private Button? _build7LocalButton;
    private Button? _build7DomainButton;
    private Button? _build7UploadButton;
    private Button? _build7DatabaseButton;
    private Button? _build7SpaceshipButton;
    private Button? _build7AllButton;

    private readonly HttpClient _spaceshipHttpClient =
        new()
        {
            Timeout = TimeSpan.FromSeconds(45)
        };

    private void InitializeBuild7()
    {
        Loaded += Build7_Loaded;
    }

    private void Build7_Loaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            var tabControl = FindVisualChild<TabControl>(this);

            if (tabControl is null)
            {
                throw new InvalidOperationException(
                    "Не найден основной TabControl.");
            }

            if (tabControl.Items
                .OfType<TabItem>()
                .Any(item =>
                    string.Equals(
                        item.Header?.ToString(),
                        "Процессы / Spaceship",
                        StringComparison.Ordinal)))
            {
                return;
            }

            tabControl.Items.Insert(
                0,
                CreateBuild7Tab());

            LoadSpaceshipSettings();

            AddLog(
                "INFO",
                string.Empty,
                "Сборка 7 загружена: раздельные процессы, " +
                "общий запуск и настройки Spaceship API.");
        }
        catch (Exception exception)
        {
            var message = SensitiveDataRedactor.Redact(
                exception.Message);

            SetStatus("Ошибка Сборки 7: " + message);
            AddLog("ERROR", string.Empty, message);
        }
    }

    private TabItem CreateBuild7Tab()
    {
        var root = new Grid
        {
            Margin = new Thickness(14)
        };

        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });

        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });

        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });

        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });

        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });

        var processBox = new GroupBox
        {
            Header = "Раздельный и общий запуск процессов",
            Margin = new Thickness(0, 0, 0, 12)
        };

        var processPanel = new WrapPanel
        {
            Margin = new Thickness(10)
        };

        _build7LocalButton = CreateProcessButton(
            "1. Локальная обработка",
            185,
            async () =>
            {
                Start_Click(this, new RoutedEventArgs());
                await WaitForLegacyOperationAsync();
            });

        _build7DomainButton = CreateProcessButton(
            "2. Создать WWW-домен",
            190,
            async () =>
            {
                EnsureSelectedJob();
                CreateSelectedWebDomain_Click(
                    this,
                    new RoutedEventArgs());

                await WaitForLegacyOperationAsync();
            });

        _build7UploadButton = CreateProcessButton(
            "3. Загрузить файлы",
            175,
            async () =>
            {
                EnsureSelectedJob();
                DeploySelectedSite_Click(
                    this,
                    new RoutedEventArgs());

                await WaitForLegacyOperationAsync();
            });

        _build7DatabaseButton = CreateProcessButton(
            "4. Создать БД + SQL",
            185,
            async () =>
            {
                EnsureSelectedJob();
                DeployDatabaseButton_Click(
                    this,
                    new RoutedEventArgs());

                await WaitForLegacyOperationAsync();
            });

        _build7SpaceshipButton = CreateProcessButton(
            "5. Проверить Spaceship",
            190,
            TestSpaceshipForSelectedJobAsync);

        _build7AllButton = CreateProcessButton(
            "ЗАПУСТИТЬ ВСЕ ПРОЦЕССЫ",
            245,
            RunAllProcessesAsync);

        _build7AllButton.Background =
            new SolidColorBrush(Color.FromRgb(31, 122, 63));

        _build7AllButton.Foreground = Brushes.White;
        _build7AllButton.FontWeight = FontWeights.Bold;

        processPanel.Children.Add(_build7LocalButton);
        processPanel.Children.Add(_build7DomainButton);
        processPanel.Children.Add(_build7UploadButton);
        processPanel.Children.Add(_build7DatabaseButton);
        processPanel.Children.Add(_build7SpaceshipButton);
        processPanel.Children.Add(_build7AllButton);

        processBox.Content = processPanel;

        Grid.SetRow(processBox, 0);
        root.Children.Add(processBox);

        var apiBox = new GroupBox
        {
            Header = "Spaceship API",
            Margin = new Thickness(0, 0, 0, 12)
        };

        var apiGrid = new Grid
        {
            Margin = new Thickness(10)
        };

        apiGrid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(205)
            });

        apiGrid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

        for (var index = 0; index < 7; index++)
        {
            apiGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });
        }

        _spaceshipBaseUrlTextBox = AddTextSetting(
            apiGrid,
            0,
            "Base URL:",
            "https://spaceship.dev/api/v1");

        _spaceshipKeyTextBox = AddTextSetting(
            apiGrid,
            1,
            "API Key:",
            string.Empty);

        _spaceshipSecretPasswordBox =
            new PasswordBox
            {
                Margin = new Thickness(0, 0, 0, 8)
            };

        AddLabel(
            apiGrid,
            2,
            "API Secret:");

        Grid.SetRow(_spaceshipSecretPasswordBox, 2);
        Grid.SetColumn(_spaceshipSecretPasswordBox, 1);
        apiGrid.Children.Add(_spaceshipSecretPasswordBox);

        _spaceshipSecretStatus = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.DimGray,
            Text = "API Secret ещё не сохранён."
        };

        Grid.SetRow(_spaceshipSecretStatus, 3);
        Grid.SetColumn(_spaceshipSecretStatus, 1);
        apiGrid.Children.Add(_spaceshipSecretStatus);

        _spaceshipDomainTextBox = AddTextSetting(
            apiGrid,
            4,
            "Домен для проверки:",
            string.Empty);

        _spaceshipDnsPathTextBox = AddTextSetting(
            apiGrid,
            5,
            "DNS path:",
            "dns/records/{domain}");

        var apiButtons = new WrapPanel
        {
            Margin = new Thickness(0, 5, 0, 0)
        };

        var saveButton = new Button
        {
            Content = "Сохранить Spaceship",
            Width = 190,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 8)
        };

        saveButton.Click += (_, _) =>
        {
            try
            {
                SaveSpaceshipSettings();

                SetBuild7Status(
                    0,
                    "Настройки Spaceship сохранены. " +
                    "API Secret защищён Windows DPAPI.");

                AddLog(
                    "INFO",
                    string.Empty,
                    "Настройки Spaceship сохранены. " +
                    "API Secret в журнал не записан.");
            }
            catch (Exception exception)
            {
                ShowBuild7Error(
                    string.Empty,
                    exception);
            }
        };

        var testButton = new Button
        {
            Content = "Проверить Spaceship API",
            Width = 205,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 8)
        };

        testButton.Click += async (_, _) =>
            await ExecuteBuild7OperationAsync(
                TestSpaceshipForSelectedJobAsync);

        apiButtons.Children.Add(saveButton);
        apiButtons.Children.Add(testButton);

        Grid.SetRow(apiButtons, 6);
        Grid.SetColumnSpan(apiButtons, 2);
        apiGrid.Children.Add(apiButtons);

        apiBox.Content = apiGrid;

        Grid.SetRow(apiBox, 1);
        root.Children.Add(apiBox);

        _build7Progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 22,
            Margin = new Thickness(0, 0, 0, 8)
        };

        Grid.SetRow(_build7Progress, 2);
        root.Children.Add(_build7Progress);

        var statusBorder = new Border
        {
            Padding = new Thickness(10),
            Background = new SolidColorBrush(
                Color.FromRgb(241, 247, 241)),
            BorderBrush = new SolidColorBrush(
                Color.FromRgb(150, 190, 150)),
            BorderThickness = new Thickness(1)
        };

        _build7Status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Выберите задание в таблице «Задания» или " +
                "запустите полный процесс."
        };

        statusBorder.Child = _build7Status;

        Grid.SetRow(statusBorder, 3);
        root.Children.Add(statusBorder);

        return new TabItem
        {
            Header = "Процессы / Spaceship",
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                Content = root
            }
        };
    }

    private Button CreateProcessButton(
        string caption,
        double width,
        Func<Task> action)
    {
        var button = new Button
        {
            Content = caption,
            Width = width,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 8, 8)
        };

        button.Click += async (_, _) =>
            await ExecuteBuild7OperationAsync(action);

        return button;
    }

    private async Task ExecuteBuild7OperationAsync(
        Func<Task> action)
    {
        if (_isRunning)
        {
            MessageBox.Show(
                this,
                "Дождитесь завершения текущей операции.",
                "Процессы",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        SetBuild7ButtonsEnabled(false);

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetBuild7Status(
                _build7Progress?.Value ?? 0,
                "Операция отменена.");

            AddLog(
                "WARN",
                string.Empty,
                "Операция Сборки 7 отменена.");
        }
        catch (Exception exception)
        {
            ShowBuild7Error(
                GetSelectedJob()?.Domain ?? string.Empty,
                exception);
        }
        finally
        {
            SetBuild7ButtonsEnabled(true);
        }
    }

    private async Task RunAllProcessesAsync()
    {
        var confirmation = MessageBox.Show(
            this,
            "Запустить последовательный процесс?\n\n" +
            "1. Локальная обработка, если задания ещё не созданы.\n" +
            "2. Создание WWW-домена.\n" +
            "3. Загрузка файлов.\n" +
            "4. Создание БД и импорт SQL.\n" +
            "5. Проверка доступа к DNS через Spaceship API.\n\n" +
            "Текущие обработчики могут запросить дополнительное " +
            "подтверждение опасных операций.",
            "Полный процесс",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_jobs.Any(job =>
                !string.IsNullOrWhiteSpace(job.OutputPath) &&
                Directory.Exists(job.OutputPath)))
        {
            SetBuild7Status(
                2,
                "Этап 1: локальная обработка.");

            Start_Click(
                this,
                new RoutedEventArgs());

            await WaitForLegacyOperationAsync();
        }

        var jobs = _jobs
            .Where(job =>
                !string.IsNullOrWhiteSpace(job.OutputPath) &&
                Directory.Exists(job.OutputPath))
            .Reverse()
            .ToList();

        if (jobs.Count == 0)
        {
            throw new InvalidOperationException(
                "Нет готовых локальных заданий. " +
                "Проверьте исходную папку, домены и правила замен.");
        }

        var totalStages = jobs.Count * 4;
        var completedStages = 0;

        foreach (var job in jobs)
        {
            JobsGrid.SelectedItem = job;
            JobsGrid.ScrollIntoView(job);

            SetBuild7Status(
                CalculateAllPercent(
                    completedStages,
                    totalStages),
                $"{job.Domain}: создание WWW-домена.");

            CreateSelectedWebDomain_Click(
                this,
                new RoutedEventArgs());

            await WaitForLegacyOperationAsync();
            completedStages++;

            SetBuild7Status(
                CalculateAllPercent(
                    completedStages,
                    totalStages),
                $"{job.Domain}: загрузка файлов.");

            DeploySelectedSite_Click(
                this,
                new RoutedEventArgs());

            await WaitForLegacyOperationAsync();
            completedStages++;

            SetBuild7Status(
                CalculateAllPercent(
                    completedStages,
                    totalStages),
                $"{job.Domain}: создание БД и импорт SQL.");

            DeployDatabaseButton_Click(
                this,
                new RoutedEventArgs());

            await WaitForLegacyOperationAsync();
            completedStages++;

            SetBuild7Status(
                CalculateAllPercent(
                    completedStages,
                    totalStages),
                $"{job.Domain}: проверка Spaceship DNS API.");

            if (_spaceshipDomainTextBox is not null)
            {
                _spaceshipDomainTextBox.Text = job.Domain;
            }

            await TestSpaceshipAsync(job.Domain);
            completedStages++;
        }

        SetBuild7Status(
            100,
            $"Полный процесс завершён. Обработано доменов: " +
            jobs.Count + ".");

        AddLog(
            "INFO",
            string.Empty,
            $"Полный процесс завершён. Доменов: {jobs.Count}.");

        MessageBox.Show(
            this,
            $"Полный процесс завершён.\nОбработано доменов: " +
            jobs.Count + ".",
            "Готово",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task WaitForLegacyOperationAsync()
    {
        // async void обработчики переключают _isRunning до первого
        // длительного await. Небольшая задержка позволяет обработчику
        // перейти в рабочее состояние.
        await Task.Delay(150);

        while (_isRunning)
        {
            await Task.Delay(250);
        }
    }

    private static int CalculateAllPercent(
        int completed,
        int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp(
            completed * 100 / total,
            0,
            99);
    }

    private async Task TestSpaceshipForSelectedJobAsync()
    {
        var domain =
            _spaceshipDomainTextBox?.Text.Trim();

        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = GetSelectedJob()?.Domain;
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new InvalidOperationException(
                "Укажите домен Spaceship или выберите задание.");
        }

        await TestSpaceshipAsync(domain);
    }

    private async Task TestSpaceshipAsync(string domain)
    {
        var settings = ReadSpaceshipSettings();

        if (string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            throw new InvalidOperationException(
                "Введите или сохраните Spaceship API Secret.");
        }

        SaveSpaceshipSettings();

        var relativePath = settings.DnsPath
            .Replace(
                "{domain}",
                Uri.EscapeDataString(domain),
                StringComparison.OrdinalIgnoreCase)
            .TrimStart('/');

        var baseUrl = settings.BaseUrl.TrimEnd('/');
        var requestUrl = baseUrl + "/" + relativePath;
        // Spaceship API pagination.
        var paginationSeparator =
            requestUrl.Contains('?') ? "&" : "?";

        requestUrl +=
            paginationSeparator + "take=100&skip=0";

        SetBuild7Status(
            _build7Progress?.Value ?? 0,
            $"Spaceship API: чтение DNS-зоны {domain}.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            requestUrl);

        request.Headers.TryAddWithoutValidation(
            "X-API-Key",
            settings.ApiKey);

        request.Headers.TryAddWithoutValidation(
            "X-API-Secret",
            settings.ApiSecret);

        using var response =
            await _spaceshipHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var safeBody = LimitAndRedact(
                body,
                settings.ApiKey,
                settings.ApiSecret);

            throw new InvalidOperationException(
                $"Spaceship API вернул HTTP " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}. {safeBody}");
        }

        var resultDescription = DescribeJson(body);

        SetBuild7Status(
            Math.Max(
                1,
                (int)(_build7Progress?.Value ?? 0)),
            $"Spaceship API доступен. Домен: {domain}. " +
            resultDescription);

        AddLog(
            "INFO",
            domain,
            "Spaceship API: DNS-зона успешно прочитана.");
    }

    private SpaceshipRuntimeSettings ReadSpaceshipSettings()
    {
        if (_spaceshipBaseUrlTextBox is null ||
            _spaceshipKeyTextBox is null ||
            _spaceshipSecretPasswordBox is null ||
            _spaceshipDnsPathTextBox is null)
        {
            throw new InvalidOperationException(
                "Интерфейс Spaceship не инициализирован.");
        }

        var stored = LoadSpaceshipSettingsFile();

        var secret =
            _spaceshipSecretPasswordBox.Password;

        if (string.IsNullOrEmpty(secret) &&
            !string.IsNullOrWhiteSpace(stored.EncryptedSecret))
        {
            secret = UnprotectSecret(
                stored.EncryptedSecret);
        }

        var settings = new SpaceshipRuntimeSettings
        {
            BaseUrl = _spaceshipBaseUrlTextBox.Text.Trim(),
            ApiKey = _spaceshipKeyTextBox.Text.Trim(),
            ApiSecret = secret,
            DnsPath = _spaceshipDnsPathTextBox.Text.Trim()
        };

        if (!Uri.TryCreate(
                settings.BaseUrl,
                UriKind.Absolute,
                out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Spaceship Base URL должен быть абсолютным HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException(
                "Не указан Spaceship API Key.");
        }

        if (string.IsNullOrWhiteSpace(settings.DnsPath) ||
            !settings.DnsPath.Contains(
                "{domain}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DNS path должен содержать {domain}.");
        }

        return settings;
    }

    private void SaveSpaceshipSettings()
    {
        var settings = ReadSpaceshipSettings();

        var stored = new SpaceshipStoredSettings
        {
            BaseUrl = settings.BaseUrl,
            ApiKey = settings.ApiKey,
            DnsPath = settings.DnsPath,
            EncryptedSecret = ProtectSecret(
                settings.ApiSecret)
        };

        var directory = Path.GetDirectoryName(
            SpaceshipSettingsPath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "Не удалось определить папку настроек.");
        }

        Directory.CreateDirectory(directory);

        var temporaryPath =
            SpaceshipSettingsPath + ".tmp";

        var json = JsonSerializer.Serialize(
            stored,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(
            temporaryPath,
            json,
            new UTF8Encoding(false));

        File.Move(
            temporaryPath,
            SpaceshipSettingsPath,
            true);

        _spaceshipSecretPasswordBox?.Clear();

        if (_spaceshipSecretStatus is not null)
        {
            _spaceshipSecretStatus.Text =
                "API Secret сохранён и защищён Windows DPAPI.";
        }
    }

    private void LoadSpaceshipSettings()
    {
        var stored = LoadSpaceshipSettingsFile();

        if (_spaceshipBaseUrlTextBox is not null)
        {
            _spaceshipBaseUrlTextBox.Text =
                string.IsNullOrWhiteSpace(stored.BaseUrl)
                    ? "https://spaceship.dev/api/v1"
                    : stored.BaseUrl;
        }

        if (_spaceshipKeyTextBox is not null)
        {
            _spaceshipKeyTextBox.Text = stored.ApiKey;
        }

        if (_spaceshipDnsPathTextBox is not null)
        {
            _spaceshipDnsPathTextBox.Text =
                string.IsNullOrWhiteSpace(stored.DnsPath)
                    ? "dns/records/{domain}"
                    : stored.DnsPath;
        }

        if (_spaceshipSecretStatus is not null)
        {
            _spaceshipSecretStatus.Text =
                string.IsNullOrWhiteSpace(stored.EncryptedSecret)
                    ? "API Secret ещё не сохранён."
                    : "Зашифрованный API Secret сохранён.";
        }
    }

    private SpaceshipStoredSettings LoadSpaceshipSettingsFile()
    {
        if (!File.Exists(SpaceshipSettingsPath))
        {
            return new SpaceshipStoredSettings();
        }

        try
        {
            var json = File.ReadAllText(
                SpaceshipSettingsPath,
                Encoding.UTF8);

            return JsonSerializer.Deserialize<
                       SpaceshipStoredSettings>(json) ??
                   new SpaceshipStoredSettings();
        }
        catch
        {
            return new SpaceshipStoredSettings();
        }
    }

    private static string ProtectSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(value);

        try
        {
            var encrypted = ProtectedData.Protect(
                plainBytes,
                SpaceshipEntropy,
                DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static string UnprotectSecret(string value)
    {
        var encrypted = Convert.FromBase64String(value);
        var plainBytes = ProtectedData.Unprotect(
            encrypted,
            SpaceshipEntropy,
            DataProtectionScope.CurrentUser);

        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static string DescribeJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Ответ получен без тела.";
        }

        try
        {
            using var document = JsonDocument.Parse(text);

            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array =>
                    "Получен массив элементов: " +
                    document.RootElement.GetArrayLength() + ".",

                JsonValueKind.Object =>
                    "Получен корректный JSON-объект.",

                _ =>
                    "Получен корректный JSON-ответ."
            };
        }
        catch
        {
            return "Получен ответ API.";
        }
    }

    private static string LimitAndRedact(
        string value,
        params string[] secrets)
    {
        var result = value ?? string.Empty;

        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(
                    secret,
                    "[СКРЫТО]",
                    StringComparison.Ordinal);
            }
        }

        result = SensitiveDataRedactor.Redact(result)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return result.Length > 1000
            ? result[..1000]
            : result;
    }

    private DomainJob? GetSelectedJob()
    {
        return JobsGrid.SelectedItem as DomainJob;
    }

    private DomainJob EnsureSelectedJob()
    {
        return GetSelectedJob() ??
               throw new InvalidOperationException(
                   "Выберите домен во вкладке «Задания».");
    }

    private void SetBuild7Status(
        double progress,
        string message)
    {
        if (_build7Progress is not null)
        {
            _build7Progress.Value = Math.Clamp(
                progress,
                0,
                100);
        }

        if (_build7Status is not null)
        {
            _build7Status.Text = message;
        }

        SetStatus(message);
    }

    private void SetBuild7ButtonsEnabled(bool enabled)
    {
        if (_build7LocalButton is not null)
            _build7LocalButton.IsEnabled = enabled;

        if (_build7DomainButton is not null)
            _build7DomainButton.IsEnabled = enabled;

        if (_build7UploadButton is not null)
            _build7UploadButton.IsEnabled = enabled;

        if (_build7DatabaseButton is not null)
            _build7DatabaseButton.IsEnabled = enabled;

        if (_build7SpaceshipButton is not null)
            _build7SpaceshipButton.IsEnabled = enabled;

        if (_build7AllButton is not null)
            _build7AllButton.IsEnabled = enabled;
    }

    private void ShowBuild7Error(
        string domain,
        Exception exception)
    {
        var message = SensitiveDataRedactor.Redact(
            exception.Message);

        SetBuild7Status(
            _build7Progress?.Value ?? 0,
            message);

        AddLog(
            "ERROR",
            domain,
            message);

        MessageBox.Show(
            this,
            message,
            "Ошибка процесса",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static TextBox AddTextSetting(
        Grid grid,
        int row,
        string label,
        string defaultValue)
    {
        AddLabel(grid, row, label);

        var textBox = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(0, 0, 0, 8)
        };

        Grid.SetRow(textBox, row);
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(textBox);

        return textBox;
    }

    private static void AddLabel(
        Grid grid,
        int row,
        string text)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8)
        };

        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);
    }

    private static T? FindVisualChild<T>(
        DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(
                parent,
                index);

            if (child is T result)
            {
                return result;
            }

            var nested = FindVisualChild<T>(child);

            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static string SpaceshipSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TextFileProcessor",
            "spaceship-settings.json");

    private static readonly byte[] SpaceshipEntropy =
        Encoding.UTF8.GetBytes(
            "TextFileProcessor.Spaceship.Build7.v1");

    private sealed class SpaceshipRuntimeSettings
    {
        public string BaseUrl { get; init; } =
            "https://spaceship.dev/api/v1";

        public string ApiKey { get; init; } = string.Empty;

        public string ApiSecret { get; init; } = string.Empty;

        public string DnsPath { get; init; } =
            "dns/records/{domain}";
    }

    private sealed class SpaceshipStoredSettings
    {
        public string BaseUrl { get; set; } =
            "https://spaceship.dev/api/v1";

        public string ApiKey { get; set; } = string.Empty;

        public string EncryptedSecret { get; set; } =
            string.Empty;

        public string DnsPath { get; set; } =
            "dns/records/{domain}";
    }
}
