<#
    把 RefreshRateTray 安装到当前用户目录（无需管理员权限）：
        %LOCALAPPDATA%\Programs\RefreshRateTray

    用法：
        .\install.ps1                 只安装
        .\install.ps1 -Autostart      安装并加入开机自启
        .\install.ps1 -Start          安装后立即启动托盘程序

    注意：请在工作区之外运行这些 exe。DSH 沙箱会禁止位于
    C:\Users\lin\Documents\deepseek-harness\default-workspace 下的程序
    修改显示设置，所以要把程序复制到别处再用。
#>
[CmdletBinding()]
param(
    [switch]$Autostart,
    [switch]$Start
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $here 'dist'
$target = Join-Path $env:LOCALAPPDATA 'Programs\RefreshRateTray'

if (-not (Test-Path (Join-Path $dist 'RefreshRateTray.exe'))) {
    throw "找不到 $dist\RefreshRateTray.exe，请先运行 build.ps1"
}

# 已运行的实例先退出（单实例 Mutex 会阻止新实例接管）
Get-Process RefreshRateTray -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.CloseMainWindow() | Out-Null } catch { }
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $dist 'RefreshRateTray.exe') $target -Force
Copy-Item (Join-Path $dist 'RefreshRateTray.cli.exe') $target -Force
Copy-Item (Join-Path $here 'README.md') $target -Force -ErrorAction SilentlyContinue

$exe = Join-Path $target 'RefreshRateTray.exe'
Write-Host "已安装到: $target"

if ($Autostart) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'RefreshRateTray' -Value ('"' + $exe + '"')
    Write-Host '已加入开机自启（HKCU\...\Run\RefreshRateTray）'
}

if ($Start) {
    Start-Process -FilePath $exe
    Write-Host '已启动，托盘区应出现显示当前刷新率的小图标。'
}