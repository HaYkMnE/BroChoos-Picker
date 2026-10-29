<#
.SYNOPSIS
    Console smoke test for the CURRENT --no-ui contract: `--no-ui <url>` is refused
    fail-closed (exit 1) and opens nothing.

.DESCRIPTION
    THE CONTRACT THIS SCRIPT ASSERTS (W1.1 fail-closed, src\BroChoos.Core\Runners\UrlRunner.cs,
    Primary branch). --no-ui never performs browser discovery or launch: an explicit browser
    choice is required and auto-selecting one is not allowed. A primary instance therefore
    writes "No-UI activation requires an explicit browser choice; nothing was opened." to
    stderr and returns ExitCodes.GeneralError (1). Bootstrap prints "[startup] INGRESS_ACCEPTED"
    on stdout before handing the request to UrlRunner.

    Case 1 : runs BroChoos --no-ui <url> and expects exit 1, the refusal notice on stderr,
             no "Opening in <browser>" on stdout, and NO "[secondary]" forward line. The last
             check matters: a secondary instance that forwards to an already-running primary
             exits 0 and opens nothing itself, so without it a stray primary would hide the
             real contract.

    REMOVED CHECKS (obsolete since the W1.1 fail-closed contract, see UrlRunner.cs):
      - "positive launch, expect exit 0 and 'Opening in <browser>'": --no-ui no longer launches
        anything, so exit 0 is no longer a valid outcome for a primary.
      - "no-browser (BROCHOOS_TEST_NO_BROWSERS=1), expect exit 4 NoBrowserDetected": --no-ui
        returns before detection runs (startup detection is deferred, UrlRunner.BuildSnapshot),
        so forcing detection empty cannot change the outcome and exit 4 is unreachable here.
        That override and its Debug-only requirement are therefore no longer used.

    The script only reads exit codes and stdout/stderr. It opens no browser window and
    changes no machine state.

    This file is deliberately pure ASCII: Windows PowerShell 5.1 reads a .ps1 saved
    as UTF-8 without BOM using the ANSI code page, which corrupts non-ASCII text.
#>

[CmdletBinding()]
param(
    [string] $Url = 'https://example.com',
    [string] $Configuration = 'Debug',
    [switch] $RenderEvidenceOnly,
    [switch] $UnsafePassThrough,
    [string] $SyntheticRepoRoot,
    [string] $SyntheticUrl,
    [string] $SyntheticExePath,
    [string] $SyntheticStdOut,
    [string] $SyntheticStdErr,
    [string] $SyntheticLogPath,
    [string] $SyntheticBackupPath,
    [string] $SyntheticTempPath,
    [string] $SyntheticUserChoice,
    [string] $SyntheticAccount,
    [string] $SyntheticProfile,
    [string] $SyntheticToken
)

if ($RenderEvidenceOnly) {
    Import-Module (Join-Path $PSScriptRoot 'SmokeEvidenceRenderer.psm1') -Force
    Write-SmokeEvidenceRender -UnsafePassThrough:$UnsafePassThrough -Fields @{
        Script       = 'smoke-launch.ps1'
        Mode         = 'RenderEvidenceOnly'
        repoRoot     = $SyntheticRepoRoot
        Url          = $SyntheticUrl
        exePath      = $SyntheticExePath
        StdOut       = $SyntheticStdOut
        StdErr       = $SyntheticStdErr
        UserChoice   = $SyntheticUserChoice
        logPath      = $SyntheticLogPath
        backupPath   = $SyntheticBackupPath
        tempPath     = $SyntheticTempPath
        account      = $SyntheticAccount
        profile      = $SyntheticProfile
        token        = $SyntheticToken
    }
    exit 0
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'SmokeEvidenceRenderer.psm1') -Force

function Protect-EvidenceText {
    param([string] $Value)
    return (Protect-SmokeEvidenceValue -Value $Value)
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\BroChoos.App\BroChoos.App.csproj'
$script:failures = 0

function Write-Result {
    param(
        [string] $Name,
        [bool] $Ok,
        [string] $Detail
    )

    if ($Ok) {
        Write-Host "PASS: $Name - $Detail"
    }
    else {
        Write-Host "FAIL: $Name - $Detail"
        $script:failures = $script:failures + 1
    }
}

function Format-Stream {
    param([string] $Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return '(empty)'
    }

    $singleLine = $Text -replace "`r", '' -replace "`n", ' | '
    return $singleLine.Trim()
}

Write-Host '=== BroChoos --no-ui fail-closed console smoke ==='
Write-Host ("Repo root : {0}" -f (Protect-EvidenceText -Value $repoRoot))
Write-Host ("URL       : {0}" -f (Protect-EvidenceText -Value $Url))
Write-Host "Config    : $Configuration"
Write-Host ''

# Build once, then invoke the produced executable directly so the measured exit
# code is the application's own and not the build driver's.
Write-Host "Building ($Configuration)..."
& dotnet build $appProject --configuration $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: build - dotnet build exited with $LASTEXITCODE"
    exit 1
}

# Ask MSBuild where the build actually wrote the assembly instead of searching bin\
# for the newest BroChoos.exe. That search was ambiguous: this repo sets Platforms=x64,
# so both bin\Debug\... and bin\x64\Debug\... can hold an exe, and "newest by
# LastWriteTime" could pick a stale artifact from an unrelated build.
$targetDll = (& dotnet msbuild $appProject -getProperty:TargetPath -p:Configuration=$Configuration -nologo) 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: locate executable - could not read TargetPath from MSBuild"
    exit 1
}

$targetDll = ($targetDll | Out-String).Trim()
$exePath = [IO.Path]::ChangeExtension($targetDll, '.exe')

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Host ("FAIL: locate executable - expected build output was not found at {0}" -f (Protect-EvidenceText -Value $exePath))
    exit 1
}

Write-Host ("Executable: {0}" -f (Protect-EvidenceText -Value $exePath))
Write-Host ''

function Invoke-BroChoos {
    param(
        [string[]] $Arguments
    )

    $stdoutFile = [IO.Path]::GetTempFileName()
    $stderrFile = [IO.Path]::GetTempFileName()

    try {
        $process = Start-Process -FilePath $exePath -ArgumentList $Arguments -Wait -PassThru `
            -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile -NoNewWindow

        $out = Get-Content -Raw -Path $stdoutFile -ErrorAction SilentlyContinue
        $err = Get-Content -Raw -Path $stderrFile -ErrorAction SilentlyContinue

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = if ($null -eq $out) { '' } else { $out }
            StdErr   = if ($null -eq $err) { '' } else { $err }
        }
    }
    finally {
        Remove-Item -Path $stdoutFile -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $stderrFile -Force -ErrorAction SilentlyContinue
    }
}

# --- Case 1: --no-ui is refused fail-closed, exit 1, nothing opened ---------
# Contract: src\BroChoos.Core\Runners\UrlRunner.cs (Primary + NoUi -> ExitCodes.GeneralError).
Write-Host '--- Case 1: --no-ui <url> (fail-closed, expect exit 1, nothing opened) ---'
$noUi = Invoke-BroChoos -Arguments @('--no-ui', $Url)
Write-Host "exit code : $($noUi.ExitCode)"
Write-Host ("stdout    : {0}" -f (Protect-EvidenceText -Value (Format-Stream -Text $noUi.StdOut)))
Write-Host ("stderr    : {0}" -f (Protect-EvidenceText -Value (Format-Stream -Text $noUi.StdErr)))
Write-Result -Name 'no-ui exit code' -Ok ($noUi.ExitCode -eq 1) -Detail "expected 1 (ExitCodes.GeneralError), got $($noUi.ExitCode)"
Write-Result -Name 'no-ui ingress accepted' -Ok ($noUi.StdOut -match '\[startup\] INGRESS_ACCEPTED') -Detail 'the URL must reach the URL path (Bootstrap prints INGRESS_ACCEPTED) so the refusal is the runner''s, not a parse error'
Write-Result -Name 'no-ui refusal notice' -Ok ($noUi.StdErr -match 'requires an explicit browser choice; nothing was opened') -Detail 'stderr must state that nothing was opened'
Write-Result -Name 'no-ui opened nothing' -Ok ($noUi.StdOut -notmatch 'Opening in ') -Detail 'stdout must NOT report a browser launch'
# A secondary forwards to an already-running primary and exits 0 without running this branch.
Write-Result -Name 'no-ui ran as the primary instance' -Ok ($noUi.StdOut -notmatch '\[secondary\]') -Detail 'stdout must NOT report a forward to an already-running primary (close any running BroChoos and re-run)'
Write-Host ''

Write-Host '=== Summary ==='
if ($script:failures -eq 0) {
    Write-Host 'PASS: all smoke checks passed'
    exit 0
}

Write-Host "FAIL: $script:failures smoke check(s) failed"
exit 1
