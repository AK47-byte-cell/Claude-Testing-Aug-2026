# Registers a Scheduled Task that starts soundcore-sync (hidden) whenever you log on,
# and restarts it if it stops. Run from PowerShell:
#   powershell -ExecutionPolicy Bypass -File install\windows\install-task.ps1
param(
    [string]$TaskName = "soundcore-sync",
    [string]$Python = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if (-not $Python) {
    $candidate = Join-Path $root ".venv\Scripts\pythonw.exe"
    if (Test-Path $candidate) { $Python = $candidate } else { $Python = "pythonw.exe" }
}
$action   = New-ScheduledTaskAction -Execute $Python -Argument "-m soundcore_sync run" -WorkingDirectory $root
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 `
            -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -MultipleInstances IgnoreNew `
            -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' installed and started. Log: $env:USERPROFILE\.soundcore-sync\soundcore-sync.log"
