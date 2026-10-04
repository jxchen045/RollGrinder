<#
.SYNOPSIS
    一键全量测试：环境 → 构建 → 分模块单元/集成测试 → 三轮界面自检 → 汇总 → 打包成一个 zip 供上传分析。

.DESCRIPTION
    在装有 .NET 8 或更高 SDK（含 VS2026 自带的 10.0）的 Windows 桌面上运行（界面自检要真的把 WPF 窗口开出来，
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

.PARAMETER KeepStaleFiles
    发现旧版本残留的源文件时只报告、不挪走。默认会把它们挪进本次结果目录的 quarantine\ 再构建。

.PARAMETER UiPasses
    要跑的界面自检轮次，默认三轮全跑：sim（仿真全流程）、offline（离线模式）、en-US（英文界面渲染巡检）。
    另有两轮只做渲染巡检加版面体检（每页 / 每组 / 每个子视图走一遍，查被裁、半遮、透底、软键不齐），一轮一分钟左右：
    render（中文、-Layout 指定的档位）、compact（中文、紧凑档位）。只改了界面时跑 render,compact,en-US 就够了。

.PARAMETER UiTimeoutMinutes
    每一轮界面自检的超时（分钟）。

.PARAMETER Shots
    截图策略：key（默认：失败 + 每页 / 子视图 / 菜单态各一张，画面没变的不重复存）、
    fail（只截失败的步骤，包最小）、all（另外把巡检里每按一个键都截一张，排查具体按键时才用）。
    offline 一轮的页面与 sim 一样，除非 -Shots all，否则只截失败的步骤。

.PARAMETER MaxShots
    每一轮最多存几张截图（失败的步骤不受限）。默认 80；en-US 渲染巡检一轮最多 50。
    截图按 1440×810、JPEG 65 存，一张约 100 KB；默认三轮的包约 10 MB，-Shots fail 约 2 MB。

.PARAMETER Layout
    界面档位：standard（1920×1080，默认）或 compact（1366×768）。固定档位，截图在任何屏幕上都是同一尺寸。

.PARAMETER IncludeData
    把每一轮的自检数据库也打进 zip（复现问题时用）。默认不带：日志与结果已足够分析，数据库只会让包变大。

.PARAMETER AllowNonInteractive
    没有交互桌面也照样跑界面自检（云端 Windows 构建机用）。截图按画布原尺寸画，不依赖屏幕。

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
    [switch] $KeepStaleFiles,
    [ValidateSet('sim', 'offline', 'en-US', 'render', 'compact')]
    [string[]] $UiPasses = @('sim', 'offline', 'en-US'),
    [int] $UiTimeoutMinutes = 25,
    [ValidateSet('key', 'fail', 'all')]
    [string] $Shots = 'key',
    [ValidateRange(0, 5000)]
    [int] $MaxShots = 80,
    [ValidateSet('standard', 'compact')]
    [string] $Layout = 'standard',
    [switch] $IncludeData,
    [switch] $AllowNonInteractive,
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
$envLines.Add("params           : SimSpeed=$SimSpeed SkipBuild=$SkipBuild SkipUnitTests=$SkipUnitTests SkipUi=$SkipUi KeepStaleFiles=$KeepStaleFiles UiPasses=$($UiPasses -join ',') Shots=$Shots MaxShots=$MaxShots Layout=$Layout IncludeData=$IncludeData")
$envLines | Set-Content -Path (Join-Path $Run 'environment.txt') -Encoding UTF8
$envLines | ForEach-Object { Write-Host "  $_" }

# ─── 0b. 源码核对：有没有旧版本残留的文件 ─────────────────────────────────────────
# 把新源码 zip 解压覆盖到旧目录上，只会新增和覆盖、不会删除——早已删掉的旧文件还躺在那里，
# 与新文件里的同名类型撞车，构建报一串 CS0101 / CS0111（第三轮测试就是这么失败的）。
#
# 判断依据：本目录就是 git 仓库的根时，看未跟踪的源文件；否则对照源码 zip 附带的 SOURCE-MANIFEST.txt。
# （只认"本目录是仓库根"：目录若只是躺在别的 git 仓库里面，那个仓库会把全部源码都当成未跟踪。）
#
# 处理：默认把残留**挪进本次结果目录的 quarantine\**（原路径保留，可原样放回），然后照常构建、
# 单元测试、界面自检——不因为残留就把后面的测试整段跳过。-KeepStaleFiles 时只报告、不挪。
function Get-NormalizedPath([string] $Path) {
    return ([System.IO.Path]::GetFullPath($Path)).TrimEnd('\', '/').ToLowerInvariant()
}

$sourcePattern = '^(src|tests)/.+\.(cs|xaml|csproj|props|targets|resx)$'
$manifest = Join-Path $RepoRoot 'SOURCE-MANIFEST.txt'
$stale = @()
$sourceCheck = $null
$gitTop = Try-Run { git -C $RepoRoot rev-parse --show-toplevel } ''
if ($gitTop -and ((Get-NormalizedPath $gitTop) -eq (Get-NormalizedPath $RepoRoot))) {
    $stale = @(git -C $RepoRoot ls-files --others --exclude-standard -- src tests | Where-Object { $_ -match $sourcePattern })
    $sourceCheck = 'git'
}
elseif (Test-Path $manifest) {
    $known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    Get-Content $manifest -Encoding UTF8 | ForEach-Object { [void]$known.Add($_.Trim()) }
    $prefixLength = ([System.IO.Path]::GetFullPath($RepoRoot)).TrimEnd('\', '/').Length + 1
    $stale = @(Get-ChildItem -Path (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName.Substring($prefixLength).Replace('\', '/') } |
        Where-Object { $_ -match $sourcePattern -and -not $known.Contains($_) })
    $sourceCheck = 'SOURCE-MANIFEST.txt'
}

if ($null -eq $sourceCheck) {
    Add-Result 'SourceCheck' 'SKIP' 'not a git repository root and no SOURCE-MANIFEST.txt: leftover files cannot be detected'
}
elseif ($stale.Count -eq 0) {
    Add-Result 'SourceCheck' 'PASS' "no leftover files ($sourceCheck)"
}
else {
    $stale | Set-Content -Path (Join-Path $Run 'stale-files.txt') -Encoding UTF8
    $shown = ($stale | Select-Object -First 10) -join ', '
    if ($KeepStaleFiles) {
        Add-Result 'SourceCheck' 'WARN' ("{0} leftover file(s) from an older copy kept (-KeepStaleFiles); the build will probably fail: {1}" -f $stale.Count, $shown)
    }
    else {
        $quarantine = Join-Path $Run 'quarantine'
        foreach ($relative in $stale) {
            $target = Join-Path $quarantine ($relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Move-Item -LiteralPath (Join-Path $RepoRoot $relative) -Destination $target -Force
        }
        Add-Result 'SourceCheck' 'WARN' ("moved {0} leftover file(s) from an older copy to quarantine\ (paths kept; move back to restore): {1}" -f $stale.Count, $shown)
        Write-Host ''
        Write-Host "  以下文件不属于这一版源码（旧版本残留），已挪到 $quarantine" -ForegroundColor Yellow
        $stale | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
        Write-Host '  下次请把源码解压到空文件夹，或用 git bundle 更新。' -ForegroundColor Yellow
        Write-Host ''
    }
}

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

    # 截图策略：offline 的页面与 sim 一样，只截失败的；其余按 -Shots。
    $passShots = $Shots
    if ($Pass -eq 'offline' -and $Shots -ne 'all') { $passShots = 'fail' }
    $passMax = $MaxShots
    if ($Pass -in 'en-US', 'render', 'compact') { $passMax = [math]::Min($MaxShots, 50) }
    $arguments = @('--selftest', '--selftest-label', $Pass, '--data', $data, '--config', $config, '--selftest-out', $result,
        '--selftest-shots', $passShots, '--selftest-max-shots', "$passMax")

    # 每一轮都放一份 hmi.json：固定界面档位（截图尺寸不随屏幕变）、自检不全屏；en-US 一轮再换语言。
    $hmi = Get-Content (Join-Path $RepoRoot 'config\hmi.sample.json') -Raw -Encoding UTF8
    $passLayout = if ($Pass -eq 'compact') { 'compact' } else { $Layout }
    $hmi = $hmi -replace '"layout"\s*:\s*"[^"]*"', ('"layout": "' + $passLayout + '"')
    $hmi = $hmi -replace '"fullScreen"\s*:\s*(true|false)', '"fullScreen": false'
    if ($Pass -eq 'en-US') { $hmi = $hmi -replace '"culture"\s*:\s*"[^"]*"', '"culture": "en-US"' }
    [System.IO.File]::WriteAllText((Join-Path $config 'hmi.json'), $hmi, (New-Object System.Text.UTF8Encoding $false))

    switch ($Pass) {
        'sim' { $arguments += @('--gateway', 'sim', '--sim-speed', "$SimSpeed", '--selftest-scope', 'full') }
        'offline' { $arguments += @('--offline', '--selftest-scope', 'full') }
        { $_ -in 'en-US', 'render', 'compact' } { $arguments += @('--gateway', 'sim', '--sim-speed', "$SimSpeed", '--selftest-scope', 'render') }
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

    # 自检数据库只有自检自己造的数据，日志与结果已足够分析；默认不打包（-IncludeData 才留）。
    if (-not $IncludeData -and (Test-Path $data)) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }

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
elseif (-not $buildOk) {
    # 构建失败时 bin 里可能还躺着上一次的 exe——拿旧程序跑自检只会得出误导的结论，所以不跑；
    # 记 FAIL 而不是 SKIP：界面自检没跑成是要处理的问题，不能在汇总里显得"一切正常只是略过"。
    Add-Result 'UI' 'FAIL' 'NOT RUN: the build failed (see Build / build.log); an older RollGrinder.App.exe in bin would give misleading results'
}
elseif (-not (Test-Path $exe)) {
    Add-Result 'UI' 'FAIL' "RollGrinder.App.exe not found under $binDir (build first)"
}
elseif (-not [Environment]::UserInteractive -and -not $AllowNonInteractive) {
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
$md.Add("- screenshots: policy $Shots, max $MaxShots per pass, layout $Layout; data\ databases $(if ($IncludeData) { 'included' } else { 'left out (-IncludeData to keep)' })")
$md | Set-Content -Path (Join-Path $Run 'SUMMARY.md') -Encoding UTF8
$md | ForEach-Object { Write-Host "  $_" }

# ─── 5. 打包 ──────────────────────────────────────────────────────────────────
Write-Stage '5/5  Package'
$zip = Join-Path $ResultsRoot "RollGrinder-TestRun-$Stamp.zip"
Compress-Archive -Path (Join-Path $Run '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)

# 体积账：哪一块占得多一目了然；超过 15 MB 提示怎么减。
$sizes = Get-ChildItem -Path $Run -Directory | ForEach-Object {
    $bytes = (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    foreach ($sub in @(Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue)) {
        $subBytes = (Get-ChildItem $sub.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        [pscustomobject]@{ Part = "$($_.Name)\$($sub.Name)"; MB = [math]::Round($subBytes / 1MB, 1) }
    }
    [pscustomobject]@{ Part = $_.Name; MB = [math]::Round($bytes / 1MB, 1) }
} | Sort-Object MB -Descending | Select-Object -First 8
$sizes | ForEach-Object { Write-Host ("    {0,6} MB  {1}" -f $_.MB, $_.Part) }
$shotCount = @(Get-ChildItem -Path $Run -Recurse -Filter '*.jpg' -ErrorAction SilentlyContinue).Count
Write-Host "  screenshots: $shotCount (policy $Shots, max $MaxShots per pass)"
if ($sizeMb -gt 15) {
    Write-Host "  包偏大：可加 -Shots fail 只截失败的步骤，或 -MaxShots 60，或 -UiPasses sim 只跑一轮。" -ForegroundColor Yellow
}
Write-Host ''
Write-Host "  结论 / verdict : $verdict" -ForegroundColor $(if ($verdict -eq 'FAIL') { 'Red' } elseif ($verdict -eq 'PASS') { 'Green' } else { 'Yellow' })
Write-Host "  请上传这个文件 / upload this file ($sizeMb MB):" -ForegroundColor Cyan
Write-Host "  $zip" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
exit 0
