<#
.SYNOPSIS
    Milestone 0A-5 test tooling: the D-v3.5-493 kill-process test for D-063 atomic writes.

.DESCRIPTION
    Proves the claim locked decision D-v3.5-493 asks for - "atomic writes verified by
    kill-process test" - by killing a real writer process at EVERY checkpoint of
    AtomicJsonFileStore.Write, on BOTH of its publish branches, and asserting the destination is
    byte-for-byte either the complete OLD document or the complete NEW one - or, before a
    first-create publish, absent - never a partial or hybrid state.

    HOW IT AVOIDS BEING A RACE. The child (BroChoos.exe, internal 0A-5 child mode) attaches
    an IAtomicWriteObserver that signals a named "reached" event at the armed stage and then
    PARKS on a named "proceed" event. The parent waits for "reached", inspects the destination
    WHILE THE CHILD IS STILL PARKED THERE, and only then kills it. The destination is
    therefore examined at a known instant rather than after a timing gamble, and because the
    parent never sets "proceed" in a kill case, the run provably died at the intended stage.
    Every event name carries a per-run GUID, so a stale signal from an earlier run can never
    be consumed - the classic way a kill test passes for the wrong reason.

    BOTH PUBLISH BRANCHES, because they are different Win32 APIs on different code paths and a
    branch that is never killed is a branch that is never verified. 13 kill cases:

      REPLACING (destination seeded first, so File.Exists is true -> File.Replace). All SEVEN
      stages, AfterPrune included:
        Validated / TempFlushed / LockAcquired / BeforePublish -> destination must stay OLD
        AfterPublish / AfterPrune / AfterVerify                -> destination must be NEW

      FIRST-CREATE (nothing seeded, so File.Exists is false -> File.Move). SIX stages, never
      AfterPrune - PruneBackups is called only inside the File.Exists branch, so
      OnStage(AfterPrune) is never invoked on a first-create write and arming it would only burn
      the parent's bounded wait and report a misleading FAIL:
        Validated / TempFlushed / LockAcquired / BeforePublish -> destination must stay ABSENT
        AfterPublish / AfterVerify                             -> destination must be NEW
      Backups stay 0 at EVERY first-create stage: BuildBackupPath is only reached inside the
      File.Exists branch, so backupPath stays null and no .bak is ever minted.

    SAFETY. Everything happens under an isolated sandbox root
    (<TEMP>\0a5-kill\<runId>), never under %LocalAppData%\BroChoos: the test
    must not touch the machine owner's real config. Destination, temps and backups all live
    under that one root on ONE volume, because File.Replace requires source, destination and
    backup to share a volume. Processes are killed BY PID ONLY, after re-verifying identity
    (PID resolves + ProcessName matches + StartTime exactly equal to the recorded value).
    Killing by name was done once in this project's history and would have destroyed unrelated
    user processes; the recorded lesson is "by PID only, identity verified".

    WHAT THIS DOES NOT ESTABLISH. A process kill is not a power cut. TerminateProcess removes
    a user-mode process; it does not discard the OS file cache, the NTFS metadata journal or
    the disk's own write cache.

    This file is deliberately pure ASCII: Windows PowerShell 5.1 reads a .ps1 saved as UTF-8
    without BOM using the ANSI code page, which corrupts non-ASCII text.
#>

[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    # Skips the build and locates the already-built exe. Used when a `dotnet test` run has
    # just built the same configuration, so a rebuild would add nothing.
    [switch] $SkipBuild,
    [string] $ExePath = '',
    [int] $ReachedWaitSeconds = 30,
    # Must exceed the parent's inspect+kill work after "reached", so a loaded machine cannot
    # make the child's self-enforced park expire mid-inspection.
    [int] $ChildParkSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- DEBUG ONLY, AND REFUSED RATHER THAN LEFT TO FAIL OBSCURELY --------------------------
#
# The child mode this harness drives is compiled ONLY into Debug builds. It is excluded from
# every other configuration by a Compile Remove in src\BroChoos.App\BroChoos.App.csproj and by
# an #if DEBUG in each Scaffold0A5 source, because the product is a Windows URL handler that
# receives URLs as command-line arguments and that mode performs a real config write to an
# argument-supplied path. A shipped build must not contain it.
#
# WITHOUT THIS GUARD the failure would be maximally confusing: a Release exe would start, fall
# straight through to the normal application (the trigger is just an unknown argument there),
# never signal `reached`, and every one of the 13 cases would report "child never signalled"
# after burning its full 30 s bounded wait - roughly seven minutes of waiting to produce 13
# FAILs that point at the write path instead of at the configuration. Refusing up front turns
# that into one line.
if ($Configuration -ne 'Debug') {
    Write-Host "FAIL: configuration - this harness requires the DEBUG build. The 0A-5 kill-test child mode is compiled only into Debug (Compile Remove in BroChoos.App.csproj plus #if DEBUG in each Scaffold0A5 source), so a '$Configuration' exe does not contain it and every case would fail as 'child never signalled'."
    Write-Host 'Re-run without -Configuration, or with -Configuration Debug.'
    exit 1
}

# THE CORROBORATING ENVIRONMENT VARIABLE, which is a gate and not a convenience.
#
# AtomicWriteKillChild.IsTriggered requires BOTH that the trigger is args[0] AND that this
# variable is set. That pair is what makes the mode unreachable by ARGUMENT INJECTION even in a
# Debug build that is registered as the OS URL handler: a URL arrives as an argument and cannot
# set an environment variable, and a URL breaking out of the registered "%1" quoting delivers
# its extra tokens after the URL, never in args[0].
#
# Set on the PARENT because a child started by Start-Process inherits this process's
# environment; PowerShell 5.1's Start-Process has no per-child environment parameter. Restored
# in the run's finally block so the setting does not outlive the harness in the developer's
# shell.
#
# STATED HONESTLY: the early `exit 1` paths BELOW this line - a missing build output, or a
# failed canonical capture - return before that finally and therefore leave the variable set in
# the calling shell. That is a loose end rather than a hole: the variable alone reaches nothing.
# The child mode also requires the trigger to be args[0] AND a Debug build that contains the
# mode at all, so a leftover variable cannot be combined with a URL to reach a write. Left
# unrestructured deliberately, to keep this change to the gating it is about.
$script:killChildEnvName = 'BROCHOOS_0A5_KILL_CHILD'
$script:killChildEnvPrevious = [Environment]::GetEnvironmentVariable($script:killChildEnvName)
[Environment]::SetEnvironmentVariable($script:killChildEnvName, '1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\BroChoos.App\BroChoos.App.csproj'
$script:failures = 0

# Every child this run starts, recorded as PID **plus the StartTime read while it was alive** -
# not as a bare PID. The leftover-process check is scoped to exactly these, never to "every process
# named BroChoos": an unrelated instance the developer happens to be running is none of this test's
# business, and failing on it would be a false alarm.
#
# The StartTime is carried because the final stray check runs the SAME identity gate as every kill
# site. Matching on ProcessName alone left that check identity-BLIND: a PID recycled onto an
# unrelated BroChoos process after our child died would have been reported as this run's leftover
# stray - a false FAIL on the one check that guards the machine owner's data. Report-only, so there
# was never a kill risk, but a false FAIL on that particular check is still the wrong outcome.
$script:childRecords = @()

# Records one child for the end-of-run stray check. Takes the StartTime as a VALUE rather than
# re-reading it from the process, so the stray check compares against the byte-identical value the
# kill gate used - one recorded fact per child, not two reads that could disagree.
function Register-ChildRecord {
    param(
        [int] $TargetPid,
        [datetime] $Start
    )

    $script:childRecords = $script:childRecords + [pscustomobject]@{
        ChildPid = $TargetPid
        Start    = $Start
    }
}

# --- P8 bookkeeping: created / removed / kept -------------------------------------------
$script:createdNote = ''
$script:removedNote = ''
$script:keptNote = ''

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
    Write-Host "DEGRADED: $Name - $Detail (print-only; observation could not be made on this host)"
}

# Reused from tools\url-activation-sim.ps1: opens with FileShare.ReadWrite because the child
# still holds the handle to its redirected stream.
#
# WRAPPED, and it matters. This is called on the redirected log of a JUST-KILLED child, whose
# stdout/stderr handles are released asynchronously - the process is gone but the handle can
# survive it by a few milliseconds. An unguarded open therefore loses a genuine race to a
# transient sharing violation and, under $ErrorActionPreference='Stop', would terminate the
# whole run and lose every remaining stage. Returning '' instead makes the caller's marker
# assertion FAIL (an empty string matches no marker), which is the correct outcome: a harness
# read problem must surface as a FAIL line, never as a crash and never as a false pass.
function Read-StreamTail {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return '' }

    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    }
    catch {
        return ''
    }

    try {
        $reader = New-Object System.IO.StreamReader($fs)
        return $reader.ReadToEnd()
    }
    catch {
        return ''
    }
    finally {
        $fs.Dispose()
    }
}

# Returns the file's bytes, or $null if it cannot be read at all. Exists so that a PRODUCT
# defect (a locked, vanished or unreadable file at a checkpoint) is reported as a FAIL line
# instead of terminating the harness with a PowerShell error - a crashed harness proves nothing
# and loses every remaining stage.
function Get-BytesOrNull {
    param([string] $Path)

    try {
        return , ([System.IO.File]::ReadAllBytes($Path))
    }
    catch {
        return $null
    }
}

function Get-Sha256Hex {
    param([byte[]] $Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '')
    }
    finally {
        $sha.Dispose()
    }
}

# A well-behaved concurrent reader: FileShare.ReadWrite + Delete so it never BLOCKS the
# publisher. Opening with the default FileShare.Read would deny File.Replace its own access
# and turn this reader into the cause of a write failure - which would look like a product
# defect and prove nothing about tearing.
function Read-DestinationBytes {
    param([string] $Path)

    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    try {
        $buffer = New-Object byte[] $fs.Length
        $read = 0
        while ($read -lt $buffer.Length) {
            $chunk = $fs.Read($buffer, $read, $buffer.Length - $read)
            if ($chunk -le 0) { break }
            $read = $read + $chunk
        }

        # `,` on purpose. A bare `return $buffer` writes the array to the pipeline and
        # PowerShell UNROLLS it into individual bytes, so the caller would get an Object[] -
        # and, for a zero-length file, $null. Wrapping in a one-element array survives the
        # single level of unrolling and hands back the byte[] intact, which is what the
        # byte-for-byte comparisons downstream depend on.
        return , $buffer
    }
    finally {
        $fs.Dispose()
    }
}

# Mirrors AtomicJsonFileStore.BuildMutexName exactly: SHA256 over the UPPER-CASED full path,
# first 16 digest bytes as hex, "Local\" scope. Recomputed here rather than exported from
# production code, so the harness cannot silently drift into asserting against whatever the
# product happens to do.
function Get-WriteMutexName {
    param([string] $Path)

    $normalized = ([System.IO.Path]::GetFullPath($Path)).ToUpperInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    }
    finally {
        $sha.Dispose()
    }

    # Cast the slice back to byte[]: a PowerShell range index yields Object[], and
    # BitConverter.ToString has no Object[] overload.
    $hex = ([BitConverter]::ToString([byte[]]($digest[0..15])) -replace '-', '')
    return "Local\BroChoos_ConfigWrite_$hex"
}

# Enumerated unfiltered and matched ORDINALLY, deliberately - the same choice
# AtomicJsonFileStore.SweepStaleTemps and PruneBackups make, and for the same reason: a
# -Filter wildcard is matched by the filesystem and its behaviour depends on the volume's 8.3
# short-name configuration, which is machine state this harness cannot see. An explicit
# ordinal test states exactly what counts as a temp or a backup, in terms this script owns.
function Get-MatchingFiles {
    param(
        [string] $Directory,
        [string] $Suffix
    )

    $prefix = 'app.json.'
    $all = @(Get-ChildItem -LiteralPath $Directory -File -ErrorAction SilentlyContinue)
    return @($all | Where-Object {
            $_.Name.StartsWith($prefix, [StringComparison]::Ordinal) -and
            $_.Name.EndsWith($Suffix, [StringComparison]::Ordinal) -and
            $_.Name.Length -gt ($prefix.Length + $Suffix.Length)
        })
}

# Bounded-retry removal. A just-killed child releases its redirected stdout/stderr handles
# asynchronously - the process is gone but the file handle can survive it by a few
# milliseconds - so a single Remove-Item can lose a genuine race and report leftover state that
# is not really leftover. Bounded, never unbounded: it gives up and lets the caller FAIL.
function Remove-TreeWithRetry {
    param(
        [string] $Path,
        [int] $AttemptCount = 10,
        [int] $DelayMilliseconds = 300
    )

    for ($attempt = 1; $attempt -le $AttemptCount; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) { return $true }

        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $Path)) { return $true }

        Start-Sleep -Milliseconds $DelayMilliseconds
    }

    return (-not (Test-Path -LiteralPath $Path))
}

function Get-TempFiles {
    param([string] $Directory)
    return Get-MatchingFiles -Directory $Directory -Suffix '.tmp'
}

function Get-BackupFiles {
    param([string] $Directory)
    return Get-MatchingFiles -Directory $Directory -Suffix '.bak'
}

# --- kill safety: one identity gate in front of EVERY kill ------------------------------
#
# A PID alone is not an identity. Windows recycles PIDs, and a Process object obtained from
# Start-Process does not necessarily hold an open handle to the child (the empty ExitCode field
# on a killed child is evidence that it may not), so nothing pins the number to the process this
# run started. Between a child's death and a cleanup kill in a `finally`, that PID can therefore
# belong to something else entirely on the owner's machine. This project already killed by
# process NAME once and would have destroyed unrelated user processes; the bar here is not
# "unlikely", it is "checked".
#
# Three facts must all agree: the PID still resolves, the process name matches the exe under
# test, and StartTime is EXACTLY the value recorded while the child was provably alive. Exactly,
# with no tolerance window - StartTime does not drift for a live process, so a window would only
# widen the door for a recycled PID. A failed check NEVER kills; it reports and moves on.
$script:identityDetail = ''

function Test-ChildIdentity {
    param(
        [int] $TargetPid,
        [string] $ExpectedName,
        [datetime] $ExpectedStart
    )

    $script:identityDetail = ''

    $live = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
    if ($null -eq $live) {
        $script:identityDetail = "PID $TargetPid no longer resolves; refusing to kill"
        return $false
    }

    $liveStart = $null
    try {
        # Wrapped: StartTime throws once the process has exited, and a race between the
        # resolve above and this read is exactly the case this gate exists to survive.
        $liveStart = $live.StartTime
    }
    catch {
        $liveStart = $null
    }

    $nameOk = ($live.ProcessName -eq $ExpectedName)
    $startOk = ($null -ne $liveStart) -and ($liveStart -eq $ExpectedStart)
    $script:identityDetail = "PID resolves, ProcessName='$($live.ProcessName)' (match=$nameOk), StartTime exact match=$startOk"

    return ($nameOk -and $startOk)
}

# The cleanup/abandon kills. Identity-gated exactly like the primary kill, and never fatal:
# -ErrorAction SilentlyContinue because the child can exit between the gate and the kill, and a
# teardown must not convert that race into a terminated run.
function Stop-VerifiedChild {
    param(
        [int] $TargetPid,
        [string] $ExpectedName,
        [datetime] $ExpectedStart,
        [string] $Reason
    )

    if (Test-ChildIdentity -TargetPid $TargetPid -ExpectedName $ExpectedName -ExpectedStart $ExpectedStart) {
        Stop-Process -Id $TargetPid -Force -ErrorAction SilentlyContinue
        return
    }

    Write-Host "  SKIPPED KILL ($Reason): $script:identityDetail"
}

# Composite fingerprint of a directory tree: one line per file, hashed. Used to prove the
# owner's real config root was never touched.
#
# Two properties this deliberately has, because their absence made the earlier version weaker
# than it read. It keys on the path RELATIVE TO THE ROOT, not on the bare file name: with a bare
# name, a file moved between backups\ and logs\ at identical size and mtime produced an identical
# composite and the move was invisible. And it SORTS before joining: Get-ChildItem's order is not
# contractual, so an unsorted join could differ between the before and after reads for a tree
# nobody touched - a false FAIL on the single check that guards the machine owner's data.
function Get-TreeSnapshot {
    param([string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) { return '(absent)' }

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not $rootFull.EndsWith('\', [StringComparison]::Ordinal)) {
        $rootFull = $rootFull + '\'
    }

    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName)
    $stamp = ($files | ForEach-Object {
            $full = [System.IO.Path]::GetFullPath($_.FullName)
            $relative = if ($full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
                $full.Substring($rootFull.Length)
            }
            else {
                $full
            }

            "$relative|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
        }) -join ';'

    return Get-Sha256Hex -Bytes ([System.Text.Encoding]::UTF8.GetBytes($stamp))
}

# --- locate the executable under test ---------------------------------------------------

Write-Host '=== BroChoos 0A-5 atomic-write kill-process test (D-v3.5-493) ==='
# REDACTED on purpose. $repoRoot derives from $PSScriptRoot, so printing it raw emits the machine
# owner's full absolute project path, and an installed-mode path of this shape embeds the Windows
# account name. D-v3.5-075 / D-v3.5-097 forbid full user file paths in logs and evidence
# artifacts, and THIS STDOUT is precisely what gets pasted into a gate artifact - the trap is for
# whoever pastes it, not for this run. The leaf alone tells a human which worktree ran, without
# naming anything above it. Keeps the rest of the file's posture: the exe is reduced to a name,
# the sandbox to a placeholder, and %LocalAppData% is printed as the literal token, unexpanded.
Write-Host "Repo root : <repo>\$(Split-Path -Leaf $repoRoot) (redacted per D-075/D-097)"
# DEBUG IS NOT A WEAKENING OF THIS EVIDENCE, and the reason is worth stating where the
# configuration is printed rather than only in the artifact. The property under test is the
# atomicity of publishing a file by File.Replace / File.Move: a filesystem contract about a
# directory-entry swap, enforced by the OS, not a behaviour of generated code. It does not
# depend on JIT optimisation, on inlining, or on anything else the Debug/Release switch
# changes. A Release run would exercise the SAME two Win32 calls in the same order against the
# same bytes. What Release cannot do is contain the kill-test child at all - that mode is
# deliberately excluded from shipped builds - so Debug is the only configuration in which this
# experiment can be performed, and it is the correct one to perform it in.
Write-Host "Config    : $Configuration (required: the kill-test child mode is Debug-only; File.Replace atomicity is an OS contract, not an optimisation-dependent behaviour)"

if ($ExePath -ne '') {
    $exePath = $ExePath
}
elseif ($SkipBuild) {
    # Reuse whatever build already ran. A solution build maps Any CPU to x64 and writes to
    # bin\x64\<Config>\<tfm>\win-x64\, while a direct project build without -p:Platform writes to
    # bin\<Config>\<tfm>\win-x64\. Search all of bin, keep only this configuration (never a
    # publish folder), and take the newest, so the binary under test is the one just built.
    $binRoot = Join-Path $repoRoot 'src\BroChoos.App\bin'
    $configSegment = "\$Configuration\"
    $found = @(Get-ChildItem -LiteralPath $binRoot -Recurse -Filter 'BroChoos.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'BroChoos.exe' -and
            $_.FullName.IndexOf($configSegment, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $_.FullName.IndexOf('\publish\', [StringComparison]::OrdinalIgnoreCase) -lt 0 } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($found.Count -eq 0) {
        # REDACTED like every other print in this file. $binRoot is $repoRoot-derived, so emitting
        # it raw would put the owner's full absolute path into stdout that gets pasted into a gate
        # artifact - exactly what D-v3.5-075 / D-v3.5-097 forbid. The repo-relative form below is
        # the whole actionable content of the message anyway: the part above <repo> tells the reader
        # nothing they need in order to fix a missing build.
        Write-Host "FAIL: locate executable - no $Configuration BroChoos.exe under <repo>\src\BroChoos.App\bin (run without -SkipBuild)"
        exit 1
    }
    $exePath = $found[0].FullName
}
else {
    Write-Host "Building ($Configuration)..."
    # Same platform as a solution build (Any CPU maps to x64), so this branch and -SkipBuild
    # resolve the same bin\x64\<Config> layout. Build and lookup use identical properties.
    & dotnet build $appProject --configuration $Configuration -p:Platform=x64 --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL: build - dotnet build exited with $LASTEXITCODE"
        exit 1
    }

    $targetDll = (& dotnet msbuild $appProject -getProperty:TargetPath -p:Configuration=$Configuration -p:Platform=x64 -nologo) 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'FAIL: locate executable - could not read TargetPath from MSBuild'
        exit 1
    }

    $targetDll = ($targetDll | Out-String).Trim()
    $exePath = [IO.Path]::ChangeExtension($targetDll, '.exe')
}

if (-not (Test-Path -LiteralPath $exePath)) {
    # REDACTED per D-v3.5-075 / D-v3.5-097, like every other print in this file: $exePath is either
    # $repoRoot-derived or caller-supplied, so printing it raw emits a full user path into stdout
    # that gets pasted into a gate artifact.
    #
    # The leaf is taken by SUBSTRING, not by [IO.Path]::GetFileName, on purpose. This is a failure
    # path: the path that reached it may be malformed, and GetFileName throws on invalid path
    # characters under .NET Framework - which would crash the harness on the one line whose entire
    # job is to report a clean FAIL. LastIndexOf returning -1 makes Substring(0) hand back the whole
    # string, so the worst case degrades to "print what we have" and never to an exception.
    #
    # BOTH separators are scanned. A caller-supplied -ExePath may use forward slashes - .NET path
    # APIs and Test-Path both accept them - and scanning only '\' would return -1, making
    # Substring(0) print the ENTIRE absolute path: a D-075/D-097 leak on the one line whose whole
    # job is to avoid one. Max() keeps the no-throw property for a path with neither separator.
    $cut = [Math]::Max($exePath.LastIndexOf('\'), $exePath.LastIndexOf('/'))
    $exeLeaf = if ($exePath -eq '') { '(empty path)' } else { $exePath.Substring($cut + 1) }
    Write-Host "FAIL: locate executable - '$exeLeaf' not found at the resolved location (path redacted per D-075/D-097)"
    exit 1
}

$exeName = [IO.Path]::GetFileNameWithoutExtension($exePath)
# REDACTED per D-v3.5-075 / D-v3.5-097: print the exe path relative to src\BroChoos.App\bin, which
# names the platform/config/tfm/rid layout actually resolved without anything above the repo. A
# caller-supplied -ExePath outside that folder is reduced to its file name.
$exeBinRoot = (Join-Path $repoRoot 'src\BroChoos.App\bin') + '\'
$exeFull = [IO.Path]::GetFullPath($exePath)
if ($exeFull.StartsWith($exeBinRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "Executable: $($exeFull.Substring($exeBinRoot.Length)) (relative to src\BroChoos.App\bin)"
}
else {
    Write-Host "Executable: $([IO.Path]::GetFileName($exeFull)) (caller-supplied, outside src\BroChoos.App\bin; path redacted per D-075/D-097)"
}

# --- isolated sandbox root --------------------------------------------------------------

$runId = ([guid]::NewGuid().ToString('N'))
$sandboxParent = Join-Path ([System.IO.Path]::GetTempPath()) '0a5-kill'
$runRoot = Join-Path $sandboxParent $runId

Write-Host "Run id    : $runId"
Write-Host "Sandbox   : <TEMP>\0a5-kill\$runId (never %LocalAppData%\BroChoos)"
Write-Host ''

# D-075/D-097 posture, and a real safety proof: snapshot the OWNER's config root so the
# summary can state it was never touched, rather than merely asserting it.
$realRoot = Join-Path $env:LOCALAPPDATA 'BroChoos'
$realRootBefore = Get-TreeSnapshot -Root $realRoot

$null = New-Item -ItemType Directory -Path $runRoot -Force
$script:createdNote = "1 sandbox run root (<TEMP>\0a5-kill\$runId) holding 13 per-case data/backups subdir pairs (7 replacing + 6 first-create), 1 reader case, 1 canonical dir, and the child stdout/stderr logs; plus 2 scaffold sources, 1 tool, 1 test file, 1 evidence file in the repo"

# Runs the child and WAITS. Used for canonical byte capture, seeding and the no-wedge write,
# where -Wait makes ExitCode reliable (see the PS 5.1 note in url-activation-sim.ps1).
function Invoke-ChildWrite {
    param(
        [string] $Destination,
        [string] $BackupsDirectory,
        [int] $Revision,
        [string] $Label,
        # 0 = the 52-byte AppDocument envelope (every kill case). >0 = a BrowsersDocument with
        # that many synthetic entries, used to give the concurrent-reader case a payload that
        # spans many filesystem clusters instead of one single Read.
        [int] $BrowserCount = 0
    )

    $stdoutFile = Join-Path $runRoot "$Label.stdout.log"
    $stderrFile = Join-Path $runRoot "$Label.stderr.log"

    $arguments = @(
        '--internal-0a5-atomic-write-child',
        '--dest', $Destination,
        '--run-id', $runId,
        '--revision', "$Revision",
        '--stage', 'none'
    )
    if ($BackupsDirectory -ne '') {
        $arguments = $arguments + @('--backups', $BackupsDirectory)
    }
    if ($BrowserCount -gt 0) {
        $arguments = $arguments + @('--browsers', "$BrowserCount")
    }

    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile

    # Wrapped, and MinValue on failure. This child ran under -Wait, so it has ALREADY EXITED by the
    # time this line runs, and StartTime throws on an exited process whose handle was not retained.
    # Under $ErrorActionPreference='Stop' an unguarded read would terminate the whole run over a
    # bookkeeping detail. MinValue can never equal a real StartTime, so the stray check's identity
    # gate will simply decline to claim the PID - which is the right answer for a process that is
    # already gone and can never be a leftover.
    $waitedStart = [datetime]::MinValue
    try {
        $waitedStart = $process.StartTime
    }
    catch {
        $waitedStart = [datetime]::MinValue
    }
    Register-ChildRecord -TargetPid $process.Id -Start $waitedStart

    $out = Read-StreamTail -Path $stdoutFile
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut   = $out
        Ok       = ($process.ExitCode -eq 0) -and ($out -match 'KILLCHILD_WRITE_OK')
    }
}

# --- canonical bytes --------------------------------------------------------------------
#
# The OLD / NEW / THIRD byte images are produced by the STORE ITSELF (three ordinary verified
# writes into a scratch directory), not by re-deriving JsonSerializerOptions in PowerShell -
# PS 5.1 has no System.Text.Json at all, and the tests' WriteOptions mirror is a private
# member of another assembly. Taking the bytes from the real writer is strictly stronger than
# any mirror: it cannot drift from the production options, and each image is one the store has
# already round-tripped through its own step-2 validation and post-publish read-back.
#
# The three documents differ ONLY in schemaRevision, because AppDocument in 0A-5 carries only
# the D-064 schema envelope - that single field is the whole differing surface available.
$canonicalDir = Join-Path $runRoot 'canonical'
$null = New-Item -ItemType Directory -Path $canonicalDir -Force

$oldRevision = 100
$newRevision = 200
$thirdRevision = 300

$canonical = @{}
foreach ($pair in @(@{ N = 'old'; R = $oldRevision }, @{ N = 'new'; R = $newRevision }, @{ N = 'third'; R = $thirdRevision })) {
    $file = Join-Path $canonicalDir "app.json"
    $target = Join-Path $canonicalDir "$($pair.N).image"
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }

    $res = Invoke-ChildWrite -Destination $file -BackupsDirectory '' -Revision $pair.R -Label "canonical-$($pair.N)"
    if (-not $res.Ok) {
        Write-Host "FAIL: canonical capture ($($pair.N)) - child exit $($res.ExitCode); cannot compute reference bytes"
        $null = Remove-TreeWithRetry -Path $runRoot
        exit 1
    }

    Move-Item -LiteralPath $file -Destination $target -Force
    $bytes = [System.IO.File]::ReadAllBytes($target)
    $canonical[$pair.N] = [pscustomobject]@{
        Bytes  = $bytes
        Length = $bytes.Length
        Sha    = Get-Sha256Hex -Bytes $bytes
    }
}

Write-Host "canonical images: old=$($canonical['old'].Length)B sha=$($canonical['old'].Sha.Substring(0,16)).. new=$($canonical['new'].Length)B sha=$($canonical['new'].Sha.Substring(0,16)).. third=$($canonical['third'].Length)B"
Write-Result -Name 'canonical OLD and NEW differ' -Ok ($canonical['old'].Sha -ne $canonical['new'].Sha) -Detail 'the two revisions must be distinguishable byte-for-byte or every assertion below is vacuous'

# --- BULK canonical images, for the concurrent-reader case only --------------------------
#
# The kill cases use the 52-byte AppDocument envelope, and that is sound: they compare the
# destination against complete images, and the publish is a directory-entry swap whose atomicity
# does not depend on payload size.
#
# The READER case is the one place where size mattered and 52 bytes was too small to prove
# anything. The reader's loop is `while ($read -lt $buffer.Length)`, and a 52-byte file always
# satisfies that in a SINGLE FileStream.Read - so observing a torn read would have required the
# swap to land between two reads of one file, which cannot occur when there is only ever one
# read. "torn/unparseable=0" was therefore near-vacuous. A payload of tens of KB spans many
# filesystem clusters and takes several reads, which finally gives the observation a real chance
# to fail if the destination were ever mutated in place.
$bulkBrowserCount = 300

$bulkPairs = @(@{ N = 'bulkOld'; R = $oldRevision }, @{ N = 'bulkNew'; R = $newRevision })
foreach ($pair in $bulkPairs) {
    $file = Join-Path $canonicalDir 'app.json'
    $target = Join-Path $canonicalDir "$($pair.N).image"
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }

    $res = Invoke-ChildWrite -Destination $file -BackupsDirectory '' -Revision $pair.R -Label "canonical-$($pair.N)" -BrowserCount $bulkBrowserCount
    if (-not $res.Ok) {
        Write-Host "FAIL: canonical bulk capture ($($pair.N)) - child exit $($res.ExitCode); cannot compute reference bytes"
        $null = Remove-TreeWithRetry -Path $runRoot
        exit 1
    }

    Move-Item -LiteralPath $file -Destination $target -Force
    $bytes = [System.IO.File]::ReadAllBytes($target)
    $canonical[$pair.N] = [pscustomobject]@{
        Bytes  = $bytes
        Length = $bytes.Length
        Sha    = Get-Sha256Hex -Bytes $bytes
    }
}

Write-Host "bulk images ($bulkBrowserCount browsers): old=$($canonical['bulkOld'].Length)B new=$($canonical['bulkNew'].Length)B"
Write-Result -Name 'canonical BULK OLD and NEW differ' -Ok ($canonical['bulkOld'].Sha -ne $canonical['bulkNew'].Sha) `
    -Detail 'the reader case needs two distinguishable large images, or its torn-read check is vacuous'

# EQUAL LENGTH IS LOAD-BEARING, so it is asserted rather than assumed.
#
# The reader sizes its buffer from $fs.Length at open time and then reads until that buffer is
# full, classifying the result by hash: anything that is not exactly OLD or exactly NEW is counted
# as torn. Equal lengths are what keep that classification airtight. Once the two images differ in
# length, a truncated read of the longer one can have exactly the byte COUNT of a complete shorter
# one, so a partial observation stops being distinguishable by size from a legitimate whole
# document and the torn-read check has to rest entirely on the hash comparison never colliding.
# Keeping the lengths equal removes that whole class of ambiguity instead of reasoning about it.
#
# THE HONEST REASON THEY MATCH, which is not the one an earlier version of the evidence file gave:
# it is NOT the fixed-width index padding in the synthetic entries. That padding makes the ENTRIES
# uniform, which is a different property. OLD and NEW differ ONLY in schemaRevision, and they come
# out the same length purely because 100 and 200 render to the same NUMBER OF DIGITS. Change
# $newRevision to 1000 and the two images differ by one byte. This assertion is what turns that
# coincidence into a checked precondition instead of a silent assumption.
Write-Result -Name 'canonical BULK OLD and NEW are the same LENGTH' -Ok ($canonical['bulkOld'].Length -eq $canonical['bulkNew'].Length) `
    -Detail "bulkOld=$($canonical['bulkOld'].Length)B, bulkNew=$($canonical['bulkNew'].Length)B - equal because both revision numbers render to the same digit count ($oldRevision / $newRevision); if they differed, a short read of the longer image could pass as a complete read of the shorter one and the torn-read check would leak"

# Pins the whole point of the bulk payload. If a future change collapsed it back to a
# single-cluster document, the reader case would silently return to proving nothing - so the size
# is asserted rather than assumed. 16 KB is several NTFS clusters (4 KB default) and forces the
# reader's loop to iterate.
$bulkFloor = 16384
Write-Result -Name 'BULK payload spans many filesystem clusters' -Ok ($canonical['bulkNew'].Length -ge $bulkFloor) `
    -Detail "the reader payload is $($canonical['bulkNew'].Length)B, which must be >= ${bulkFloor}B so a read takes several passes instead of one"
Write-Host ''

# --- one kill case ----------------------------------------------------------------------

function Invoke-KillCase {
    param(
        [string] $Stage,
        [ValidateSet('replacing', 'first-create')]
        [string] $Branch,
        [string] $ExpectDocument,   # 'old', 'new' or 'absent'
        [int] $ExpectTemps,
        [int] $ExpectBackups,
        [bool] $CheckAbandonedMutex
    )

    $label = "$Stage/$Branch"
    Write-Host "--- stage $Stage [$Branch branch] (expect $($ExpectDocument.ToUpperInvariant()) destination, temps=$ExpectTemps, backups=$ExpectBackups) ---"

    # A first-create case MUST NOT share event names with the replacing case of the same stage.
    # The names embed the stage; "reached" is a ManualReset LATCH; and the replacing case leaves
    # it SET, because its child is killed while parked on it and the parent never resets it.
    # Reusing the run id would therefore hand this case a signal from the previous one: it would
    # assert against a child that had not parked yet and PASS FOR THE WRONG REASON - precisely
    # the failure the per-run GUID exists to prevent. The reader case already carries its own id
    # for this reason; the first-create sweep gets one too, so the collision is impossible by
    # construction instead of depending on a kernel handle refcount having reached zero.
    $caseRunId = if ($Branch -eq 'first-create') { "${runId}fc" } else { $runId }
    $caseKey = if ($Branch -eq 'first-create') { "$Stage-firstcreate" } else { $Stage }

    $caseRoot = Join-Path $runRoot $caseKey
    $dataDir = Join-Path $caseRoot 'data'
    $backupsDir = Join-Path $caseRoot 'backups'
    $null = New-Item -ItemType Directory -Path $dataDir -Force
    $null = New-Item -ItemType Directory -Path $backupsDir -Force
    $destination = Join-Path $dataDir 'app.json'

    if ($Branch -eq 'replacing') {
        # Seed the OLD document by copying the canonical image: byte-identical to what the store
        # itself writes, and it makes the destination EXIST so this case takes the replacing
        # branch (File.Replace + a real backup), which is what stages AfterPublish and later need.
        [System.IO.File]::WriteAllBytes($destination, $canonical['old'].Bytes)
        $seedSha = Get-Sha256Hex -Bytes ([System.IO.File]::ReadAllBytes($destination))
        Write-Result -Name "$label seed is canonical OLD" -Ok ($seedSha -eq $canonical['old'].Sha) -Detail 'the case starts from the exact OLD byte image'
    }
    else {
        # DELIBERATELY NOT SEEDED. File.Exists(destination) is then false, so the write takes the
        # FIRST-CREATE branch: File.Move rather than File.Replace - a different Win32 API on a
        # different code path - and backupPath stays null, because BuildBackupPath is only called
        # inside the File.Exists branch. That is why every first-create stage expects ZERO
        # backups, including the post-publish ones.
        #
        # Asserted rather than assumed: if a previous case or a stray had leaked a file to this
        # path, the write would silently take the replacing branch and every "first-create"
        # conclusion below would be about the wrong code path.
        Write-Result -Name "$label starts with NO destination" -Ok (-not (Test-Path -LiteralPath $destination)) `
            -Detail 'nothing is seeded, so File.Exists is false and the write must take the File.Move first-create branch'
    }

    $reachedName = "Local\BroChoos_KillTest_${caseRunId}_${Stage}_reached"
    $proceedName = "Local\BroChoos_KillTest_${caseRunId}_${Stage}_proceed"
    $reached = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $reachedName)
    $proceed = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $proceedName)

    # Opened BEFORE the kill and held across it. A named mutex's kernel object dies with its
    # last handle, so without this the child's death would free the object and the parent's
    # later `new Mutex(...)` would create a FRESH one - reporting a clean acquisition and
    # silently failing to observe the abandonment it exists to prove.
    $mutexWatch = $null
    if ($CheckAbandonedMutex) {
        $mutexWatch = New-Object System.Threading.Mutex($false, (Get-WriteMutexName -Path $destination))
    }

    $stdoutFile = Join-Path $runRoot "$caseKey.child.stdout.log"
    $stderrFile = Join-Path $runRoot "$caseKey.child.stderr.log"
    $process = $null

    # Pre-initialized because the `finally` identity gate reads it and Set-StrictMode makes an
    # unassigned variable a terminating error. MinValue can never equal a real StartTime, so if
    # Start-Process failed before the recording below, the gate simply refuses to kill - which is
    # the correct outcome when the harness does not know what it started.
    $recordedStart = [datetime]::MinValue

    try {
        # --backups is passed on BOTH branches, first-create included, and that is what makes the
        # zero-backup expectation meaningful. With no backups directory configured,
        # BuildBackupPath would return null for trivial reasons and "backups=0" would prove
        # nothing. Configured, a zero count says something real: the store never entered the
        # File.Exists branch, so it never minted a backup path at all.
        $arguments = @(
            '--internal-0a5-atomic-write-child',
            '--dest', $destination,
            '--backups', $backupsDir,
            '--run-id', $caseRunId,
            '--revision', "$newRevision",
            '--stage', $Stage,
            '--park-timeout-seconds', "$ChildParkSeconds"
        )

        $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile

        $childPid = $process.Id
        # Recorded ONCE, while the process is provably alive, and compared for EXACT equality
        # before the kill. PID plus start time is what makes "this is still the process I
        # started" checkable at all; a PID alone can be recycled onto something else.
        $recordedStart = $process.StartTime
        # The same recorded value the kill gate below compares against, handed to the stray check so
        # it can run that identical gate at the end of the run.
        Register-ChildRecord -TargetPid $childPid -Start $recordedStart

        # Bounded, never unbounded: a child that never reaches the stage must fail the case,
        # not hang the harness.
        $reachedSignalled = $reached.WaitOne([TimeSpan]::FromSeconds($ReachedWaitSeconds))
        if (-not $reachedSignalled) {
            Write-Result -Name "$label reached checkpoint" -Ok $false -Detail "child never signalled '$Stage' within $ReachedWaitSeconds s; killing and abandoning this case"
            Stop-VerifiedChild -TargetPid $childPid -ExpectedName $exeName -ExpectedStart $recordedStart -Reason "$label abandon"
            return
        }

        Write-Result -Name "$label reached checkpoint" -Ok $true -Detail "child signalled reached and is parked at $Stage"

        # ---------- assertions taken WHILE THE CHILD IS STILL PARKED ----------
        #
        # 'absent' is the pre-publish first-create expectation: there is no previous revision and
        # the swap has not run, so the correct state is NO destination at all. The document
        # assertions A2/A3/A4 are about WHICH complete document is on disk, so they have no
        # subject here and are skipped rather than faked - and A1 carries the whole claim for
        # these stages: the write must not have conjured a partial file into existence.
        $expectAbsent = ($ExpectDocument -eq 'absent')
        $expected = if ($expectAbsent) { $null } else { $canonical[$ExpectDocument] }

        # A1
        $exists = Test-Path -LiteralPath $destination
        if ($expectAbsent) {
            Write-Result -Name "$label A1 destination still ABSENT" -Ok (-not $exists) `
                -Detail 'before the swap on a first-create write there is nothing to publish yet, so a kill must leave NO destination behind - not an empty file, not a partial one'
        }
        else {
            Write-Result -Name "$label A1 destination exists" -Ok $exists -Detail 'a kill must never leave the destination missing'
        }

        $destSha = ''
        $destLen = -1
        if ($exists) {
            # Tolerant open: a destination that cannot be read AT a checkpoint is itself a
            # finding, so it must surface through A2/A3 as a FAIL rather than as a crash.
            $destBytes = $null
            try {
                $destBytes = Read-DestinationBytes -Path $destination
            }
            catch {
                $destBytes = $null
            }

            if ($null -ne $destBytes) {
                $destLen = $destBytes.Length
                $destSha = Get-Sha256Hex -Bytes $destBytes
            }
        }

        # A2/A3/A4 are the "WHICH complete document is on disk" axis. On a pre-publish
        # first-create stage there is no document on disk at all and none is expected, so they
        # have no subject: A1 above already carries the entire claim for those stages. Skipped
        # explicitly rather than evaluated against a $null expectation, which would report three
        # meaningless FAILs and bury the one assertion that matters.
        if (-not $expectAbsent) {
            # A2 - the D-493 claim itself.
            $isComplete = ($destSha -eq $canonical['old'].Sha) -or ($destSha -eq $canonical['new'].Sha)
            Write-Result -Name "$label A2 destination is a COMPLETE document" -Ok $isComplete -Detail "sha256 is exactly OLD or exactly NEW (observed ${destLen}B $(if($destSha -eq $canonical['old'].Sha){'=OLD'}elseif($destSha -eq $canonical['new'].Sha){'=NEW'}else{'=NEITHER'}))"

            # A3 - membership alone would pass a wrong-stage regression.
            Write-Result -Name "$label A3 it is the STAGE-SPECIFIC document" -Ok ($destSha -eq $expected.Sha) -Detail "at $Stage on the $Branch branch the destination must be $($ExpectDocument.ToUpperInvariant()), not merely one of the two"

            # A4 - valid, of the right type, with the right content. Byte-identical
            # re-serialization follows from A2: the reference images were produced BY the store
            # from AppDocument{schemaRevision=N}, so bytes equal to an image deserialize to that
            # document and re-serialize to those same bytes.
            $parsed = $null
            $parseOk = $false
            if ($exists) {
                try {
                    $parsed = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json
                    $parseOk = $true
                }
                catch {
                    $parseOk = $false
                }
            }
            $expectRevision = if ($ExpectDocument -eq 'old') { $oldRevision } else { $newRevision }

            # Both guards matter under Set-StrictMode, and for the same reason: a FAILING product
            # must produce a FAIL line, never a crashed harness. An empty destination makes
            # ConvertFrom-Json yield nothing instead of throwing ($null guard), and reading a
            # property that does not exist on a PSCustomObject is a terminating error - which is
            # exactly what a partial-but-parseable document would hit. Presence is therefore tested
            # before value, and -and short-circuits so the value reads are only reached when safe.
            $hasFields = $false
            if ($parseOk -and ($null -ne $parsed)) {
                $names = @($parsed.PSObject.Properties.Name)
                $hasFields = ($names -contains 'schemaVersion') -and ($names -contains 'schemaRevision')
            }

            $fieldsOk = $hasFields -and ($parsed.schemaVersion -eq 1) -and ($parsed.schemaRevision -eq $expectRevision)
            Write-Result -Name "$label A4 parses as the document type" -Ok $fieldsOk -Detail "valid JSON with schemaVersion=1 and schemaRevision=$expectRevision (round-trip identity follows from A2 against store-produced images)"
        }

        # A5. The @() around the call is REQUIRED, not decoration: PowerShell unrolls a
        # function's array return into the caller's pipeline, so an EMPTY result arrives as
        # $null and `$null.Count` is a terminating error under Set-StrictMode. Wrapping at the
        # call site normalizes nothing/one/many into an array whose .Count is always readable.
        $temps = @(Get-TempFiles -Directory $dataDir)
        $tempCountOk = ($temps.Count -eq $ExpectTemps)
        $tempDetail = "expected $ExpectTemps, found $($temps.Count)"
        if ($ExpectTemps -eq 1 -and $temps.Count -eq 1) {
            $tempBytes = Get-BytesOrNull -Path $temps[0].FullName
            $tempSha = if ($null -eq $tempBytes) { '(unreadable)' } else { Get-Sha256Hex -Bytes $tempBytes }
            $tempCountOk = ($tempSha -eq $canonical['new'].Sha)
            $tempDetail = "exactly one orphan temp, and its bytes equal NEW ($(if($tempSha -eq $canonical['new'].Sha){'match'}else{"MISMATCH: $tempSha"}))"
        }
        Write-Result -Name "$label A5 temp accounting" -Ok $tempCountOk -Detail $tempDetail

        # A6 / A7 - the backup axis, in both directions.
        #
        # On the FIRST-CREATE branch this is A7 at every stage, post-publish ones included, and
        # that is a real assertion rather than a gap: BuildBackupPath is called only inside the
        # File.Exists branch, so a write that took File.Move never computed a backup path at all
        # and backupPath stayed null. A backup appearing here would mean the store had taken the
        # replacing branch against a destination that did not exist.
        $backups = @(Get-BackupFiles -Directory $backupsDir)
        if ($ExpectBackups -eq 0) {
            $zeroDetail = if ($Branch -eq 'first-create') {
                "a first-create write mints NO backup at any stage - there is no previous revision to preserve and BuildBackupPath is never reached (found $($backups.Count))"
            }
            else {
                "no backup may exist before the swap commits (found $($backups.Count))"
            }
            Write-Result -Name "$label A7 zero backups" -Ok ($backups.Count -eq 0) -Detail $zeroDetail
        }
        else {
            $backupOk = ($backups.Count -eq 1)
            $backupDetail = "expected exactly 1 fresh backup, found $($backups.Count)"
            if ($backupOk) {
                $bakBytes = Get-BytesOrNull -Path $backups[0].FullName
                $bakSha = if ($null -eq $bakBytes) { '(unreadable)' } else { Get-Sha256Hex -Bytes $bakBytes }
                $backupOk = ($bakSha -eq $canonical['old'].Sha)
                $backupDetail = "exactly one fresh backup and its bytes equal OLD ($(if($bakSha -eq $canonical['old'].Sha){'match'}else{"MISMATCH: $bakSha"}))"
            }
            Write-Result -Name "$label A6 replace-branch backup" -Ok $backupOk -Detail $backupDetail
        }

        # ---------- identity re-verification, then the kill ----------
        # The SAME gate every other kill site in this harness uses (Test-ChildIdentity): PID
        # resolves + ProcessName matches + StartTime exactly equal to the value recorded while
        # the child was provably alive.
        $identityOk = Test-ChildIdentity -TargetPid $childPid -ExpectedName $exeName -ExpectedStart $recordedStart

        Write-Result -Name "$label identity re-verified before kill" -Ok $identityOk -Detail "$script:identityDetail - by PID only, never by name"
        if (-not $identityOk) {
            Write-Result -Name "$label A9 kill proven" -Ok $false -Detail 'identity check failed, so no kill was attempted'
            return
        }

        # -ErrorAction SilentlyContinue, matching every other kill site. The child's park is
        # self-bounded, so it CAN exit on its own between the identity gate above and this call
        # (an expired park exits 70 from inside OnStage). Under $ErrorActionPreference='Stop' an
        # unguarded Stop-Process would then throw and terminate the entire run, losing every
        # remaining stage over a race that the A9 proofs below already detect and report.
        Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue

        # A9 - three INDEPENDENT exit proofs. Independent on purpose: WaitForExit is a kernel
        # wait, HasExited is a state query, and Get-Process is a fresh process-table lookup that
        # does not depend on either handle. One of them agreeing is not proof; all three are.
        $waitReturned = $process.WaitForExit(10000)
        $hasExited = $process.HasExited
        $gone = ($null -eq (Get-Process -Id $childPid -ErrorAction SilentlyContinue))

        # KNOWN GAP, deliberately left as-is rather than patched unverified: a
        # Start-Process -PassThru object with REDIRECTED streams does not populate ExitCode in
        # PowerShell 5.1 (the same limitation url-activation-sim.ps1 records), so this reports
        # empty for a killed child. The three proofs above are what actually establish the exit;
        # the code is supplementary.
        #
        # The fix, stated against the RIGHT object: force the handle open on $process - the
        # Start-Process -PassThru object whose .ExitCode is read below - while the child is still
        # alive, BEFORE the kill (touch $process.Handle). NOT on the Get-Process result: that is
        # a DIFFERENT System.Diagnostics.Process instance, and caching a handle on it does
        # nothing for the read below. An earlier revision of this comment named that object and
        # would have sent the next reader to patch the wrong one. Still not applied here, for the
        # original reason: it also touches the A9 reads above, so it must not ship without a full
        # harness re-run.
        $observedExit = '(unavailable)'
        try {
            $observedExit = "$($process.ExitCode)"
        }
        catch {
            $observedExit = '(unavailable)'
        }

        Write-Result -Name "$label A9 kill proven three ways" -Ok ($waitReturned -and $hasExited -and $gone) `
            -Detail "WaitForExit(10000)=$waitReturned, HasExited=$hasExited, Get-Process finds nothing=$gone, observed exit code=$observedExit"

        # The run provably died AT the stage: reached was signalled before the kill, and
        # proceed was never set by this harness, so the child cannot have run on.
        Write-Result -Name "$label died at the intended stage" -Ok ($reachedSignalled -and (-not $proceed.WaitOne(0))) `
            -Detail 'reached was signalled before the kill and proceed was NEVER set'

        # The negative markers include the two SELF-TERMINATION markers, and that is the point of
        # this assertion rather than a detail of it. If the child's park expires it prints
        # KILLCHILD_PARK_EXPIRED / KILLCHILD_ORPHANED and exits 70 from inside OnStage - at the
        # SAME stage, so every destination assertion above still reads correctly and the case
        # would pass. But then the process died of its own timeout, not of the parent's kill, and
        # this harness would be claiming a kill it did not perform. Both markers must therefore be
        # absent for the case to stand.
        $childOut = Read-StreamTail -Path $stdoutFile
        $killedNotSelfExited = ($childOut -match 'KILLCHILD_PARKED') -and
            ($childOut -notmatch 'KILLCHILD_RELEASED') -and
            ($childOut -notmatch 'KILLCHILD_WRITE_OK') -and
            ($childOut -notmatch 'KILLCHILD_PARK_EXPIRED') -and
            ($childOut -notmatch 'KILLCHILD_ORPHANED')
        Write-Result -Name "$label child never released the park (and did not self-exit)" -Ok $killedNotSelfExited `
            -Detail 'child markers show it parked, neither resumed nor completed the write, and did NOT terminate itself on park expiry - so the parent kill is what ended it'

        # Mutex abandonment, only where the lock is provably held by the dead child.
        if ($CheckAbandonedMutex) {
            $abandonedSeen = $false
            $acquired = $false
            try {
                $acquired = $mutexWatch.WaitOne(0)
            }
            catch {
                # Caught broadly and the chain inspected by NAME, not with a typed catch: a
                # .NET method throwing inside PowerShell surfaces wrapped in a
                # MethodInvocationException, so `catch [AbandonedMutexException]` is not
                # dependable across hosts. Walking InnerException is.
                $chain = $_.Exception
                while ($null -ne $chain) {
                    if ($chain.GetType().Name -eq 'AbandonedMutexException') {
                        # The wait SUCCEEDED and this thread now owns the mutex, so it must be
                        # released below exactly like any other acquisition.
                        $abandonedSeen = $true
                        $acquired = $true
                        break
                    }
                    $chain = $chain.InnerException
                }

                if (-not $abandonedSeen) { throw }
            }

            Write-Result -Name "$label mutex ABANDONED by the killed holder" -Ok $abandonedSeen `
                -Detail "AbandonedMutexException observed=$abandonedSeen (a clean acquisition would mean the lock was never held)"

            if ($acquired) {
                $mutexWatch.ReleaseMutex()
            }
        }

        # A8 - NO WEDGE. A killed holder must not block every future write; this is the
        # abandoned-mutex path exercised for real, from a fresh process.
        # -Label carries $caseKey, not $Stage: the replacing and first-create cases of one stage
        # would otherwise redirect their stdout to the SAME log file and overwrite each other.
        $third = Invoke-ChildWrite -Destination $destination -BackupsDirectory $backupsDir -Revision $thirdRevision -Label "$caseKey-third"
        $thirdSha = ''
        $thirdBytes = Get-BytesOrNull -Path $destination
        if ($null -ne $thirdBytes) {
            $thirdSha = Get-Sha256Hex -Bytes $thirdBytes
        }
        Write-Result -Name "$label A8 no wedge after the kill" -Ok ($third.Ok -and ($thirdSha -eq $canonical['third'].Sha)) `
            -Detail "a fresh write of a THIRD document succeeded (child exit $($third.ExitCode)) and the destination is now THIRD byte-for-byte"
    }
    finally {
        if ($null -ne $process) {
            try {
                # HasExited first, purely to stay quiet on the normal path: by this point the
                # child has almost always been killed already, and running the gate anyway would
                # print a "SKIPPED KILL" line for every passing case straight into the evidence
                # artifact. When the child IS still alive, the kill goes through the same identity
                # gate as every other kill site - and this is where a recycled PID is most
                # plausible, because the number may already have been reassigned.
                if (-not $process.HasExited) {
                    Stop-VerifiedChild -TargetPid $process.Id -ExpectedName $exeName -ExpectedStart $recordedStart -Reason "$label cleanup"
                }
            }
            catch {
                Write-Degraded -Name "$label cleanup" -Detail 'could not re-check the child state while tearing down'
            }
        }

        $reached.Dispose()
        $proceed.Dispose()
        if ($null -ne $mutexWatch) { $mutexWatch.Dispose() }
    }
}

# --- A10: concurrent reader across one UNKILLED write -----------------------------------

function Invoke-ConcurrentReaderCase {
    Write-Host '--- A10 concurrent reader across one UNKILLED write ---'

    $caseRoot = Join-Path $runRoot 'reader'
    $dataDir = Join-Path $caseRoot 'data'
    $backupsDir = Join-Path $caseRoot 'backups'
    $null = New-Item -ItemType Directory -Path $dataDir -Force
    $null = New-Item -ItemType Directory -Path $backupsDir -Force
    $destination = Join-Path $dataDir 'app.json'

    # Seeded with the BULK image, and the write below publishes the bulk NEW revision. Tens of KB
    # rather than 52 bytes, so the read loop genuinely iterates and a torn observation is
    # something this case could actually catch - see the bulk canonical capture above.
    [System.IO.File]::WriteAllBytes($destination, $canonical['bulkOld'].Bytes)

    $stage = 'TempFlushed'

    # A DISTINCT run id, not the shared one. The TempFlushed kill case earlier in this run used
    # the same stage name, and its `reached` latch was left SET when its child was killed. Those
    # names only became free because every handle to them closed; relying on that refcount is
    # precisely the stale-signal trap the per-run id exists to prevent - if any handle lingered,
    # this case would see `reached` already set, assert against a child that never parked, and
    # pass for the wrong reason. A separate id makes the collision impossible by construction.
    $readerRunId = "${runId}reader"
    $reachedName = "Local\BroChoos_KillTest_${readerRunId}_${stage}_reached"
    $proceedName = "Local\BroChoos_KillTest_${readerRunId}_${stage}_proceed"
    $reached = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $reachedName)
    $proceed = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::ManualReset, $proceedName)

    $stdoutFile = Join-Path $runRoot 'reader.child.stdout.log'
    $stderrFile = Join-Path $runRoot 'reader.child.stderr.log'
    $process = $null

    # Same reason as in Invoke-KillCase: the teardown gate reads this, and Set-StrictMode makes an
    # unassigned read fatal. MinValue matches no real StartTime, so an unrecorded child is never
    # killed.
    $recordedStart = [datetime]::MinValue

    try {
        # Parked at TempFlushed - i.e. BEFORE the destination changes - so releasing it puts
        # the publish squarely inside the read loop below rather than before it.
        #
        # --browsers makes this the BULK payload (tens of KB), which is what gives the read loop
        # below several passes per observation instead of one.
        $arguments = @(
            '--internal-0a5-atomic-write-child',
            '--dest', $destination,
            '--backups', $backupsDir,
            '--run-id', $readerRunId,
            '--revision', "$newRevision",
            '--stage', $stage,
            '--park-timeout-seconds', "$ChildParkSeconds",
            '--browsers', "$bulkBrowserCount"
        )

        $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
        # StartTime read FIRST, then registered: the stray check's identity gate is only as good as
        # the value it compares against, so the record is never created without one.
        $recordedStart = $process.StartTime
        Register-ChildRecord -TargetPid $process.Id -Start $recordedStart

        if (-not $reached.WaitOne([TimeSpan]::FromSeconds($ReachedWaitSeconds))) {
            Write-Result -Name 'A10 reader case reached checkpoint' -Ok $false -Detail "child never parked at $stage within $ReachedWaitSeconds s"
            Stop-VerifiedChild -TargetPid $process.Id -ExpectedName $exeName -ExpectedStart $recordedStart -Reason 'A10 abandon'
            return
        }

        $observations = 0
        $tornOrUnparseable = 0
        $openFailures = 0
        $sawOld = $false
        $sawNew = $false

        # Release the park, then keep hammering read+parse straight through the publish.
        $null = $proceed.Set()

        # A SAFETY BOUND, NOT A TUNING KNOB - stated explicitly because it has already been
        # mistaken for one. The loop below exits on `if ($process.HasExited -and $sawNew) { break }`
        # a few lines down, i.e. as soon as the child is gone AND the new revision has been seen, so
        # in a healthy run this 20 s bound is never what ends the loop. RAISING IT THEREFORE
        # PRODUCES NO ADDITIONAL OBSERVATIONS; it only lengthens the ceiling on a run that has
        # already gone wrong, which is the one job it should have.
        #
        # THE REAL LEVER, for whoever wants a higher observation count later: the reader has to span
        # several SEQUENTIAL PUBLISHES rather than one - a fresh parked child per publish, released
        # one after another while a single reader loop keeps reading - because the count is bounded
        # by how long one publish takes, not by how long the parent is willing to wait. Deliberately
        # NOT implemented here: 3-5 observations that provably straddle the publish are adequate
        # corroboration for D-493 once the transition assertion enforces sawOld AND sawNew AND
        # observations >= 2, and A10 corroborates the claim rather than carrying it.
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            try {
                $bytes = Read-DestinationBytes -Path $destination
                $sha = Get-Sha256Hex -Bytes $bytes
                $observations = $observations + 1

                # Compared against the BULK images, because this case seeds and publishes those.
                # A short read now lands here as NEITHER and is counted as torn - which is the
                # detection this case exists for, and which a 52-byte payload could not reach.
                if ($sha -eq $canonical['bulkOld'].Sha) { $sawOld = $true }
                elseif ($sha -eq $canonical['bulkNew'].Sha) { $sawNew = $true }
                else { $tornOrUnparseable = $tornOrUnparseable + 1 }

                $text = [System.Text.Encoding]::UTF8.GetString($bytes)
                try {
                    $null = $text | ConvertFrom-Json
                }
                catch {
                    $tornOrUnparseable = $tornOrUnparseable + 1
                }
            }
            catch {
                # A transient sharing violation on OPEN is not a torn read: no partial content
                # was ever observed. Counted and reported separately rather than swallowed.
                $openFailures = $openFailures + 1
            }

            if ($process.HasExited -and $sawNew) { break }
        }

        $null = $process.WaitForExit(15000)
        $childOut = Read-StreamTail -Path $stdoutFile

        Write-Host "reader observations=$observations torn/unparseable=$tornOrUnparseable openRetries=$openFailures sawOld=$sawOld sawNew=$sawNew"
        Write-Result -Name 'A10 write completed unkilled' -Ok ($childOut -match 'KILLCHILD_WRITE_OK') -Detail 'the reader case must exercise a real, successful publish'
        Write-Result -Name 'A10 reader never saw a torn or unparseable destination' -Ok ($tornOrUnparseable -eq 0) `
            -Detail "$observations complete observations, 0 partial; every read was exactly OLD or exactly NEW and parsed"
        # ALL THREE conditions are enforced, not just $sawNew, and that is the whole point of this
        # assertion rather than a refinement of it.
        #
        # Gating on $sawNew alone let this case PASS FOR THE WRONG REASON - the exact pattern the
        # rest of this harness is built to prevent. A regression that delayed the reader until
        # after the swap would yield observations=1, sawOld=False, sawNew=True: the loop would have
        # observed only the post-publish state, straddled nothing, and still reported PASS on all
        # three A10 lines. $sawOld was printed in the detail string but never enforced, so the
        # load-bearing condition ("the window provably contains the publish") lived in prose and
        # not in code.
        #
        #   $sawOld           - the reader was already reading BEFORE the swap landed
        #   $sawNew           - it was still reading AFTER the swap landed
        #   $observations >= 2 - there were at least two distinct looks, so "before" and "after"
        #                        are genuinely two observations and not one relabelled
        #
        # The floor is 2 deliberately and must not be raised: two observed runs produced 3 and 5,
        # so the count is host-dependent and a higher floor would convert ordinary host variance
        # into a spurious FAIL on a write path that is behaving correctly.
        $spannedPublish = $sawOld -and $sawNew -and ($observations -ge 2)
        Write-Result -Name 'A10 reader observed the transition' -Ok $spannedPublish `
            -Detail "the loop must provably straddle the publish: sawOld=$sawOld (read before the swap), sawNew=$sawNew (read after it), observations=$observations (must be >= 2, so before/after are two distinct looks)"
    }
    finally {
        if ($null -ne $process) {
            try {
                # Identity-gated like every other kill site. This case releases its child rather
                # than killing it, so reaching a live process here means the write overran the
                # loop's deadline - still no excuse to kill a bare PID.
                if (-not $process.HasExited) {
                    Stop-VerifiedChild -TargetPid $process.Id -ExpectedName $exeName -ExpectedStart $recordedStart -Reason 'A10 cleanup'
                }
            }
            catch {
                Write-Degraded -Name 'A10 cleanup' -Detail 'could not re-check the child state while tearing down'
            }
        }
        $reached.Dispose()
        $proceed.Dispose()
    }
}

# --- run every case ---------------------------------------------------------------------

try {
    # THE REPLACING BRANCH - File.Replace. Stage, branch, expected document, expected temps,
    # expected backups, check abandoned mutex. The destination is seeded first, so File.Exists is
    # true and all SEVEN stages are reachable, AfterPrune included.
    Invoke-KillCase -Stage 'Validated'     -Branch 'replacing' -ExpectDocument 'old' -ExpectTemps 0 -ExpectBackups 0 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'TempFlushed'   -Branch 'replacing' -ExpectDocument 'old' -ExpectTemps 1 -ExpectBackups 0 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'LockAcquired'  -Branch 'replacing' -ExpectDocument 'old' -ExpectTemps 1 -ExpectBackups 0 -CheckAbandonedMutex $true
    Invoke-KillCase -Stage 'BeforePublish' -Branch 'replacing' -ExpectDocument 'old' -ExpectTemps 1 -ExpectBackups 0 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'AfterPublish'  -Branch 'replacing' -ExpectDocument 'new' -ExpectTemps 0 -ExpectBackups 1 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'AfterPrune'    -Branch 'replacing' -ExpectDocument 'new' -ExpectTemps 0 -ExpectBackups 1 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'AfterVerify'   -Branch 'replacing' -ExpectDocument 'new' -ExpectTemps 0 -ExpectBackups 1 -CheckAbandonedMutex $false

    Write-Host ''

    # THE FIRST-CREATE BRANCH - File.Move. A DIFFERENT Win32 API on a DIFFERENT code path, and
    # therefore a distinct claim: D-493 asks for the write path to be verified, not most of it.
    # Nothing is seeded, so File.Exists is false.
    #
    # SIX cases, not seven. AfterPrune is deliberately absent: PruneBackups is called only inside
    # the File.Exists branch, so OnStage(AfterPrune) is never invoked on a first-create write.
    # Arming it would not hang - the child would simply finish the write and exit - but the parent
    # would then burn its full bounded `reached` wait and report a misleading FAIL. A first-create
    # write fires exactly six stages, so six cases is the complete set.
    #
    # Backups stay 0 at EVERY first-create stage, post-publish included, because BuildBackupPath
    # is only reached inside the File.Exists branch and backupPath therefore stays null.
    Invoke-KillCase -Stage 'Validated'     -Branch 'first-create' -ExpectDocument 'absent' -ExpectTemps 0 -ExpectBackups 0 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'TempFlushed'   -Branch 'first-create' -ExpectDocument 'absent' -ExpectTemps 1 -ExpectBackups 0 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'LockAcquired'  -Branch 'first-create' -ExpectDocument 'absent' -ExpectTemps 1 -ExpectBackups 0 -CheckAbandonedMutex $true
    Invoke-KillCase -Stage 'BeforePublish' -Branch 'first-create' -ExpectDocument 'absent' -ExpectTemps 1 -ExpectBackups 0 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'AfterPublish'  -Branch 'first-create' -ExpectDocument 'new'    -ExpectTemps 0 -ExpectBackups 0 -CheckAbandonedMutex $false
    Invoke-KillCase -Stage 'AfterVerify'   -Branch 'first-create' -ExpectDocument 'new'    -ExpectTemps 0 -ExpectBackups 0 -CheckAbandonedMutex $false

    Write-Host ''
    Invoke-ConcurrentReaderCase
}
finally {
    Write-Host ''
    Write-Host '--- finally: cleanup and safety checks ---'

    # A11: the isolated root goes away entirely. Nothing this test made is a keeper.
    $removed = Remove-TreeWithRetry -Path $runRoot

    # The shared parent is removed too, but ONLY when this run's own root was the last thing in
    # it: a concurrent run's directory must never be deleted from under it.
    if ($removed -and (Test-Path -LiteralPath $sandboxParent)) {
        $siblings = @(Get-ChildItem -LiteralPath $sandboxParent -Force -ErrorAction SilentlyContinue)
        if ($siblings.Count -eq 0) {
            Remove-Item -LiteralPath $sandboxParent -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Result -Name 'A11 isolated root removed' -Ok $removed -Detail 'the sandbox run root and every file under it are gone'
    $script:removedNote = 'the entire sandbox run root (canonical + bulk images, 13 kill case dirs, the reader case dir, child stdout/stderr logs)'

    # No leftover child may survive the run. Scoped to this run's own children only.
    # Scoped to the PIDs THIS run started, and each one's identity re-verified before it is
    # counted: a recycled PID now belonging to something else must not be reported as our stray,
    # and must certainly never be killed.
    # IDENTITY-GATED, via the SAME Test-ChildIdentity helper that gates all five kill sites: the PID
    # must resolve, ProcessName must match, and StartTime must be EXACTLY the value recorded while
    # that child was provably alive.
    #
    # Matching on ProcessName alone left this check identity-blind. A PID recycled onto an unrelated
    # BroChoos instance after our child exited would have been counted as this run's leftover stray,
    # producing a FALSE FAIL on one of the three checks that guard the machine owner's data. There
    # was never a kill risk here - this check is report-only - but a check that guards the owner's
    # data is the last place to accept a known false-positive mode.
    #
    # A child recorded with StartTime = MinValue (the -Wait children, which had already exited when
    # their record was made) can never match, which is the correct answer: a process that is already
    # gone cannot be a leftover.
    $strays = 0
    foreach ($record in $script:childRecords) {
        if (Test-ChildIdentity -TargetPid $record.ChildPid -ExpectedName $exeName -ExpectedStart $record.Start) {
            $strays = $strays + 1
            Write-Host "  stray: PID $($record.ChildPid) still alive AND identity-confirmed as this run's child ($script:identityDetail)"
        }
    }
    Write-Result -Name 'no leftover child process' -Ok ($strays -eq 0) `
        -Detail "$strays of $($script:childRecords.Count) tracked child(ren) still running (matched by PID + ProcessName + exact StartTime, never by name alone)"

    # The owner's real config root must be untouched. Same helper as the "before" read, so the
    # two composites are computed identically by construction.
    $realRootAfter = Get-TreeSnapshot -Root $realRoot
    Write-Result -Name 'owner config root untouched' -Ok ([string]::Equals($realRootAfter, $realRootBefore, [StringComparison]::Ordinal)) `
        -Detail 'relativepath+size+mtime composite of %LocalAppData%\BroChoos (sorted by full path) is ordinal-identical before and after'

    $script:keptNote = 'nothing outside the repo; in the repo: 2 scaffold sources, this tool, 1 test file, 1 evidence file (all deliverables of this task)'

    # The trigger gate is restored to whatever it was before this run, so the harness does not
    # leave a variable behind that would let a stray argument reach the child mode later in the
    # same shell session.
    [Environment]::SetEnvironmentVariable($script:killChildEnvName, $script:killChildEnvPrevious)
}

Write-Host ''
Write-Host "created $($script:createdNote) / removed $($script:removedNote) / kept $($script:keptNote)"
Write-Host ''
Write-Host '=== Summary ==='
if ($script:failures -eq 0) {
    Write-Host 'PASS: all atomic-write kill-process checks passed'
    exit 0
}

Write-Host "FAIL: $script:failures kill-process check(s) failed"
exit 1
