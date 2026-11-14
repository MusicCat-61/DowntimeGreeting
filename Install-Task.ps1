<#
.SYNOPSIS
    Регистрирует задачу Планировщика Windows для автозапуска DowntimeGreeting при входе пользователя.

.DESCRIPTION
    Создаёт (или пересоздаёт) задачу «DowntimeGreeting», которая запускает DowntimeGreeting.exe --greet
    после входа текущего пользователя в систему.

    Параметры задачи:
      * триггер AtLogOn для текущего пользователя с задержкой 2 секунды;
      * запуск от имени текущего пользователя без повышения прав (LeastPrivilege);
      * условия «только при подключении к сети» и «остановить при переходе на батарею» отключены;
      * ограничение времени выполнения — 5 минут.

    По умолчанию путь к DowntimeGreeting.exe определяется относительно этого скрипта
    (ожидается каталог сборки bin\Release\net10.0-windows).

.PARAMETER ExePath
    Путь к DowntimeGreeting.exe. Если не указан, ищется рядом со скриптом.

.PARAMETER TaskName
    Имя задачи в Планировщике. По умолчанию «DowntimeGreeting».

.EXAMPLE
    .\Install-Task.ps1
    Регистрирует задачу для DowntimeGreeting.exe, найденного рядом со скриптом.

.EXAMPLE
    .\Install-Task.ps1 -ExePath "D:\Apps\DowntimeGreeting\DowntimeGreeting.exe"
    Регистрирует задачу для приложения, установленного в другой каталог.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ExePath,

    [Parameter(Mandatory = $false)]
    [string]$TaskName = 'DowntimeGreeting'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-DowntimeGreetingPath {
    param([string]$RequestedPath)

    if ($RequestedPath) {
        $resolved = (Resolve-Path -LiteralPath $RequestedPath -ErrorAction SilentlyContinue)
        if (-not $resolved) {
            throw "Файл не найден: $RequestedPath"
        }
        return $resolved.Path
    }

    $candidates = @(
        (Join-Path -Path $PSScriptRoot -ChildPath 'DowntimeGreeting.exe'),
        (Join-Path -Path $PSScriptRoot -ChildPath 'bin\Release\net10.0-windows\DowntimeGreeting.exe'),
        (Join-Path -Path $PSScriptRoot -ChildPath 'bin\Debug\net10.0-windows\DowntimeGreeting.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "DowntimeGreeting.exe не найден. Укажите путь явно: .\Install-Task.ps1 -ExePath ""C:\путь\DowntimeGreeting.exe"""
}

function Set-TaskSettings {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Executable
    )

    $task = Get-ScheduledTask -TaskName $Name -ErrorAction Stop

    # «Выполнять только при подключении к сети» — выключено.
    if ($task.Settings.RunOnlyIfNetworkAvailable) {
        $task.Settings.RunOnlyIfNetworkAvailable = $false
    }

    # «Остановить при переходе на батарею» — выключено.
    if ($task.Settings.StopIfGoingOnBatteries) {
        $task.Settings.StopIfGoingOnBatteries = $false
    }

    # «Запускать только при питании от сети» — выключено (запуск и на батарее).
    if ($task.Settings.DisallowStartIfOnBatteries) {
        $task.Settings.DisallowStartIfOnBatteries = $false
    }

    Set-ScheduledTask -TaskName $Name -Settings $task.Settings | Out-Null
    Write-Host "  Условия питания и сети: выключены"
}

Write-Host "=== DowntimeGreeting: установка автозапуска ===" -ForegroundColor Cyan

$exeFullPath = Resolve-DowntimeGreetingPath -RequestedPath $ExePath
Write-Host "  Исполняемый файл: $exeFullPath"

$workingDirectory = Split-Path -Parent $exeFullPath
$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$action = New-ScheduledTaskAction -Execute $exeFullPath -Argument '--greet' -WorkingDirectory $workingDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$trigger.Delay = 'PT2S'

$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
    -MultipleInstances IgnoreNew

$settings.RunOnlyIfNetworkAvailable = $false
$settings.DisallowStartIfOnBatteries = $false
$settings.StopIfGoingOnBatteries = $false

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'Приветственная плашка с длительностью простоя компьютера при входе в Windows.' `
    -Force | Out-Null

Set-TaskSettings -Name $TaskName -Executable $exeFullPath

Write-Host ""
Write-Host "Готово. Задача «$TaskName» зарегистрирована для $currentUser." -ForegroundColor Green
Write-Host "Проверить: Get-ScheduledTask -TaskName $TaskName | Format-List"
Write-Host "Запустить сейчас: Start-ScheduledTask -TaskName $TaskName"