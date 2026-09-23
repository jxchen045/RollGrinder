<#
.SYNOPSIS
    一键全量测试：环境 → 构建 → 分模块单元/集成测试 → 三轮界面自检 → 汇总 → 打包成一个 zip 供上传分析。

.DESCRIPTION
    在装有 .NET 8 SDK 的 Windows 桌面上运行（界面自检要真的把 WPF 窗口开出来，
    所以必须在有桌面会话的登录用户下跑，不能在远程无界面会话或服务里跑）。

    界面自检只对仿真/离线机床跑，并且用每一轮新建的专用数据目录——
    不会碰现场的 config\ 与 data\，也不会连真机床。

    产物全部在 TestResults\run-<时间戳>\ 下，最后打成 TestResults\RollGrinder-TestRun-<时间戳>.zip。
    把这个 zip 上传即可。

.PARAMETER SimSpeed
    仿真时间倍率（1–100）。默认 20：一支辊约 15 秒磨完。

.PARAMETER SkipBuild
    跳过构建（已经构建过时用）。

.PARAMETER SkipUnitTests
    跳过单元/集成测试。

.PARAMETER SkipUi
    跳过界面自检。

.PARAMETER UiPasses
    要跑的界面自检轮次，默认三轮全跑：sim（仿真全流程）、offline（离线模式）、en-US（英文界面渲染巡检）。

.PARAMETER UiTimeoutMinutes
    每一轮界面自检的超时（分钟）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-full-test.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-full-test.ps1 -SkipBuild -UiPasses sim
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [double] $SimSpeed = 20,
    [switch] $SkipBuild,
    [switch] $SkipUnitTests,
    [switch] $SkipUi,
    [ValidateSet('sim', 'offline', 'en-US')]
    [string[]] $UiPasses = @('sim', 'offline', 'en-US'),
    [int] $UiTimeoutMinutes = 25,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# 子进程输出统一按 UTF-8 读写；MSBuild/dotnet 的提示用英文，方便分析。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$ResultsRoot = Join-Path $RepoRoot 'TestResults'
$Run = Join-Path $ResultsRoot "run-$Stamp"
New-Item -ItemType Directory -Force -Path $Run | Out-Null

$Summary = New-Object System.Collections.Generic.List[object]
$ScriptStarted = Get-Date

function Write-Stage([string] $Text) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
}

function Add-Result([string] $Stage, [string] $Status, [string] $Detail) {
    $Summary.Add([pscustomobject]@{ Stage = $Stage; Status = $Status; Detail = $Detail })
    $color = 'Green'
    if ($Status -eq 'FAIL') { $color = 'Red' } elseif ($Status -ne 'PASS') { $color = 'Yellow' }
    Write-Host ("  [{0}] {1}  {2}" -f $Status, $Stage, $Detail) -ForegroundColor $color
}

# 运行一个命令：屏幕上实时显示，同时按 UTF-8 存进日志。返回退出码。
function Invoke-Logged([string] $Exe, [string[]] $Arguments, [string] $LogPath) {
    "> $Exe $($Arguments -join ' ')" | Set-Content -Path $LogPath -Encoding UTF8
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $captured = @()
    try {
        & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -Variable captured | Write-Host
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($captured) { $captured | Add-Content -Path $LogPath -Encoding UTF8 }
    "exit code: $code" | Add-Content -Path $LogPath -Encoding UTF8
    return $code
}

function Try-Run([scriptblock] $Block, [string] $Fallback = '?') {
    try { $value = & $Block; if ($null -eq $value) { return $Fallback }; return ($value | Out-String).Trim() }
    catch { return $Fallback }
}

# ─── 0. 环境 ──────────────────────────────────────────────────────────────────
Write-Stage '0/5  Environment'
$commit = Try-Run { git -C $RepoRoot rev-parse HEAD }
$env:ROLLGRINDER_COMMIT = $commit
$envLines = New-Object System.Collections.Generic.List[string]
$envLines.Add("started          : $(Get-Date -Format o)")
$envLines.Add("repo             : $RepoRoot")
$envLines.Add("git.commit       : $commit")
$envLines.Add("git.branch       : $(Try-Run { git -C $RepoRoot rev-parse --abbrev-ref HEAD })")
$envLines.Add("git.dirtyFiles   : $(Try-Run { (git -C $RepoRoot status --porcelain | Measure-Object).Count })")
$envLines.Add("git.lastCommit   : $(Try-Run { git -C $RepoRoot log -1 --format='%h %ci %s' })")
$envLines.Add("os               : $(Try-Run { (Get-CimInstance Win32_OperatingSystem).Caption + ' ' + (Get-CimInstance Win32_OperatingSystem).Version })")
$envLines.Add("powershell       : $($PSVersionTable.PSVersion)")
$envLines.Add("culture          : $((Get-Culture).Name) / ui $((Get-UICulture).Name)")
$envLines.Add("cpu              : $(Try-Run { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name })")
$envLines.Add("memoryGB         : $(Try-Run { [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1) })")
$envLines.Add("screens          : $(Try-Run { Add-Type -AssemblyName System.Windows.Forms; ([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { '{0}x{1}{2}' -f $_.Bounds.Width, $_.Bounds.Height, $(if ($_.Primary) { '*' } else { '' }) }) -join ', ' })")
$envLines.Add("interactive      : $([Environment]::UserInteractive)")
$envLines.Add("dotnet.sdks      : $(Try-Run { (dotnet --list-sdks) -join '; ' })")
$envLines.Add("dotnet.runtimes  : $(Try-Run { ((dotnet --list-runtimes) | Where-Object { $_ -match 'WindowsDesktop|NETCore.App' }) -join '; ' })")
$envLines.Add("params           : SimSpeed=$SimSpeed SkipBuild=$SkipBuild SkipUnitTests=$SkipUnitTests SkipUi=$SkipUi UiPasses=$($UiPasses -join ',')")
$envLines | Set-Content -Path (Join-Path $Run 'environment.txt') -Encoding UTF8
$envLines | ForEach-Object { Write-Host "  $_" }

# ─── 1. 构建 ──────────────────────────────────────────────────────────────────
Write-Stage '1/5  Build'
$solution = Join-Path $RepoRoot 'RollGrinder.sln'
$buildOk = $true
if ($SkipBuild) {
    Add-Result 'Build' 'SKIP' 'skipped by -SkipBuild'
}
else {
    $code = Invoke-Logged 'dotnet' @('build', $solution, '-c', $Configuration, '-nologo', '-v', 'minimal') (Join-Path $Run 'build.log')
    $buildText = Get-Content (Join-Path $Run 'build.log') -Raw
    $warnings = ([regex]::Matches($buildText, ': warning [A-Z]+\d+')).Count
    $errors = ([regex]::Matches($buildText, ': error [A-Z]+\d+')).Count
    if ($code -eq 0) {
        Add-Result 'Build' 'PASS' "warnings=$warnings"
    }
    else {
        $buildOk = $false
        Add-Result 'Build' 'FAIL' "exit=$code errors=$errors warnings=$warnings (see build.log)"
    }
}

# ─── 2. 单元 / 集成测试（逐个测试工程） ─────────────────────────────────────────
Write-Stage '2/5  Unit and integration tests (per module)'
$unitDir = Join-Path $Run 'unit'
New-Item -ItemType Directory -Force -Path $unitDir | Out-Null
if ($SkipUnitTests) {
    Add-Result 'UnitTests' 'SKIP' 'skipped by -SkipUnitTests'
}
elseif (-not $buildOk) {
    Add-Result 'UnitTests' 'SKIP' 'build failed'
}
else {
    $projects = Get-ChildItem -Path (Join-Path $RepoRoot 'tests') -Filter '*.csproj' -Recurse | Sort-Object Name
    foreach ($project in $projects) {
        $name = $project.BaseName
        $log = Join-Path $unitDir "$name.log"
        $code = Invoke-Logged 'dotnet' @(
            'test', $project.FullName, '-c', $Configuration, '--no-build',
            '--logger', "trx;LogFileName=$name.trx",
            '--logger', 'console;verbosity=normal',
            '--results-directory', $unitDir) $log

        # 从 TRX 取精确计数与失败用例名。
        $trx = Join-Path $unitDir "$name.trx"
        if (Test-Path $trx) {
            [xml] $doc = Get-Content $trx -Raw -Encoding UTF8
            $counters = $doc.TestRun.ResultSummary.Counters
            $failedNames = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName })
            $detail = "total=$($counters.total) passed=$($counters.passed) failed=$($counters.failed) skipped=$($counters.notExecuted)"
            if ($failedNames.Count -gt 0) {
                $detail += ' | failed: ' + (($failedNames | Select-Object -First 10) -join '; ')
                $failedNames | Set-Content -Path (Join-Path $unitDir "$name.failed.txt") -Encoding UTF8
            }

            $status = 'PASS'
            if ([int] $counters.failed -gt 0 -or $code -ne 0) { $status = 'FAIL' }
            Add-Result "UnitTests/$name" $status $detail
        }
        else {
            Add-Result "UnitTests/$name" 'FAIL' "exit=$code, no TRX produced (see unit\$name.log)"
        }
    }
}

# ─── 3. 界面自检（真的把 WPF 窗口开出来） ────────────────────────────────────────
Write-Stage '3/5  UI self-test (real WPF window, simulated/offline machine)'
$uiDir = Join-Path $Run 'ui'
New-Item -ItemType Directory -Force -Path $uiDir | Out-Null

$binDir = Join-Path $RepoRoot "src\RollGrinder.App\bin\$Configuration\net8.0-windows"
$exe = Join-Path $binDir 'RollGrinder.App.exe'

function Invoke-UiPass([string] $Pass) {
    $passDir = Join-Path $uiDir $Pass
    $data = Join-Path $passDir 'data'
    $config = Join-Path $passDir 'config'
    $result = Join-Path $passDir 'result'
    New-Item -ItemType Directory -Force -Path $config | Out-Null

    $arguments = @('--selftest', '--selftest-label', $Pass, '--data', $data, '--config', $config, '--selftest-out', $result)
    switch ($Pass) {
        'sim' { $arguments += @('--gateway', 'sim', '--sim-speed', "$SimSpeed", '--selftest-scope', 'full') }
        'offline' { $arguments += @('--offline', '--selftest-scope', 'full') }
        'en-US' {
            # 英文界面：先放一份 culture=en-US 的 hmi.json，其余配置照常从模板生成。
            $sample = Get-Content (Join-Path $RepoRoot 'config\hmi.sample.json') -Raw -Encoding UTF8
            $english = $sample -replace '"culture"\s*:\s*"[^"]*"', '"culture": "en-US"'
            [System.IO.File]::WriteAllText((Join-Path $config 'hmi.json'), $english, (New-Object System.Text.UTF8Encoding $false))
            $arguments += @('--gateway', 'sim', '--sim-speed', "$SimSpeed", '--selftest-scope', 'render')
        }
    }

    $quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    "RollGrinder.App.exe $($quoted -join ' ')" | Set-Content -Path (Join-Path $passDir 'command.txt') -Encoding UTF8
    Write-Host "  starting pass '$Pass' ..."
    $started = Get-Date
    $process = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru -WorkingDirectory $binDir `
        -RedirectStandardError (Join-Path $passDir 'stderr.txt') -RedirectStandardOutput (Join-Path $passDir 'stdout.txt')
    # 先取一次句柄：Start-Process 不缓存句柄的话，进程退出后读不到 ExitCode。
    $null = $process.Handle
    $finished = $process.WaitForExit($UiTimeoutMinutes * 60 * 1000)
    if (-not $finished) {
        try { $process.Kill() } catch { }
        Add-Result "UI/$Pass" 'FAIL' "timed out after $UiTimeoutMinutes min and was killed (partial log kept)"
    }
    $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds)

    # 应用自己的运行日志（Serilog）一并带走：崩溃堆栈在这里面。
    $appLogs = Join-Path $data 'logs'
    if (Test-Path $appLogs) { Copy-Item $appLogs (Join-Path $passDir 'app-logs') -Recurse -Force }

    if (-not $finished) { return }
    $exit = $process.ExitCode
    $summaryPath = Join-Path $result 'summary.json'
    if (Test-Path $summaryPath) {
        $s = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $detail = "exit=$exit steps=$($s.Total) pass=$($s.Passed) warn=$($s.Warned) fail=$($s.Failed) skip=$($s.Skipped) ${seconds}s"
        if ($s.Aborted) { $detail += " ABORTED: $($s.AbortReason)" }
        if ($s.Failed -gt 0) { $detail += ' | ' + (($s.FailedSteps | Select-Object -First 5) -join ' || ') }
        $status = 'PASS'
        if ($exit -ne 0 -or $s.Failed -gt 0 -or $s.Aborted) { $status = 'FAIL' } elseif ($s.Warned -gt 0) { $status = 'WARN' }
        Add-Result "UI/$Pass" $status $detail
    }
    else {
        $err = ''
        if (Test-Path (Join-Path $passDir 'stderr.txt')) { $err = (Get-Content (Join-Path $passDir 'stderr.txt') -Raw) }
        Add-Result "UI/$Pass" 'FAIL' "exit=$exit, no summary.json (app did not finish; see app-logs, stderr.txt) $err"
    }
}

if ($SkipUi) {
    Add-Result 'UI' 'SKIP' 'skipped by -SkipUi'
}
elseif (-not (Test-Path $exe)) {
    Add-Result 'UI' 'FAIL' "RollGrinder.App.exe not found under $binDir (build first)"
}
elseif (-not [Environment]::UserInteractive) {
    Add-Result 'UI' 'SKIP' 'no interactive desktop session: run this script from a logged-in desktop'
}
else {
    foreach ($pass in $UiPasses) { Invoke-UiPass $pass }
}

# ─── 4. 汇总 ──────────────────────────────────────────────────────────────────
Write-Stage '4/5  Summary'
$failed = @($Summary | Where-Object { $_.Status -eq 'FAIL' }).Count
$warned = @($Summary | Where-Object { $_.Status -eq 'WARN' }).Count
$verdict = 'PASS'
if ($failed -gt 0) { $verdict = 'FAIL' } elseif ($warned -gt 0) { $verdict = 'PASS WITH WARNINGS' }

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# RollGrinder test run $Stamp")
$md.Add('')
$md.Add("- verdict: **$verdict**")
$md.Add("- commit: $commit")
$md.Add("- duration: $([math]::Round(((Get-Date) - $ScriptStarted).TotalMinutes, 1)) min")
$md.Add('')
$md.Add('| stage | status | detail |')
$md.Add('|---|---|---|')
foreach ($r in $Summary) { $md.Add("| $($r.Stage) | $($r.Status) | $($r.Detail -replace '\|', '/') |") }
$md.Add('')
$md.Add('## Files')
$md.Add('- environment.txt, build.log')
$md.Add('- unit\<project>.log / .trx / .failed.txt')
$md.Add('- ui\<pass>\result\selftest.log (readable), selftest.jsonl (per step), summary.json, screenshots\, files\, prints\')
$md.Add('- ui\<pass>\app-logs\ (application log incl. stack traces)')
$md | Set-Content -Path (Join-Path $Run 'SUMMARY.md') -Encoding UTF8
$md | ForEach-Object { Write-Host "  $_" }

# ─── 5. 打包 ──────────────────────────────────────────────────────────────────
Write-Stage '5/5  Package'
$zip = Join-Path $ResultsRoot "RollGrinder-TestRun-$Stamp.zip"
# 自检数据库里只有自检自己造的数据；一并打包，便于复现。
Compress-Archive -Path (Join-Path $Run '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "  结论 / verdict : $verdict" -ForegroundColor $(if ($verdict -eq 'FAIL') { 'Red' } elseif ($verdict -eq 'PASS') { 'Green' } else { 'Yellow' })
Write-Host "  请上传这个文件 / upload this file ($sizeMb MB):" -ForegroundColor Cyan
Write-Host "  $zip" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
exit 0
