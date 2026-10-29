<#
.SYNOPSIS
    Milestone 0A-4 smoke: full Default Apps capability cycle on the REAL user hive.

.DESCRIPTION
    Cycle: verify (before) -> register -> verify (registered) -> open-default-apps
    (prints the guide; Windows Settings WILL open once - EXPECTED) -> unregister ->
    verify (removed). Every step also runs the D-187 invariant check: the raw
    UserChoice state (ProgId + Hash for http/https, FileExts for .url) is captured
    BEFORE and compared AFTER with an ORDINAL comparison; any difference - even a
    case-only ProgId mutation - is a FAIL.

    The script mutates only BroChoos-owned HKCU capability keys via the product
    itself, and NEVER writes UserChoice. On failure (and always on exit) it runs
    a final --unregister so no registered state is ever left behind.

    Artifacts the PRODUCT leaves on purpose (D-291/D-299): system_changes.log and
    registration-*.json backups under %LocalAppData%\BroChoos. The script leaves
    them in place: they are the audit trail and the restore points (the log is
    shown in the PASS summary). Only the script's own temp files are removed.

    This file is deliberately pure ASCII: Windows PowerShell 5.1 reads a .ps1 saved
    as UTF-8 without BOM using the ANSI code page, which corrupts non-ASCII text.
#>

[CmdletBinding()]
param(
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
        Script       = 'registration-smoke.ps1'
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

function Read-UserChoiceSnapshot {
    # Raw composite of every UserChoice bit this milestone could ever touch.
    # READ-ONLY: absence is reported as a token, values verbatim (host-local data,
    # printed for proof only - it never leaves this script).
    $parts = @()
    $targets = @(
        @{ Label = 'http'; Key = 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice' },
        @{ Label = 'https'; Key = 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice' },
        @{ Label = '.url'; Key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.url\UserChoice' }
    )

    foreach ($t in $targets) {
        if (Test-Path -LiteralPath $t.Key) {
            $progId = (Get-ItemProperty -LiteralPath $t.Key -Name 'ProgId' -ErrorAction SilentlyContinue).ProgId
            $hash = (Get-ItemProperty -LiteralPath $t.Key -Name 'Hash' -ErrorAction SilentlyContinue).Hash
            if ($null -eq $progId) { $progId = '(no-progid)' }
            if ($null -eq $hash) { $hash = '(no-hash)' }
            $parts += "$($t.Label)=[$progId|$hash]"
        }
        else {
            $parts += "$($t.Label)=(absent)"
        }
    }

    return [string]::Join(' ; ', $parts)
}

Write-Host '=== BroChoos 0A-4 registration cycle smoke ==='
Write-Host ("Repo root : {0}" -f (Protect-EvidenceText -Value $repoRoot))
Write-Host "Config    : $Configuration"
Write-Host ''

# Build once; resolve the produced exe through MSBuild (the repo emits x64 paths).
Write-Host "Building ($Configuration)..."
& dotnet build $appProject --configuration $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: build - dotnet build exited with $LASTEXITCODE"
    exit 1
}

$targetDll = (& dotnet msbuild $appProject -getProperty:TargetPath -p:Configuration=$Configuration -nologo) 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host 'FAIL: locate executable - could not read TargetPath from MSBuild'
    exit 1
}

$targetDll = ($targetDll | Out-String).Trim()
$exePath = [IO.Path]::ChangeExtension($targetDll, '.exe')

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Host ("FAIL: locate executable - expected build output not found at {0}" -f (Protect-EvidenceText -Value $exePath))
    exit 1
}

Write-Host ("Executable: {0}" -f (Protect-EvidenceText -Value $exePath))
Write-Host ''

$stdinFile = [IO.Path]::GetTempFileName()

function Invoke-BroChoos {
    param([string[]] $Arguments)

    $stdoutFile = [IO.Path]::GetTempFileName()
    $stderrFile = [IO.Path]::GetTempFileName()

    try {
        # Empty stdin file keeps --open-default-apps non-interactive (redirected
        # input means the runner skips the Enter pause by design).
        $process = Start-Process -FilePath $exePath -ArgumentList $Arguments -Wait -PassThru `
            -RedirectStandardInput $stdinFile -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile

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

function Assert-StatusRows {
    param(
        [string] $StepName,
        [string] $Output,
        [string] $ExpectedWord
    )

    foreach ($scheme in @('http', 'https', '\.url')) {
        $row = ($Output -split "`r?`n") | Where-Object { $_ -match "^\s+$scheme\s+\|" } | Select-Object -First 1
        $hasWord = ($null -ne $row) -and ($row -match "\|\s*$ExpectedWord\s*\|")
        Write-Result -Name "$StepName row: $scheme" -Ok $hasWord -Detail "expected '| $ExpectedWord |' in three-step row"
    }
}

$before = Read-UserChoiceSnapshot
Write-Host ("UserChoice BEFORE: {0}" -f (Protect-EvidenceText -Value $before))
Write-Host ''

try {
    Write-Host '--- Step 1: verify (before) ---'
    $v1 = Invoke-BroChoos -Arguments @('--verify-defaults')
    Write-Result -Name 'verify(before) exit code' -Ok ($v1.ExitCode -eq 0) -Detail "expected 0, got $($v1.ExitCode)"
    Write-Result -Name 'verify(before) prints three-step table' -Ok ($v1.StdOut -match 'target \| registered \| selected') -Detail 'table header expected'

    Write-Host '--- Step 2: register ---'
    $reg = Invoke-BroChoos -Arguments @('--register')
    Write-Result -Name 'register exit code' -Ok ($reg.ExitCode -eq 0) -Detail "expected 0, got $($reg.ExitCode)"
    Write-Result -Name 'register prints capabilities-declared guidance' -Ok ($reg.StdOut -match 'Capabilities declared') -Detail 'stdout should confirm declaration'
    Write-Result -Name 'register prints the backup path' -Ok ($reg.StdOut -match 'Backup of the pre-write state:.*registration-.*\.json') -Detail 'a D-299 backup must exist before any write'

    Write-Host '--- Step 3: verify (after register) ---'
    $v2 = Invoke-BroChoos -Arguments @('--verify-defaults')
    Write-Result -Name 'verify(registered) exit code' -Ok ($v2.ExitCode -eq 0) -Detail "expected 0, got $($v2.ExitCode)"
    Assert-StatusRows -StepName 'verify(registered)' -Output $v2.StdOut -ExpectedWord 'yes'
    Write-Result -Name 'selection step stays read-only-honest' -Ok ($v2.StdOut -match 'UserChoice') -Detail 'the selected column names its read-only source'

    Write-Host '--- Step 4: open-default-apps (guide; a Settings window WILL open - EXPECTED) ---'
    $openRes = Invoke-BroChoos -Arguments @('--open-default-apps')
    Write-Result -Name 'open-default-apps exit code' -Ok ($openRes.ExitCode -eq 0) -Detail "expected 0, got $($openRes.ExitCode)"
    Write-Result -Name 'guide text names BroChoos' -Ok ($openRes.StdOut -match 'BroChoos') -Detail 'the Russian guide text must name the app to pick'
    Write-Result -Name 'guide re-runs verification' -Ok ($openRes.StdOut -match 'Status after your choice:') -Detail 'after the user action the three-step table prints again'

    Write-Host '--- Step 5: unregister ---'
    $unreg = Invoke-BroChoos -Arguments @('--unregister')
    Write-Result -Name 'unregister exit code' -Ok ($unreg.ExitCode -eq 0) -Detail "expected 0, got $($unreg.ExitCode)"
    Write-Result -Name 'unregister prints removed paths' -Ok ($unreg.StdOut -match 'Removed: HKCU\\Software\\') -Detail 'removed HKCU paths expected'
    Write-Result -Name 'unregister prints restore path' -Ok ($unreg.StdOut -match 'Restore path:') -Detail 'D-299 success message notes the restore path'

    Write-Host '--- Step 6: verify (removed) ---'
    $v3 = Invoke-BroChoos -Arguments @('--verify-defaults')
    Write-Result -Name 'verify(removed) exit code' -Ok ($v3.ExitCode -eq 0) -Detail "expected 0, got $($v3.ExitCode)"
    Assert-StatusRows -StepName 'verify(removed)' -Output $v3.StdOut -ExpectedWord 'no'

    Write-Host '--- Step 7: D-187 invariant (UserChoice ordinal-identical) ---'
    $after = Read-UserChoiceSnapshot
    Write-Host ("UserChoice AFTER : {0}" -f (Protect-EvidenceText -Value $after))
    # Ordinal on purpose: -eq is case-insensitive and would pass a case-only
    # ProgId mutation, silently breaking the byte-level claim.
    Write-Result -Name 'UserChoice untouched' -Ok ([string]::Equals($after, $before, [StringComparison]::Ordinal)) -Detail 'ProgId+Hash composite before == after, ordinal-identical'
}
finally {
    # Never leave registered state behind, whatever happened above. --unregister
    # on a clean hive is an honest no-op by design, so this is always safe.
    Write-Host '--- finally: safety unregister ---'
    $cleanup = Invoke-BroChoos -Arguments @('--unregister')
    Write-Result -Name 'finally-unregister exit code' -Ok ($cleanup.ExitCode -eq 0) -Detail "expected 0, got $($cleanup.ExitCode)"

    $final = Invoke-BroChoos -Arguments @('--verify-defaults')
    Assert-StatusRows -StepName 'finally' -Output $final.StdOut -ExpectedWord 'no'

    $endSnapshot = Read-UserChoiceSnapshot
    Write-Host ("UserChoice FINAL : {0}" -f (Protect-EvidenceText -Value $endSnapshot))
    Write-Result -Name 'UserChoice untouched at end' -Ok ([string]::Equals($endSnapshot, $before, [StringComparison]::Ordinal)) -Detail 'final composite == before composite, ordinal-identical'

    Remove-Item -Path $stdinFile -Force -ErrorAction SilentlyContinue
}

Write-Host ''
$logPath = Join-Path (Join-Path $env:LOCALAPPDATA 'BroChoos\logs') 'system_changes.log'
Write-Host ("system_changes.log: {0} (product audit trail, intentionally kept)" -f (Protect-EvidenceText -Value $logPath))
Write-Host ''

Write-Host '=== Summary ==='
if ($script:failures -eq 0) {
    Write-Host 'PASS: all registration-cycle smoke checks passed'
    exit 0
}

Write-Host "FAIL: $script:failures smoke check(s) failed"
exit 1
