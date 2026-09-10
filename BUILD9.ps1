#requires -Version 5.1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$Files = @{
    Project = Join-Path $ProjectRoot 'TextFileProcessor.csproj'
    Main    = Join-Path $ProjectRoot 'MainWindow.xaml.cs'
    Build2  = Join-Path $ProjectRoot 'MainWindow.Build2.cs'
    Build3  = Join-Path $ProjectRoot 'MainWindow.Build3.cs'
    Build4  = Join-Path $ProjectRoot 'MainWindow.Build4.cs'
    Build7  = Join-Path $ProjectRoot 'MainWindow.Build7.cs'
}

function Read-Source {
    param([string]$Path)

    return [IO.File]::ReadAllText($Path).Replace(
        "`r`n",
        "`n"
    ).Replace(
        "`r",
        "`n"
    )
}

function Write-Source {
    param(
        [string]$Path,
        [string]$Text
    )

    [IO.File]::WriteAllText(
        $Path,
        $Text.Replace("`n", "`r`n"),
        $script:Utf8NoBom
    )
}

function Replace-Required {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Name,
        [string]$Marker
    )

    if (-not [string]::IsNullOrWhiteSpace($Marker) -and
        $Text.Contains($Marker)) {
        Write-Host "Уже исправлено: $Name" -ForegroundColor Yellow
        return $Text
    }

    if (-not $Text.Contains($Old)) {
        throw "Не найден блок для исправления: $Name"
    }

    Write-Host "Исправлено: $Name" -ForegroundColor Green
    return $Text.Replace($Old, $New)
}

try {
    Write-Host ''
    Write-Host '=== BUILD 9: проверка проекта ===' `
        -ForegroundColor Cyan

    foreach ($path in $Files.Values) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Не найден файл: $path"
        }
    }

    if (Get-Process `
        -Name 'TextFileProcessor' `
        -ErrorAction SilentlyContinue) {
        throw 'Сначала закройте TextFileProcessor.exe.'
    }

    if (-not (Get-Command dotnet.exe `
        -ErrorAction SilentlyContinue)) {
        throw 'Не найден .NET SDK. Установите .NET 8 SDK x64.'
    }

    Write-Host "Проект: $ProjectRoot"
    Write-Host "SDK: $(& dotnet.exe --version)" `
        -ForegroundColor Green

    # ---------------------------------------------------------
    # Резервная копия
    # ---------------------------------------------------------

    Write-Host ''
    Write-Host '=== Резервная копия ===' `
        -ForegroundColor Cyan

    $Timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $Backup = Join-Path `
        $ProjectRoot `
        ".build9-backup-$Timestamp"

    New-Item `
        -ItemType Directory `
        -Path $Backup `
        -Force |
        Out-Null

    foreach ($path in $Files.Values) {
        Copy-Item `
            -LiteralPath $path `
            -Destination $Backup `
            -Force
    }

    Write-Host "Копия: $Backup" -ForegroundColor Green

    # ---------------------------------------------------------
    # MainWindow.xaml.cs
    # ---------------------------------------------------------

    Write-Host ''
    Write-Host '=== Локальная обработка ===' `
        -ForegroundColor Cyan

    $Text = Read-Source $Files.Main

    $Old = @'
            AddLog(
                "INFO",
                string.Empty,
                StatusTextBlock.Text);
        }
        catch (Exception exception)
        {
            var message =
                SensitiveDataRedactor.Redact(
                    exception.Message);
'@

    $New = @'
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
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'результат локальной обработки' `
        -Marker 'Локальная обработка завершилась с ошибками.'

    Write-Source $Files.Main $Text

    # ---------------------------------------------------------
    # MainWindow.Build2.cs
    # ---------------------------------------------------------

    Write-Host ''
    Write-Host '=== ISPmanager ===' `
        -ForegroundColor Cyan

    $Text = Read-Source $Files.Build2

    $Old = @'
            MessageBox.Show(
                this,
                result.Message,
                "ISPmanager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
'@

    $New = @'
            MessageBox.Show(
                this,
                result.Message,
                "ISPmanager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            CompleteBuild7LegacyOperation();
        }
        catch (Exception exception)
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'успешное завершение ISPmanager' `
        -Marker 'CompleteBuild7LegacyOperation();'

    $Old = @'
    private void ShowBuild2Error(Exception exception)
    {
        var message =
'@

    $New = @'
    private void ShowBuild2Error(Exception exception)
    {
        CaptureBuild7Error(exception);

        var message =
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'передача ошибки ISPmanager' `
        -Marker @'
    private void ShowBuild2Error(Exception exception)
    {
        CaptureBuild7Error(exception);
'@

    Write-Source $Files.Build2 $Text

    # ---------------------------------------------------------
    # MainWindow.Build3.cs
    # ---------------------------------------------------------

    Write-Host ''
    Write-Host '=== SSH/SFTP ===' `
        -ForegroundColor Cyan

    $Text = Read-Source $Files.Build3

    $Old = @'
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
'@

    $New = @'
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

            CompleteBuild7LegacyOperation();
        }
        catch (Exception exception)
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'успешное завершение загрузки файлов' `
        -Marker @'
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

            CompleteBuild7LegacyOperation();
'@

    $Old = @'
    private void ShowBuild3Error(Exception exception)
    {
        var message =
'@

    $New = @'
    private void ShowBuild3Error(Exception exception)
    {
        CaptureBuild7Error(exception);

        var message =
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'передача ошибки SSH/SFTP' `
        -Marker @'
    private void ShowBuild3Error(Exception exception)
    {
        CaptureBuild7Error(exception);
'@

    Write-Source $Files.Build3 $Text

    # ---------------------------------------------------------
    # MainWindow.Build4.cs
    # ---------------------------------------------------------

    Write-Host ''
    Write-Host '=== База данных ===' `
        -ForegroundColor Cyan

    $Text = Read-Source $Files.Build4

    $Old = @'
            MessageBox.Show(
                this,
                message,
                "Сборка 4 — успешно",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            var message =
'@

    $New = @'
            MessageBox.Show(
                this,
                message,
                "Сборка 4 — успешно",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            CompleteBuild7LegacyOperation();
        }
        catch (Exception exception)
        {
            CaptureBuild7Error(exception);

            var message =
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'результат развёртывания базы данных' `
        -Marker @'
                "Сборка 4 — успешно",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            CompleteBuild7LegacyOperation();
'@

    Write-Source $Files.Build4 $Text

    # ---------------------------------------------------------
    # MainWindow.Build7.cs
    # ---------------------------------------------------------

    Write-Host ''
    Write-Host '=== Последовательный запуск ===' `
        -ForegroundColor Cyan

    $Text = Read-Source $Files.Build7

    $Old = @'
    private Button? _build7SpaceshipButton;
    private Button? _build7AllButton;
    private readonly HttpClient _spaceshipHttpClient =
'@

    $New = @'
    private Button? _build7SpaceshipButton;
    private Button? _build7AllButton;

    private TaskCompletionSource<object?>?
        _build7LegacyCompletion;

    private readonly HttpClient _spaceshipHttpClient =
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'контроллер завершения этапов' `
        -Marker '_build7LegacyCompletion;'

    # Все вызовы Start_Click + ненадёжное ожидание.
    $Old = @'
            Start_Click(
                this,
                new RoutedEventArgs());
            await WaitForLegacyOperationAsync();
'@

    $New = @'
            await RunBuild7LegacyOperationAsync(
                () => Start_Click(
                    this,
                    new RoutedEventArgs()),
                "локальная обработка");
'@

    if ($Text.Contains($Old)) {
        $Text = $Text.Replace($Old, $New)
        Write-Host 'Исправлены вызовы локальной обработки.' `
            -ForegroundColor Green
    }
    elseif (-not $Text.Contains(
        '"локальная обработка");'
    )) {
        throw 'Не найдены вызовы локальной обработки Build7.'
    }

    # Все вызовы создания WWW-домена.
    $Old = @'
            CreateSelectedWebDomain_Click(
                this,
                new RoutedEventArgs());
            await WaitForLegacyOperationAsync();
'@

    $New = @'
            await RunBuild7LegacyOperationAsync(
                () => CreateSelectedWebDomain_Click(
                    this,
                    new RoutedEventArgs()),
                "создание WWW-домена");
'@

    if ($Text.Contains($Old)) {
        $Text = $Text.Replace($Old, $New)
        Write-Host 'Исправлены вызовы создания WWW-домена.' `
            -ForegroundColor Green
    }
    elseif (-not $Text.Contains(
        '"создание WWW-домена");'
    )) {
        throw 'Не найдены вызовы создания WWW-домена Build7.'
    }

    # Все вызовы загрузки файлов.
    $Old = @'
            DeploySelectedSite_Click(
                this,
                new RoutedEventArgs());
            await WaitForLegacyOperationAsync();
'@

    $New = @'
            await RunBuild7LegacyOperationAsync(
                () => DeploySelectedSite_Click(
                    this,
                    new RoutedEventArgs()),
                "загрузка файлов");
'@

    if ($Text.Contains($Old)) {
        $Text = $Text.Replace($Old, $New)
        Write-Host 'Исправлены вызовы загрузки файлов.' `
            -ForegroundColor Green
    }
    elseif (-not $Text.Contains(
        '"загрузка файлов");'
    )) {
        throw 'Не найдены вызовы загрузки файлов Build7.'
    }

    # Все вызовы развёртывания БД.
    $Old = @'
            DeployDatabaseButton_Click(
                this,
                new RoutedEventArgs());
            await WaitForLegacyOperationAsync();
'@

    $New = @'
            await RunBuild7LegacyOperationAsync(
                () => DeployDatabaseButton_Click(
                    this,
                    new RoutedEventArgs()),
                "создание БД и импорт SQL");
'@

    if ($Text.Contains($Old)) {
        $Text = $Text.Replace($Old, $New)
        Write-Host 'Исправлены вызовы базы данных.' `
            -ForegroundColor Green
    }
    elseif (-not $Text.Contains(
        '"создание БД и импорт SQL");'
    )) {
        throw 'Не найдены вызовы базы данных Build7.'
    }

    # Полностью заменяем ненадёжный WaitForLegacyOperationAsync.
    $Old = @'
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
'@

    $New = @'
    private async Task RunBuild7LegacyOperationAsync(
        Action startOperation,
        string operationName)
    {
        if (_build7LegacyCompletion is not null &&
            !_build7LegacyCompletion.Task.IsCompleted)
        {
            throw new InvalidOperationException(
                "Предыдущий этап ещё не завершён.");
        }

        var completion =
            new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        _build7LegacyCompletion = completion;

        try
        {
            startOperation();

            // Обработчик async void выполняется синхронно до
            // первого await. Если он вернулся, не установив
            // _isRunning, пользователь отказался от подтверждения
            // либо предварительная проверка не позволила запуск.
            await Task.Yield();

            if (!completion.Task.IsCompleted &&
                !_isRunning)
            {
                completion.TrySetCanceled();
            }

            try
            {
                await completion.Task.WaitAsync(
                    TimeSpan.FromMinutes(65));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Превышено время ожидания этапа: " +
                    $"{operationName}.",
                    exception);
            }
        }
        finally
        {
            if (ReferenceEquals(
                    _build7LegacyCompletion,
                    completion))
            {
                _build7LegacyCompletion = null;
            }
        }
    }

    private void CompleteBuild7LegacyOperation()
    {
        _build7LegacyCompletion?.TrySetResult(null);
    }

    private void CaptureBuild7Error(Exception exception)
    {
        var completion = _build7LegacyCompletion;
        if (completion is null ||
            completion.Task.IsCompleted)
        {
            return;
        }

        if (exception is OperationCanceledException)
        {
            completion.TrySetCanceled();
            return;
        }

        completion.TrySetException(exception);
    }
'@

    $Text = Replace-Required `
        -Text $Text `
        -Old $Old `
        -New $New `
        -Name 'надёжное ожидание async void' `
        -Marker 'private async Task RunBuild7LegacyOperationAsync('

    Write-Source $Files.Build7 $Text

    # ---------------------------------------------------------
    # Проверка отсутствия старого ожидания
    # ---------------------------------------------------------

    $Build7Check = Read-Source $Files.Build7

    if ($Build7Check.Contains(
        'WaitForLegacyOperationAsync'
    )) {
        throw `
            'В Build7 остался старый WaitForLegacyOperationAsync.'
    }

    # ---------------------------------------------------------
    # Сборка
    # ---------------------------------------------------------

    Write-Host ''
    Write-Host '=== Восстановление пакетов ===' `
        -ForegroundColor Cyan

    Push-Location $ProjectRoot

    try {
        & dotnet.exe restore $Files.Project

        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet restore завершился с ошибкой.'
        }

        Write-Host ''
        Write-Host '=== Проверочная сборка ===' `
            -ForegroundColor Cyan

        & dotnet.exe build `
            $Files.Project `
            --configuration Release `
            --no-restore

        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet build завершился с ошибкой.'
        }

        Write-Host ''
        Write-Host '=== Публикация EXE ===' `
            -ForegroundColor Cyan

        $PublishDirectory = Join-Path `
            $ProjectRoot `
            'publish\BUILD9-win-x64'

        if (Test-Path -LiteralPath $PublishDirectory) {
            Remove-Item `
                -LiteralPath $PublishDirectory `
                -Recurse `
                -Force
        }

        & dotnet.exe publish `
            $Files.Project `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --output $PublishDirectory `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None `
            -p:DebugSymbols=false

        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet publish завершился с ошибкой.'
        }
    }
    finally {
        Pop-Location
    }

    $Exe = Get-ChildItem `
        -LiteralPath $PublishDirectory `
        -Filter '*.exe' `
        -File |
        Select-Object -First 1

    if ($null -eq $Exe) {
        throw "EXE не найден в $PublishDirectory"
    }

    Write-Host ''
    Write-Host 'ГОТОВО!' -ForegroundColor Green
    Write-Host 'Исправление применено и собрано.' `
        -ForegroundColor Green
    Write-Host ''
    Write-Host 'Новый EXE:' -ForegroundColor Cyan
    Write-Host $Exe.FullName -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Резервная копия:' -ForegroundColor Cyan
    Write-Host $Backup -ForegroundColor Yellow

    Start-Process `
        -FilePath 'explorer.exe' `
        -ArgumentList (
            '/select,"{0}"' -f $Exe.FullName
        )
}
catch {
    Write-Host ''
    Write-Host 'ОШИБКА BUILD 9:' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    Write-Host ''

    if (Get-Variable `
        -Name Backup `
        -ErrorAction SilentlyContinue) {
        Write-Host 'Резервная копия сохранена:' `
            -ForegroundColor Cyan
        Write-Host $Backup -ForegroundColor Yellow
    }

    exit 1
}
