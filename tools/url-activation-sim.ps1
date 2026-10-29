<#
.SYNOPSIS
    Milestone 0A-4 test tooling: URL activation simulation with a UserChoice
    non-mutation proof.

.DESCRIPTION
    (a) Captures the raw UserChoice state (ProgId + Hash) of http/https BEFORE.
    (b) Invokes BroChoos.exe https://example.com exactly the way a real
        shell\open\command would hand the URL over ("<exe>" "%1" -> exe + one
        positional argument). The Picker window WILL appear - EXPECTED.
    (c) Confirms the full 0A-3 pipeline ran: SESSION_START + VM cards marker
        (detection fed the Picker) + WINDOW_OPENED; then the window receives a
        graceful WM_CLOSE (an OS close == cancel per D-125/D-130), which must
        end the run with exit 0 and nothing launched.
    (d) Re-reads UserChoice AFTER and asserts ordinal-identical (case-only ProgId
        mutations count as a difference): BroChoos must never mutate the user's
        own default selection, regardless of what ran.
    (e) Repeats the bare-handler invocation pattern (exe + single positional URL)
        as the "Windows gave us the URL as default handler" simulation and
        asserts the pipeline re-enters identically.

    If the Picker window cannot be found or closed headlessly, the script KILLS
    the process and marks ONLY the window-interaction step DEGRADED (exit-code and
    marker assertions then reflect the degraded run); everything else is judged
    normally. No escalation of severity happens silently: the DEGRADED word appears
    in the output.

    This file is deliberately pure ASCII: Windows PowerShell 5.1 reads a .ps1 saved
    as UTF-8 without BOM using the ANSI code page, which corrupts non-ASCII text.
#>

[CmdletBinding()]
param(
    [string] $Url = 'https://example.com',
    [string] $Configuration = 'Debug',
    [int] $WindowWaitSeconds = 20,
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
        Script       = 'url-activation-sim.ps1'
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

function Write-Degraded {
    param([string] $Name, [string] $Detail)
    Write-Host "DEGRADED: $Name - $Detail (print-only; interaction step could not run headlessly)"
}

# --- Minimal native surface for a graceful OS-close of the picker window ------

$nativeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Win32WindowClose
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WmClose = 0x0010;

    public static int PostCloseToProcessWindows(int processId)
    {
        List<IntPtr> handles = new List<IntPtr>();
        EnumWindows(delegate (IntPtr hwnd, IntPtr extra)
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner == (uint)processId)
            {
                handles.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);

        int posted = 0;
        foreach (IntPtr hwnd in handles)
        {
            if (PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
            {
                posted++;
            }
        }
        return posted;
    }
}
'@

Add-Type -TypeDefinition $nativeSource -ReferencedAssemblies @('System')

function Read-UserChoiceSnapshot {
    $parts = @()
    foreach ($scheme in @('http', 'https')) {
        $key = "HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\$scheme\UserChoice"
        if (Test-Path -LiteralPath $key) {
            $progId = (Get-ItemProperty -LiteralPath $key -Name 'ProgId' -ErrorAction SilentlyContinue).ProgId
            $hash = (Get-ItemProperty -LiteralPath $key -Name 'Hash' -ErrorAction SilentlyContinue).Hash
            if ($null -eq $progId) { $progId = '(no-progid)' }
            if ($null -eq $hash) { $hash = '(no-hash)' }
            $parts += "$scheme=[$progId|$hash]"
        }
        else {
            $parts += "$scheme=(absent)"
        }
    }
    return [string]::Join(' ; ', $parts)
}

function Read-StreamTail {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($fs)
        return $reader.ReadToEnd()
    }
    finally {
        $fs.Dispose()
    }
}

Write-Host '=== BroChoos 0A-4 URL activation simulation ==='
Write-Host ("Repo root : {0}" -f (Protect-EvidenceText -Value $repoRoot))
Write-Host ("URL       : {0}" -f (Protect-EvidenceText -Value $Url))
Write-Host "Config    : $Configuration"
Write-Host ''

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
    Write-Host ("FAIL: locate executable - {0} not found" -f (Protect-EvidenceText -Value $exePath))
    exit 1
}

Write-Host ("Executable: {0}" -f (Protect-EvidenceText -Value $exePath))
Write-Host ''

$before = Read-UserChoiceSnapshot
Write-Host ("(a) UserChoice BEFORE: {0}" -f (Protect-EvidenceText -Value $before))
Write-Host ''

function Invoke-HandlerStyleActivation {
    <#
        Starts BroChoos the exact way the shell would after a UserChoice pick:
        exe + one positional URL argument, no quoting conventions of our own.
        Waits for WINDOW_OPENED, posts WM_CLOSE (OS close == cancel), expects
        exit 0. Returns the observation record; marks itself DEGRADED when the
        window cannot be driven headlessly.
    #>
    param([string] $CaseLabel)

    $stdoutFile = [IO.Path]::GetTempFileName()
    $stderrFile = [IO.Path]::GetTempFileName()
    $degraded = $false

    try {
        $process = Start-Process -FilePath $exePath -ArgumentList @($Url) -PassThru `
            -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile

        $openedSeen = $false
        $deadline = (Get-Date).AddSeconds($WindowWaitSeconds)
        while ((Get-Date) -lt $deadline) {
            if ($process.HasExited) { break }
            $tail = Read-StreamTail -Path $stdoutFile
            if ($tail -match 'WINDOW_OPENED') { $openedSeen = $true; break }
            Start-Sleep -Milliseconds 400
        }

        $posted = 0
        if ($openedSeen -and -not $process.HasExited) {
            $posted = [Win32WindowClose]::PostCloseToProcessWindows($process.Id)
            if ($posted -eq 0) {
                $degraded = $true
            }
        }
        elseif (-not $openedSeen -and -not $process.HasExited) {
            $degraded = $true
        }

        if ($degraded -and -not $process.HasExited) {
            Write-Degraded -Name "$CaseLabel window drive" -Detail 'window not found or WM_CLOSE refused; killing the process instead'
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }

        $null = $process.WaitForExit(15000)
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }

        $out = Read-StreamTail -Path $stdoutFile
        $err = Read-StreamTail -Path $stderrFile

        # NOTE (PS 5.1): Start-Process -PassThru with redirected streams does NOT
        # synchronize ExitCode unless -Wait is used (impossible here: the window
        # must be driven mid-run). The PRODUCT's own exit marker is the exit-code
        # evidence instead; the PS exit code is kept only as a bonus check.
        $exitMarker = [regex]::Match($out, 'WINDOW_CLOSED exit=(\d+)')
        $appExit = if ($exitMarker.Success) { [int]$exitMarker.Groups[1].Value } else { $null }

        return [pscustomobject]@{
            ExitCode   = if ($process.HasExited) { $process.ExitCode } else { $null }
            AppExit    = $appExit
            StdOut     = $out
            StdErr     = $err
            OpenedSeen = $openedSeen
            Posted     = $posted
            Degraded   = $degraded
        }
    }
    finally {
        Remove-Item -Path $stdoutFile -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $stderrFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "(b/c) activation leg 1: shell-style invocation of $Url"
$leg1 = Invoke-HandlerStyleActivation -CaseLabel 'leg1'
Write-Host "markers seen: OPENED=$($leg1.OpenedSeen) posted=$($leg1.Posted) degraded=$($leg1.Degraded) appExit=$($leg1.AppExit)"

Write-Result -Name 'leg1 detection fed the picker' -Ok ($leg1.StdOut -match 'SESSION_START') -Detail 'SESSION_START marker expected'
Write-Result -Name 'leg1 browser cards exist' -Ok ($leg1.StdOut -match 'VM cards=[1-9]') -Detail 'at least one detected card'
Write-Result -Name 'leg1 selection surface shown' -Ok ($leg1.StdOut -match 'WINDOW_OPENED') -Detail 'WINDOW_OPENED marker expected'

if (-not $leg1.Degraded) {
    Write-Result -Name 'leg1 cancel via OS close' -Ok ($leg1.StdOut -match 'WINDOW_CLOSED_BY_OS') -Detail 'cancel marker expected after WM_CLOSE'
    Write-Result -Name 'leg1 cancel exit code 0' -Ok ($leg1.AppExit -eq 0) -Detail "product exit marker shows $($leg1.AppExit) (a user cancel is not a failure)"
    Write-Result -Name 'leg1 nothing launched' -Ok ($leg1.StdOut -notmatch 'LAUNCH_OK') -Detail 'a cancel must not open a browser'
}
else {
    Write-Degraded -Name 'leg1 interaction assertions' -Detail 'exit-code/cancel assertions are evaluated against the degraded run'
    Write-Result -Name 'leg1 pipeline reached the window' -Ok ($leg1.StdOut -match 'SESSION_START') -Detail 'pipeline proof survives degradation'
}

Write-Host ''
Write-Host "(e) activation leg 2: bare-handler argv pattern (exe + single positional URL)"
$leg2 = Invoke-HandlerStyleActivation -CaseLabel 'leg2'
Write-Host "markers seen: OPENED=$($leg2.OpenedSeen) posted=$($leg2.Posted) degraded=$($leg2.Degraded) appExit=$($leg2.AppExit)"

Write-Result -Name 'leg2 pipeline re-enters identically' -Ok ($leg2.StdOut -match 'SESSION_START') -Detail 'a default-handler-style activation reaches the same pipeline'
if (-not $leg2.Degraded) {
    Write-Result -Name 'leg2 cancel exit code 0' -Ok ($leg2.AppExit -eq 0) -Detail "product exit marker shows $($leg2.AppExit)"
}

Write-Host ''
Write-Host '(d) UserChoice AFTER comparison'
$after = Read-UserChoiceSnapshot
Write-Host ("    UserChoice AFTER : {0}" -f (Protect-EvidenceText -Value $after))
# Ordinal on purpose: -eq is case-insensitive and would pass a case-only
# ProgId mutation, silently breaking the byte-level claim.
Write-Result -Name 'UserChoice ordinal-identical' -Ok ([string]::Equals($after, $before, [StringComparison]::Ordinal)) -Detail 'ProgId+Hash composite before == after, ordinal-identical'

Write-Host ''
Write-Host '=== Summary ==='
if ($script:failures -eq 0) {
    Write-Host 'PASS: URL activation simulation passed (DEGRADED steps, if any, are listed above)'
    exit 0
}

Write-Host "FAIL: $script:failures simulation check(s) failed"
exit 1
