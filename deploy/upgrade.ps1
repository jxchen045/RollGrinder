<#
.SYNOPSIS
    就地升级机床工控机上的上位机，保留现场配置与数据。

.DESCRIPTION
    程序目录下的 config\ 与 data\ 是现场资产：
      - config\*.json  由调试人员按本台机床改过，升级绝不覆盖；
      - data\          数据库与日志，升级绝不删除。
    升级只替换程序文件与 config\*.sample.json 模板。
    首次启动时，程序会把缺失的 config\*.json 从对应模板复制一份。

.PARAMETER SourceDirectory
    publish.ps1 产出的目录。

.PARAMETER TargetDirectory
    机床上的安装目录。
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $SourceDirectory,
    [Parameter(Mandatory = $true)] [string] $TargetDirectory
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SourceDirectory)) {
    throw "找不到发布目录：$SourceDirectory"
}

if (Get-Process -Name 'RollGrinder.App' -ErrorAction SilentlyContinue) {
    throw '上位机正在运行，请先退出再升级。（注意：即便强行结束，当前这支辊仍由 NC 继续磨完。）'
}

New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null

# 备份现场配置，升级失败也能回到原状。
$backupDirectory = Join-Path $TargetDirectory ("config-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$liveConfig = Join-Path $TargetDirectory 'config'
if (Test-Path $liveConfig) {
    Copy-Item $liveConfig $backupDirectory -Recurse
    Write-Host "已备份现场配置：$backupDirectory"
}

# 只替换程序文件：config\*.json 与 data\ 原样保留。
robocopy $SourceDirectory $TargetDirectory /E /XD data /XF *.json.bak /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "复制失败，robocopy 退出码 $LASTEXITCODE"
}

Write-Host '升级完成。现场的 config\*.json 与 data\ 未被改动。'
