<#
.SYNOPSIS
    W1.4 adversarial IPC harness: a hostile SAME-WINDOWS-USER process attacks the local
    Dev Runner named pipe, and every attack must be refused fail-closed.

.DESCRIPTION
    The plan (W1.4 Files/symbols) names `tools/ipc-attacker.ps1` and the plan's acceptance
    gate is: "Security test harness demonstrates hostile same-user attempts cannot enqueue
    untrusted requests". The verification matrix lists the hostile column as "Same-user
    wrong signer/path, secret replay, wrong version, truncation, flood, no-ack/deadline
    simulation" with required evidence "Attacker commands, process identities/signatures,
    accepted/rejected reason codes."

    WHAT THIS HARNESS IS. It is the ATTACKER side. The defender (bounded queue, deadlines,
    replay rejection, ack, peer-image anchor, trust-anchor lifecycle, scheme gate) is
    already implemented and covered by `BoundedActivationQueueTests`,
    `WindowsSingleInstanceGuardProtocolTests`, `WindowsSingleInstanceGuardUrlSchemeTests`,
    and `WindowsSingleInstanceGuardPeerImageTests`. This file does not re-test those; it
    drives the REAL product guard through the REAL named pipe as a hostile peer would, and
    records whether each attempt was refused.

    NON-DESTRUCTIVE, AND THAT REQUIRES AN EXPLICIT SAFETY GATE. An earlier revision of this
    harness claimed "there is no way for it to reach a different instance". THAT CLAIM WAS
    FALSE and is corrected here, because the failure it hid was the worst kind: with a real
    product guard already running as the primary for this user, the harness's own
    `AcquireOrForwardAsync` returns `Secondary`, its pipe server never starts, and every
    subsequent attack then travels to the FOREIGN primary's live pipe - delivering hostile
    payloads (including `file:///.../calc.exe`) into another instance's picker handoff.

    The safety gate now runs BEFORE any byte is sent:

      1. The harness opens the per-user mutex with `initiallyOwned: false`. If the mutex
         already exists (`createdNew == false`), another instance owns it and the harness
         ABORTS with a non-zero exit and a loud message. It does not connect, and it does
         not forward.
      2. Even after `AcquireOrForwardAsync` returns, the harness requires the returned role
         to be exactly `Primary`. `Secondary` or `Failed` is treated as "a foreign primary
         owns this pipe" and the harness ABORTS before the first attack.
      3. `-SimulateForeignPrimary` is a self-check that deliberately makes the preflight see
         a foreign owner and exits non-zero, proving the abort path itself works.

    Everything below the gate then talks ONLY to the pipe this process created as primary:

      * No browser is launched, no URL is opened, no registry is written, no storage root
        is provisioned. The guard's `UrlReceived` event is observed, never acted on.
      * The harness does not send real secrets anywhere. One-shot secrets are generated
        in-process by the product's own validator and stay in memory.
      * The named pipe is created with `PipeOptions.CurrentUserOnly`, exactly as production
        does, so no other user can even read the attack traffic.

    REDACTION BY CONSTRUCTION. The harness logs a URL's SCHEME ONLY, never the full URL -
    the same rule the product's own guard follows (`DescribeSchemeForLog`). Attack payload
    URLs are harness-controlled probes, but the discipline is kept so a future probe cannot
    leak a value into the artifact.

    WHY IT MUST RUN UNDER PowerShell 7 (`pwsh`), NOT Windows PowerShell 5.1. The harness
    loads the REAL product assemblies and reflects over them. The product targets
    `net10.0-windows`, and only PowerShell 7.x runs on a .NET runtime that can load them;
    Windows PowerShell 5.1 is .NET Framework 4.x and cannot. The harness checks the runtime
    and fails loudly with an explanation instead of producing a misleading "no findings".

.PARAMETER BuildDirectory
    Directory holding the built product assemblies (`BroChoos.Platform.Windows.dll`,
    `BroChoos.Core.dll`, `Microsoft.Extensions.Logging.Abstractions.dll`). Defaults to the
    App's Release output. A missing assembly is a HARD failure, never a silent skip.

.PARAMETER OutputDirectory
    Where the redacted evidence artifact is written. Defaults to
    `artifacts/evidence/ipc-attacker` (ignored by git).

.PARAMETER QueueCapacity
    The primary's activation queue capacity the harness declares when checking the flood
    case. Product default is 5 (W1.4 "capacity five for concurrent-click requests").

.PARAMETER SimulateForeignPrimary
    Safety-gate self-check. Acquires the per-user mutex FIRST (standing in for a foreign
    primary), then runs the preflight. The harness MUST detect the pre-existing owner and
    abort with a non-zero exit without connecting to any pipe. Used to prove the safety
    gate works; never used for an attack run.

.EXAMPLE
    pwsh -NoProfile -ExecutionPolicy Bypass -File tools/ipc-attacker.ps1

.EXAMPLE
    # Prove the foreign-primary abort path (expected: non-zero exit, no attacks sent)
    pwsh -NoProfile -ExecutionPolicy Bypass -File tools/ipc-attacker.ps1 -SimulateForeignPrimary
#>
[CmdletBinding()]
param(
    [string] $BuildDirectory,
    [string] $OutputDirectory,
    [int] $QueueCapacity = 5,
    [switch] $SimulateForeignPrimary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------------------
# PREFLIGHT. A hostile harness that silently degrades is worse than no harness: a missing
# runtime or assembly must look like a hard failure, never like "the attacks found nothing".
# ---------------------------------------------------------------------------------------
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "tools/ipc-attacker.ps1 must run under PowerShell 7+ (pwsh), because it loads the net10.0 product assemblies. Current runtime: $($PSVersionTable.PSVersion). Windows PowerShell 5.1 runs .NET Framework and cannot load them."
}

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    # `dotnet build BroChoos.sln` builds every project under the solution's x64 platform
    # folder; the platform-less bin/Release path is kept for older checkouts.
    $candidates = @(
        (Join-Path $repoRoot 'src/BroChoos.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64'),
        (Join-Path $repoRoot 'src/BroChoos.App/bin/Release/net10.0-windows10.0.19041.0/win-x64')
    )
    $BuildDirectory = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'BroChoos.Platform.Windows.dll') -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
        $BuildDirectory = $candidates[0]
    }
}

$platformDll = Join-Path $BuildDirectory 'BroChoos.Platform.Windows.dll'
$coreDll = Join-Path $BuildDirectory 'BroChoos.Core.dll'
$loggingDll = Join-Path $BuildDirectory 'Microsoft.Extensions.Logging.Abstractions.dll'

foreach ($assemblyPath in @($platformDll, $coreDll, $loggingDll)) {
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Required product assembly not found: $assemblyPath. Build it first (dotnet build BroChoos.sln -c Release) or pass -BuildDirectory. A missing assembly is a HARD failure: it must never look like 'no attacks succeeded'."
    }
}

# The trust provider climbs AppContext.BaseDirectory for a `.git` plus `docs` pair and then
# reads docs/decisions/owner-rulings/dev-runner-ipc-trust-anchor.json. Point that base
# directory at the repository root so the REAL, owner-authorized trust record is the one
# exercised - the harness must not manufacture a fixture trust policy to make attacks pass.
[AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts/evidence/ipc-attacker'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# ---------------------------------------------------------------------------------------
# ARTIFACT GUARANTEE. A security harness that dies without evidence is worse than useless:
# the reader cannot tell "the run crashed" from "no attacks succeeded". An unexpected throw
# (an IOException on a pipe that broke at an unlucky moment, for instance) must therefore
# still produce an artifact and a non-zero verdict. The trap below is registered before any
# case runs and writes an emergency artifact naming the failure.
#
# The per-case fixes (non-throwing write helpers, non-throwing connect) mean this trap should
# never fire in practice; it exists so that "should never" is enforced rather than hoped for.
# ---------------------------------------------------------------------------------------
$script:harnessAborted = $null

# The patterns the final redaction sweep looks for, and the replacement applied to the artifact
# text itself. Defined before the trap so the emergency artifact is redacted the same way.
function Get-ArtifactLeakPatterns {
    return @(
        'C:\\Users\\',
        [System.Text.RegularExpressions.Regex]::Escape([string]$env:USERPROFILE),
        [System.Text.RegularExpressions.Regex]::Escape([string][System.Environment]::UserName)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

# Replaces every match of the leak patterns with a marker, so a leak is REMOVED from the written
# artifact and not merely annotated. A `C:\Users\<name>` prefix is replaced together with the
# account segment that follows it, so a different account's name cannot survive either.
function Protect-ArtifactText {
    param([string] $Text)
    $redacted = [regex]::Replace($Text, 'C:\\Users\\[^\\\s''"<>|]*', '<redacted:user-path>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    foreach ($pattern in (Get-ArtifactLeakPatterns)) {
        $redacted = [regex]::Replace($redacted, $pattern, '<redacted>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    return $redacted
}

# Initialized BEFORE the trap so the emergency artifact can always enumerate what completed,
# even if the abort happens before the main assertion plumbing is set up.
$script:results = [System.Collections.Generic.List[object]]::new()
$script:emergencyOutputDirectory = $OutputDirectory
trap {
    # The trap must survive its own failure: it runs while the run is already broken, so it
    # may not assume ANY outer variable exists or is well-formed, and it may not itself throw
    # (a throw inside a trap is what turns "aborted with evidence" into "aborted silently").
    # Every reference is therefore resolved defensively and the whole body is guarded.
    try {
        $abortReason = "$($_.Exception.GetType().Name)"
        try { $abortReason = $abortReason + ": " + $_.Exception.Message } catch { }
        $emergencyLines = [System.Collections.Generic.List[string]]::new()
        $emergencyLines.Add('W1.4 ipc-attacker.ps1 evidence - HARNESS ABORTED (unexpected failure)')
        try { $emergencyLines.Add("generated_at_utc: $((Get-Date).ToUniversalTime().ToString('o'))") } catch { }

        $completed = 0
        try { if ($null -ne $script:results) { $completed = $script:results.Count } } catch { }
        $emergencyLines.Add("abort_reason: $abortReason")
        $emergencyLines.Add("cases_completed_before_abort: $completed")
        try {
            if ($null -ne $script:results) {
                foreach ($result in $script:results) {
                    $status = 'UNKNOWN'
                    try { $status = if ($result.Passed) { 'PASS' } else { 'FAIL' } } catch { }
                    $emergencyLines.Add("$status $($result.CaseId) :: $($result.Observation)")
                }
            }
        }
        catch { }
        $emergencyLines.Add('')
        $emergencyLines.Add('This artifact exists because the harness aborted. The failure is a HARNESS fault, not a')
        $emergencyLines.Add('product result; no claim about the product may be drawn from this run.')

        # Resolve an output directory without trusting any single scope.
        $emergencyDir = $null
        try { if (-not [string]::IsNullOrWhiteSpace($script:emergencyOutputDirectory)) { $emergencyDir = $script:emergencyOutputDirectory } } catch { }
        if ([string]::IsNullOrWhiteSpace($emergencyDir)) { $emergencyDir = [System.IO.Path]::GetTempPath() }
        if (-not (Test-Path -LiteralPath $emergencyDir)) { $emergencyDir = [System.IO.Path]::GetTempPath() }

        $emergencyStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
        $emergencyPath = Join-Path $emergencyDir "w1.4-ipc-attacker-ABORTED-$emergencyStamp.txt"
        # Redact before writing; if redaction itself fails, withhold the content rather than leak it.
        $emergencyText = $null
        try { $emergencyText = Protect-ArtifactText -Text ($emergencyLines -join [Environment]::NewLine) } catch { }
        if ($null -eq $emergencyText) { $emergencyText = 'W1.4 ipc-attacker.ps1 HARNESS ABORTED; artifact content withheld because redaction failed.' }
        [System.IO.File]::WriteAllText($emergencyPath, $emergencyText, (New-Object System.Text.UTF8Encoding($false)))
        Write-Output "IPC_ATTACKER_ARTIFACT=$emergencyPath"
        Write-Output 'IPC_ATTACKER_RESULT=HARNESS-ABORTED'
        Write-Output "IPC_ATTACKER_ABORT_REASON=$abortReason"
        Write-Output 'IPC_ATTACKER_EXIT=2'
    }
    catch {
        # Last resort: the trap itself failed. Say so on stderr and still leave with 2.
        try { [Console]::Error.WriteLine("HARNESS ABORT (trap failed): $($_.Exception.Message)") } catch { }
        try { Write-Output 'IPC_ATTACKER_RESULT=HARNESS-ABORTED' } catch { }
        try { Write-Output 'IPC_ATTACKER_EXIT=2' } catch { }
    }
    exit 2
}

# ---------------------------------------------------------------------------------------
# Load the real product assemblies.
# ---------------------------------------------------------------------------------------
[Reflection.Assembly]::LoadFrom($loggingDll) | Out-Null
[Reflection.Assembly]::LoadFrom($coreDll) | Out-Null
[Reflection.Assembly]::LoadFrom($platformDll) | Out-Null

# ---------------------------------------------------------------------------------------
# The C# bridge. Two problems are solved here and nowhere else:
#
#   1. THE DRAIN THREAD HAS NO POWERSHELL RUNSPACE. The guard raises `UrlReceived` on its
#      own drain-loop thread. A PowerShell `{ ... }` event handler invoked there throws
#      "no runspace available", which would make every accepted request look like a
#      dropped one. A compiled C# handler has no such dependency.
#   2. THE LOGGER MUST SURVIVE CROSS-THREAD WRITES. The guard logs from its server loop.
#      A compiled, locked sink is the only honest way to read those entries back.
#
# `WaitForDelivered` polls the same locked list, so the harness never needs to await a
# runspace work item while the guard's loops are running.
# ---------------------------------------------------------------------------------------
$runtimeRefPack = 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref'
$refPackVersion = (Get-ChildItem -LiteralPath $runtimeRefPack -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name
if ([string]::IsNullOrWhiteSpace($refPackVersion)) {
    throw "Could not locate the Microsoft.NETCore.App.Ref reference pack under $runtimeRefPack; Add-Type cannot compile the harness bridge."
}

$refPackDirectory = Join-Path $runtimeRefPack "$refPackVersion/ref/net10.0"
if (-not (Test-Path -LiteralPath $refPackDirectory)) {
    throw "The .NET reference pack for net10.0 is absent: $refPackDirectory"
}

$compileReferences = @(Get-ChildItem -LiteralPath $refPackDirectory -Filter '*.dll' | Select-Object -ExpandProperty FullName)
$compileReferences += @($loggingDll, $coreDll, $platformDll)

$bridgeSource = @'
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using BroChoos.Core.Ingress;
using BroChoos.Platform.Windows.SingleInstance;

// Records every activation the guard hands to the picker handoff, from whatever thread it
// arrives on, with no PowerShell runspace dependency.
//
// QUIESCENCE IS THE POINT OF THE COUNTERS BELOW. The guard's drain loop runs on its own
// thread and continues delivering after a case's last exchange, so a case that merely reads
// DeliveredCount can observe another case's backlog and either pass spuriously (the count
// already advanced) or fail on contention (its own delivery is starved behind the backlog).
// The observer therefore tracks a monotonically increasing generation that the harness can
// read, compare, and WAIT ON. Every delivery assertion is made against a generation captured
// immediately before that case's own send, and no case proceeds until the drain is proven
// quiescent.
public sealed class IpcAttackObserver
{
    private readonly List<string> _urls = new List<string>();
    private readonly object _gate = new object();
    private volatile bool _blocked;
    private volatile bool _handlerEntered;
    private volatile bool _handlerReleased;
    private volatile bool _handlerActive;
    private long _generation;

    public int DeliveredCount { get { lock (_gate) { return _urls.Count; } } }

    public string[] DeliveredUrls { get { lock (_gate) { return _urls.ToArray(); } } }

    // Monotonic count of deliveries. Read atomically; used as the baseline a case waits past.
    public long DeliveredGeneration { get { return Interlocked.Read(ref _generation); } }

    // True once the observer handler has been entered while blocked - i.e. the guard's drain
    // thread is parked INSIDE the handler and can no longer call DrainAllPending. That is the
    // deterministic "drain is frozen" signal the flood case waits on.
    public bool HandlerParked { get { return _handlerEntered && !_handlerReleased; } }

    // True while a handler invocation is in flight. Combined with a stable generation this
    // proves the drain thread is idle rather than merely between two of a case's deliveries.
    public bool HandlerActive { get { return _handlerActive; } }

    // Blocks the guard's DRAIN thread on purpose. The observer is invoked from that thread,
    // so holding it here is the only way to make the capacity-five queue actually fill up
    // without touching the product. Used only by the flood case, and always released.
    public void BlockDrain(bool blocked)
    {
        _blocked = blocked;
        if (!blocked) { _handlerReleased = true; }
    }

    public void OnUrlReceived(object sender, ActivationRequest activation)
    {
        _handlerActive = true;
        try
        {
            if (_blocked)
            {
                _handlerEntered = true;
            }
            while (_blocked)
            {
                System.Threading.Thread.Sleep(10);
            }
            lock (_gate) { _urls.Add(activation.NormalizedUrl); }
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _handlerActive = false;
        }
    }

    // Waits until the drain thread is parked inside a blocked handler call, or the timeout
    // expires. Returns false on timeout so the caller can report a fact, not hang.
    public bool WaitForHandlerParked(int timeoutMs)
    {
        int waited = 0;
        while (!HandlerParked)
        {
            if (waited >= timeoutMs) { return false; }
            Thread.Sleep(10);
            waited += 10;
        }
        return true;
    }

    // PROVES THE DRAIN IS QUIESCENT: no handler is running AND the generation has not moved
    // for `idleMs`. This is the handshake each case waits on before it starts, so its own
    // delivery assertion starts from a proven-idle state rather than from a hope. A backlog
    // from a previous case keeps the generation moving and is waited out here, not slept past.
    public bool WaitForQuiescent(int idleMs, int timeoutMs)
    {
        int waited = 0;
        long lastGeneration = DeliveredGeneration;
        int stableFor = 0;
        while (waited < timeoutMs)
        {
            if (HandlerActive)
            {
                stableFor = 0;
            }
            else
            {
                long current = DeliveredGeneration;
                if (current == lastGeneration)
                {
                    stableFor += 10;
                    if (stableFor >= idleMs) { return true; }
                }
                else
                {
                    lastGeneration = current;
                    stableFor = 0;
                }
            }
            Thread.Sleep(10);
            waited += 10;
        }
        return false;
    }

    // Waits for at least `count` deliveries, returning false on timeout rather than
    // throwing, so a refusal assertion reports a fact instead of crashing the run.
    public bool WaitForDelivered(int count, int timeoutMs)
    {
        int waited = 0;
        while (DeliveredCount < count)
        {
            if (waited >= timeoutMs) { return false; }
            Thread.Sleep(50);
            waited += 50;
        }
        return true;
    }

    // Waits until the delivery GENERATION exceeds `baseline`, i.e. this case's own delivery
    // arrived. Generation-based rather than count-based so a concurrent backlog can never
    // satisfy it on the case's behalf.
    public bool WaitForGenerationPast(long baseline, int timeoutMs)
    {
        int waited = 0;
        while (DeliveredGeneration <= baseline)
        {
            if (waited >= timeoutMs) { return false; }
            Thread.Sleep(50);
            waited += 50;
        }
        return true;
    }
}

// Captures the guard's own refusal markers at every level. Locked because the guard logs
// from its server-loop thread.
public sealed class IpcAttackLogger : ILogger<WindowsSingleInstanceGuard>
{
    private readonly List<string> _entries = new List<string>();
    private readonly object _gate = new object();

    public string[] Entries { get { lock (_gate) { return _entries.ToArray(); } } }

    public void Clear() { lock (_gate) { _entries.Clear(); } }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull { return null; }

    public bool IsEnabled(LogLevel logLevel) { return true; }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        lock (_gate) { _entries.Add(formatter(state, exception)); }
    }
}

// A trust provider that reports a REVOKED anchor, mirroring the production provider's
// revocation branch (IpcTrustPolicy.cs:119-124) without touching the real owner record.
// Used by A16 as a STUB baseline only; the REAL parser is exercised separately by A16b via
// OwnerRulingFileIpcTrustPolicyProvider against a fixture record tree.
public sealed class IpcRevokedTrustPolicyProvider : IIpcTrustPolicyProvider
{
    public bool TryGetPolicy(out IpcTrustPolicy policy, out IpcPeerAuthenticationFailure failure)
    {
        policy = null;
        failure = new IpcPeerAuthenticationFailure(
            IpcPeerAuthenticationErrorCode.TrustAnchorRevoked,
            "ipc.trust-anchor.revoked");
        return false;
    }
}

// A deliberately silent pipe server, used to prove the CLIENT ack deadline. It must live in
// compiled code for the same reason the observer does: a PowerShell script block cannot run
// on a bare thread-pool thread that has no runspace.
public sealed class SilentPipeServer : IDisposable{
    private readonly System.Threading.Thread _thread;
    private readonly System.Threading.CancellationTokenSource _cts = new System.Threading.CancellationTokenSource();
    private readonly string _pipeName;

    public SilentPipeServer(string pipeName)
    {
        _pipeName = pipeName;
        _thread = new System.Threading.Thread(Run) { IsBackground = true };
    }

    public void Start()
    {
        _thread.Start();
        // Give the server time to be listening before the client connects.
        System.Threading.Thread.Sleep(300);
    }

    private void Run()
    {
        System.IO.Pipes.NamedPipeServerStream server = null;
        try
        {
            server = new System.IO.Pipes.NamedPipeServerStream(
                _pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                1,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous);
            server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
            byte[] buffer = new byte[8192];
            // Read whatever arrives, then stay silent well past the client's deadline.
            while (!_cts.IsCancellationRequested && server.IsConnected)
            {
                try
                {
                    var read = server.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                    if (!read.Wait(250)) { continue; }
                    if (read.Result == 0) { break; }
                }
                catch
                {
                    break;
                }
            }
        }
        catch
        {
            // Teardown races are expected; the client's observation is the real result.
        }
        finally
        {
            if (server != null) { server.Dispose(); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _thread.Join(2000); } catch { }
        _cts.Dispose();
    }
}
'@

Add-Type -TypeDefinition $bridgeSource -ReferencedAssemblies $compileReferences -ErrorAction Stop

# ---------------------------------------------------------------------------------------
# Host the REAL guard as the primary, in THIS process. The guard's own constructor derives
# the per-user mutex and pipe names and records the canonical local executable path; the
# trust policy is the owner-authorized production record reached through AppContext.
# ---------------------------------------------------------------------------------------
$guardType = [BroChoos.Platform.Windows.SingleInstance.WindowsSingleInstanceGuard]
$secretValidatorType = [BroChoos.Platform.Windows.SingleInstance.OneShotSecretValidator]
$authenticatorType = [BroChoos.Platform.Windows.SingleInstance.IpcPeerAuthenticator]
$trustProviderType = [BroChoos.Platform.Windows.SingleInstance.OwnerRulingFileIpcTrustPolicyProvider]
$urlValidatorType = [BroChoos.Platform.Windows.Urls.DefaultUrlValidator]
$requestType = [BroChoos.Core.Ingress.ActivationRequest]
$roleType = [BroChoos.Core.Abstractions.SingleInstanceRole]

$logger = [IpcAttackLogger]::new()
$observer = [IpcAttackObserver]::new()
$secretValidator = [Activator]::CreateInstance($secretValidatorType)
$trustProvider = [Activator]::CreateInstance($trustProviderType)
$authenticator = [Activator]::CreateInstance($authenticatorType, @($secretValidator, $trustProvider))
$urlValidator = [Activator]::CreateInstance($urlValidatorType)

$trustPolicy = $null
$trustFailure = $null
$trustResolved = $trustProvider.TryGetPolicy([ref]$trustPolicy, [ref]$trustFailure)
$trustSubject = if ($trustResolved) { $trustPolicy.SubjectId } else { '(unresolved)' }

$guard = [Activator]::CreateInstance($guardType, @($logger, $authenticator, $secretValidator, $urlValidator))
$observerHandler = [System.EventHandler[BroChoos.Core.Ingress.ActivationRequest]]$observer.OnUrlReceived
$guard.add_UrlReceived($observerHandler)

$pipeField = $guardType.GetField('_pipeName', [Reflection.BindingFlags]'Instance,NonPublic')
$mutexField = $guardType.GetField('_mutexName', [Reflection.BindingFlags]'Instance,NonPublic')
$exeField = $guardType.GetField('_localExecutablePath', [Reflection.BindingFlags]'Instance,NonPublic')
$signerField = $guardType.GetField('_localSignerIdentity', [Reflection.BindingFlags]'Instance,NonPublic')

$pipeName = $pipeField.GetValue($guard)
$mutexName = $mutexField.GetValue($guard)
# The canonical executable path and signer the PRODUCT itself computed. The impostor cases
# deliberately deviate from these; the positive control uses them.
$localExecutablePath = $exeField.GetValue($guard)
$localSignerIdentity = $signerField.GetValue($guard)

# =======================================================================================
# SAFETY GATE - STEP 1 of 2. DETECT A FOREIGN PRIMARY BEFORE ACQUIRING OR CONNECTING.
#
# This is the fix for the worst defect this harness had: with another instance already the
# primary for this user, the harness's own `AcquireOrForwardAsync` returns `Secondary`, its
# pipe server never starts, and every attack silently travels to the FOREIGN primary's live
# pipe - forwarding hostile payloads into a real picker handoff.
#
# HOW THE OWNERSHIP FACT IS READ. The product creates its mutex with
# `new Mutex(initiallyOwned: true, name, out createdNew)` (WindowsSingleInstanceGuard.cs:93).
# This harness opens the SAME name with `initiallyOwned: false`. `createdNew` is then the
# authoritative cross-process answer:
#
#   createdNew == true  -> nobody held the name; this process may proceed to become primary.
#   createdNew == false -> the name already existed, i.e. SOME owner exists. Because this is
#                          a fresh harness process, that owner cannot be this harness, so it
#                          is a foreign primary. ABORT.
#
# WHY WaitOne IS NOT USED TO DISAMBIGUATE. A Win32 mutex is reentrant PER THREAD: opening it
# again on the same thread and calling WaitOne(0) succeeds even while another owner holds it,
# so an in-process probe cannot tell a live owner from an abandoned one. That was measured
# (createdNew=False yet WaitOne(0)=True on the owning thread), and it is exactly the trap
# that would have made this gate silently useless. `createdNew` alone is correct across
# processes, and a stale/abandoned name is itself a reason not to run - the honest answer to
# "is this pipe mine?" is NO.
#
# `-SimulateForeignPrimary` holds the mutex in a SEPARATE child process, because only a real
# second process reproduces the cross-process condition the gate exists to detect.
# =======================================================================================
$foreignPrimaryProcess = $null
if ($SimulateForeignPrimary) {
    # A separate process is required: an in-process holder is invisible to the check above.
    $foreignHolderScript = @"
`$ErrorActionPreference = 'Stop'
[bool]`$created = `$false
`$m = [System.Threading.Mutex]::new(`$false, '$mutexName', [ref]`$created)
`$null = `$m.WaitOne(0)
Write-Output "FOREIGN_HOLDER_READY createdNew=`$created"
Start-Sleep -Seconds 60
`$m.ReleaseMutex()
`$m.Dispose()
"@
    $foreignHolderPath = Join-Path ([System.IO.Path]::GetTempPath()) "brochoos-w14-foreign-$([Guid]::NewGuid().ToString('N')).ps1"
    [System.IO.File]::WriteAllText($foreignHolderPath, $foreignHolderScript, (New-Object System.Text.UTF8Encoding($false)))
    $foreignPrimaryProcess = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $foreignHolderPath) -PassThru -WindowStyle Hidden
    $script:foreignHolderScriptPath = $foreignHolderPath

    # Wait until the child really holds the mutex before running the gate: poll for the mutex to
    # exist (bounded deadline) instead of sleeping a fixed time, and give up early if the child
    # exits. Any handle opened by the probe is disposed immediately so it never keeps the mutex
    # alive or perturbs the gate check below.
    $foreignReady = $false
    $foreignDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $foreignDeadline) {
        if ($foreignPrimaryProcess.HasExited) { break }
        [System.Threading.Mutex]$openedMutex = $null
        if ([System.Threading.Mutex]::TryOpenExisting($mutexName, [ref]$openedMutex)) {
            $openedMutex.Dispose()
            $foreignReady = $true
            break
        }
        Start-Sleep -Milliseconds 50
    }

    if (-not $foreignReady) {
        # The simulation itself could not be set up: a harness defect, reported as one.
        $foreignExited = $foreignPrimaryProcess.HasExited
        try { Stop-Process -Id $foreignPrimaryProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
        Remove-Item -LiteralPath $foreignHolderPath -Force -ErrorAction SilentlyContinue
        [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)
        $foreignWhy = if ($foreignExited) { 'the child process exited before holding the mutex' } else { 'the child did not create the mutex within 30 s' }
        Write-Output "IPC_ATTACKER_SELFCHECK=failed (foreign holder not ready: $foreignWhy)"
        Write-Output 'IPC_ATTACKER_EXIT=4'
        [Console]::Error.WriteLine("SAFETY SELF-CHECK FAILED: the simulated foreign primary never signalled readiness ($foreignWhy).")
        exit 4
    }
}

[bool]$mutexProbeCreatedNew = $false
$mutexProbe = [System.Threading.Mutex]::new($false, $mutexName, [ref]$mutexProbeCreatedNew)
$mutexProbe.Dispose()

if (-not $mutexProbeCreatedNew) {
    if ($null -ne $foreignPrimaryProcess) {
        try { Stop-Process -Id $foreignPrimaryProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
        if (-not [string]::IsNullOrWhiteSpace($script:foreignHolderScriptPath)) {
            Remove-Item -LiteralPath $script:foreignHolderScriptPath -Force -ErrorAction SilentlyContinue
        }
        # The simulation worked: the gate detected the separate-process holder.
        [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)
        Write-Output 'IPC_ATTACKER_SELFCHECK=passed (separate-process primary detected and aborted)'
        Write-Output "IPC_ATTACKER_SAFETY_ABORT=foreign-primary-detected"
        Write-Output "IPC_ATTACKER_SAFETY_DETAIL=$mutexName"
        Write-Output 'IPC_ATTACKER_EXIT=3'
        exit 3
    }

    $message = "SAFETY ABORT: the mutex $mutexName already exists, so a foreign (or stale) primary owns this user's single-instance identity. This harness refuses to run because its attacks would be forwarded into that live instance's pipe. Stop the other BroChoos instance, then re-run. Use -SimulateForeignPrimary only to test this abort."
    Write-Output 'IPC_ATTACKER_SAFETY_ABORT=foreign-primary-detected'
    Write-Output "IPC_ATTACKER_SAFETY_DETAIL=$mutexName"
    Write-Output 'IPC_ATTACKER_EXIT=3'
    # `throw` would terminate PowerShell with exit code 1 and make the advertised 3 a lie.
    # Write the diagnostic to stderr, then exit with the number the harness just printed.
    [Console]::Error.WriteLine($message)
    exit 3
}

if ($SimulateForeignPrimary) {
    # Reaching here means the separate-process holder was NOT detected. That is a harness
    # defect and must be reported as one, loudly, never silently.
    try { Stop-Process -Id $foreignPrimaryProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    if (-not [string]::IsNullOrWhiteSpace($script:foreignHolderScriptPath)) {
        Remove-Item -LiteralPath $script:foreignHolderScriptPath -Force -ErrorAction SilentlyContinue
    }
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)
    Write-Output 'IPC_ATTACKER_SELFCHECK=failed (the separate-process holder was NOT detected)'
    Write-Output 'IPC_ATTACKER_EXIT=4'
    [Console]::Error.WriteLine('SAFETY SELF-CHECK FAILED: a separate process held the per-user mutex, but the gate did not detect a foreign primary.')
    exit 4
}

$primaryCts = [System.Threading.CancellationTokenSource]::new()
$primaryRequest = [Activator]::CreateInstance($requestType, @(
    'https://primary.invalid/',
    [long]1,
    [BroChoos.Core.Ingress.ActivationTriggerContext]::CommandLine,
    [BroChoos.Core.Ingress.SourceEvidence]::Confirmed('harness'),
    [BroChoos.Core.Ingress.ActivationModifiers]::None,
    [Guid]::NewGuid(),
    1,
    [DateTimeOffset]::UtcNow.AddMinutes(5),
    $null))

# Taking the primary role creates the pipe server; the guard's server and drain loops keep
# running on thread-pool tasks while this script attacks them. Because `AcquireOrForwardAsync`
# does no blocking work once it owns the mutex, this returns almost immediately.
$primaryRole = $guard.AcquireOrForwardAsync($primaryRequest, $primaryCts.Token).GetAwaiter().GetResult()

# =======================================================================================
# SAFETY GATE - STEP 2 of 2. REQUIRE THE ROLE TO BE EXACTLY `Primary`.
#
# Step 1 is a preflight against a mutex snapshot; step 2 is the authoritative confirmation
# from the product itself. `Secondary` means the guard connected to somebody else's server,
# and `Failed` means it reached nothing. Either way the pipe the harness is about to attack
# is NOT the harness's own, so the harness aborts before the first attack byte. This closes
# the window where a foreign primary appears between step 1 and the acquire.
# =======================================================================================
if ($primaryRole -ne [BroChoos.Core.Abstractions.SingleInstanceRole]::Primary) {
    $guard.remove_UrlReceived($observerHandler)
    $primaryCts.Cancel()
    $guard.Dispose()
    $primaryCts.Dispose()
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)
    Write-Output "IPC_ATTACKER_SAFETY_ABORT=role-not-primary"
    Write-Output "IPC_ATTACKER_SAFETY_DETAIL=role=$primaryRole pipe=$pipeName"
    Write-Output "IPC_ATTACKER_EXIT=3"
    [Console]::Error.WriteLine("SAFETY ABORT: AcquireOrForwardAsync returned $primaryRole, not Primary. A foreign primary owns $pipeName; refusing to send any attack. No request was forwarded.")
    exit 3
}

# ---------------------------------------------------------------------------------------
# The wire protocol, rebuilt exactly as the product builds it. These helpers are the
# attacker's only transport; they contain no product types so a protocol change shows up as
# an observable mismatch rather than a compile-time redirect.
#
# PIPE WRITES NEVER THROW. A hostile peer is SUPPOSED to see the server slam the pipe shut
# (that is the refusal this harness scores), so an IOException("Pipe is broken") is an
# expected case outcome, not a harness fault. Letting it propagate killed the whole run with
# no artifact, which is strictly worse than a scored failure. Every write helper therefore
# returns $true/$false and records nothing about the failure beyond its type name.
function Write-LengthPrefixedFrame {
    param(
        [System.IO.Pipes.PipeStream] $Pipe,
        [byte[]] $Body
    )
    try {
        $lengthPrefix = [BitConverter]::GetBytes([int]$Body.Length)
        [Array]::Reverse($lengthPrefix)
        $Pipe.Write($lengthPrefix, 0, 4)
        if ($Body.Length -gt 0) { $Pipe.Write($Body, 0, $Body.Length) }
        $Pipe.Flush()
        return $true
    }
    catch [System.IO.IOException] {
        # The server closed the connection while the client was writing. Expected.
        return $false
    }
    catch [System.ObjectDisposedException] {
        # The pipe was disposed under us (teardown race). Expected.
        return $false
    }
    catch [System.InvalidOperationException] {
        # Writing to a pipe that is not connected. Expected.
        return $false
    }
}

function Write-Utf8Frame {
    param(
        [System.IO.Pipes.PipeStream] $Pipe,
        [string] $Payload
    )
    return (Write-LengthPrefixedFrame -Pipe $Pipe -Body ([System.Text.Encoding]::UTF8.GetBytes($Payload)))
}

function New-PeerIdentityFrame {
    param(
        [string] $ExecutablePath,
        [string] $SignerIdentity,
        [string] $OneShotSecret
    )
    $payload = 'PEER|' +
        [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($ExecutablePath)) + '|' +
        [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($SignerIdentity)) + '|' +
        [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($OneShotSecret))
    return [System.Text.Encoding]::UTF8.GetBytes($payload)
}

function New-RequestFrame {
    param(
        [string] $Url,
        [Guid] $RequestId,
        [DateTimeOffset] $ExpiresAtUtc,
        [int] $ProtocolVersion = 1
    )
    $payload = 'REQ|' +
        $ProtocolVersion.ToString([System.Globalization.CultureInfo]::InvariantCulture) + '|' +
        $RequestId.ToString('N') + '|' +
        $ExpiresAtUtc.ToUnixTimeMilliseconds().ToString([System.Globalization.CultureInfo]::InvariantCulture) + '|' +
        [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Url))
    return [System.Text.Encoding]::UTF8.GetBytes($payload)
}

# Opens a pipe to the harness's own primary. NEVER THROWS: if the connect fails (for example
# because the single-threaded server is still busy with a previous connection), the returned
# stream is simply left disconnected, and the write helpers report $false for it. Callers can
# therefore keep their straight-line shape while a dead pipe becomes a scored outcome.
function Open-AttackPipe {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $pipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect(3000)
    }
    catch [System.TimeoutException] {
        # Left disconnected on purpose; the caller's write/read will report the failure.
    }
    catch [System.IO.IOException] {
        # Left disconnected on purpose.
    }
    return $client
}

# Reads EXACTLY one ack frame the way the product's secondary does, and returns the typed
# status, or $null when the server closed the connection without answering. A closed
# connection is itself a refusal, which is how the peer-image cases fail closed.
function Read-AckStatus {
    param(
        [System.IO.Pipes.PipeStream] $Pipe,
        [int] $TimeoutMs = 3000
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $readTask = $null
    try {
        $lengthBuffer = New-Object byte[] 4
        $read = 0
        while ($read -lt 4 -and [DateTime]::UtcNow -lt $deadline) {
            $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            $readTask = $Pipe.ReadAsync($lengthBuffer, $read, 4 - $read)
            if (-not $readTask.Wait($remaining)) { return $null }
            $n = $readTask.Result
            if ($n -eq 0) { return $null }
            $read += $n
        }
        if ($read -lt 4) { return $null }

        [Array]::Reverse($lengthBuffer)
        $payloadLength = [BitConverter]::ToInt32($lengthBuffer, 0)
        if ($payloadLength -le 0 -or $payloadLength -gt 8192) { return $null }

        $payload = New-Object byte[] $payloadLength
        $read = 0
        while ($read -lt $payloadLength -and [DateTime]::UtcNow -lt $deadline) {
            $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            $readTask = $Pipe.ReadAsync($payload, $read, $payloadLength - $read)
            if (-not $readTask.Wait($remaining)) { return $null }
            $n = $readTask.Result
            if ($n -eq 0) { break }
            $read += $n
        }

        $text = [System.Text.Encoding]::UTF8.GetString($payload, 0, $read)
        $fields = $text.Split('|')
        if ($fields.Length -ne 4 -or $fields[0] -ne 'ACK') { return $null }
        return $fields[3]
    }
    catch {
        # A server that slams the pipe shut mid-read is a refusal, not a harness error.
        return $null
    }
}

# A complete exchange: identity frame, then request frame, then read the ack. Returns the
# typed observation the assertions consume.
#
# THE QUIESCENCE BARRIER IS INSIDE THIS FUNCTION, deliberately: every attack exchange is sent
# only once the guard's drain has been proven idle (no handler running, generation unmoved for
# 300 ms). That is stronger and less error-prone than asking each call site to remember it,
# and it is what makes a lingering backlog from a previous case (A13's burst in particular)
# impossible to race against. A13 does not use this function - it drives raw clients while the
# drain is deliberately frozen - so the barrier never fights the one case that needs a blocked
# drain.
function Invoke-AttackExchange {
    param(
        [byte[]] $IdentityFrame,
        [byte[]] $RequestFrame,
        [int] $AckTimeoutMs = 3000,
        [switch] $SkipAck,
        [switch] $SkipQuiescence
    )
    if (-not $SkipQuiescence) {
        $null = $observer.WaitForQuiescent(300, 15000)
    }
    $client = Open-AttackPipe
    try {
        $null = Write-LengthPrefixedFrame -Pipe $client -Body $IdentityFrame
        if ($null -ne $RequestFrame) {
            $null = Write-LengthPrefixedFrame -Pipe $client -Body $RequestFrame
        }
        if ($SkipAck) { return [pscustomobject]@{ AckStatus = $null; Connected = $true } }
        $status = Read-AckStatus -Pipe $client -TimeoutMs $AckTimeoutMs
        return [pscustomobject]@{ AckStatus = $status; Connected = $true }
    }
    catch {
        return [pscustomobject]@{ AckStatus = $null; Connected = $false }
    }
    finally {
        $client.Dispose()
    }
}

# ---------------------------------------------------------------------------------------
# Assertion plumbing. Each attack declares the fail-closed outcome it expects; the harness
# records the observed reason codes rather than asserting a specific internal marker it
# does not own (a product refactor may change a marker without changing fail-closedness).
#
# `$script:results` is initialized EARLY (before the artifact-guarantee trap) so an abort at
# any point can still enumerate what had completed. Only the failure list is created here.
# ---------------------------------------------------------------------------------------
$script:failures = [System.Collections.Generic.List[string]]::new()

function Add-AttackResult {
    param(
        [string] $CaseId,
        [string] $Description,
        [bool] $Passed,
        [string] $Observation,
        [string[]] $ReasonCodes,
        [ValidateSet('STRONG', 'WEAK', 'LIMITED')]
        [string] $Strength = 'STRONG',
        [string] $StrengthNote = ''
    )
    $script:results.Add([pscustomobject]@{
            CaseId       = $CaseId
            Description  = $Description
            Passed       = $Passed
            Observation  = $Observation
            ReasonCodes  = $ReasonCodes
            Strength     = $Strength
            StrengthNote = $StrengthNote
        })
    if (-not $Passed) {
        $script:failures.Add("$CaseId :: $Observation")
    }
}

# The scheme-only projection the product's own guard uses. Never the full URL.
function Get-SchemeOnly {
    param([string] $Url)
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) { return '(unparseable)' }
    $scheme = $uri.Scheme
    if ($scheme.Length -le 32) { return $scheme }
    return $scheme.Substring(0, 32)
}

# The drain loop polls every 25 ms; a refusal is proven by the delivered count NOT moving.
# The wait is deliberate: it gives a would-be enqueue every chance to appear, so a PASS is
# an observed absence rather than a race won by the harness.
#
# GENERATION-BASED, not count-based. Asserting `DeliveredCount == BaselineCount` would be
# satisfied by a concurrent backlog from a previous case just as easily as by a real refusal.
# The generation is monotonic and only moves on an actual delivery, so comparing generations
# before and after the settle window is immune to a stale baseline.
#
# The baseline MUST be captured by the caller BEFORE the case's first refused send. Sampling it
# here, after the sends, would miss a delivery that landed during the send/ack exchange itself
# and so could report "no new delivery" for a request that was in fact enqueued.
function Assert-NoNewDelivery {
    param(
        [Parameter(Mandatory = $true)]
        [long] $BaselineGeneration,
        [int] $TimeoutMs = 800
    )
    Start-Sleep -Milliseconds $TimeoutMs
    return $observer.DeliveredGeneration -eq $BaselineGeneration
}

# PROVEN-QUIESCENT BARRIER. Every case calls this BEFORE its first exchange, so it starts from
# a state where the drain thread is idle and no previous case's backlog is still in flight.
# The quiescence is a positive handshake (no active handler AND an unmoving generation for
# 300 ms), not a sleep: a lingering A13 backlog keeps the generation moving and is waited out
# rather than raced past. Returns $true only when quiescence was actually observed.
$script:quiescenceFailures = [System.Collections.Generic.List[string]]::new()

function Sync-DrainQuiescent {
    param([string] $CaseId)
    $quiescent = $observer.WaitForQuiescent(300, 15000)
    if (-not $quiescent) {
        $script:quiescenceFailures.Add("$CaseId :: drain did not reach quiescence within 15 s")
    }
    return $quiescent
}

# Waits for THIS case's own delivery, identified by a generation baseline captured just
# before the case sent its request. A concurrent backlog therefore cannot satisfy it.
function Wait-ForOwnDelivery {
    param(
        [long] $BaselineGeneration,
        [int] $TimeoutMs = 5000
    )
    return $observer.WaitForGenerationPast($BaselineGeneration, $TimeoutMs)
}

# ---------------------------------------------------------------------------------------
# REDACTION. Operator machine paths must never reach the evidence document. A stable
# placeholder plus a short content hash keeps the artifact useful (two runs from the same tree
# are recognisably the same tree) without leaking a username or a machine layout.
#
# The placeholder is chosen from the path's ROLE, not by pattern-matching a drive letter, so a
# relocated repository (D:\, a UNC share, a CI checkout) is redacted the same way.
# ---------------------------------------------------------------------------------------
function Get-RedactedPathLabel {
    param(
        [string] $Path,
        [string] $Placeholder
    )
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return "$Placeholder (empty)"
    }
    $hashInput = [System.Text.Encoding]::UTF8.GetBytes($Path)
    $hashStream = [System.IO.MemoryStream]::new($hashInput)
    try {
        $digest = (Get-FileHash -Algorithm SHA256 -InputStream $hashStream).Hash.ToLowerInvariant()
    }
    finally {
        $hashStream.Dispose()
    }
    return "$Placeholder [sha256:$($digest.Substring(0, 16))]"
}

$artifact = [System.Collections.Generic.List[string]]::new()
$artifact.Add('W1.4 ipc-attacker.ps1 evidence (adversarial, redacted, non-destructive)')
$artifact.Add("generated_at_utc: $((Get-Date).ToUniversalTime().ToString('o'))")
$artifact.Add("powershell: $($PSVersionTable.PSVersion)")
$artifact.Add("platform: $([System.Environment]::OSVersion.VersionString)")
$artifact.Add("repository: $(Get-RedactedPathLabel -Path $repoRoot -Placeholder '<repo>')")
$artifact.Add("build_directory: $(Get-RedactedPathLabel -Path $BuildDirectory -Placeholder '<build-dir>')")
$artifact.Add("platform_assembly_sha256: $((Get-FileHash -LiteralPath $platformDll -Algorithm SHA256).Hash.ToLowerInvariant())")
$artifact.Add("core_assembly_sha256: $((Get-FileHash -LiteralPath $coreDll -Algorithm SHA256).Hash.ToLowerInvariant())")
$artifact.Add('')
$artifact.Add('ATTACKER MODEL')
$artifact.Add('- Same Windows user as the primary, on the same machine, with the same SID. The')
$artifact.Add('  harness therefore exercises the threat the plan names: a hostile process that')
$artifact.Add('  already passes the per-user mutex/pipe ACL and must be stopped by the IPC')
$artifact.Add('  protocol itself, not by OS file permissions.')
$artifact.Add("- primary role acquired by this harness: $primaryRole")
$artifact.Add("- trust policy resolved from the owner record: $trustResolved (subject=$trustSubject)")
$artifact.Add('- secret: generated in-process by the product validator; never written to the artifact')
$artifact.Add('')
$artifact.Add('SAFETY GATE (non-destructive guarantee)')
$artifact.Add('- Step 1 (preflight): the per-user mutex is opened WITHOUT taking ownership. If an')
$artifact.Add('  owner already exists and cannot be claimed, the harness aborts with exit 3 before')
$artifact.Add('  writing a single byte. This closes the earlier defect where a foreign primary made')
$artifact.Add('  AcquireOrForwardAsync return Secondary and the attacks were forwarded into that')
$artifact.Add('  live instance.')
$artifact.Add('- Step 2 (authoritative): the role RETURNED BY THE PRODUCT must be exactly Primary.')
$artifact.Add('  Secondary or Failed aborts with exit 3 before the first attack.')
$artifact.Add("- foreign-primary mutex preflight result: createdNew=$mutexProbeCreatedNew (createdNew=true means no foreign owner)")
$artifact.Add('- self-check: run with -SimulateForeignPrimary to prove the abort path (exit 3).')
$artifact.Add('')
$artifact.Add('WIRE PROTOCOL OBSERVED FROM SOURCE (not invented)')
$artifact.Add('- frame: 4-byte big-endian length prefix + UTF-8 payload')
$artifact.Add('- peer identity payload: PEER|<b64 exe>|<b64 signer>|<b64 secret>')
$artifact.Add('- request payload: REQ|<version>|<guid N>|<expiry unix ms>|<b64 url>')
$artifact.Add('- ack payload: ACK|<version>|<guid N>|Accepted|Rejected')
$artifact.Add('')

$baselineDelivered = $observer.DeliveredCount
$null = Sync-DrainQuiescent -CaseId 'CONTROL'

# =======================================================================================
# POSITIVE CONTROL. A harness that only ever sees refusals cannot tell a working defender
# from a dead pipe. This runs FIRST and must be ACCEPTED, or every later PASS is suspect.
# =======================================================================================
$logger.Clear()
$controlUrl = 'https://example.com/w1.4-control'
$controlRequestId = [Guid]::NewGuid()
$controlGenerationBaseline = $observer.DeliveredGeneration
$controlResult = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url $controlUrl -RequestId $controlRequestId -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$controlDelivered = Wait-ForOwnDelivery -BaselineGeneration $controlGenerationBaseline -TimeoutMs 5000
$controlPassed = ($controlResult.AckStatus -eq 'Accepted') -and $controlDelivered
Add-AttackResult -CaseId 'CONTROL' `
    -Description 'A well-formed same-image peer request is accepted and delivered (proves the pipe server is live)' `
    -Passed $controlPassed `
    -Observation "ack=$($controlResult.AckStatus) delivered=$controlDelivered" `
    -ReasonCodes @()
$baselineDelivered = $observer.DeliveredCount

# =======================================================================================
# A1. DUPLICATE REQUEST-ID FLOOD. The same request id is sent repeatedly; the queue must
#     reject every repeat as a duplicate and must never deliver a second copy.
# =======================================================================================
$logger.Clear()
$null = Sync-DrainQuiescent -CaseId 'A1'
$duplicateRequestId = [Guid]::NewGuid()
$duplicateGenerationBaseline = $observer.DeliveredGeneration
$duplicateFirst = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-dup-1' -RequestId $duplicateRequestId -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$firstDelivered = Wait-ForOwnDelivery -BaselineGeneration $duplicateGenerationBaseline -TimeoutMs 5000
$baselineAfterFirst = $observer.DeliveredCount

# The first request was legitimately delivered and awaited above; the repeat is the refused send.
$duplicateRefusedBaseline = $observer.DeliveredGeneration
$duplicateSecond = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-dup-2' -RequestId $duplicateRequestId -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$noDuplicateDelivery = Assert-NoNewDelivery -BaselineGeneration $duplicateRefusedBaseline
$duplicateReasonCodes = @($logger.Entries | Where-Object { $_ -match 'ipc\.request\.duplicate' })
$duplicatePassed = ($duplicateFirst.AckStatus -eq 'Accepted') -and $firstDelivered -and
    ($duplicateSecond.AckStatus -ne 'Accepted') -and $noDuplicateDelivery
Add-AttackResult -CaseId 'A1' `
    -Description 'Duplicate request-ID flood: the repeat is rejected and only the first is delivered' `
    -Passed $duplicatePassed `
    -Observation "firstAck=$($duplicateFirst.AckStatus) secondAck=$($duplicateSecond.AckStatus) firstDelivered=$firstDelivered duplicateDelivered=$(-not $noDuplicateDelivery)" `
    -ReasonCodes $duplicateReasonCodes

# =======================================================================================
# A2. MALFORMED FRAME. A valid identity followed by a frame whose payload is invalid UTF-8.
# =======================================================================================
$logger.Clear()
$baselineBeforeMalformed = $observer.DeliveredCount
$malformedGenerationBaseline = $observer.DeliveredGeneration
$malformedClient = Open-AttackPipe
try {
    $null = Write-LengthPrefixedFrame -Pipe $malformedClient -Body (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue())
    # length prefix 2, then bytes C3 28 -- an invalid UTF-8 continuation sequence.
    $null = Write-LengthPrefixedFrame -Pipe $malformedClient -Body ([byte[]]@(0xC3, 0x28))
    $malformedAck = Read-AckStatus -Pipe $malformedClient -TimeoutMs 2000
}
finally {
    $malformedClient.Dispose()
}
$noMalformedDelivery = Assert-NoNewDelivery -BaselineGeneration $malformedGenerationBaseline
$malformedReasonCodes = @($logger.Entries | Where-Object { $_ -match 'frame\.' })
$malformedPassed = $noMalformedDelivery -and ($malformedAck -ne 'Accepted') -and ($malformedReasonCodes.Count -gt 0)
Add-AttackResult -CaseId 'A2' `
    -Description 'Malformed UTF-8 request frame is rejected and never enqueued' `
    -Passed $malformedPassed `
    -Observation "ack=$malformedAck delivered=$(-not $noMalformedDelivery) markerCount=$($malformedReasonCodes.Count)" `
    -ReasonCodes $malformedReasonCodes

# =======================================================================================
# A3. OVERSIZED FRAME, WITH A REAL BODY, ASSERTING THE READER BOUND SPECIFICALLY.
#
#     Two earlier shapes of this case were tautological and are replaced:
#       * declare a huge length and send NOTHING -> "no delivery" is trivially true while the
#         server is still waiting for bytes; passes with the bound removed.
#       * send a body and assert only "the server closed" -> the product has a SECOND length
#         check in ActivationProtocol.TryDecodeRequest (`frame.oversized`), so the decoder
#         still refuses and the connection still closes even with the READER bound removed.
#         Measured: with the reader bound disabled, the observable was unchanged.
#
#     THE DISTINGUISHING OBSERVABLE, measured against a real mutation of
#     WindowsSingleInstanceGuard.cs:447-451 (`if (false && ...)`):
#
#       * reader bound INTACT (baseline) -> the server refuses the declared length and stops
#         reading, so the client's write of the honest over-cap body FAILS. Measured
#         writeFailed=True for the honest-length probe on every baseline run.
#       * reader bound REMOVED (mutation) -> the server consumes the whole 9000-byte body, so
#         the client's write SUCCEEDS and only the decoder refuses afterwards. Measured
#         writeFailed=False for the honest-length probe on the mutation build.
#
#     The case therefore asserts writeFailed=True for the honest-length (9000-byte) probe:
#     the reader refused BEFORE consuming the body. That flips under the mutation.
# =======================================================================================
$logger.Clear()
$baselineBeforeOversized = $observer.DeliveredCount
$oversizedGenerationBaseline = $observer.DeliveredGeneration
$oversizedOutcomes = [System.Collections.Generic.List[string]]::new()
# ActivationProtocol.MaxFrameBytes is 8 KiB and the prefix is 4 bytes, so anything above
# 8188 declared payload bytes is over the protocol maximum. 9000 is unambiguously over.
$oversizedBodyBytes = 9000
$oversizedHonestWriteFailed = $false

foreach ($declaredLength in @(0x7FFFFFFF, $oversizedBodyBytes)) {
    $isHonestLengthProbe = ($declaredLength -eq $oversizedBodyBytes)
    $client = Open-AttackPipe
    try {
        $null = Write-LengthPrefixedFrame -Pipe $client -Body (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue())
        # Real bytes follow the declared prefix. The body is a benign filler, not a URL.
        $body = New-Object byte[] $oversizedBodyBytes
        for ($i = 0; $i -lt $body.Length; $i++) { $body[$i] = 0x41 }
        $declaredPrefix = [BitConverter]::GetBytes([int]$declaredLength)
        [Array]::Reverse($declaredPrefix)
        $client.Write($declaredPrefix, 0, 4)
        $client.Flush()

        $oversizedStarted = [DateTime]::UtcNow
        $writeFailed = $false
        try {
            $client.Write($body, 0, $body.Length)
            $client.Flush()
        }
        catch {
            # The server refused the declared length and stopped reading, so the write fails.
            $writeFailed = $true
        }

        $readBuffer = New-Object byte[] 4
        $readTask = $client.ReadAsync($readBuffer, 0, 4)
        $observedResponse = if (-not $readTask.Wait(6000)) { 'client-timeout' } elseif ($readTask.Result -eq 0) { 'server-closed' } else { "server-data:$($readTask.Result)" }
        $oversizedElapsedMs = [int]([DateTime]::UtcNow - $oversizedStarted).TotalMilliseconds
        if ($isHonestLengthProbe) { $oversizedHonestWriteFailed = $writeFailed }
        $oversizedOutcomes.Add("declaredLength=$declaredLength writeFailed=$writeFailed response=$observedResponse elapsedMs=$oversizedElapsedMs")
    }
    catch {
        $oversizedOutcomes.Add("declaredLength=$declaredLength harness-error:$($_.Exception.GetType().Name)")
    }
    finally {
        $client.Dispose()
    }
}

$noOversizedDelivery = Assert-NoNewDelivery -BaselineGeneration $oversizedGenerationBaseline
$oversizedReasonCodes = @($logger.Entries | Where-Object { $_ -match 'frame|length|read-failed|truncated' })
# STRONG assertion: the reader refused before consuming the honest over-cap body. This is
# the observable that flips when the reader's length bound is removed.
$oversizedPassed = $noOversizedDelivery -and $oversizedHonestWriteFailed
Add-AttackResult -CaseId 'A3' `
    -Description 'Oversized frame with a real over-cap body: the READER refuses before consuming it and never enqueues' `
    -Passed $oversizedPassed `
    -Observation "attempts=$($oversizedOutcomes -join '; ') delivered=$(-not $noOversizedDelivery) honestBodyWriteRefused=$oversizedHonestWriteFailed markers=$($oversizedReasonCodes.Count)" `
    -ReasonCodes $oversizedReasonCodes `
    -Strength 'STRONG' `
    -StrengthNote 'Asserts the client write of the honest over-cap body is refused, which is true only while the reader length bound is intact; measured writeFailed=True baseline vs writeFailed=False with the bound disabled.'

# =======================================================================================
# A4. STALLED READ / SERVER READ DEADLINE. The earlier version slept 2600 ms locally and
#     asserted elapsedMs >= 2400 - which measured the harness's own Start-Sleep, not the
#     product. Removing the server's `timeoutCts.CancelAfter(ReadTimeoutMs)` did not change
#     that assertion at all. Tautology.
#
#     This version measures the SERVER's deadline from the outside: send a valid identity,
#     send NOTHING else, and block on a client read whose deadline is far longer than the
#     server's 2000 ms. The server cancels its own read (WindowsSingleInstanceGuard.cs:439-445)
#     and the handler returns, closing the connection. The client then observes EOF at
#     roughly the server deadline.
#
#       * With the deadline intact  -> client reads EOF at ~2000 ms.
#       * With CancelAfter removed  -> the server blocks indefinitely and the client hits its
#                                      own 15 s deadline, reading nothing.
#
#     The assertion is "the client observed a server close within the product's deadline
#     window", which flips under the mutation.
# =======================================================================================
$logger.Clear()
$baselineBeforeStall = $observer.DeliveredCount
$stallGenerationBaseline = $observer.DeliveredGeneration
# Product read deadline is ReadTimeoutMs = 2000. Allow generous scheduling slack but stay
# well under the client's own 15 s ceiling so a removed server deadline cannot pass.
$serverDeadlineMs = 2000
$stallUpperBoundMs = 8000
$stallAck = 'not-read'
$stallElapsedMs = -1

foreach ($stallAttempt in @(1, 2)) {
    $stallClient = Open-AttackPipe
    $stallSentAt = [DateTime]::UtcNow
    try {
        $null = Write-LengthPrefixedFrame -Pipe $stallClient -Body (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue())
        # A blocking read with a 15 s ceiling. The SERVER is expected to close first.
        $stallRead = $stallClient.ReadAsync((New-Object byte[] 4), 0, 4)
        $stallObserved = if (-not $stallRead.Wait(15000)) { 'client-timeout' } elseif ($stallRead.Result -eq 0) { 'server-closed' } else { "server-data:$($stallRead.Result)" }
        $stallElapsedMs = [int]([DateTime]::UtcNow - $stallSentAt).TotalMilliseconds
        $stallAck = $stallObserved
        if ($stallObserved -eq 'server-closed') { break }
    }
    catch {
        $stallElapsedMs = [int]([DateTime]::UtcNow - $stallSentAt).TotalMilliseconds
        $stallAck = 'connection-closed'
        break
    }
    finally {
        $stallClient.Dispose()
    }
}

$noStallDelivery = Assert-NoNewDelivery -BaselineGeneration $stallGenerationBaseline
$stallReasonCodes = @($logger.Entries | Where-Object { $_ -match 'timeout|read-failed|truncated' })
# STRONG assertion: a server-initiated close inside the product's deadline window. A close
# that never happened (client-timeout at 15 s) fails, which is the mutation signature.
$stallPassed = $noStallDelivery `
    -and ($stallAck -eq 'server-closed' -or $stallAck -eq 'connection-closed') `
    -and ($stallElapsedMs -ge ($serverDeadlineMs - 400)) `
    -and ($stallElapsedMs -le $stallUpperBoundMs)
Add-AttackResult -CaseId 'A4' `
    -Description "Stalled read: the SERVER read deadline closes the connection (window ${serverDeadlineMs}-${stallUpperBoundMs} ms), never enqueues" `
    -Passed $stallPassed `
    -Observation "observed=$stallAck serverCloseElapsedMs=$stallElapsedMs delivered=$(-not $noStallDelivery) markers=$($stallReasonCodes.Count)" `
    -ReasonCodes $stallReasonCodes `
    -Strength 'STRONG' `
    -StrengthNote 'Measures the server-side close from the client, not a local sleep; flips to client-timeout at 15 s if the server deadline is suppressed.'

# =======================================================================================
# A5. ABRUPT DISCONNECT. A peer that connects and drops. Strengthened beyond "nothing was
#     delivered", which a peer that never writes would also satisfy: this now records the
#     PRODUCT's own reaction marker for the dropped connection, so the case shows the guard
#     observed the disconnect and refused that connection rather than simply idling.
#
#     Two shapes: a bare connect/dispose, and a connect that writes a partial length prefix
#     (2 of 4 bytes) then disconnects - a truncated frame mid-header.
# =======================================================================================
$logger.Clear()
$baselineBeforeDisconnect = $observer.DeliveredCount
$disconnectGenerationBaseline = $observer.DeliveredGeneration

$abrupt = Open-AttackPipe
$abrupt.Dispose()

$partial = Open-AttackPipe
try {
    $partial.Write([byte[]]@(0x00, 0x00), 0, 2)
    $partial.Flush()
}
finally {
    $partial.Dispose()
}

$noDisconnectDelivery = Assert-NoNewDelivery -BaselineGeneration $disconnectGenerationBaseline
# The product logs "ipc.validation.read-failed" when the identity read hits a closed pipe
# (WindowsSingleInstanceGuard.cs:348). Its presence is the observable reaction to the drop.
$disconnectReasonCodes = @($logger.Entries | Where-Object { $_ -match 'read-failed|truncated|timeout|peer' })
$disconnectPassed = $noDisconnectDelivery -and ($disconnectReasonCodes.Count -ge 1)
Add-AttackResult -CaseId 'A5' `
    -Description 'Abrupt disconnect (bare and mid-prefix) is observed by the guard and never enqueues' `
    -Passed $disconnectPassed `
    -Observation "delivered=$(-not $noDisconnectDelivery) guardReactionMarkers=$($disconnectReasonCodes.Count)" `
    -ReasonCodes $disconnectReasonCodes `
    -Strength 'STRONG' `
    -StrengthNote 'Asserts the guard logged its read-failed reaction to the dropped connection, not merely an empty queue.'

# =======================================================================================
# A6. REPLAYED ONE-SHOT SECRET. Exchange one successful request, then present the SAME
#     secret again. The second use must be rejected as a replay.
# =======================================================================================
$logger.Clear()
$null = Sync-DrainQuiescent -CaseId 'A6'
$replaySecret = $secretValidator.Issue()
$replayUrl = 'https://example.com/w1.4-replay'
$replayFirstId = [Guid]::NewGuid()
$replayGenerationBaseline = $observer.DeliveredGeneration
$replayFirst = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $replaySecret) `
    -RequestFrame (New-RequestFrame -Url $replayUrl -RequestId $replayFirstId -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$replayFirstDelivered = Wait-ForOwnDelivery -BaselineGeneration $replayGenerationBaseline -TimeoutMs 5000
$baselineAfterReplayFirst = $observer.DeliveredCount

# The first use was legitimately delivered and awaited above; the replay is the refused send.
$replayRefusedBaseline = $observer.DeliveredGeneration
$replaySecond = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $replaySecret) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-replay-2' -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$noReplayDelivery = Assert-NoNewDelivery -BaselineGeneration $replayRefusedBaseline
$replayReasonCodes = @($logger.Entries | Where-Object { $_ -match 'ipc\.secret\.replayed' })
$replayPassed = ($replayFirst.AckStatus -eq 'Accepted') -and $replayFirstDelivered -and
    ($replaySecond.AckStatus -ne 'Accepted') -and $noReplayDelivery -and ($replayReasonCodes.Count -gt 0)
Add-AttackResult -CaseId 'A6' `
    -Description 'Replayed one-shot secret is rejected on second use and never enqueued' `
    -Passed $replayPassed `
    -Observation "firstAck=$($replayFirst.AckStatus) secondAck=$($replaySecond.AckStatus) firstDelivered=$replayFirstDelivered replayDelivered=$(-not $noReplayDelivery)" `
    -ReasonCodes $replayReasonCodes

# =======================================================================================
# A7. MISSING/UNISSUED SECRET. The secret the product's own validator never produced, and
#     separately an empty secret. Both must be refused. (There is no "unissued" registry by
#     design - see OneShotSecretValidator's history note - so the real discriminator here is
#     EMPTY, which is the missing-secret case.)
# =======================================================================================
$logger.Clear()
$baselineBeforeMissing = $observer.DeliveredCount
$missingGenerationBaseline = $observer.DeliveredGeneration
$missingOutcomes = [System.Collections.Generic.List[string]]::new()
$missingAnyAccepted = $false

foreach ($candidate in @('', '   ')) {
    $result = Invoke-AttackExchange `
        -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $candidate) `
        -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-missing-secret' -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
    $missingOutcomes.Add("secretLength=$($candidate.Length) ack=$($result.AckStatus)")
    # Every candidate must be refused: an Accepted acknowledgment for any of them is a failure.
    if ($result.AckStatus -eq 'Accepted') { $missingAnyAccepted = $true }
}

$noMissingDelivery = Assert-NoNewDelivery -BaselineGeneration $missingGenerationBaseline
$missingReasonCodes = @($logger.Entries | Where-Object { $_ -match 'ipc\.secret\.missing' })
$missingPassed = $noMissingDelivery -and (-not $missingAnyAccepted) -and ($missingReasonCodes.Count -gt 0)
Add-AttackResult -CaseId 'A7' `
    -Description 'Missing/blank one-shot secret is refused fail-closed' `
    -Passed $missingPassed `
    -Observation "$($missingOutcomes -join '; ') delivered=$(-not $noMissingDelivery)" `
    -ReasonCodes $missingReasonCodes

# =======================================================================================
# A8. FORGED PEER IDENTITY. A well-formed identity frame whose DECLARED path and signer are
#     deliberately wrong.
#
#     TRUE MECHANISM, corrected in round 5 after an independent review falsified the earlier
#     claim. The guard checks peer identity in TWO ordered layers
#     (WindowsSingleInstanceGuard.TryValidatePeerAsync, WindowsSingleInstanceGuard.cs:309-378):
#
#       1. PeerProcessImageVerifier.VerifyClientProcessImage - the OS-reported image of the
#          connecting process (kernel truth, first line).
#       2. The frame's declared path/signer plus the one-shot secret (defense in depth).
#
#     THE HARNESS HOSTS BOTH ENDS IN ONE PROCESS. WindowsSingleInstanceGuard.cs:66 sets
#     `_localExecutablePath = Environment.ProcessPath`, and the harness's pipe CLIENT is that
#     same pwsh process, so layer 1 asks the kernel for the client image and compares it to
#     the value it just read from the very same process. It ALWAYS MATCHES. That was measured
#     directly against the real verifier over a real connected pipe:
#
#         REAL PeerProcessImageVerifier vs a SAME-PROCESS pipe client
#         expectedPathIsThisProcess = True
#         success = True   failureCode = none
#
#     So layer 1 ALWAYS PASSES here and A8 reaches layer 2, where the forged path/signer are
#     rejected (`ipc.identity.path-mismatch`). A8 therefore isolates LAYER 2, not the OS
#     anchor. The observation below prints `refusalLayer=layer-2:frame-identity` and must
#     agree with this paragraph; the earlier note claimed layer 1 fired first, which the
#     harness's own output contradicted.
#
#     OS-ANCHOR COVERAGE: NOT EXERCISED BY THIS HARNESS. Exercising layer 1 needs a pipe
#     client that is a genuinely DIFFERENT image from `Environment.ProcessPath` and speaks
#     the full secured protocol (trust policy + one-shot secret) while being refused only by
#     the image comparison. That means a second compiled executable acting as the peer, which
#     this single-process PowerShell harness cannot produce. The production path is
#     `PeerProcessImageVerifier.VerifyClientProcessImage`, and it IS covered by
#     `tests/BroChoos.Platform.Windows.Tests/SingleInstance/PeerProcessImageVerifierTests.cs`
#     (real connected pipes, same-image accept / different-image reject) and end to end by
#     `WindowsSingleInstanceGuardPeerImageTests.ForwardFromADifferentImage_IsRejectedBeforeEnqueue`,
#     which spawns a real separate process. Those tests are the OS-anchor evidence; this
#     harness is not.
# =======================================================================================
$logger.Clear()
$null = Sync-DrainQuiescent -CaseId 'A8'
$baselineBeforeForged = $observer.DeliveredCount
$forgedGenerationBaseline = $observer.DeliveredGeneration
$forgedExecutablePath = 'C:\Windows\System32\notepad.exe'
$forgedSignerIdentity = 'CN=Definitely Not BroChoos'
$forgedResult = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $forgedExecutablePath -SignerIdentity $forgedSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-forged' -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$noForgedDelivery = Assert-NoNewDelivery -BaselineGeneration $forgedGenerationBaseline
$forgedReasonCodes = @($logger.Entries | Where-Object { $_ -match 'ipc\.(peer|identity|signer)' })
# Which layer actually fired. Because the harness client is this same image, layer 1
# self-matches and the expected answer is layer 2; anything else is reported, not assumed.
$forgedLayer1Markers = @($logger.Entries | Where-Object { $_ -match 'ipc\.peer\.image' })
$forgedRefusalLayer = if ($forgedLayer1Markers.Count -gt 0) { 'layer-1:OS-peer-image' } elseif ($forgedReasonCodes.Count -gt 0) { 'layer-2:frame-identity' } else { 'none-observed' }
$forgedPassed = ($forgedResult.AckStatus -ne 'Accepted') -and $noForgedDelivery -and ($forgedReasonCodes.Count -gt 0)
Add-AttackResult -CaseId 'A8' `
    -Description 'Forged frame-declared peer path and signer are refused at layer 2 (the OS anchor self-matches in a single-process harness)' `
    -Passed $forgedPassed `
    -Observation "ack=$($forgedResult.AckStatus) delivered=$(-not $noForgedDelivery) refusalLayer=$forgedRefusalLayer osAnchor=NOT-EXERCISED-BY-THIS-HARNESS markerCount=$($forgedReasonCodes.Count)" `
    -ReasonCodes $forgedReasonCodes `
    -Strength 'WEAK' `
    -StrengthNote 'Isolates LAYER 2 only. The harness hosts guard and client in one process, so PeerProcessImageVerifier compares the process to itself and ALWAYS PASSES (measured: success=True over a real pipe); the OS peer-image anchor is NOT EXERCISED BY THIS HARNESS. Layer 1 is covered by PeerProcessImageVerifierTests and WindowsSingleInstanceGuardPeerImageTests.'

# =======================================================================================
# A9. TRUST ANCHOR UNAVAILABLE / REVOKED. The owner record is deliberately withheld from the
#     provider's search root, so the production provider returns TrustAnchorUnavailable and
#     the guard must refuse EVERY connection. The harness does not edit the real record: it
#     points AppContext at a directory with no ruling, hosts a SECOND guard there, and
#     attacks that instance. The main guard and the real record are untouched.
# =======================================================================================
$logger.Clear()
$trustStarvedRoot = Join-Path ([System.IO.Path]::GetTempPath()) "brochoos-w1.4-no-trust-$([Guid]::NewGuid().ToString('N'))"
$trustStarvedResult = $null
$noTrustDelivery = $false
$noTrustReasonCodes = @()

try {
    New-Item -ItemType Directory -Path $trustStarvedRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $trustStarvedRoot 'docs') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $trustStarvedRoot '.git') -Value 'gitdir: absent' -NoNewline

    # A separate guard object, constructed while the search root has no owner record.
    $starvedProvider = [Activator]::CreateInstance($trustProviderType)
    $starvedSecret = [Activator]::CreateInstance($secretValidatorType)
    $starvedAuth = [Activator]::CreateInstance($authenticatorType, @($starvedSecret, $starvedProvider))
    $starvedLogger = [IpcAttackLogger]::new()
    $starvedObserver = [IpcAttackObserver]::new()

    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $trustStarvedRoot)
    $starvedGuard = [Activator]::CreateInstance($guardType, @($starvedLogger, $starvedAuth, $starvedSecret, $urlValidator))

    $starvedPolicy = $null
    $starvedFailure = $null
    $starvedResolved = $starvedProvider.TryGetPolicy([ref]$starvedPolicy, [ref]$starvedFailure)
    $starvedReason = if ($starvedResolved) { 'resolved' } else { $starvedFailure.RedactedReason }

    if ($starvedResolved) {
        # If the fallback search ever found a record anyway, that is itself a finding: the
        # harness must not claim a refusal it did not observe.
        Add-AttackResult -CaseId 'A9' `
            -Description 'Trust anchor unavailable: guard refuses every connection' `
            -Passed $false `
            -Observation 'the provider resolved a policy even with the ruling withheld; the harness cannot manufacture this case' `
            -ReasonCodes @()
    }
    else {
        # Point the guard's own path expectations at the starved root's non-existent binary.
        # The connection must be refused at OR before the image/trust check.
        $starvedPipeName = $pipeField.GetValue($starvedGuard)
        $starvedRequest = [Activator]::CreateInstance($requestType, @(
                'https://primary.starved.invalid/', [long]1,
                [BroChoos.Core.Ingress.ActivationTriggerContext]::CommandLine,
                [BroChoos.Core.Ingress.SourceEvidence]::Confirmed('harness'),
                [BroChoos.Core.Ingress.ActivationModifiers]::None,
                [Guid]::NewGuid(), 1, [DateTimeOffset]::UtcNow.AddMinutes(5), $null))
        $starvedCts = [System.Threading.CancellationTokenSource]::new()
        # The starved guard cannot become primary under the real pipe name while the main
        # guard holds the mutex, so this exercises the FAIL-CLOSED PRE-ENQUEUE decision
        # directly through the guard's own authentication seam, which is the same code path
        # TryValidatePeerAsync calls.
        $seamType = $guardType
        $seamMethod = $seamType.GetMethod('ValidatePeerForW1_3Seam', [Reflection.BindingFlags]'Instance,NonPublic')
        $seamResult = $seamMethod.Invoke($starvedGuard, @(
                $localExecutablePath, $localExecutablePath,
                $localSignerIdentity, $localSignerIdentity,
                $starvedSecret.Issue()))
        $noTrustDelivery = (-not $seamResult.Success)
        $noTrustReasonCodes = @($seamResult.Failure.RedactedReason)
        # The pipe name embeds the current user's SID, which is an operator identifier; it is
        # redacted like the paths so the artifact carries no per-user value.
        $residentPipeLabel = Get-RedactedPathLabel -Path "$starvedPipeName" -Placeholder '<pipe>'
        Add-AttackResult -CaseId 'A9' `
            -Description 'Trust anchor unavailable: validation refuses fail-closed before enqueue' `
            -Passed $noTrustDelivery `
            -Observation "residentPipe=$residentPipeLabel seamSuccess=$($seamResult.Success) providerReason=$starvedReason refusal=$($seamResult.Failure.RedactedReason)" `
            -ReasonCodes $noTrustReasonCodes
        $starvedCts.Cancel()
    }

    $starvedGuard.Dispose()
}
catch {
    Add-AttackResult -CaseId 'A9' `
        -Description 'Trust anchor unavailable: guard refuses every connection' `
        -Passed $false `
        -Observation "harness error: $($_.Exception.GetType().Name): $($_.Exception.Message)" `
        -ReasonCodes @()
}
finally {
    # Restore the real search root and remove the scratch directory.
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)
    if (Test-Path -LiteralPath $trustStarvedRoot) {
        Remove-Item -LiteralPath $trustStarvedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# =======================================================================================
# A10. NON-WEB URL SCHEME INJECTION. A structurally valid request frame carrying a scheme
#      that, if it reached the picker's F key, would hit ShellExecute. The scheme gate must
#      refuse BEFORE the enqueue.
# =======================================================================================
$logger.Clear()
$baselineBeforeScheme = $observer.DeliveredCount
$schemeGenerationBaseline = $observer.DeliveredGeneration
$schemeProbes = @('file', 'javascript', 'ms-settings', 'vbscript')
$schemeOutcomes = [System.Collections.Generic.List[string]]::new()

foreach ($probe in $schemeProbes) {
    $probeUrl = switch ($probe) {
        'file' { 'file:///C:/Windows/System32/calc.exe' }
        'javascript' { 'javascript:alert(1)' }
        'ms-settings' { 'ms-settings:defaultapps' }
        'vbscript' { 'vbscript:msgbox(1)' }
        default { "$probe`:probe" }
    }
    $result = Invoke-AttackExchange `
        -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
        -RequestFrame (New-RequestFrame -Url $probeUrl -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
    $schemeOutcomes.Add("$(Get-SchemeOnly -Url $probeUrl)=$($result.AckStatus)")
}

$noSchemeDelivery = Assert-NoNewDelivery -BaselineGeneration $schemeGenerationBaseline
$schemeReasonCodes = @($logger.Entries | Where-Object { $_ -match 'ipc\.forwarded-url\.rejected' })
$schemePassed = $noSchemeDelivery -and ($schemeReasonCodes.Count -ge $schemeProbes.Count)
# The scheme markers must carry no URL: assert the probes' path fragments are absent.
$schemeLeak = @($logger.Entries | Where-Object { $_ -match 'calc\.exe|alert\(1\)|defaultapps|msgbox' })
Add-AttackResult -CaseId 'A10' `
    -Description 'Non-web URL scheme injection (file/javascript/ms-settings/vbscript) is refused before enqueue' `
    -Passed ($schemePassed -and $schemeLeak.Count -eq 0) `
    -Observation "$($schemeOutcomes -join '; ') delivered=$(-not $noSchemeDelivery) schemeMarkers=$($schemeReasonCodes.Count) urlLeaks=$($schemeLeak.Count)" `
    -ReasonCodes $schemeReasonCodes

# =======================================================================================
# A11. WRONG PROTOCOL VERSION. A frame declaring a version the protocol does not support.
# =======================================================================================
$logger.Clear()
$baselineBeforeVersion = $observer.DeliveredCount
$versionGenerationBaseline = $observer.DeliveredGeneration
$versionResult = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-version' -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)) -ProtocolVersion 9999)
$noVersionDelivery = Assert-NoNewDelivery -BaselineGeneration $versionGenerationBaseline
$versionReasonCodes = @($logger.Entries | Where-Object { $_ -match 'frame\.version' })
$versionPassed = ($versionResult.AckStatus -ne 'Accepted') -and $noVersionDelivery -and ($versionReasonCodes.Count -gt 0)
Add-AttackResult -CaseId 'A11' `
    -Description 'Unsupported protocol version is rejected and never enqueued' `
    -Passed $versionPassed `
    -Observation "ack=$($versionResult.AckStatus) delivered=$(-not $noVersionDelivery) markerCount=$($versionReasonCodes.Count)" `
    -ReasonCodes $versionReasonCodes

# =======================================================================================
# A12. EXPIRED REQUEST. A structurally valid frame whose expiry is already in the past.
# =======================================================================================
$logger.Clear()
$baselineBeforeExpiry = $observer.DeliveredCount
$expiryGenerationBaseline = $observer.DeliveredGeneration
$expiryResult = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-expired' -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(-10)))
$noExpiryDelivery = Assert-NoNewDelivery -BaselineGeneration $expiryGenerationBaseline
$expiryReasonCodes = @($logger.Entries | Where-Object { $_ -match 'expired' })
$expiryPassed = ($expiryResult.AckStatus -ne 'Accepted') -and $noExpiryDelivery -and ($expiryReasonCodes.Count -gt 0)
Add-AttackResult -CaseId 'A12' `
    -Description 'Expired request is rejected and never enqueued' `
    -Passed $expiryPassed `
    -Observation "ack=$($expiryResult.AckStatus) delivered=$(-not $noExpiryDelivery) markerCount=$($expiryReasonCodes.Count)" `
    -ReasonCodes $expiryReasonCodes

# =======================================================================================
# A13. QUEUE FLOOD / CAPACITY - DETERMINISTIC. The earlier version raced the drain and its
#      `accepted` count varied 5..9 across runs; a later attempt still varied (one of twelve
#      runs produced firstRejectionIndex=7). "Deterministic" was asserted, not shown.
#
#      WHY IT RACED. WindowsSingleInstanceGuard.RunPipeServerAsync handles ONE connection at
#      a time, and DrainQueueAsync calls DrainAllPending() BEFORE invoking UrlReceived
#      (WindowsSingleInstanceGuard.cs:384-387). Blocking the handler therefore parks the
#      drain thread INSIDE the handler - but only if the handler had already been entered.
#      Before it parks, the 25 ms poll can still dequeue items, freeing slots, so how many
#      of the burst's first frames land in a drained queue varies.
#
#      THE DETERMINISTIC FIX. Do not start the burst until the drain is provably frozen:
#        1. Block the drain.
#        2. Send ONE priming request that the drain will dequeue and then park on. Wait for
#           `HandlerParked` (the observer reports it entered a blocked handler call).
#        3. ONLY THEN send the burst. While parked, the drain thread is inside the handler
#           and never returns to DrainAllPending, so the queue cannot free a slot.
#
#      With the drain provably frozen, the first `capacity` burst requests fill the queue and
#      the next request is refused: first refusal at index `capacity`. That is the assertion,
#      and it is stable because the freeze is waited on rather than assumed.
# =======================================================================================
$logger.Clear()
$null = Sync-DrainQuiescent -CaseId 'A13'
$baselineBeforeFlood = $observer.DeliveredCount
$floodGenerationBaseline = $observer.DeliveredGeneration
$floodAttempts = $QueueCapacity + 4
$floodOutcomes = [System.Collections.Generic.List[string]]::new()
$floodPrimed = $false

$observer.BlockDrain($true)
try {
    # STEP 2: prime, and wait until the drain thread is parked inside the blocked handler.
    $floodPrimeClient = Open-AttackPipe
    try {
        $null = Write-LengthPrefixedFrame -Pipe $floodPrimeClient -Body (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue())
        $null = Write-LengthPrefixedFrame -Pipe $floodPrimeClient -Body (New-RequestFrame -Url 'https://example.com/w1.4-flood-prime' -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
        $floodPrimed = (Read-AckStatus -Pipe $floodPrimeClient -TimeoutMs 3000) -eq 'Accepted'
    }
    finally {
        $floodPrimeClient.Dispose()
    }
    $drainParked = $observer.WaitForHandlerParked(3000)

    if ($drainParked) {
        # STEP 3: the drain is frozen - the queue can no longer free a slot. Fill it.
        for ($i = 0; $i -lt $floodAttempts; $i++) {
            $floodClient = Open-AttackPipe
            try {
                $null = Write-LengthPrefixedFrame -Pipe $floodClient -Body (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue())
                $null = Write-LengthPrefixedFrame -Pipe $floodClient -Body (New-RequestFrame -Url "https://example.com/w1.4-flood-$i" -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
                $floodOutcomes.Add((Read-AckStatus -Pipe $floodClient -TimeoutMs 3000))
            }
            catch {
                $floodOutcomes.Add('closed')
            }
            finally {
                $floodClient.Dispose()
            }
        }
    }
}
finally {
    # ALWAYS release the drain, including on a harness error, or the guard would hang.
    $observer.BlockDrain($false)
}

# THE BACKLOG DRAIN IS PART OF THE CASE, NOT SOMETHING THE NEXT CASE HOPES TO OUTRUN.
# Releasing the block lets the drain thread finish the parked handler and then replay the
# queued backlog. That replay is asynchronous, so A13 must wait for it to FINISH here -
# otherwise A14-A18 begin while their observer is still being handed A13's URLs, which is the
# exact inter-case race that made earlier revisions fail ~27% of runs. Waiting for proven
# quiescence makes every later case start from an idle drain.
$floodBacklogDrained = $observer.WaitForQuiescent(300, 20000)

$floodReasonCodes = @($logger.Entries | Where-Object { $_ -match 'ipc\.queue\.full' })
$firstRejectionIndex = $floodOutcomes.IndexOf('Rejected')
# Deterministic assertions once the drain is provably frozen:
#   * the first `capacity` burst attempts are all Accepted (the queue filled to capacity), and
#   * the FIRST refusal lands exactly at index `capacity`.
$floodFirstCapacityAccepted = @($floodOutcomes | Select-Object -First $QueueCapacity | Where-Object { $_ -eq 'Accepted' }).Count -eq $QueueCapacity
$floodRejectionAtCapacity = ($firstRejectionIndex -eq $QueueCapacity) -and ($floodReasonCodes.Count -ge 1)
# The delivered slice is taken from the URLs recorded after this case's own count baseline.
$floodDelivered = @($observer.DeliveredUrls | Select-Object -Skip $baselineBeforeFlood)
$floodDistinctDelivered = @($floodDelivered | Select-Object -Unique).Count
# The burst can legitimately deliver more than `capacity` because the drain replays the queue
# after the block is released; what must hold is that no URL was delivered twice and that the
# frozen-drain refusal landed exactly at capacity.
$floodPassed = $floodPrimed `
    -and $drainParked `
    -and $floodBacklogDrained `
    -and $floodFirstCapacityAccepted `
    -and $floodRejectionAtCapacity `
    -and ($floodDistinctDelivered -eq $floodDelivered.Count)
Add-AttackResult -CaseId 'A13' `
    -Description 'Queue flood with the drain provably frozen: exactly `capacity` accepted, then refusal at index capacity' `
    -Passed $floodPassed `
    -Observation "primed=$floodPrimed drainParked=$drainParked backlogDrained=$floodBacklogDrained attempts=$floodAttempts capacity=$QueueCapacity firstRejectionIndex=$firstRejectionIndex fullMarkers=$($floodReasonCodes.Count) distinctDelivered=$floodDistinctDelivered ackPattern=$($floodOutcomes -join ',')" `
    -ReasonCodes $floodReasonCodes `
    -Strength 'STRONG' `
    -StrengthNote 'Waits for the drain thread to park before the burst, so the queue cannot free a slot; asserts first refusal at exactly index capacity. Flips to all-accepted when the capacity check is removed.'

# =======================================================================================
# A14. STALLED ACK / SERVER NEVER ANSWERS. HONEST RELABEL.
#
#     The previous version spun up a fake silent server and timed the HARNESS's own
#     `Read-AckStatus` against it. That measures the harness, not the product, and it was
#     presented as if it exercised the product's secondary ack deadline. It does not.
#
#     The product's own ack deadline lives in WindowsSingleInstanceGuard.AcquireOrForwardAsync
#     (AckTimeoutMs = 2000, WindowsSingleInstanceGuard.cs:146-152). Driving that path
#     requires the product's secondary role, which requires the per-user mutex - which THIS
#     harness must own as primary in order to run the attack server at all. The two roles
#     cannot both exist in one process, and the real secondary path is therefore exercised
#     by `WindowsSingleInstanceGuardProtocolTests.StalledAckFromPrimary_ReturnsFailed`
#     (a genuine product secondary against a deliberately silent primary), not here.
#
#     What this case still does, and what it now claims, is narrower and true: it proves the
#     HARNESS's own client read does not hang forever against a silent peer, which keeps the
#     harness itself from deadlocking. It is marked WEAK and must not be read as product
#     evidence.
# =======================================================================================
$stallAckPassed = $true
$stallAckObservation = 'harness client ack-read deadline confirmed against a silent server (harness self-check, not product evidence)'
$silentPipeName = "BroChoos.W1.4.Silent.$([Guid]::NewGuid().ToString('N'))"
$silentServer = [SilentPipeServer]::new($silentPipeName)

try {
    $silentServer.Start()
    $silentClient = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $silentPipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $silentClient.Connect(3000)
    $null = Write-Utf8Frame -Pipe $silentClient -Payload 'PEER|Zm9v|Zm9v|Zm9v'
    $null = Write-Utf8Frame -Pipe $silentClient -Payload 'REQ|1|00000000000000000000000000000001|99999999999999|aHR0cHM6Ly9leGFtcGxlLmNvbS8='
    $started = [DateTime]::UtcNow
    $silentAck = Read-AckStatus -Pipe $silentClient -TimeoutMs 2500
    $elapsed = [int]([DateTime]::UtcNow - $started).TotalMilliseconds
    $stallAckPassed = ($null -eq $silentAck) -and ($elapsed -ge 2000) -and ($elapsed -le 6000)
    $stallAckObservation = "silent-server ack=$silentAck elapsedMs=$elapsed (harness client stopped waiting; NOT product ack-deadline evidence)"
    $silentClient.Dispose()
}
catch {
    $stallAckPassed = $false
    $stallAckObservation = "harness error: $($_.Exception.GetType().Name): $($_.Exception.Message)"
}
finally {
    $silentServer.Dispose()
}

Add-AttackResult -CaseId 'A14' `
    -Description 'Harness self-check: the harness client ack-read does not hang against a silent peer' `
    -Passed $stallAckPassed `
    -Observation $stallAckObservation `
    -ReasonCodes @() `
    -Strength 'WEAK' `
    -StrengthNote 'Measures the HARNESS client, not the product secondary ack deadline. Product ack-deadline evidence is WindowsSingleInstanceGuardProtocolTests.StalledAckFromPrimary_ReturnsFailed.'

# =======================================================================================
# A15. TRUNCATED REQUEST FRAME MID-REQUEST, WITH AN HONEST SENSITIVITY CAVEAT.
#
#      The case: a connection whose identity is accepted declares a request length larger
#      than the bytes it delivers, then disconnects. The guard must not enqueue the partial
#      frame, and its accept loop must keep serving.
#
#      WHAT WAS TRIED FOR MUTATION SENSITIVITY, AND WHAT WAS MEASURED. Two mutations of the
#      cancellation/truncation path were built and A15 was run against each:
#
#        M1  WindowsSingleInstanceGuard.cs:486-489 - replace `throw new EndOfStreamException`
#            with `break`, so the short buffer is returned as a complete frame.
#            RESULT: A15 still PASSES. The zero-padded tail makes the base64 URL field
#            undecodable, so ActivationProtocol.TryDecodeRequest refuses it anyway. The
#            truncation throw is therefore backed by the decoder, and A15's no-enqueue half
#            has no observable that M1 moves.
#
#        M2  WindowsSingleInstanceGuard.cs:223-228 - add `break` to the server loop's generic
#            catch, so the accept loop would exit after one bad frame.
#            RESULT: A15 still PASSES. The truncation throw happens inside
#            HandleAcceptedConnectionAsync, whose own catch converts it to a Rejected result;
#            RunPipeServerAsync never sees an exception, so the loop survives regardless.
#            A15's liveness half therefore cannot be flipped through this path.
#
#      HONEST CONCLUSION: A15 is a real fail-closed observation (nothing enqueued, loop
#      alive), but its mutation sensitivity is INFERRED, not demonstrated, because both
#      available mutations are absorbed by downstream checks. It is labeled WEAK below rather
#      than claimed STRONG. The plan's `cancellation` requirement is met as an observation;
#      a demonstrated-sensitivity variant would need a product seam that does not exist.
# =======================================================================================
$logger.Clear()
$null = Sync-DrainQuiescent -CaseId 'A15'
$baselineBeforeCancel = $observer.DeliveredCount
$cancelRefusedBaseline = $observer.DeliveredGeneration
$cancelRequestId = [Guid]::NewGuid()
$cancelValidFrame = New-RequestFrame -Url 'https://example.com/w1.4-cancel-partial' -RequestId $cancelRequestId -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5))

$cancelClient = Open-AttackPipe
try {
    $null = Write-LengthPrefixedFrame -Pipe $cancelClient -Body (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue())
    # Declare far more than will be sent, then send a COMPLETE valid frame and disconnect.
    $cancelDeclared = [BitConverter]::GetBytes([int]4000)
    [Array]::Reverse($cancelDeclared)
    $cancelClient.Write($cancelDeclared, 0, 4)
    $cancelClient.Write($cancelValidFrame, 0, $cancelValidFrame.Length)
    $cancelClient.Flush()
}
finally {
    $cancelClient.Dispose()
}

$noCancelDelivery = Assert-NoNewDelivery -BaselineGeneration $cancelRefusedBaseline
# LIVENESS: the guard's accept loop must still serve a good request after the truncation.
$cancelControlId = [Guid]::NewGuid()
$cancelGenerationBaseline = $observer.DeliveredGeneration
$cancelControl = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-cancel-liveness' -RequestId $cancelControlId -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$cancelLiveness = ($cancelControl.AckStatus -eq 'Accepted') -and (Wait-ForOwnDelivery -BaselineGeneration $cancelGenerationBaseline -TimeoutMs 5000)
$cancelReasonCodes = @($logger.Entries | Where-Object { $_ -match 'read-failed|truncated|timeout' })
$cancelPassed = $noCancelDelivery -and $cancelLiveness
Add-AttackResult -CaseId 'A15' `
    -Description 'Truncated request frame is never enqueued, and the accept loop stays live' `
    -Passed $cancelPassed `
    -Observation "delivered=$(-not $noCancelDelivery) livenessAck=$($cancelControl.AckStatus) livenessDelivered=$cancelLiveness guardReactions=$($cancelReasonCodes.Count)" `
    -ReasonCodes $cancelReasonCodes `
    -Strength 'WEAK' `
    -StrengthNote 'Fail-closed observation is real, but mutation sensitivity is INFERRED: M1 (throw->break at WindowsSingleInstanceGuard.cs:486-489) and M2 (break in the server loop catch at :223-228) were both built and both left A15 passing, because the truncation is absorbed by the decoder and the inner handler respectively. No product seam exists to demonstrate sensitivity without a product change.'

# =======================================================================================
# A16. TRUST-ANCHOR REVOCATION (distinct from A9's UNAVAILABLE). A9 covers a provider that
#      returns nothing. This covers a provider that returns a REVOKED policy, which the
#      production gate classifies as IpcPeerAuthenticationErrorCode.TrustAnchorRevoked
#      (IpcTrustPolicy.cs:22-24,119-124) and refuses before enqueue. The revoked provider is
#      a harness-owned IIpcTrustPolicyProvider, so no real record is edited; the case proves
#      the guard's fail-closed branch for revocation, which the plan names separately.
# =======================================================================================
$logger.Clear()
$revokedPassed = $false
$revokedObservation = 'not run'
try {
    # A provider that reports revocation exactly as OwnerRulingFileIpcTrustPolicyProvider does
    # when the record's revocation field says "revoked".
    $revokedProvider = [IpcRevokedTrustPolicyProvider]::new()
    $revokedSecret = [Activator]::CreateInstance($secretValidatorType)
    $revokedAuth = [Activator]::CreateInstance($authenticatorType, @($revokedSecret, $revokedProvider))
    # The authenticator applies the same trust gate the guard's TryValidatePeerAsync uses, so
    # this is the production decision for a revoked anchor.
    $revokedResult = $revokedAuth.ValidateTrustAndIdentity(
        $localExecutablePath, $localExecutablePath, $localSignerIdentity, $localSignerIdentity)
    $revokedCode = if ($null -ne $revokedResult.Failure) { $revokedResult.Failure.Code.ToString() } else { 'none' }
    $revokedReason = if ($null -ne $revokedResult.Failure) { $revokedResult.Failure.RedactedReason } else { '' }
    $revokedPassed = (-not $revokedResult.Success) -and ($revokedCode -eq 'TrustAnchorRevoked')
    $revokedObservation = "seamSuccess=$($revokedResult.Success) code=$revokedCode reason=$revokedReason"
}
catch {
    $revokedPassed = $false
    $revokedObservation = "harness error: $($_.Exception.GetType().Name): $($_.Exception.Message)"
}
Add-AttackResult -CaseId 'A16' `
    -Description 'Revoked trust anchor is refused with TrustAnchorRevoked (distinct from unavailable)' `
    -Passed $revokedPassed `
    -Observation $revokedObservation `
    -ReasonCodes @() `
    -Strength 'LIMITED' `
    -StrengthNote 'Exercises the trust gate through the guard auth seam with a harness revoked provider; it does not edit the real owner record, which stays valid.'

# =======================================================================================
# A16b. REAL REVOCATION RECORD PARSER. A16 above uses a harness stub, so the REAL JSON
#       parsing of `trustAnchorLifecycle.revocation` in
#       src/BroChoos.Platform.Windows/SingleInstance/IpcTrustPolicy.cs:113-125 was NOT
#       covered. This case drives the REAL `OwnerRulingFileIpcTrustPolicyProvider` against a
#       fixture record tree, so the production parse is what produces the refusal.
#
#       HOW IT REACHES THE REAL PARSER. FindRepositoryRoot() climbs AppContext.BaseDirectory
#       looking for a directory that contains both `.git` and `docs`, then reads
#       `<root>/docs/decisions/owner-rulings/dev-runner-ipc-trust-anchor.json`. The harness
#       already redirects AppContext for the trust-starved case (A9); here it points at a
#       FIXTURE tree whose record is a complete, otherwise-valid document that differs from
#       the real record only in `trustAnchorLifecycle.revocation`. The REAL record is never
#       read or edited by this case.
#
#       THE ASSERTION is that the real parser returns TrustAnchorRevoked, and - to prove it is
#       the revocation branch and not some other parse failure - a CONTROL record identical
#       except for a non-revoking value must parse to a valid policy. Two fixtures, one
#       variable, so the observed difference is attributable to the revocation string.
# =======================================================================================
$logger.Clear()
$a16bPassed = $false
$a16bObservation = 'not run'
$labelFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "brochoos-w14-trustparser-$([Guid]::NewGuid().ToString('N'))"
try {
    $fixtureRecordDir = Join-Path $labelFixtureRoot 'docs/decisions/owner-rulings'
    New-Item -ItemType Directory -Path $fixtureRecordDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $labelFixtureRoot 'docs') -Force | Out-Null
    # `.git` may be a file or a directory; the real provider only requires that ONE of them exists.
    Set-Content -LiteralPath (Join-Path $labelFixtureRoot '.git') -Value 'gitdir: fixture' -NoNewline
    $fixtureRecordPath = Join-Path $fixtureRecordDir 'dev-runner-ipc-trust-anchor.json'

    # A complete record whose ONLY meaningful variable is the revocation string. Every other
    # field matches what IpcTrustPolicyGate.ResolveProductionPolicy requires to be valid.
    $buildRecord = {
        param([string] $revocation)
        return @"
{
  "schemaVersion": "1.0.0",
  "recordVersion": "1.0.0",
  "subjectId": "DEV-RUNNER-IPC-TRUST-ANCHOR",
  "resolutionState": { "disposition": "approved", "priorStatus": "OPEN" },
  "scope": "local Dev Runner IPC only",
  "signerIdentityModel": "x509-chain",
  "authority": {
    "source": "DH-2026-09-11",
    "approver": "owner@brochoos",
    "approvedAtUtc": "2026-09-12T00:00:00Z"
  },
  "validation": { "status": "PASS", "reviewedBy": "security-review@brochoos", "reviewedAtUtc": "2026-09-12T00:00:00Z" },
  "publicTrustMetadata": { "testOnly": false },
  "trustAnchorLifecycle": {
    "provisioning": "manual owner-controlled provisioning",
    "storage": "local machine trust store reference only",
    "rotation": "owner-authorized rotation with overlap window",
    "revocation": "$revocation",
    "constraints": "no fixture fallback; local Dev Runner IPC only"
  }
}
"@
    }

    # CONTROL first: a non-revoking record must parse into a usable policy.
    [System.IO.File]::WriteAllText($fixtureRecordPath, (& $buildRecord 'not revoked - explicit revocation list and deny on mismatch'), (New-Object System.Text.UTF8Encoding($false)))
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', (Join-Path $labelFixtureRoot 'docs'))
    $controlProvider = [Activator]::CreateInstance($trustProviderType)
    $controlPolicy = $null
    $controlFailure = $null
    $controlResolved = $controlProvider.TryGetPolicy([ref]$controlPolicy, [ref]$controlFailure)
    $controlGate = [BroChoos.Platform.Windows.SingleInstance.IpcTrustPolicyGate]::ResolveProductionPolicy($controlProvider, [ref]$controlPolicy)
    $controlGateOk = $controlGate.Success
    # Sanity: the fixture tree must actually be the root the provider read, or the control is
    # meaningless. A resolved policy proves FindRepositoryRoot found the fixture.
    $controlFoundFixture = $controlResolved -and $controlPolicy -and ($controlPolicy.SubjectId -eq 'DEV-RUNNER-IPC-TRUST-ANCHOR')

    # VARIABLE: flip ONLY the revocation string to a revoking value.
    [System.IO.File]::WriteAllText($fixtureRecordPath, (& $buildRecord 'revoked'), (New-Object System.Text.UTF8Encoding($false)))
    $revokedRealProvider = [Activator]::CreateInstance($trustProviderType)
    $revokedRealPolicy = $null
    $revokedRealFailure = $null
    $revokedRealResolved = $revokedRealProvider.TryGetPolicy([ref]$revokedRealPolicy, [ref]$revokedRealFailure)
    $revokedRealCode = if ($null -ne $revokedRealFailure) { $revokedRealFailure.Code.ToString() } else { 'none' }
    $revokedRealReason = if ($null -ne $revokedRealFailure) { $revokedRealFailure.RedactedReason } else { '' }

    # The REAL revocation branch fired; the control (same record, different value) did not.
    $a16bPassed = $controlFoundFixture -and $controlGateOk -and (-not $revokedRealResolved) -and ($revokedRealCode -eq 'TrustAnchorRevoked')
    $a16bObservation = "controlFoundFixture=$controlFoundFixture controlGateOk=$controlGateOk revokedResolved=$revokedRealResolved revokedCode=$revokedRealCode revokedReason=$revokedRealReason"
}
catch {
    $a16bPassed = $false
    $a16bObservation = "harness error: $($_.Exception.GetType().Name): $($_.Exception.Message)"
}
finally {
    # Restore the real search root before ANYTHING else, then remove the fixture tree.
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)
    if (Test-Path -LiteralPath $labelFixtureRoot) {
        Remove-Item -LiteralPath $labelFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Add-AttackResult -CaseId 'A16b' `
    -Description 'REAL OwnerRulingFileIpcTrustPolicyProvider parses a revoked record and returns TrustAnchorRevoked' `
    -Passed $a16bPassed `
    -Observation $a16bObservation `
    -ReasonCodes @('ipc.trust-anchor.revoked') `
    -Strength 'STRONG' `
    -StrengthNote 'Drives the production JSON parser (IpcTrustPolicy.cs:113-125) against a fixture record tree; a control record differing only in the revocation string parses clean, so the difference is attributable to the revocation branch.'

# =======================================================================================
# A19. TRUST-ANCHOR ROTATION - COVERAGE GAP, MEASURED AND RECORDED, NOT INVENTED.
#
#      The plan's W1.4 Negative/security/privacy clause names "trust-anchor rotation/revocation
#      tests". Revocation is covered (A16 stub seam + A16b real parser). ROTATION IS NOT
#      IMPLEMENTED IN THE PRODUCT, so there is no rotation behaviour to exercise. The evidence
#      is the product source itself, read in this repository at this commit:
#
#        * src/BroChoos.Platform.Windows/SingleInstance/IpcTrustPolicy.cs parses
#          `trustAnchorLifecycle` and reads EXACTLY ONE member from it: `revocation`
#          (IpcTrustPolicy.cs:113-125). `provisioning`, `storage`, `rotation` and
#          `constraints` are documented lifecycle fields in the owner record but are never
#          read by any code path.
#        * The string "rotation" does not appear as a JSON key read anywhere under src/
#          (verified by a case-insensitive search for the property name: zero hits).
#        * The `IpcTrustPolicy` record (IpcTrustPolicy.cs:38-47) has no rotation, overlap,
#          previous-anchor or grace-period field, and no provider exposes one.
#        * tests/ contains zero rotation tests (search for rotation/overlap: zero hits).
#
#      What this case ASSERTS is therefore the honest negative: the production policy provider
#      has no rotation concept, so the owner record's `rotation` text is inert metadata. It
#      proves the gap by reading a REAL record whose rotation field says a rotation is in
#      progress while the rest of the record stays valid, and by showing the provider returns
#      the same single policy regardless - there is no second/previous anchor to accept.
#
#      This is recorded, NOT fixed: implementing rotation is a product change, which this task
#      forbids. The product gap is named as F-W1.4-3 below.
# =======================================================================================
$a19Passed = $false
$a19Observation = 'not run'
$a19FixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "brochoos-w14-rotation-$([Guid]::NewGuid().ToString('N'))"
try {
    $a19RecordDir = Join-Path $a19FixtureRoot 'docs/decisions/owner-rulings'
    New-Item -ItemType Directory -Path $a19RecordDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $a19FixtureRoot 'docs') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $a19FixtureRoot '.git') -Value 'gitdir: fixture' -NoNewline
    $a19RecordPath = Join-Path $a19RecordDir 'dev-runner-ipc-trust-anchor.json'
    $a19Record = @"
{
  "schemaVersion": "1.0.0",
  "recordVersion": "1.0.0",
  "subjectId": "DEV-RUNNER-IPC-TRUST-ANCHOR",
  "resolutionState": { "disposition": "approved", "priorStatus": "OPEN" },
  "scope": "local Dev Runner IPC only",
  "signerIdentityModel": "x509-chain",
  "authority": { "source": "DH-2026-09-11", "approver": "owner@brochoos", "approvedAtUtc": "2026-09-12T00:00:00Z" },
  "validation": { "status": "PASS", "reviewedBy": "security-review@brochoos", "reviewedAtUtc": "2026-09-12T00:00:00Z" },
  "publicTrustMetadata": { "testOnly": false },
  "trustAnchorLifecycle": {
    "provisioning": "manual owner-controlled provisioning",
    "storage": "local machine trust store reference only",
    "rotation": "rotation in progress: new anchor provisioned, old anchor inside overlap window",
    "revocation": "not revoked",
    "constraints": "no fixture fallback; local Dev Runner IPC only"
  }
}
"@
    [System.IO.File]::WriteAllText($a19RecordPath, $a19Record, (New-Object System.Text.UTF8Encoding($false)))
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', (Join-Path $a19FixtureRoot 'docs'))

    $a19Provider = [Activator]::CreateInstance($trustProviderType)
    $a19Policy = $null
    $a19Failure = $null
    $a19Resolved = $a19Provider.TryGetPolicy([ref]$a19Policy, [ref]$a19Failure)

    # The gap, stated as a measurable fact: a record that DECLARES an in-progress rotation is
    # indistinguishable to the provider from one that declares none. There is no accessor for
    # a rotation state, no overlap-window decision, and no second anchor to accept.
    $a19PolicyType = [BroChoos.Platform.Windows.SingleInstance.IpcTrustPolicy]
    $a19PropertyNames = @($a19PolicyType.GetProperties() | ForEach-Object { $_.Name })
    $a19RotationProperties = @($a19PropertyNames | Where-Object { $_ -match 'Rotation|Overlap|Previous|Grace|Anchor' })
    $a19ResolvedButNoRotationConcept = $a19Resolved -and ($a19RotationProperties.Count -eq 0)
    $a19Passed = $a19ResolvedButNoRotationConcept
    $a19Observation = "recordDeclaredRotation=True resolved=$a19Resolved policyProperties=$($a19PropertyNames.Count) rotationAwareProperties=$($a19RotationProperties.Count) verdict=ROTATION-NOT-IMPLEMENTED"
}
catch {
    $a19Passed = $false
    $a19Observation = "harness error: $($_.Exception.GetType().Name): $($_.Exception.Message)"
}
finally {
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $repoRoot)
    if (Test-Path -LiteralPath $a19FixtureRoot) {
        Remove-Item -LiteralPath $a19FixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Add-AttackResult -CaseId 'A19' `
    -Description 'Trust-anchor rotation: NOT IMPLEMENTED in the product (coverage gap recorded, not invented)' `
    -Passed $a19Passed `
    -Observation $a19Observation `
    -ReasonCodes @() `
    -Strength 'LIMITED' `
    -StrengthNote 'Asserts the honest negative: the owner record may declare a rotation, but IpcTrustPolicy reads only `revocation` from trustAnchorLifecycle (IpcTrustPolicy.cs:113-125), the IpcTrustPolicy record has no rotation/overlap/previous-anchor field, and the string "rotation" is never read under src/. Rotation behaviour therefore CANNOT be tested; the plan clause is unmet by the product. Recorded as F-W1.4-3.'

# =======================================================================================
# A17. PEER PROCESS IDENTITY IN THE EVIDENCE. The plan's verification matrix requires the
#      evidence to carry "process identities/signatures" for accepted requests. The CONTROL
#      exchange is re-run here and the harness records the identity material it can observe
#      WITHOUT leaking a secret: the kernel-reported image of the harness process and the
#      signer label are recorded as presence/length facts, never as values that could carry
#      a secret. This makes the accepted-request identity explicit in the artifact.
# =======================================================================================
$logger.Clear()
$null = Sync-DrainQuiescent -CaseId 'A17'
$baselineBeforeIdentity = $observer.DeliveredCount
$identityControlId = [Guid]::NewGuid()
$identityGenerationBaseline = $observer.DeliveredGeneration
$identityControl = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-identity-evidence' -RequestId $identityControlId -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$identityDelivered = Wait-ForOwnDelivery -BaselineGeneration $identityGenerationBaseline -TimeoutMs 5000
$identityPassed = ($identityControl.AckStatus -eq 'Accepted') -and $identityDelivered
# Redacted identity facts: a SHA-256 of the image path and its length, never the path itself.
$identityImageHash = if ([string]::IsNullOrWhiteSpace($localExecutablePath)) { '(none)' } else { (Get-FileHash -Algorithm SHA256 -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($localExecutablePath)))).Hash.ToLowerInvariant().Substring(0, 16) }
$identityObservation = "ack=$($identityControl.AckStatus) delivered=$identityDelivered kernelImageHash16=$identityImageHash kernelImagePathLength=$($localExecutablePath.Length) signerLength=$($localSignerIdentity.Length) signerModel=x509-chain"
Add-AttackResult -CaseId 'A17' `
    -Description 'Accepted request carries peer process identity in the evidence (redacted form)' `
    -Passed $identityPassed `
    -Observation $identityObservation `
    -ReasonCodes @() `
    -Strength 'STRONG' `
    -StrengthNote 'Records kernel image hash/length and signer length for an ACCEPTED request; no path or secret value is written.'

# =======================================================================================
# A18. NO RETRY LOOP / NO INSECURE FALLBACK. The plan's W1.4 Minimal behavior forbids a
#      retry loop or an insecure fallback. This case sends a request that is REFUSED
#      (non-web scheme) and then proves the guard made exactly ONE decision for it: one
#      refusal marker, no Accepted ack, no second attempt, and no delivery - i.e. the
#      refusal did not trigger a retry or a downgraded path. A single-exchange count is the
#      observable form of "no retry loop".
# =======================================================================================
$logger.Clear()
$null = Sync-DrainQuiescent -CaseId 'A18'
$baselineBeforeNoRetry = $observer.DeliveredCount
$noRetryProbeUrl = 'javascript:alert(1)'
$noRetryResult = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url $noRetryProbeUrl -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
# A second, well-formed exchange on a FRESH connection proves the refusal did not poison the
# loop or leave a fallback armed.
$noRetryGenerationBaseline = $observer.DeliveredGeneration
$noRetryControl = Invoke-AttackExchange `
    -IdentityFrame (New-PeerIdentityFrame -ExecutablePath $localExecutablePath -SignerIdentity $localSignerIdentity -OneShotSecret $secretValidator.Issue()) `
    -RequestFrame (New-RequestFrame -Url 'https://example.com/w1.4-no-retry-control' -RequestId ([Guid]::NewGuid()) -ExpiresAtUtc ([DateTimeOffset]::UtcNow.AddMinutes(5)))
$noRetryLiveness = ($noRetryControl.AckStatus -eq 'Accepted') -and (Wait-ForOwnDelivery -BaselineGeneration $noRetryGenerationBaseline -TimeoutMs 5000)
$noRetrySchemeMarkers = @($logger.Entries | Where-Object { $_ -match 'ipc\.forwarded-url\.rejected' })
$noRetryDelivered = $observer.DeliveredCount - $baselineBeforeNoRetry
# Exactly one scheme refusal for the one refused probe; the control delivered and nothing else.
$noRetryPassed = ($noRetryResult.AckStatus -eq 'Rejected') `
    -and ($noRetrySchemeMarkers.Count -eq 1) `
    -and ($noRetryDelivered -eq 1) `
    -and $noRetryLiveness
Add-AttackResult -CaseId 'A18' `
    -Description 'A refused request produces exactly one decision: no retry loop and no insecure fallback' `
    -Passed $noRetryPassed `
    -Observation "refusedAck=$($noRetryResult.AckStatus) schemeRefusals=$($noRetrySchemeMarkers.Count) deliveredTotal=$noRetryDelivered livenessAck=$($noRetryControl.AckStatus) liveness=$noRetryLiveness" `
    -ReasonCodes $noRetrySchemeMarkers `
    -Strength 'STRONG' `
    -StrengthNote 'Exactly one refusal marker and exactly one delivery (the control) is the observable form of "no retry, no fallback".'

# =======================================================================================
# REPORT. Write the redacted artifact and print a summary. Exit 0 only when every case
# passed; any failure is a finding to report and fix separately, never a silent fix here.
# =======================================================================================
foreach ($result in $script:results) {
    $artifact.Add((('[{0}] {1} ({2})' -f $(if ($result.Passed) { 'PASS' } else { 'FAIL' }), $result.CaseId, $result.Strength)))
    $artifact.Add("  description: $($result.Description)")
    $artifact.Add("  observation: $($result.Observation)")
    if (-not [string]::IsNullOrWhiteSpace($result.StrengthNote)) {
        $artifact.Add("  strength note: $($result.StrengthNote)")
    }
    if ($result.ReasonCodes.Count -gt 0) {
        $artifact.Add("  reason codes: $($result.ReasonCodes -join '; ')")
    }
}

$artifact.Add('')
$artifact.Add('CROSS-CASE QUIESCENCE (why this harness is deterministic)')
$artifact.Add('- Every Invoke-AttackExchange waits for a PROVEN-IDLE drain before sending: no handler')
$artifact.Add('  invocation in flight and the delivery generation unmoved for 300 ms.')
$artifact.Add('- A13 releases its frozen drain and then WAITS for the resulting backlog replay to')
$artifact.Add('  finish before returning, so no later case can be starved by or count A13 URLs.')
$artifact.Add('- Delivery assertions are GENERATION-based: each case captures its own generation')
$artifact.Add('  baseline immediately before sending and waits for the generation to move past it, so a')
$artifact.Add('  concurrent delivery can never satisfy the assertion on behalf of the case.')
$artifact.Add("- quiescence handshake timeouts observed this run: $($script:quiescenceFailures.Count)")
if ($script:quiescenceFailures.Count -gt 0) {
    foreach ($q in $script:quiescenceFailures) { $artifact.Add("  QUIESCENCE-FAILURE: $q") }
}
$artifact.Add('')
$artifact.Add('LIMITS (explicit)')
$artifact.Add('- The harness hosts a REAL product guard as primary; the safety gate (mutex preflight')
$artifact.Add('  plus a role==Primary requirement) makes it abort before sending anything if a foreign')
$artifact.Add('  primary is present. It never contacts a user-run BroChoos.')
$artifact.Add('- A9 (unavailable) and A16 (revoked) use the product auth seam because only one primary')
$artifact.Add('  can hold the per-user mutex and pipe at a time; the seam is the exact function the')
$artifact.Add('  pipe server calls, so the refusal decision is the production decision.')
$artifact.Add('- A8 is WEAK and isolates LAYER 2 only: the harness hosts guard and pipe client in')
$artifact.Add('  one process, so PeerProcessImageVerifier compares the connecting process to itself and')
$artifact.Add('  always passes (measured: success=True over a real pipe). The OS peer-image anchor is')
$artifact.Add('  NOT EXERCISED BY THIS HARNESS; it is covered by PeerProcessImageVerifierTests and')
$artifact.Add('  WindowsSingleInstanceGuardPeerImageTests, which spawn a real second process.')
$artifact.Add('- A13 asserts the FIRST capacity attempts are accepted and the refusal starts at index')
$artifact.Add('  capacity; total throughput above capacity is legitimate (the drain frees slots) and is')
$artifact.Add('  NOT asserted as a cap.')
$artifact.Add('- URLs are never written to this artifact; only schemes, counts, and typed acks.')
$artifact.Add('')
$artifact.Add('PRODUCT OBSERVABILITY FINDINGS (recorded, NOT fixed here)')
$artifact.Add('- F-W1.4-1 (MEDIUM): WindowsSingleInstanceGuard.cs:244-253 - the read-deadline and')
$artifact.Add('  frame-read failures are caught by generic catch blocks that emit NO typed log marker.')
$artifact.Add('  A4 can only assert the server-side close timing, not a named reason code.')
$artifact.Add('- F-W1.4-2 (LOW-MEDIUM): WindowsSingleInstanceGuard.cs:447-451 - the oversized-length')
$artifact.Add('  refusal throws InvalidDataException("ipc.frame.length") into the same generic catch')
$artifact.Add('  and emits no marker. A3 can only assert the server-side close, not a reason code.')
$artifact.Add('- F-W1.4-3 (MEDIUM, coverage gap): trust-anchor ROTATION is NOT IMPLEMENTED.')
$artifact.Add('  IpcTrustPolicy.cs:113-125 reads only `revocation` from trustAnchorLifecycle; the')
$artifact.Add('  IpcTrustPolicy record has no rotation/overlap/previous-anchor field; the string')
$artifact.Add('  "rotation" is never read anywhere under src/; tests/ has zero rotation tests. The')
$artifact.Add('  plan names rotation alongside revocation, so this clause is unmet by the product.')
$artifact.Add('  A19 records the gap; it does not simulate coverage that does not exist.')
$artifact.Add('- Both observability findings and the rotation gap are product changes and are')
$artifact.Add('  deliberately NOT made in this harness task.')

# ---------------------------------------------------------------------------------------
# FINAL REDACTION SWEEP. Before the artifact is written, scan every line for an absolute
# Windows path or the current user name and refuse to ship a leaking document. This turns
# "redacted by construction" into "verified redacted", so a future edit that prints a raw
# path fails loudly here instead of leaking into evidence.
# ---------------------------------------------------------------------------------------
$leakPatterns = @(Get-ArtifactLeakPatterns)
$leaks = [System.Collections.Generic.List[string]]::new()
$lineNumber = 0
foreach ($line in $artifact) {
    $lineNumber++
    foreach ($pattern in $leakPatterns) {
        if ($line -match $pattern) {
            $leaks.Add("line $lineNumber matched '$pattern'")
        }
    }
}
$artifact.Add('')
$artifact.Add('REDACTION SWEEP')
$artifact.Add("- absolute-path / user-name hits: $($leaks.Count)")
if ($leaks.Count -gt 0) {
    foreach ($leak in $leaks) { $artifact.Add("  LEAK: $leak") }
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$artifactPath = Join-Path $OutputDirectory "w1.4-ipc-attacker-$stamp.txt"
# The sweep above only COUNTS and annotates leaks (and fails the verdict). The text that is
# written is redacted as well, including the annotations themselves, which echo the patterns.
$artifactText = Protect-ArtifactText -Text ($artifact -join [Environment]::NewLine)
[System.IO.File]::WriteAllText($artifactPath, $artifactText, (New-Object System.Text.UTF8Encoding($false)))

# ---------------------------------------------------------------------------------------
# Teardown. Order matters: the handler is detached before the guard is disposed so no
# in-flight delivery reaches a half-torn-down observer.
# ---------------------------------------------------------------------------------------
try { $guard.remove_UrlReceived($observerHandler) } catch { }
$primaryCts.Cancel()
$guard.Dispose()
$primaryCts.Dispose()

Write-Output "IPC_ATTACKER_ARTIFACT=$artifactPath"
Write-Output "IPC_ATTACKER_CASES=$($script:results.Count)"
Write-Output "IPC_ATTACKER_QUIESCENCE_FAILURES=$($script:quiescenceFailures.Count)"
Write-Output "IPC_ATTACKER_REDACTION_LEAKS=$($leaks.Count)"
foreach ($result in $script:results) {
    $status = if ($result.Passed) { 'PASS' } else { 'FAIL' }
    Write-Output "$status $($result.CaseId) :: $($result.Observation)"
}

# A quiescence handshake timeout is a harness failure even if every case passed: it means a
# case's starting state was not provably idle, which is exactly the condition that made
# earlier revisions non-deterministic. A redaction leak is likewise a harness failure: the
# artifact must never carry an operator path. Both are folded into the verdict.
if (($script:failures.Count -eq 0) -and ($script:quiescenceFailures.Count -eq 0) -and ($leaks.Count -eq 0)) {
    Write-Output 'IPC_ATTACKER_RESULT=ALL-ATTACKS-REFUSED-FAIL-CLOSED'
    Write-Output 'IPC_ATTACKER_EXIT=0'
    exit 0
}

Write-Output "IPC_ATTACKER_RESULT=$($script:failures.Count)+$($script:quiescenceFailures.Count)+$($leaks.Count)-FAILURE(S)"
foreach ($failure in $script:failures) {
    Write-Output "IPC_ATTACKER_FAILURE=$failure"
}
foreach ($quiescenceFailure in $script:quiescenceFailures) {
    Write-Output "IPC_ATTACKER_QUIESCENCE_FAILURE=$quiescenceFailure"
}
foreach ($leak in $leaks) {
    Write-Output "IPC_ATTACKER_REDACTION_LEAK=$leak"
}
Write-Output 'IPC_ATTACKER_EXIT=1'
exit 1
