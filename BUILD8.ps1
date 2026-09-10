#requires -Version 5.1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = 'C:\Users\User\Desktop\Adminki\. Программа домены\Новая папка'

$ProjectFile = Join-Path $ProjectRoot 'TextFileProcessor.csproj'
$DatabaseFile = Join-Path $ProjectRoot 'Services\DatabaseDeploymentService.cs'
$MainWindowFile = Join-Path $ProjectRoot 'MainWindow.Build7.cs'
$Build7File = Join-Path $ProjectRoot 'BUILD7.ps1'
$PublishDirectory = Join-Path $ProjectRoot 'publish\win-x64'

function Write-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    Write-Host ''
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Assert-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Не найден файл: $Path"
    }
}

function Read-TextFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.File]::ReadAllText(
        $Path,
        [System.Text.Encoding]::UTF8
    )
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        $encoding
    )
}

function Add-SpaceshipPagination {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $text = Read-TextFile -Path $Path

    if ($text.Contains('paginationSeparator + "take=100&skip=0"')) {
        Write-Host "Исправление уже присутствует: $Path" `
            -ForegroundColor Yellow

        return
    }

    $pattern = '(?m)^(?<indent>[ \t]*)var baseUrl = settings\.BaseUrl\.TrimEnd\(''/''\);[ \t]*\r?\n[ \t]*var requestUrl = baseUrl \+ "/" \+ relativePath;[ \t]*$'

    $regex = New-Object System.Text.RegularExpressions.Regex(
        $pattern,
        [System.Text.RegularExpressions.RegexOptions]::Multiline
    )

    $matches = $regex.Matches($text)

    if ($matches.Count -ne 1) {
        throw (
            "Не удалось однозначно найти блок Spaceship в файле: $Path. " +
            "Найдено совпадений: $($matches.Count)"
        )
    }

    $evaluator = [System.Text.RegularExpressions.MatchEvaluator] {
        param($match)

        $indent = $match.Groups['indent'].Value

        if ($match.Value.Contains("`r`n")) {
            $newline = "`r`n"
        }
        else {
            $newline = "`n"
        }

        return (
            $indent + "var baseUrl = settings.BaseUrl.TrimEnd('/');" +
            $newline +
            $indent + 'var requestUrl = baseUrl + "/" + relativePath;' +
            $newline +
            $newline +
            $indent + '// Spaceship API требует параметры пагинации.' +
            $newline +
            $indent + 'var paginationSeparator =' +
            $newline +
            $indent + '    requestUrl.Contains(''?'') ? "&" : "?";' +
            $newline +
            $newline +
            $indent + 'requestUrl +=' +
            $newline +
            $indent + '    paginationSeparator + "take=100&skip=0";'
        )
    }

    $updated = $regex.Replace(
        $text,
        $evaluator,
        1
    )

    if ($updated -eq $text) {
        throw "Файл не был изменён: $Path"
    }

    Write-Utf8File `
        -Path $Path `
        -Content $updated

    $check = Read-TextFile -Path $Path

    if (-not $check.Contains('paginationSeparator + "take=100&skip=0"')) {
        throw "Не удалось подтвердить исправление: $Path"
    }

    Write-Host "Исправлен Spaceship API: $Path" `
        -ForegroundColor Green
}

function Fix-DatabaseDeployment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $text = Read-TextFile -Path $Path

    if (
        $text.Contains('DATABASE_CONNECTION_OK') -and
        $text.Contains('DATABASE_ALREADY_EXISTS') -and
        $text.Contains('DB_ATTEMPT=1')
    ) {
        Write-Host 'Исправление базы данных уже присутствует.' `
            -ForegroundColor Yellow

        return
    }

    $pattern = '(?ms)^DATABASE_CREATED=0[ \t]*\r?\n.*?(?=^"\$DB_CLIENT"[ \t]*\\[ \t]*\r?\n[ \t]*--default-character-set=utf8mb4)'

    $regex = New-Object System.Text.RegularExpressions.Regex(
        $pattern,
        (
            [System.Text.RegularExpressions.RegexOptions]::Multiline `
            -bor
            [System.Text.RegularExpressions.RegexOptions]::Singleline
        )
    )

    $matches = $regex.Matches($text)

    if ($matches.Count -ne 1) {
        throw (
            'Не удалось однозначно найти старый блок создания базы данных. ' +
            "Найдено совпадений: $($matches.Count)"
        )
    }

    $replacement = @'
DATABASE_CREATED=0

# Сначала проверяем, доступна ли существующая база данных.
if "$DB_CLIENT" \
    --user="$DATABASE_USER" \
    --database="$DATABASE_NAME" \
    --connect-timeout=5 \
    --execute="SELECT 1" \
    >/dev/null 2>&1
then
    echo "DATABASE_ALREADY_AVAILABLE"
else
    CREATE_OUTPUT="$(
        /usr/local/mgr5/sbin/mgrctl \
            -m ispmgr \
            db.edit \
            name="$DATABASE_NAME" \
            owner="$DATABASE_OWNER" \
            server="MySQL" \
            charset="utf8mb4" \
            user="*" \
            username="$DATABASE_USER" \
            -e 'password=$DB_PASSWORD' \
            -e 'confirm=$DB_PASSWORD' \
            hide_remote_access=on \
            remote_access=off \
            sok=ok 2>&1
    )"

    CREATE_STATUS=$?

    if [ "$CREATE_STATUS" -eq 0 ]; then
        DATABASE_CREATED=1
        echo "DATABASE_CREATED"
    else
        case "$CREATE_OUTPUT" in
            *"exists(name)"*|*"already exists"*|*"already_exists"*)
                echo "DATABASE_ALREADY_EXISTS"
                printf '%s\n' "$CREATE_OUTPUT" >&2
                ;;
            *)
                printf '%s\n' "$CREATE_OUTPUT" >&2
                echo "ISPmanager не создал базу данных." >&2
                exit "$CREATE_STATUS"
                ;;
        esac
    fi
fi

# Создание базы и пользователя может завершаться не мгновенно.
# Проверяем подключение до 20 раз с паузой 3 секунды.
DB_READY=0
DB_ATTEMPT=1
LAST_DB_ERROR=""

while [ "$DB_ATTEMPT" -le 20 ]; do
    LAST_DB_ERROR="$(
        "$DB_CLIENT" \
            --user="$DATABASE_USER" \
            --database="$DATABASE_NAME" \
            --connect-timeout=5 \
            --execute="SELECT 1" 2>&1
    )"

    DB_STATUS=$?

    if [ "$DB_STATUS" -eq 0 ]; then
        DB_READY=1
        echo "DATABASE_CONNECTION_OK"
        break
    fi

    echo "Ожидание готовности БД: попытка $DB_ATTEMPT из 20." >&2

    if [ -n "$LAST_DB_ERROR" ]; then
        printf 'MySQL: %s\n' "$LAST_DB_ERROR" >&2
    fi

    if [ "$DB_ATTEMPT" -lt 20 ]; then
        sleep 3
    fi

    DB_ATTEMPT=$((DB_ATTEMPT + 1))
done

if [ "$DB_READY" -ne 1 ]; then
    echo "Подключение к базе данных не удалось." >&2

    if [ -n "$LAST_DB_ERROR" ]; then
        printf 'Последняя ошибка MySQL: %s\n' "$LAST_DB_ERROR" >&2
    fi

    echo "Проверьте имя базы, пользователя и пароль в config.php." >&2
    exit 41
fi
'@

    if ($matches[0].Value.Contains("`r`n")) {
        $replacement = $replacement.Replace(
            "`n",
            "`r`n"
        )
    }

    $evaluator = [System.Text.RegularExpressions.MatchEvaluator] {
        param($match)

        return $replacement
    }

    $updated = $regex.Replace(
        $text,
        $evaluator,
        1
    )

    if ($updated -eq $text) {
        throw 'Файл DatabaseDeploymentService.cs не был изменён.'
    }

    Write-Utf8File `
        -Path $Path `
        -Content $updated

    $check = Read-TextFile -Path $Path

    $markers = @(
        'DATABASE_ALREADY_EXISTS',
        'DATABASE_CONNECTION_OK',
        'DB_ATTEMPT=1',
        'LAST_DB_ERROR',
        'exit 41'
    )

    foreach ($marker in $markers) {
        if (-not $check.Contains($marker)) {
            throw "После исправления не найден маркер: $marker"
        }
    }

    Write-Host "Исправлено создание базы данных: $Path" `
        -ForegroundColor Green
}

try {
    Write-Step 'Проверка файлов'

    Assert-File -Path $ProjectFile
    Assert-File -Path $DatabaseFile
    Assert-File -Path $MainWindowFile
    Assert-File -Path $Build7File

    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue

    if ($null -eq $dotnet) {
        throw 'dotnet.exe не найден.'
    }

    $running = @(
        Get-Process `
            -Name 'TextFileProcessor' `
            -ErrorAction SilentlyContinue
    )

    if ($running.Count -gt 0) {
        throw 'Закройте TextFileProcessor.exe и повторите запуск.'
    }

    $dotnetVersion = & dotnet.exe --version

    if ($LASTEXITCODE -ne 0) {
        throw 'Не удалось определить версию .NET SDK.'
    }

    Write-Host "Проект: $ProjectRoot"
    Write-Host ".NET SDK: $dotnetVersion" -ForegroundColor Green

    Write-Step 'Создание резервной копии'

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDirectory = Join-Path `
        $ProjectRoot `
        ".build8-backup-$timestamp"

    $backupServices = Join-Path `
        $backupDirectory `
        'Services'

    New-Item `
        -ItemType Directory `
        -Path $backupServices `
        -Force | Out-Null

    Copy-Item `
        -LiteralPath $MainWindowFile `
        -Destination (Join-Path $backupDirectory 'MainWindow.Build7.cs') `
        -Force

    Copy-Item `
        -LiteralPath $Build7File `
        -Destination (Join-Path $backupDirectory 'BUILD7.ps1') `
        -Force

    Copy-Item `
        -LiteralPath $DatabaseFile `
        -Destination (
            Join-Path `
                $backupServices `
                'DatabaseDeploymentService.cs'
        ) `
        -Force

    Write-Host "Резервная копия: $backupDirectory" `
        -ForegroundColor Green

    Write-Step 'Исправление Spaceship API'

    Add-SpaceshipPagination -Path $MainWindowFile
    Add-SpaceshipPagination -Path $Build7File

    Write-Step 'Исправление создания базы данных'

    Fix-DatabaseDeployment -Path $DatabaseFile

    Write-Step 'Проверка внесённых изменений'

    $mainWindowCheck = Read-TextFile -Path $MainWindowFile
    $build7Check = Read-TextFile -Path $Build7File
    $databaseCheck = Read-TextFile -Path $DatabaseFile

    if (-not $mainWindowCheck.Contains('take=100&skip=0')) {
        throw 'Исправление отсутствует в MainWindow.Build7.cs.'
    }

    if (-not $build7Check.Contains('take=100&skip=0')) {
        throw 'Исправление отсутствует в BUILD7.ps1.'
    }

    if (-not $databaseCheck.Contains('DATABASE_CONNECTION_OK')) {
        throw 'Исправление отсутствует в DatabaseDeploymentService.cs.'
    }

    Write-Host 'Все изменения подтверждены.' `
        -ForegroundColor Green

    Write-Step 'Очистка предыдущей сборки'

    $directoriesToDelete = @(
        (Join-Path $ProjectRoot 'bin'),
        (Join-Path $ProjectRoot 'obj'),
        $PublishDirectory
    )

    foreach ($directory in $directoriesToDelete) {
        if (Test-Path -LiteralPath $directory) {
            Write-Host "Удаление: $directory"

            Remove-Item `
                -LiteralPath $directory `
                -Recurse `
                -Force
        }
    }

    Push-Location $ProjectRoot

    try {
        Write-Step 'Восстановление пакетов'

        & dotnet.exe restore `
            $ProjectFile `
            --runtime win-x64

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore завершился кодом $LASTEXITCODE"
        }

        Write-Step 'Проверочная сборка'

        & dotnet.exe build `
            $ProjectFile `
            --configuration Release `
            --runtime win-x64 `
            --no-restore

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build завершился кодом $LASTEXITCODE"
        }

        Write-Step 'Публикация программы'

        & dotnet.exe publish `
            $ProjectFile `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --no-restore `
            --output $PublishDirectory

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish завершился кодом $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    $exeFile = Join-Path `
        $PublishDirectory `
        'TextFileProcessor.exe'

    if (-not (Test-Path -LiteralPath $exeFile -PathType Leaf)) {
        throw "EXE не найден после публикации: $exeFile"
    }

    Write-Step 'ГОТОВО'

    Write-Host 'Исправленная программа:' `
        -ForegroundColor Green

    Write-Host $exeFile -ForegroundColor White

    Write-Host ''
    Write-Host 'Резервная копия:' `
        -ForegroundColor Green

    Write-Host $backupDirectory

    Write-Host ''
    Write-Host 'Внесённые исправления:' `
        -ForegroundColor Green

    Write-Host '1. В запрос Spaceship добавлены take=100 и skip=0.'
    Write-Host '2. Ошибка ISPmanager exists(name) больше не останавливает процесс.'
    Write-Host '3. Подключение к базе проверяется до 20 раз.'
    Write-Host '4. При ошибке отображается ответ клиента MySQL.'
}
catch {
    Write-Host ''
    Write-Host '=== ОШИБКА ===' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red

    if ($null -ne $_.InvocationInfo) {
        Write-Host (
            'Строка скрипта: ' +
            $_.InvocationInfo.ScriptLineNumber
        ) -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'Сборка остановлена. Существующий EXE не удаляйте.' `
        -ForegroundColor Yellow

    exit 1
}
