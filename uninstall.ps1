<#
    卸载 RefreshRateTray：结束进程、删除开机自启、删除程序目录。
    保留 HKCU\Software\RefreshRateTray 里的设置（适配器/电池目标刷新率）。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\RefreshRateTray'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process RefreshRateTray -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.CloseMainWindow() | Out-Null } catch { }
    Start-Sleep -Milliseconds 300
    if (-not $_.HasExited) { try { $_.Kill() } catch { } }
}

if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name 'RefreshRateTray' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name 'RefreshRateTray (Pocket)' -ErrorAction SilentlyContinue
    Write-Host '已移除开机自启'
}

if (Test-Path $target) {
    Remove-Item -Path $target -Recurse -Force
    Write-Host "已删除 $target"
}

Write-Host '卸载完成（设置保留在 HKCU\Software\RefreshRateTray）'