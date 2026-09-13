using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TextFileProcessor.Models;
using TextFileProcessor.Services;

namespace TextFileProcessor
{
    public sealed class Build6VerificationWindow : Window
    {
        private readonly TextBox _domainTextBox;
        private readonly TextBox _controlTextBox;
        private readonly TextBox _timeoutTextBox;
        private readonly Button _verifyButton;
        private readonly Button _cancelButton;
        private readonly TextBox _resultTextBox;
        private readonly ProgressBar _progressBar;

        private CancellationTokenSource? _cancellationTokenSource;

        public Build6VerificationWindow()
        {
            Title = "Сборка 6 — проверка опубликованного сайта";
            Width = 820;
            Height = 720;
            MinWidth = 650;
            MinHeight = 520;
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner;

            var root = new Grid
            {
                Margin = new Thickness(16)
            };

            root.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = "Финальная проверка сайта",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16)
            };

            Grid.SetRow(title, 0);
            root.Children.Add(title);

            _domainTextBox = AddInput(
                root,
                1,
                "Домен или URL:",
                "example.com");

            _controlTextBox = AddInput(
                root,
                2,
                "Контрольный текст (необязательно):",
                string.Empty);

            _timeoutTextBox = AddInput(
                root,
                3,
                "Тайм-аут в секундах:",
                "30");

            var commandPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 12)
            };

            _verifyButton = new Button
            {
                Content = "Проверить сайт",
                MinWidth = 150,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 0)
            };

            _cancelButton = new Button
            {
                Content = "Отмена",
                MinWidth = 100,
                Height = 36,
                IsEnabled = false
            };

            _progressBar = new ProgressBar
            {
                Width = 180,
                Height = 18,
                Margin = new Thickness(16, 9, 0, 0),
                IsIndeterminate = false
            };

            _verifyButton.Click += async (_, _) =>
                await VerifyAsync();

            _cancelButton.Click += (_, _) =>
                _cancellationTokenSource?.Cancel();

            commandPanel.Children.Add(_verifyButton);
            commandPanel.Children.Add(_cancelButton);
            commandPanel.Children.Add(_progressBar);

            Grid.SetRow(commandPanel, 4);
            root.Children.Add(commandPanel);

            _resultTextBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13
            };

            Grid.SetRow(_resultTextBox, 5);
            root.Children.Add(_resultTextBox);

            Content = root;
        }

        private static TextBox AddInput(
            Grid root,
            int row,
            string label,
            string defaultValue)
        {
            var panel = new Grid
            {
                Margin = new Thickness(0, 0, 0, 8)
            };

            panel.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(230) });
            panel.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var textBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center
            };

            var textBox = new TextBox
            {
                Text = defaultValue,
                MinHeight = 30,
                VerticalContentAlignment =
                    VerticalAlignment.Center
            };

            Grid.SetColumn(textBlock, 0);
            Grid.SetColumn(textBox, 1);

            panel.Children.Add(textBlock);
            panel.Children.Add(textBox);

            Grid.SetRow(panel, row);
            root.Children.Add(panel);

            return textBox;
        }

        private async Task VerifyAsync()
        {
            if (!int.TryParse(
                    _timeoutTextBox.Text,
                    out int timeoutSeconds) ||
                timeoutSeconds < 5 ||
                timeoutSeconds > 300)
            {
                MessageBox.Show(
                    this,
                    "Тайм-аут должен быть от 5 до 300 секунд.",
                    "Сборка 6",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource =
                new CancellationTokenSource();

            _verifyButton.IsEnabled = false;
            _cancelButton.IsEnabled = true;
            _progressBar.IsIndeterminate = true;
            _resultTextBox.Text = "Выполняется проверка...";

            try
            {
                var service =
                    new WebsiteVerificationService();

                WebsiteVerificationResult result =
                    await service.VerifyAsync(
                        _domainTextBox.Text,
                        _controlTextBox.Text,
                        TimeSpan.FromSeconds(timeoutSeconds),
                        _cancellationTokenSource.Token);

                _resultTextBox.Text = FormatResult(result);
            }
            catch (Exception ex)
            {
                _resultTextBox.Text =
                    "Непредвиденная ошибка: " + ex.Message;
            }
            finally
            {
                _progressBar.IsIndeterminate = false;
                _verifyButton.IsEnabled = true;
                _cancelButton.IsEnabled = false;
            }
        }

        private static string FormatResult(
            WebsiteVerificationResult result)
        {
            var text = new StringBuilder();

            text.AppendLine("РЕЗУЛЬТАТ ПРОВЕРКИ");
            text.AppendLine(
                "========================================");
            text.AppendLine($"Домен: {result.Domain}");
            text.AppendLine(
                $"Общий результат: {(result.Success ? "УСПЕШНО" : "ЕСТЬ ПРОБЛЕМЫ")}");
            text.AppendLine(
                $"Время: {result.Duration.TotalSeconds:F2} сек.");
            text.AppendLine();

            text.AppendLine(
                $"DNS: {(result.DnsResolved ? "успешно" : "ошибка")}");

            text.AppendLine(
                "IP: " +
                (result.IpAddresses.Count > 0
                    ? string.Join(", ", result.IpAddresses)
                    : "не найдены"));

            text.AppendLine();
            text.AppendLine(
                $"HTTP: {(result.HttpAvailable ? "доступен" : "недоступен")}");
            text.AppendLine(
                $"HTTP-код: {result.HttpStatusCode?.ToString() ?? "нет"}");
            text.AppendLine(
                $"Конечный HTTP URL: {result.HttpFinalUrl}");

            text.AppendLine();
            text.AppendLine(
                $"HTTPS: {(result.HttpsAvailable ? "доступен" : "недоступен")}");
            text.AppendLine(
                $"HTTPS-код: {result.HttpsStatusCode?.ToString() ?? "нет"}");
            text.AppendLine(
                $"Конечный HTTPS URL: {result.HttpsFinalUrl}");

            text.AppendLine();
            text.AppendLine(
                $"TLS-сертификат: {(result.CertificatePresent ? "получен" : "не получен")}");
            text.AppendLine(
                $"TLS действителен: {(result.CertificateValid ? "да" : "нет")}");
            text.AppendLine(
                $"Владелец: {result.CertificateSubject}");
            text.AppendLine(
                $"Издатель: {result.CertificateIssuer}");
            text.AppendLine(
                $"Действует до: {result.CertificateExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "не определено"}");

            if (!string.IsNullOrWhiteSpace(
                    result.CertificateError))
            {
                text.AppendLine(
                    $"Ошибка TLS: {result.CertificateError}");
            }

            text.AppendLine();

            if (result.ControlTextRequired)
            {
                text.AppendLine(
                    $"Контрольный текст: {(result.ControlTextFound ? "найден" : "не найден")}");
            }
            else
            {
                text.AppendLine(
                    "Контрольный текст: проверка не требовалась");
            }

            if (result.Errors.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("ОШИБКИ И ПРЕДУПРЕЖДЕНИЯ:");

                foreach (string error in result.Errors)
                {
                    text.AppendLine("- " + error);
                }
            }

            return text.ToString();
        }
    }
}