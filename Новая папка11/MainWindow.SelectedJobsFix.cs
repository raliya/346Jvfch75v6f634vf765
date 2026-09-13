using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TextFileProcessor.Models;
using TextFileProcessor.Services;

namespace TextFileProcessor;

public partial class MainWindow
{
    private List<DomainJob> SelectedJobsFixSnapshot()
    {
        var jobs = JobsGrid.SelectedItems
            .OfType<DomainJob>()
            .ToList();

        if (jobs.Count == 0)
            throw new InvalidOperationException(
                "Выделите нужные строки на вкладке «Задания».");

        var domains = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var job in jobs)
        {
            var domain = _domainService.Normalize(job.Domain);
            if (!domains.Add(domain))
                throw new InvalidOperationException(
                    $"В выделении несколько заданий для {domain}. " +
                    "Оставьте одно задание на каждый домен.");
        }

        return jobs;
    }

    private async Task RunSelectedJobsFixAsync(string mode)
    {
        if (_isRunning)
        {
            MessageBox.Show(this,
                "Дождитесь завершения текущей операции.");
            return;
        }

        string panelPassword = "";
        string sshPassword = "";
        CancellationTokenSource? queueCancellation = null;

        var previousButtons = new List<(Button Button, bool Enabled)>();
        bool started = false;
        int successful = 0;
        int failed = 0;
        int attempted = 0;
        int total = 0;
        bool cancelled = false;

        try
        {
            if (mode != "www" && mode != "db" && mode != "all")
                throw new ArgumentException("Неизвестная операция.");

            // Снимок выделения до первого await.
            // Изменение выделения во время работы не меняет очередь.
            var jobs = SelectedJobsFixSnapshot();
            total = jobs.Count;

            var description = mode switch
            {
                "www" => "Создание/проверка WWW-доменов и запрос Wildcard",
                "db" => "Создание БД и импорт SQL",
                _ => "WWW-домен → загрузка файлов → БД/SQL → проверка Spaceship"
            };

            var warning =
                description + "\n\n" +
                "Выбрано заданий: " + total + "\n" +
                string.Join("\n", jobs.Select(j => j.Domain)) + "\n\n" +
                "Ошибка одного задания не останавливает остальные.";

            if (mode == "db" || mode == "all")
                warning +=
                    "\n\nSQL импортируется только в пустую БД. " +
                    "Используйте доверенные дампы. " +
                    "Существующие пароли БД не сбрасываются.";

            if (mode == "all")
                warning +=
                    "\n\nЗагрузка файлов заменит содержимое каталогов сайтов " +
                    "локальными копиями. Перед запуском сделайте отдельную " +
                    "резервную копию сайтов." +
                    "\nЛокальная обработка автоматически не запускается: " +
                    "выберите уже подготовленные задания.";

            warning +=
                "\n\nОтправка заявки Wildcard не означает, " +
                "что сертификат уже выпущен и установлен.";

            if (MessageBox.Show(this, warning, "Выбранные задания",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var panelSettings = ReadIspmanagerSettings();
            panelPassword = _ispmanagerSettingsService.GetPassword(
                panelSettings, IspmanagerPasswordBox.Password);

            if (string.IsNullOrEmpty(panelPassword))
                throw new InvalidOperationException(
                    "Введите или сохраните пароль ISPmanager.");

            SshDeploymentSettings? sshSettings = null;

            if (mode == "db" || mode == "all")
            {
                sshSettings = ReadSshSettings();
                sshPassword = _sshDeploymentSettingsService.GetPassword(
                    sshSettings, SshPasswordBox.Password);
                sshSettings.Validate(sshPassword);

                if (!string.Equals(
                        sshSettings.Owner.Trim(),
                        panelSettings.Owner.Trim(),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Владельцы в SSH и ISPmanager должны совпадать.");
            }

            var buttons = new Button?[]
            {
                StartButton, PreviewButton,
                SaveIspmanagerSettingsButton, TestIspmanagerButton,
                CreateWebDomainButton, SaveSshSettingsButton,
                ReadSshFingerprintButton, TestSshConnectionButton,
                DeploySelectedSiteButton, DeployDatabaseButton,
                _build7LocalButton, _build7DomainButton,
                _build7UploadButton, _build7DatabaseButton,
                _build7SpaceshipButton, _build7AllButton,
                _build10SyncButton, CancelButton
            };

            foreach (var button in buttons.OfType<Button>().Distinct())
            {
                previousButtons.Add((button, button.IsEnabled));
                button.IsEnabled = false;
            }

            queueCancellation = new CancellationTokenSource();
            _cancellationTokenSource = queueCancellation;
            _isRunning = true;
            started = true;
            CancelButton.IsEnabled = true;

            var ct = queueCancellation.Token;

            foreach (var job in jobs)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                attempted++;
                SetStatus($"{attempted}/{total}: {job.Domain}");
                AddLog("INFO", job.Domain,
                    $"Запуск выбранного задания {attempted}/{total}.");

                try
                {
                    var messages = new List<string>();

                    if (mode == "www" || mode == "all")
                    {
                        using var stage =
                            CancellationTokenSource.CreateLinkedTokenSource(ct);
                        stage.CancelAfter(TimeSpan.FromMinutes(25));

                        var result = await _ispmanagerAutomationService
                            .CreateWebDomainAsync(
                                panelSettings, panelPassword,
                                job.Domain, stage.Token);

                        if (!result.Success)
                            throw new InvalidOperationException(result.Message);

                        messages.Add(result.Message);
                    }

                    if (mode == "all")
                    {
                        if (string.IsNullOrWhiteSpace(job.OutputPath) ||
                            !Directory.Exists(job.OutputPath))
                            throw new InvalidOperationException(
                                "Нет подготовленной локальной папки сайта.");

                        using var stage =
                            CancellationTokenSource.CreateLinkedTokenSource(ct);
                        stage.CancelAfter(TimeSpan.FromMinutes(30));

                        var result = await _sftpDeploymentService.DeployAsync(
                            sshSettings!, sshPassword,
                            new SiteDeploymentRequest
                            {
                                Domain = job.Domain,
                                LocalDirectory = job.OutputPath
                            },
                            new Progress<SftpDeploymentProgress>(p =>
                            {
                                Build3ProgressBar.Value = p.Percent;
                                Build3StatusTextBlock.Text =
                                    $"{job.Domain}: {p.Message}";
                            }),
                            stage.Token);

                        messages.Add(
                            $"Загружено файлов: {result.UploadedFiles}.");
                    }

                    if (mode == "db" || mode == "all")
                    {
                        using var stage =
                            CancellationTokenSource.CreateLinkedTokenSource(ct);
                        stage.CancelAfter(TimeSpan.FromMinutes(60));

                        var result = await _databaseDeploymentService.DeployAsync(
                            sshSettings!, sshPassword, job.OutputPath,
                            new Progress<DatabaseDeploymentProgress>(p =>
                            {
                                Build4ProgressBar.Value = p.Percent;
                                Build4StatusTextBlock.Text =
                                    $"{job.Domain}: {p.Message}";
                            }),
                            stage.Token, panelSettings, panelPassword);

                        job.DatabaseName = result.DatabaseName;
                        job.DatabaseUser = result.DatabaseUser;
                        messages.Add(
                            $"БД {result.DatabaseName}: SQL импортирован.");
                    }

                    if (mode == "all")
                    {
                        ct.ThrowIfCancellationRequested();
                        await TestSpaceshipAsync(job.Domain);
                        messages.Add("Доступ к Spaceship проверен.");
                    }

                    job.Message = string.Join(" ", messages);
                    _database.SaveJob(job);
                    AddLog("INFO", job.Domain, job.Message);
                    successful++;
                }
                catch (OperationCanceledException)
                    when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    job.Message =
                        "Остановка запрошена. Проверьте результат " +
                        "уже отправленных серверных операций перед повтором.";
                    _database.SaveJob(job);
                    AddLog("WARN", job.Domain, job.Message);
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    var message = ex.Message;

                    foreach (var secret in new[] { panelPassword, sshPassword }
                                 .Where(s => !string.IsNullOrEmpty(s))
                                 .OrderByDescending(s => s.Length))
                    {
                        message = message.Replace(
                            secret, "[СКРЫТО]", StringComparison.Ordinal);
                    }

                    job.Message = SensitiveDataRedactor.Redact(message);
                    _database.SaveJob(job);
                    AddLog("ERROR", job.Domain, job.Message);
                    // Следующее выбранное задание обрабатывается отдельно.
                }
            }

            var summary =
                $"Очередь {(cancelled ? "остановлена" : "завершена")}. " +
                $"Выбрано: {total}; успешно: {successful}; " +
                $"ошибок: {failed}; не начато: {total - attempted}.";

            if (cancelled)
                summary +=
                    " Незавершённое задание не засчитано как успешное.";

            if (mode != "db")
                summary +=
                    " Статус выпуска и установки Wildcard " +
                    "этим итогом не подтверждается.";

            SetStatus(summary);
            AddLog(failed > 0 || cancelled ? "WARN" : "INFO", "", summary);
            MessageBox.Show(this, summary, "Результат очереди",
                MessageBoxButton.OK,
                failed > 0 || cancelled
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var message = ex.Message;

            foreach (var secret in new[] { panelPassword, sshPassword }
                         .Where(s => !string.IsNullOrEmpty(s))
                         .OrderByDescending(s => s.Length))
            {
                message = message.Replace(
                    secret, "[СКРЫТО]", StringComparison.Ordinal);
            }

            message = SensitiveDataRedactor.Redact(message);
            SetStatus(message);
            AddLog("ERROR", "", message);
            MessageBox.Show(this, message, "Ошибка очереди",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            panelPassword = "";
            sshPassword = "";

            if (started)
            {
                if (ReferenceEquals(
                        _cancellationTokenSource, queueCancellation))
                    _cancellationTokenSource = null;

                _isRunning = false;
                foreach (var saved in previousButtons)
                    saved.Button.IsEnabled = saved.Enabled;
            }

            queueCancellation?.Dispose();
        }
    }
}