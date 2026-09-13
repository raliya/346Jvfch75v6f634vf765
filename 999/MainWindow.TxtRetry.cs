using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using TextFileProcessor.Services;

namespace TextFileProcessor;

public partial class MainWindow
{
    // FINAL_RETRY_ISPMANAGER_TXT_V2

    private Button? _txtRetryIspmanagerButton;

    private void TxtRetryAddButton(
        Panel buttonPanel)
    {
        if (_txtRetryIspmanagerButton is not null)
        {
            return;
        }

        _txtRetryIspmanagerButton = new Button
        {
            Content =
                "ПОВТОРНО ПРОВЕРИТЬ TXT В ISPMANAGER",

            MinWidth = 370,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(
                Color.FromRgb(31, 92, 160))
        };

        _txtRetryIspmanagerButton.Click +=
            TxtRetryIspmanagerButton_Click;

        buttonPanel.Children.Add(
            _txtRetryIspmanagerButton);
    }

    private async void TxtRetryIspmanagerButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (_isRunning)
        {
            MessageBox.Show(
                this,
                "Дождитесь завершения текущей операции.",
                "Повторная проверка TXT",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "ISPmanager повторно проверит TXT-записи " +
            "для доменов из основного поля «Домены».\n\n" +
            "Новые заявки Let's Encrypt создаваться не будут.\n" +
            "Будет продолжена уже существующая заявка.\n\n" +
            "Продолжить?",
            "Повторная проверка TXT",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _isRunning = true;

        if (_txtRetryIspmanagerButton is not null)
        {
            _txtRetryIspmanagerButton.IsEnabled = false;
        }

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
                    "В основном поле «Домены» не указан " +
                    "ни один домен.");
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
                    "Введите пароль ISPmanager.");
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

            ispPassword = string.Empty;

            var submitted = 0;
            var alreadyConfirmed = 0;
            var failed = 0;

            for (var index = 0;
                 index < domains.Count;
                 index++)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                var domain = domains[index];

                try
                {
                    SetBuild10Status(
                        index * 100.0 / domains.Count,
                        $"{domain}: открытие TXT-формы " +
                        "ISPmanager...");

                    var status =
                        await TxtRetryIspmanagerAsync(
                            ispClient,
                            ispUrl,
                            sessionId,
                            domain,
                            cancellationToken);

                    if (status ==
                        TxtRetryStatus.AlreadyConfirmed)
                    {
                        alreadyConfirmed++;

                        AddLog(
                            "INFO",
                            domain,
                            "ISPmanager уже подтвердил TXT. " +
                            "Повторная команда не отправлялась.");
                    }
                    else
                    {
                        submitted++;

                        AddLog(
                            "INFO",
                            domain,
                            "Команда повторной проверки TXT " +
                            "отправлена в ISPmanager.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;

                    var safeMessage =
                        SensitiveDataRedactor.Redact(
                            exception.Message);

                    AddLog(
                        "ERROR",
                        domain,
                        "Повторная проверка TXT: " +
                        safeMessage);
                }

                SetBuild10Status(
                    (index + 1) * 100.0 / domains.Count,
                    $"{domain}: обработано. " +
                    $"Отправлено: {submitted}; " +
                    $"уже подтверждено: {alreadyConfirmed}; " +
                    $"ошибок: {failed}.");
            }

            var finalMessage =
                "Повторная проверка TXT завершена.\n\n" +
                $"Команд отправлено: {submitted}\n" +
                $"Уже подтверждено: {alreadyConfirmed}\n" +
                $"Ошибок: {failed}";

            SetBuild10Status(
                100,
                finalMessage);

            SetStatus(finalMessage);

            AddLog(
                failed == 0 ? "INFO" : "WARN",
                string.Empty,
                finalMessage.Replace(
                    "\n",
                    " ",
                    StringComparison.Ordinal));

            MessageBox.Show(
                this,
                finalMessage,
                "Повторная проверка TXT",
                MessageBoxButton.OK,
                failed == 0
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            const string message =
                "Повторная проверка TXT остановлена.";

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
            var safeMessage =
                SensitiveDataRedactor.Redact(
                    exception.Message);

            SetBuild10Status(
                _build10ProgressBar?.Value ?? 0,
                safeMessage);

            AddLog(
                "ERROR",
                string.Empty,
                safeMessage);

            MessageBox.Show(
                this,
                safeMessage,
                "Ошибка повторной проверки TXT",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            _isRunning = false;

            if (_txtRetryIspmanagerButton is not null)
            {
                _txtRetryIspmanagerButton.IsEnabled = true;
            }

            if (_build10SyncButton is not null)
            {
                _build10SyncButton.IsEnabled = true;
            }

            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private enum TxtRetryStatus
    {
        Submitted,
        AlreadyConfirmed
    }

    private static async Task<TxtRetryStatus>
        TxtRetryIspmanagerAsync(
            HttpClient client,
            string panelUrl,
            string sessionId,
            string domain,
            CancellationToken cancellationToken)
    {
        /*
         * Сначала открывается форма:
         *
         * func=webdomain.letsencrypt.txt
         * elid=<домен>
         *
         * Это только чтение формы. Оно не создаёт
         * новую заявку Let's Encrypt.
         */
        var openFields =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["out"] = "devel",
                ["lang"] = "ru",
                ["auth"] = sessionId,
                ["func"] =
                    "webdomain.letsencrypt.txt",
                ["elid"] = domain,
                ["elname"] = domain,
                ["tconvert"] = "punycode"
            };

        var form =
            await Build10PostIspAsync(
                client,
                Build10NormalizeIspUrl(panelUrl),
                openFields,
                cancellationToken);

        if (TxtRetryAlreadyConfirmed(form))
        {
            return TxtRetryStatus.AlreadyConfirmed;
        }

        /*
         * Важное исправление:
         * для подтверждения используется elid,
         * возвращённый самой TXT-формой ISPmanager.
         *
         * Домен не подставляется вместо идентификатора
         * заявки при нажатии OK.
         */
        var returnedIds =
            form.Descendants()
                .Where(element =>
                    string.Equals(
                        element.Name.LocalName,
                        "elid",
                        StringComparison.OrdinalIgnoreCase))
                .Select(element =>
                    element.Value.Trim())
                .Where(value =>
                    value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        if (returnedIds.Count != 1)
        {
            throw new InvalidOperationException(
                "TXT-форма ISPmanager не вернула один " +
                "однозначный elid заявки. Найдено: " +
                returnedIds.Count + ".");
        }

        var requestId = returnedIds[0];

        if (requestId.Length > 512 ||
            requestId.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "ISPmanager вернул недопустимый elid " +
                "TXT-заявки.");
        }

        var submitFields =
            TxtRetryReadFormValues(form);

        submitFields["out"] = "xml";
        submitFields["lang"] = "ru";
        submitFields["auth"] = sessionId;
        submitFields["func"] =
            "webdomain.letsencrypt.txt";
        submitFields["elid"] = requestId;
        submitFields["elname"] = domain;
        submitFields["tconvert"] = "punycode";
        submitFields["sok"] = "ok";

        /*
         * Запрещаем случайное попадание параметров,
         * способных создать новую заявку.
         */
        foreach (var forbiddenName in new[]
        {
            "wildcard",
            "crtname",
            "domain_name",
            "from_webdomain",
            "enable_cert",
            "dns_check"
        })
        {
            submitFields.Remove(forbiddenName);
        }

        cancellationToken
            .ThrowIfCancellationRequested();

        await Build10PostIspAsync(
            client,
            Build10NormalizeIspUrl(panelUrl),
            submitFields,
            cancellationToken);

        return TxtRetryStatus.Submitted;
    }

    private static bool TxtRetryAlreadyConfirmed(
        XDocument form)
    {
        return form
            .Descendants()
            .Any(element =>
                string.Equals(
                    element.Name.LocalName,
                    "field",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    element.Attribute("name")?.Value,
                    "txt_found_banner",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string>
        TxtRetryReadFormValues(
            XDocument form)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.Ordinal);

        var root = form.Root ??
            throw new InvalidOperationException(
                "ISPmanager вернул пустую TXT-форму.");

        /*
         * ISPmanager обычно возвращает значения формы
         * непосредственными XML-элементами корня.
         */
        foreach (var element in root.Elements())
        {
            if (element.HasElements)
            {
                continue;
            }

            var name =
                element.Name.LocalName.Trim();

            if (name.Length == 0 ||
                string.Equals(
                    name,
                    "error",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = element.Value;

            if (value.Length > 65536 ||
                value.Contains('\0'))
            {
                throw new InvalidOperationException(
                    "TXT-форма ISPmanager содержит " +
                    "недопустимое значение.");
            }

            result[name] = value;
        }

        return result;
    }
}