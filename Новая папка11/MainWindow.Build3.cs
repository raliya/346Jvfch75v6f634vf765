using System.Windows;
using TextFileProcessor.Models;
using TextFileProcessor.Services;

namespace TextFileProcessor;

public partial class MainWindow
{
    private readonly SshDeploymentSettingsService
        _sshDeploymentSettingsService = new();

    private readonly SftpDeploymentService
        _sftpDeploymentService = new();

    private SshDeploymentSettings
        _sshDeploymentSettings = new();

    private void Build3_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Build2_Loaded(sender, e);

        try
        {
            _sshDeploymentSettings =
                _sshDeploymentSettingsService.Load();

            SshHostTextBox.Text =
                _sshDeploymentSettings.Host;

            SshPortTextBox.Text =
                _sshDeploymentSettings.Port.ToString();

            SshUsernameTextBox.Text =
                _sshDeploymentSettings.Username;

            SshFingerprintTextBox.Text =
                _sshDeploymentSettings.HostKeySha256;

            SshRemoteRootTextBox.Text =
                _sshDeploymentSettings.RemoteSitesRoot;

            SshOwnerTextBox.Text =
                _sshDeploymentSettings.Owner;

            SshGroupTextBox.Text =
                _sshDeploymentSettings.Group;

            SshPasswordSavedTextBlock.Text =
                string.IsNullOrWhiteSpace(
                    _sshDeploymentSettings.EncryptedPassword)
                    ? "SSH-пароль ещё не сохранён."
                    : "Зашифрованный SSH-пароль сохранён.";

            Build3StatusTextBlock.Text =
                "Получите и независимо сверьте fingerprint, " +
                "затем проверьте подключение.";
        }
        catch (Exception exception)
        {
            Build3StatusTextBlock.Text =
                SensitiveDataRedactor.Redact(
                    exception.Message);
        }
    }

    private SshDeploymentSettings ReadSshSettings()
    {
        if (!int.TryParse(
                SshPortTextBox.Text.Trim(),
                out var port))
        {
            throw new InvalidOperationException(
                "SSH-порт должен быть целым числом.");
        }

        return new SshDeploymentSettings
        {
            Host = SshHostTextBox.Text.Trim(),
            Port = port,
            Username = SshUsernameTextBox.Text.Trim(),
            HostKeySha256 =
                SshFingerprintTextBox.Text.Trim(),
            RemoteSitesRoot =
                SshRemoteRootTextBox.Text.Trim(),
            Owner = SshOwnerTextBox.Text.Trim(),
            Group = SshGroupTextBox.Text.Trim(),
            EncryptedPassword =
                _sshDeploymentSettings.EncryptedPassword
        };
    }

    private void SaveSshSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSshSettings();

            _sshDeploymentSettingsService.Save(
                settings,
                SshPasswordBox.Password);

            _sshDeploymentSettings = settings;
            SshPasswordBox.Clear();

            SshPasswordSavedTextBlock.Text =
                "SSH-настройки сохранены. Пароль зашифрован DPAPI.";

            Build3StatusTextBlock.Text =
                "SSH/SFTP-настройки сохранены.";

            AddLog(
                "INFO",
                string.Empty,
                "SSH/SFTP-настройки сохранены. " +
                "Пароль в журнал не записан.");
        }
        catch (Exception exception)
        {
            ShowBuild3Error(exception);
        }
    }

    private async void ReadSshFingerprint_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        string password = string.Empty;

        try
        {
            SetBuild3Busy(true);

            Build3StatusTextBlock.Text =
                "Получение fingerprint сервера...";

            var settings = ReadSshSettings();

            password =
                _sshDeploymentSettingsService.GetPassword(
                    settings,
                    SshPasswordBox.Password);

            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromMinutes(1));

            var fingerprint =
                await _sftpDeploymentService
                    .ReadServerFingerprintAsync(
                        settings,
                        password,
                        cancellation.Token);

            SshFingerprintTextBox.Text = fingerprint;

            var message =
                "Получен fingerprint: " +
                fingerprint +
                ". Перед сохранением сверьте его с сервером.";

            Build3StatusTextBlock.Text = message;
            SetStatus(message);

            MessageBox.Show(
                this,
                "Получен fingerprint:\n\n" +
                fingerprint +
                "\n\nСверьте его с fingerprint сервера " +
                "по независимому доверенному каналу.",
                "SSH fingerprint",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            ShowBuild3Error(exception);
        }
        finally
        {
            password = string.Empty;
            SetBuild3Busy(false);
        }
    }

    private async void TestSshConnection_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        string password = string.Empty;

        try
        {
            SetBuild3Busy(true);

            Build3StatusTextBlock.Text =
                "Проверка SSH-подключения...";

            var settings = ReadSshSettings();

            password =
                _sshDeploymentSettingsService.GetPassword(
                    settings,
                    SshPasswordBox.Password);

            settings.Validate(password);

            await _sftpDeploymentService.TestConnectionAsync(
                settings,
                password,
                CancellationToken.None);

            _sshDeploymentSettingsService.Save(
                settings,
                SshPasswordBox.Password);

            _sshDeploymentSettings = settings;
            SshPasswordBox.Clear();

            var message =
                "SSH/SFTP-подключение успешно проверено.";

            Build3StatusTextBlock.Text = message;
            SetStatus(message);

            SshPasswordSavedTextBlock.Text =
                "Зашифрованный SSH-пароль сохранён.";

            AddLog(
                "INFO",
                string.Empty,
                message);

            MessageBox.Show(
                this,
                message,
                "SSH/SFTP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowBuild3Error(exception);
        }
        finally
        {
            password = string.Empty;
            SetBuild3Busy(false);
        }
    }

    private async void DeploySelectedSite_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        var selectedJob =
            JobsGrid.SelectedItem as DomainJob;

        if (selectedJob is null)
        {
            MessageBox.Show(
                this,
                "Сначала выберите домен в таблице «Задания».",
                "SSH/SFTP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        if (string.IsNullOrWhiteSpace(
                selectedJob.OutputPath) ||
            !Directory.Exists(selectedJob.OutputPath))
        {
            MessageBox.Show(
                this,
                "У задания отсутствует готовая локальная папка.",
                "SSH/SFTP",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Загрузить сайт {selectedJob.Domain}?\n\n" +
            $"Локально:\n{selectedJob.OutputPath}\n\n" +
            $"На сервер:\n" +
            $"{SshRemoteRootTextBox.Text.TrimEnd('/')}/" +
            $"{selectedJob.Domain}/\n\n" +
            "SQL-файлы в web-root загружены не будут.",
            "Подтверждение загрузки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        string password = string.Empty;

        try
        {
            SetBuild3Busy(true);
            Build3ProgressBar.Value = 0;

            var settings = ReadSshSettings();

            password =
                _sshDeploymentSettingsService.GetPassword(
                    settings,
                    SshPasswordBox.Password);

            settings.Validate(password);

            var progress =
                new Progress<SftpDeploymentProgress>(
                    item =>
                    {
                        Build3ProgressBar.Value =
                            item.Percent;

                        Build3StatusTextBlock.Text =
                            $"{item.Percent}% — {item.Message}";

                        SetStatus(
                            $"{selectedJob.Domain}: " +
                            $"{item.Percent}% — {item.Message}");
                    });

            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromMinutes(30));

            var result =
                await _sftpDeploymentService.DeployAsync(
                    settings,
                    password,
                    new SiteDeploymentRequest
                    {
                        Domain = selectedJob.Domain,
                        LocalDirectory =
                            selectedJob.OutputPath
                    },
                    progress,
                    cancellation.Token);

            var message =
                $"Сайт {result.Domain} загружен. " +
                $"Файлов: {result.UploadedFiles}; " +
                $"байт: {result.UploadedBytes}; " +
                $"каталог: {result.RemoteDirectory}.";

            Build3ProgressBar.Value = 100;
            Build3StatusTextBlock.Text = message;
            SetStatus(message);

            AddLog(
                "INFO",
                selectedJob.Domain,
                message);

            MessageBox.Show(
                this,
                message,
                "SSH/SFTP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowBuild3Error(exception);
        }
        finally
        {
            password = string.Empty;
            SetBuild3Busy(false);
        }
    }

    private void SetBuild3Busy(bool busy)
    {
        _isRunning = busy;

        SaveSshSettingsButton.IsEnabled = !busy;
        ReadSshFingerprintButton.IsEnabled = !busy;
        TestSshConnectionButton.IsEnabled = !busy;
        DeploySelectedSiteButton.IsEnabled = !busy;

        StartButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy;
        CreateWebDomainButton.IsEnabled = !busy;
    }

    private void ShowBuild3Error(Exception exception)
    {
        var message =
            SensitiveDataRedactor.Redact(
                exception.Message);

        Build3StatusTextBlock.Text = message;
        SetStatus(message);

        AddLog(
            "ERROR",
            string.Empty,
            message);

        MessageBox.Show(
            this,
            message,
            "Ошибка SSH/SFTP",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
