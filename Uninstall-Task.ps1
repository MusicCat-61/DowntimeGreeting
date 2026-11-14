<#
.SYNOPSIS
    Удаляет задачу Планировщика Windows, созданную Install-Task.ps1.

.DESCRIPTION
    Удаляет задачу «DowntimeGreeting» из Планировщика Windows. Если задачи нет,
    скрипт сообщает об этом и завершается без ошибки.

.PARAMETER TaskName
    Имя задачи в Планировщике. По умолчанию «DowntimeGreeting».

.EXAMPLE
    .\Uninstall-Task.ps1
    Удаляет задачу «DowntimeGreeting».
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$TaskName = 'DowntimeGreeting'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "=== DowntimeGreeting: удаление автозапуска ===" -ForegroundColor Cyan

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Задача «$TaskName» не найдена — удалять нечего." -ForegroundColor Yellow
    return
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host ""
Write-Host "Задача «$TaskName» удалена. Автозагрузка отключена." -ForegroundColor Green