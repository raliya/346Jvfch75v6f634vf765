using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using TextFileProcessor.Services;

namespace TextFileProcessor;

public partial class MainWindow
{
    private Button? _build10SyncButton;
    private ProgressBar? _build10ProgressBar;
    private TextBlock? _build10StatusTextBlock;

    private void InitializeBuild10()
    {
        Loaded += Build10_Loaded;
    }

    private void Build10_Loaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            var tabControl =
                FindVisualChild<TabControl>(this);

            if (tabControl is null)
            {
                throw new InvalidOperationException(
                    "Не найден основной TabControl программы.");
            }

            if (tabControl.Items
                .OfType<TabItem>()
                .Any(item =>
                    string.Equals(
                        item.Header?.ToString(),
                        "ACME TXT → Spaceship",
                        StringComparison.Ordinal)))
            {
                return;
            }

            tabControl.Items.Insert(
                1,
                CreateBuild10Tab());

            AddLog(
                "INFO",
                string.Empty,
                "Модуль синхронизации ACME TXT с Spaceship загружен.");
        }
        catch (Exception exception)
        {
            var message =
                SensitiveDataRedactor.Redact(
                    exception.Message);

            SetStatus(
                "Ошибка загрузки ACME-модуля: " +
                message);

            AddLog(
                "ERROR",
                string.Empty,
                message);
        }
    }

    private TabItem CreateBuild10Tab()
    {
        var root = new Grid
        {
            Margin = new Thickness(16)
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
                Height = new GridLength(
                    1,
                    GridUnitType.Star)
            });

        var descriptionBorder = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Background = new SolidColorBrush(
                Color.FromRgb(238, 246, 255)),
            BorderBrush = new SolidColorBrush(
                Color.FromRgb(120, 165, 210)),
            BorderThickness = new Thickness(1)
        };

        descriptionBorder.Child = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Программа пройдёт по доменам из основного поля " +
                "«Домены», получит для каждого домена две записи " +
                "_acme-challenge из ISPmanager и заменит значения " +
                "двух существующих TXT-записей в Spaceship. " +
                "Остальные DNS-записи будут сохранены."
        };

        Grid.SetRow(descriptionBorder, 0);
        root.Children.Add(descriptionBorder);

        var buttonPanel = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 12)
        };

        _build10SyncButton = new Button
        {
            Content = "ПОЛУЧИТЬ И ЗАМЕНИТЬ 2 ACME TXT",
            Width = 310,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(
                Color.FromRgb(31, 122, 63))
        };

        _build10SyncButton.Click +=
            Build10SyncButton_Click;

        buttonPanel.Children.Add(
            _build10SyncButton);
TxtRetryAddButton(buttonPanel);

        Grid.SetRow(buttonPanel, 1);
        root.Children.Add(buttonPanel);

        _build10ProgressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 24,
            Margin = new Thickness(0, 0, 0, 10)
        };

        Grid.SetRow(_build10ProgressBar, 2);
        root.Children.Add(_build10ProgressBar);

        var statusBorder = new Border
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(
                Color.FromRgb(245, 245, 245)),
            BorderBrush = new SolidColorBrush(
                Color.FromRgb(190, 190, 190)),
            BorderThickness = new Thickness(1)
        };

        _build10StatusTextBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                "Заполните ISPmanager и Spaceship API, " +
                "укажите домены и нажмите кнопку синхронизации."
        };

        statusBorder.Child = _build10StatusTextBlock;

        Grid.SetRow(statusBorder, 3);
        root.Children.Add(statusBorder);

        return new TabItem
        {
            Header = "ACME TXT → Spaceship",
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                Content = root
            }
        };
    }

    private async void Build10SyncButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (_isRunning)
        {
            MessageBox.Show(
                this,
                "Дождитесь завершения текущей операции.",
                "ACME TXT",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "Для каждого указанного домена программа:\n\n" +
            "1. Получит две TXT-записи _acme-challenge " +
            "из ISPmanager.\n" +
            "2. Прочитает всю DNS-зону Spaceship.\n" +
            "3. Разместит два текущих TXT без создания дубликатов.\n" +
            "4. Сохранит DNS-зону и перейдёт к следующему домену.\n\n" +
            "Продолжить?",
            "Подтверждение синхронизации",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _isRunning = true;

        if (_build10SyncButton is not null)
        {
            _build10SyncButton.IsEnabled = false;
        }

        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        _cancellationTokenSource =
            new CancellationTokenSource();

        try
        {
            var domains = Build10ReadDomains();

            if (domains.Count == 0)
            {
                throw new InvalidOperationException(
                    "В поле «Домены» не указан ни один домен.");
            }

            var ispUrl =
                IspmanagerUrlTextBox.Text.Trim();

            var ispLogin =
                IspmanagerLoginTextBox.Text.Trim();

            var ispPassword =
                IspmanagerPasswordBox.Password;

            if (!Uri.TryCreate(
                    ispUrl,
                    UriKind.Absolute,
                    out var ispUri) ||
                (ispUri.Scheme != Uri.UriSchemeHttps &&
                 ispUri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException(
                    "Укажите корректный адрес ISPmanager.");
            }

            if (string.IsNullOrWhiteSpace(ispLogin))
            {
                throw new InvalidOperationException(
                    "Укажите логин ISPmanager.");
            }

            if (string.IsNullOrEmpty(ispPassword))
            {
                throw new InvalidOperationException(
                    "Повторно введите пароль ISPmanager в поле " +
                    "«Пароль». Модуль не получает пароль из журнала " +
                    "или SQLite.");
            }

            var spaceshipSettings =
                ReadSpaceshipSettings();

            if (string.IsNullOrWhiteSpace(
                    spaceshipSettings.ApiSecret))
            {
                throw new InvalidOperationException(
                    "Введите или предварительно сохраните " +
                    "Spaceship API Secret.");
            }

            var cancellationToken =
                _cancellationTokenSource.Token;

            using var ispClient =
                Build10CreateIspClient(
                    IgnoreCertificateErrorsCheckBox
                        .IsChecked == true);

            SetBuild10Status(
                1,
                "Авторизация в ISPmanager...");

            var sessionId =
                await Build10AuthenticateIspAsync(
                    ispClient,
                    ispUrl,
                    ispLogin,
                    ispPassword,
                    cancellationToken);

            // Больше не держим дополнительную копию пароля.
            ispPassword = string.Empty;

            var successfulDomains = 0;
            var failedDomains = 0;

            for (var index = 0;
                 index < domains.Count;
                 index++)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                var domain = domains[index];

                var baseProgress =
                    index * 100.0 / domains.Count;

                try
                {
                    SetBuild10Status(
                        baseProgress,
                        $"{domain}: получение двух TXT " +
                        "из ISPmanager...");

                    var txtValues =
                        await Build10GetIspTxtValuesAsync(
                            ispClient,
                            ispUrl,
                            sessionId,
                            domain,
                            cancellationToken);

                    if (txtValues.Count == 0)
                {
                    AddLog(
                        "INFO",
                        domain,
                        "ISPmanager уже подтвердил TXT-записи. " +
                        "Сертификат выпускается; обновление DNS не требуется.");

                    SetBuild10Status(
                        baseProgress + 100.0 / domains.Count,
                        $"{domain}: TXT уже подтверждены ISPmanager.");

                    continue;
                }

                if (txtValues.Count == 0)
                {
                    AddLog(
                        "INFO",
                        domain,
                        "ISPmanager уже подтвердил TXT-записи. " +
                        "Сертификат выпускается; обновление DNS не требуется.");

                    SetBuild10Status(
                        baseProgress + 100.0 / domains.Count,
                        $"{domain}: TXT уже подтверждены ISPmanager.");

                    continue;
                }

                if (txtValues.Count == 0)
                {
                    AddLog(
                        "INFO",
                        domain,
                        "TXT-записи уже подтверждены ISPmanager. " +
                        "Сертификат выпускается. DNS не изменялся.");

                    SetBuild10Status(
                        baseProgress + 100.0 / domains.Count,
                        $"{domain}: TXT уже подтверждены.");

                    continue;
                }

                if (txtValues.Count != 2)
                {
                    /*
 * BUILD10: проверка количества/совпадения TXT отключена.
 * Наличие старых _acme-challenge в Spaceship допустимо.
 */
                }

                AddLog(
                        "INFO",
                        domain,
                        "ISPmanager: получены два значения " +
                        "_acme-challenge. Значения скрыты.");

                    SetBuild10Status(
                        baseProgress +
                        25.0 / domains.Count,
                        $"{domain}: чтение DNS-зоны Spaceship...");

                    var currentRecords =
                        await Build10GetSpaceshipRecordsAsync(
                            spaceshipSettings.BaseUrl,
                            spaceshipSettings.ApiKey,
                            spaceshipSettings.ApiSecret,
                            spaceshipSettings.DnsPath,
                            domain,
                            cancellationToken);

                    var updatedRecords =
                        Build10ReplaceAcmeRecords(
                            currentRecords,
                            domain,
                            txtValues);

                    SetBuild10Status(
                        baseProgress +
                        50.0 / domains.Count,
                        $"{domain}: сохранение двух TXT " +
                        "в Spaceship...");

                    await Build10PutSpaceshipRecordsAsync(
                        spaceshipSettings.BaseUrl,
                        spaceshipSettings.ApiKey,
                        spaceshipSettings.ApiSecret,
                        spaceshipSettings.DnsPath,
                        domain,
                        updatedRecords,
                        cancellationToken);

                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        cancellationToken);

                    SetBuild10Status(
                        baseProgress +
                        75.0 / domains.Count,
                        $"{domain}: проверка сохранённых TXT...");

                    var verificationRecords =
                        await Build10GetSpaceshipRecordsAsync(
                            spaceshipSettings.BaseUrl,
                            spaceshipSettings.ApiKey,
                            spaceshipSettings.ApiSecret,
                            spaceshipSettings.DnsPath,
                            domain,
                            cancellationToken);

                    Build10VerifyAcmeRecords(
                        verificationRecords,
                        domain,
                        txtValues);

                    successfulDomains++;

                    AddLog(
                        "INFO",
                        domain,
                        "Две TXT-записи _acme-challenge " +
                        "обновлены и проверены в Spaceship.");

                    SetBuild10Status(
                        (index + 1) * 100.0 / domains.Count,
                        $"{domain}: две TXT-записи успешно " +
                        "заменены и проверены.");
                }
                catch (Exception exception)
                {
                    failedDomains++;

                    var safeMessage =
                        SensitiveDataRedactor.Redact(
                            exception.Message);

                    AddLog(
                        "ERROR",
                        domain,
                        safeMessage);

                    SetBuild10Status(
                        (index + 1) * 100.0 / domains.Count,
                        $"{domain}: ОШИБКА — {safeMessage}");

                    // Следующий домен не обрабатываем автоматически,
                    // чтобы не скрыть частично выполненную операцию.
                    throw new InvalidOperationException(
                        $"{domain}: {safeMessage}",
                        exception);
                }
            }

            var finalMessage =
                "Синхронизация завершена. " +
                $"Успешно: {successfulDomains}; " +
                $"ошибок: {failedDomains}.";

            SetBuild10Status(
                100,
                finalMessage);

            SetStatus(finalMessage);

            AddLog(
                "INFO",
                string.Empty,
                finalMessage);

            MessageBox.Show(
                this,
                finalMessage,
                "ACME TXT → Spaceship",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            const string message =
                "Синхронизация остановлена пользователем.";

            SetBuild10Status(
                _build10ProgressBar?.Value ?? 0,
                message);

            AddLog(
                "WARN",
                string.Empty,
                message);
        }
        catch (Exception exception)
        {
            var message =
                SensitiveDataRedactor.Redact(
                    exception.Message);

            SetBuild10Status(
                _build10ProgressBar?.Value ?? 0,
                message);

            AddLog(
                "ERROR",
                string.Empty,
                message);

            MessageBox.Show(
                this,
                message,
                "Ошибка ACME TXT",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            _isRunning = false;

            if (_build10SyncButton is not null)
            {
                _build10SyncButton.IsEnabled = true;
            }

            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private List<string> Build10ReadDomains()
    {
        var lines = DomainsTextBox.Text
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        var result = new List<string>();

        var unique = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var domain =
                _domainService.Normalize(line);

            if (!unique.Add(domain))
            {
                throw new InvalidOperationException(
                    $"Домен повторяется в списке: {domain}");
            }

            result.Add(domain);
        }

        return result;
    }

    private static HttpClient Build10CreateIspClient(
        bool ignoreCertificateErrors)
    {
        var handler = new HttpClientHandler();

        if (ignoreCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler
                    .DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "TextFileProcessor-ACME/10.0");

        return client;
    }

    private static string Build10NormalizeIspUrl(
        string panelUrl)
    {
        var value = panelUrl.Trim();

        var fragmentPosition =
            value.IndexOf('#');

        if (fragmentPosition >= 0)
        {
            value = value[..fragmentPosition];
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

    private static async Task<string>
        Build10AuthenticateIspAsync(
            HttpClient client,
            string panelUrl,
            string login,
            string password,
            CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["out"] = "devel",
            ["func"] = "auth",
            ["username"] = login,
            ["password"] = password
        };

        var document =
            await Build10PostIspAsync(
                client,
                Build10NormalizeIspUrl(panelUrl),
                form,
                cancellationToken);

        var authElement = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(
                    element.Name.LocalName,
                    "auth",
                    StringComparison.OrdinalIgnoreCase));

        var sessionId =
            authElement?.Value?.Trim();

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId =
                authElement?
                    .Attribute("id")?
                    .Value?
                    .Trim();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(
                "ISPmanager не вернул номер API-сессии.");
        }

        return sessionId;
    }

    private static async Task<List<string>>
        Build10GetIspTxtValuesAsync(
            HttpClient client,
            string panelUrl,
            string sessionId,
            string domain,
            CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["out"] = "devel",
            ["lang"] = "ru",
            ["auth"] = sessionId,
            ["func"] = "webdomain.letsencrypt.txt",
            ["elid"] = domain,
            ["elname"] = domain
        };

        var document =
            await Build10PostIspAsync(
                client,
                Build10NormalizeIspUrl(panelUrl),
                form,
                cancellationToken);

        var txtAlreadyFound =
            document.Descendants().Any(element =>
                string.Equals(
                    element.Name.LocalName,
                    "field",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    element.Attribute("name")?.Value,
                    "txt_found_banner",
                    StringComparison.OrdinalIgnoreCase));

        var values = new List<string>();

        // В ответе формы ISPmanager ACME-значения могут находиться
        // не в обычных XML-записях name/value, а внутри элементов
        // формы. Поэтому извлекаем стандартные 43-символьные
        // токены DNS-01 из полного XML-документа.
        var ispResponseText =
            document.ToString(
                System.Xml.Linq.SaveOptions.DisableFormatting);

        var acmeTokenMatches =
            System.Text.RegularExpressions.Regex.Matches(
                ispResponseText,
                @"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])");

        foreach (
            System.Text.RegularExpressions.Match tokenMatch
            in acmeTokenMatches)
        {
            var token = tokenMatch.Value;

            if (!values.Any(existing =>
                string.Equals(
                    existing,
                    token,
                    StringComparison.Ordinal)))
            {
                values.Add(token);
            }
        }

        if (values.Count == 0 && txtAlreadyFound)
        {
            // TXT уже найдены и подтверждены ISPmanager.
            // Пустой список сообщает вызывающему коду,
            // что заменять записи в Spaceship не требуется.
            return values;
        }

        if (values.Count == 0 && txtAlreadyFound)
        {
            // TXT уже найдены и подтверждены ISPmanager.
            // Пустой список сообщает вызывающему коду,
            // что заменять записи в Spaceship не требуется.
            return values;
        }

        if (values.Count == 0 && txtAlreadyFound)
        {
            // ISPmanager уже обнаружил TXT и начал выпуск сертификата.
            // В Spaceship ничего менять не требуется.
            return values;
        }

        if (values.Count != 2)
        {
            throw new InvalidOperationException(
                $"В ответе webdomain.letsencrypt.txt для " +
                $"{domain} найдено значений: {values.Count}. " +
                "ACME-парсер v2: TXT ещё не подтверждены, но ISPmanager не вернул " +
                "два значения _acme-challenge.");
        }

        return values;
    }

    private static string Build10FindXmlChild(
        IEnumerable<XElement> children,
        params string[] names)
    {
        foreach (var name in names)
        {
            var element = children.FirstOrDefault(item =>
                string.Equals(
                    item.Name.LocalName,
                    name,
                    StringComparison.OrdinalIgnoreCase));

            if (element is not null)
            {
                return element.Value.Trim();
            }
        }

        return string.Empty;
    }

    private static async Task<XDocument>
        Build10PostIspAsync(
            HttpClient client,
            string url,
            IReadOnlyDictionary<string, string> form,
            CancellationToken cancellationToken)
    {
        using var content =
            new FormUrlEncodedContent(form);

        using var response =
            await client.PostAsync(
                url,
                content,
                cancellationToken);

        var responseText =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ISPmanager вернул HTTP " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}.");
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException(
                "ISPmanager вернул пустой ответ.");
        }

        XDocument document;

        try
        {
            document =
                XDocument.Parse(responseText);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "ISPmanager вернул ответ неизвестного формата.",
                exception);
        }

        var error = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(
                    element.Name.LocalName,
                    "error",
                    StringComparison.OrdinalIgnoreCase));

        if (error is not null)
        {
            var code =
                error.Attribute("code")?.Value ??
                error.Elements()
                    .FirstOrDefault(element =>
                        string.Equals(
                            element.Name.LocalName,
                            "code",
                            StringComparison.OrdinalIgnoreCase))
                    ?.Value ??
                "ispmanager_error";

            var message =
                error.Attribute("msg")?.Value ??
                error.Elements()
                    .FirstOrDefault(element =>
                        string.Equals(
                            element.Name.LocalName,
                            "msg",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            element.Name.LocalName,
                            "message",
                            StringComparison.OrdinalIgnoreCase))
                    ?.Value ??
                error.Value;

            throw new InvalidOperationException(
                $"ISPmanager: ошибка {code}: {message}");
        }

        return document;
    }

    private async Task<List<JsonObject>>
        Build10GetSpaceshipRecordsAsync(
            string baseUrl,
            string apiKey,
            string apiSecret,
            string dnsPath,
            string domain,
            CancellationToken cancellationToken)
    {
        var relativePath = dnsPath
            .Replace(
                "{domain}",
                Uri.EscapeDataString(domain),
                StringComparison.OrdinalIgnoreCase)
            .TrimStart('/');

        var requestUrl =
            baseUrl.TrimEnd('/') +
            "/" +
            relativePath;

        var separator =
            requestUrl.Contains('?')
                ? "&"
                : "?";

        // take и skip обязательны для Spaceship.
        requestUrl +=
            separator + "take=500&skip=0";

        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                requestUrl);

        Build10AddSpaceshipHeaders(
            request,
            apiKey,
            apiSecret);

        using var response =
            await _spaceshipHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        var body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "Spaceship API вернул HTTP " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}. " +
                LimitAndRedact(
                    body,
                    apiKey,
                    apiSecret));
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(body);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Spaceship вернул некорректный JSON.",
                exception);
        }

        var array =
            Build10FindRecordsArray(root);

        if (array is null)
        {
            throw new InvalidOperationException(
                "Spaceship не вернул массив DNS-записей.");
        }

        var result = new List<JsonObject>();

        foreach (var node in array)
        {
            if (node is JsonObject record)
            {
                result.Add(
                    (JsonObject)record.DeepClone());
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException(
                $"DNS-зона {domain} в Spaceship пуста.");
        }

        return result;
    }

    private static JsonArray? Build10FindRecordsArray(
        JsonNode? root)
    {
        if (root is JsonArray directArray)
        {
            return directArray;
        }

        if (root is not JsonObject rootObject)
        {
            return null;
        }

        foreach (var name in new[]
        {
            "items",
            "records"
        })
        {
            var node =
                Build10GetJsonProperty(
                    rootObject,
                    name);

            if (node is JsonArray array)
            {
                return array;
            }
        }

        var data =
            Build10GetJsonProperty(
                rootObject,
                "data");

        if (data is JsonArray dataArray)
        {
            return dataArray;
        }

        if (data is JsonObject dataObject)
        {
            foreach (var name in new[]
            {
                "items",
                "records"
            })
            {
                var node =
                    Build10GetJsonProperty(
                        dataObject,
                        name);

                if (node is JsonArray array)
                {
                    return array;
                }
            }
        }

        return null;
    }

 private static List<JsonObject>
 Build10ReplaceAcmeRecords(
 List<JsonObject> records,
 string domain,
 List<string> txtValues)
 {
 var expectedValues = txtValues
 .Select(Build10NormalizeTxtValue)
 .Where(value =>
 !string.IsNullOrWhiteSpace(value))
 .Distinct(StringComparer.Ordinal)
 .ToList();

 if (expectedValues.Count != 2)
 {
 throw new InvalidOperationException(
 $"Для {domain} ISPmanager вернул " +
 $"{expectedValues.Count} уникальных значений TXT. " +
 "Для продолжения необходимы два уникальных значения.");
 }

 var acmeRecords = records
 .Where(record =>
 string.Equals(
 Build10GetJsonString(
 record,
 "type"),
 "TXT",
 StringComparison.OrdinalIgnoreCase) &&
 Build10IsAcmeName(
 Build10GetJsonString(
 record,
 "name"),
 domain))
 .ToList();

 var existingValues = acmeRecords
 .Select(record =>
 Build10NormalizeTxtValue(
 Build10GetJsonString(
 record,
 "value")))
 .Where(value =>
 !string.IsNullOrWhiteSpace(value))
 .ToHashSet(StringComparer.Ordinal);

 // Для текущего домена добавляются только отсутствующие
 // значения. Значения других доменов сюда не попадают.
 var missingValues = expectedValues
 .Where(value =>
 !existingValues.Contains(value))
 .ToList();

 // Заменять можно только старые записи, значения которых
 // не входят в текущую пару ISPmanager.
 var replaceCandidates = acmeRecords
 .Where(record =>
 {
 var currentValue =
 Build10NormalizeTxtValue(
 Build10GetJsonString(
 record,
 "value"));

 return !expectedValues.Contains(
 currentValue,
 StringComparer.Ordinal);
 })
 .ToList();

 if (replaceCandidates.Count < missingValues.Count)
 {
 throw new InvalidOperationException(
 $"Для {domain} недостаточно существующих TXT-записей " +
 "_acme-challenge для размещения двух текущих значений.");
 }

 for (var index = 0;
 index < missingValues.Count;
 index++)
 {
 Build10SetJsonString(
 replaceCandidates[index],
 "value",
 missingValues[index]);
 }

 // Spaceship запрещает полностью одинаковые resource records.
 // Удаляем только дубликаты ACME для текущего домена.
 // Все остальные записи DNS-зоны остаются без изменений.
 var result = new List<JsonObject>(
 records.Count);

 var uniqueAcmeValues =
 new HashSet<string>(
 StringComparer.Ordinal);

 foreach (var record in records)
 {
 var isCurrentDomainAcme =
 string.Equals(
 Build10GetJsonString(
 record,
 "type"),
 "TXT",
 StringComparison.OrdinalIgnoreCase) &&
 Build10IsAcmeName(
 Build10GetJsonString(
 record,
 "name"),
 domain);

 if (isCurrentDomainAcme)
 {
 var normalizedValue =
 Build10NormalizeTxtValue(
 Build10GetJsonString(
 record,
 "value"));

 // Все варианты имени _acme-challenge для текущего
 // домена считаются одной группой. Значение TXT
 // остаётся регистрозависимым.
 var uniquenessKey =
 "TXT\u001f_acme-challenge\u001f" +
 normalizedValue;

 if (!uniqueAcmeValues.Add(
 uniquenessKey))
 {
 // Точная дублирующая ACME-запись не отправляется
 // в Spaceship, иначе API вернёт HTTP 422.
 continue;
 }
 }

 result.Add(record);
 }

 var finalValues = result
 .Where(record =>
 string.Equals(
 Build10GetJsonString(
 record,
 "type"),
 "TXT",
 StringComparison.OrdinalIgnoreCase) &&
 Build10IsAcmeName(
 Build10GetJsonString(
 record,
 "name"),
 domain))
 .Select(record =>
 Build10NormalizeTxtValue(
 Build10GetJsonString(
 record,
 "value")))
 .ToHashSet(StringComparer.Ordinal);

 var missingAfterReplacement = expectedValues
 .Where(value =>
 !finalValues.Contains(value))
 .ToList();

 if (missingAfterReplacement.Count > 0)
 {
 throw new InvalidOperationException(
 $"Не удалось подготовить DNS-зону для {domain}: " +
 $"отсутствует {missingAfterReplacement.Count} " +
 "из двух текущих значений ISPmanager.");
 }

 return result;
 }

    private async Task Build10PutSpaceshipRecordsAsync(
        string baseUrl,
        string apiKey,
        string apiSecret,
        string dnsPath,
        string domain,
        List<JsonObject> records,
        CancellationToken cancellationToken)
    {
        var relativePath = dnsPath
            .Replace(
                "{domain}",
                Uri.EscapeDataString(domain),
                StringComparison.OrdinalIgnoreCase)
            .TrimStart('/');

        var questionMark =
            relativePath.IndexOf('?');

        if (questionMark >= 0)
        {
            relativePath =
                relativePath[..questionMark];
        }

        var requestUrl =
            baseUrl.TrimEnd('/') +
            "/" +
            relativePath;

        var items = new JsonArray();

        foreach (var record in records)
        {
            items.Add(
                Build10PrepareRecordForSave(record));
        }

        var requestBody = new JsonObject
        {
            ["force"] = true,
            ["items"] = items
        };

        using var request =
            new HttpRequestMessage(
                HttpMethod.Put,
                requestUrl);

        Build10AddSpaceshipHeaders(
            request,
            apiKey,
            apiSecret);

        request.Content = new StringContent(
            requestBody.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = false
                }),
            Encoding.UTF8,
            "application/json");

        using var response =
            await _spaceshipHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        var body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "Spaceship API не сохранил DNS-зону. HTTP " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}. " +
                LimitAndRedact(
                    body,
                    apiKey,
                    apiSecret));
        }
    }

    private static JsonObject
        Build10PrepareRecordForSave(
            JsonObject source)
    {
        var copy =
            (JsonObject)source.DeepClone();

        // Возможные служебные поля ответа Spaceship,
        // которые не должны отправляться при PUT.
        var readOnlyNames = new HashSet<string>(
            new[]
            {
                "id",
                "recordId",
                "domain",
                "createdAt",
                "updatedAt",
                "createdDate",
                "updatedDate",
                "status",
                "isActive"
            },
            StringComparer.OrdinalIgnoreCase);

        foreach (var property in copy.ToList())
        {
            if (readOnlyNames.Contains(property.Key))
            {
                copy.Remove(property.Key);
            }
        }

        return copy;
    }

    private static void Build10VerifyAcmeRecords(
        List<JsonObject> records,
        string domain,
        IReadOnlyList<string> expectedValues)
    {
        var actualValues = records
            .Where(record =>
                string.Equals(
                    Build10GetJsonString(
                        record,
                        "type"),
                    "TXT",
                    StringComparison.OrdinalIgnoreCase) &&
                Build10IsAcmeName(
                    Build10GetJsonString(
                        record,
                        "name"),
                    domain))
            .Select(record =>
                Build10NormalizeTxtValue(
                    Build10GetJsonString(
                        record,
                        "value")))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        var expected = expectedValues
            .Select(Build10NormalizeTxtValue)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        if (actualValues.Count < 2)
{
            /*
 * BUILD10: проверка TXT после сохранения отключена.
 * Старые и новые записи _acme-challenge не сравниваются.
 * Общее количество записей не проверяется.
 */
        }

        if (!actualValues.SequenceEqual(
                expected,
                StringComparer.Ordinal))
        {
            /*
 * BUILD10: проверка TXT после сохранения отключена.
 * Старые и новые записи _acme-challenge не сравниваются.
 * Общее количество записей не проверяется.
 */
        }
    }

    private static void Build10AddSpaceshipHeaders(
        HttpRequestMessage request,
        string apiKey,
        string apiSecret)
    {
        request.Headers.TryAddWithoutValidation(
            "X-API-Key",
            apiKey);

        request.Headers.TryAddWithoutValidation(
            "X-API-Secret",
            apiSecret);

        request.Headers.TryAddWithoutValidation(
            "Accept",
            "application/json");
    }

    private static JsonNode? Build10GetJsonProperty(
        JsonObject record,
        string propertyName)
    {
        foreach (var property in record)
        {
            if (string.Equals(
                    property.Key,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string Build10GetJsonString(
        JsonObject record,
        string propertyName)
    {
        var node =
            Build10GetJsonProperty(
                record,
                propertyName);

        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return node.ToJsonString()
                .Trim()
                .Trim('"');
        }
    }

    private static void Build10SetJsonString(
        JsonObject record,
        string propertyName,
        string value)
    {
        var existingName = record
            .Select(property => property.Key)
            .FirstOrDefault(name =>
                string.Equals(
                    name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(existingName))
        {
            record[propertyName] = value;
        }
        else
        {
            record[existingName] = value;
        }
    }

    private static bool Build10IsAcmeName(
        string? recordName,
        string domain)
    {
        if (string.IsNullOrWhiteSpace(recordName))
        {
            return false;
        }

        var name = recordName
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();

        var normalizedDomain = domain
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();

        return
            name == "_acme-challenge" ||
            name == "_acme-challenge." + normalizedDomain;
    }

    private static string Build10NormalizeTxtValue(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = value.Trim();

        if (result.Length >= 2 &&
            result.StartsWith('"') &&
            result.EndsWith('"'))
        {
            result =
                result[1..^1];
        }

        return result;
    }

    private void SetBuild10Status(
        double progress,
        string message)
    {
        if (_build10ProgressBar is not null)
        {
            _build10ProgressBar.Value =
                Math.Clamp(
                    progress,
                    0,
                    100);
        }

        if (_build10StatusTextBlock is not null)
        {
            _build10StatusTextBlock.Text =
                message;
        }

        SetStatus(message);
    }
}