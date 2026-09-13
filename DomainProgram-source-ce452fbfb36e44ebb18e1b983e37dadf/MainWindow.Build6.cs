using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TextFileProcessor
{
    public partial class MainWindow
    {
        private void InitializeBuild6()
        {
            if (FindName("Build6WebsiteVerificationButton") != null)
            {
                return;
            }

            UIElement? previousContent = Content as UIElement;

            if (previousContent == null)
            {
                return;
            }

            Content = null;

            var host = new Grid();
            host.Children.Add(previousContent);

            var button = new Button
            {
                Name = "Build6WebsiteVerificationButton",
                Content = "Сборка 6: проверить сайт",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12),
                Padding = new Thickness(14, 8, 14, 8),
                Background = new SolidColorBrush(
                    Color.FromRgb(34, 120, 190)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                ToolTip = "DNS, HTTP, HTTPS, TLS и контрольный текст"
            };

            button.Click += (_, _) =>
            {
                var verificationWindow =
                    new Build6VerificationWindow
                    {
                        Owner = this
                    };

                verificationWindow.ShowDialog();
            };

            host.Children.Add(button);
            Content = host;
        }
    }
}