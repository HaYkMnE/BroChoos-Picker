<#
.SYNOPSIS
    W7.4 reproducible, redacted network-evidence harness for the short-link helper.

.DESCRIPTION
    The plan (W7.4 Files/symbols) names `tools/capture-network.ps1` as a "reproducible
    redacted evidence harness", and the plan's evidence matrix requires, for the
    Network helper family:

      * "consent-off zero traffic"                                  -- consent OFF must
        produce NO outbound request at all;
      * "packet capture and helper trace with no cookies/Referer/auth" -- a trace of the
        real helper exchange proving the request carries none of those;
      * "redirect traces, timeout bounds, ... original fallback"    -- W7.4 GREEN.

    WHAT THIS HARNESS ACTUALLY DOES, stated up front because the distinction is the point
    of the file:

      1. It drives the REAL `BroChoos.NetworkHelper.exe` over stdin/stdout exactly as the
         App does, so the transcript is a record of production behaviour, not of a mock.
      2. It captures an APPLICATION-LAYER transcript by standing up a loopback HTTP listener
         and pointing the helper at it through the proxy environment variables. The listener
         records the exact request line and header names it received. This needs NO
         ADMINISTRATOR RIGHTS, which is why it is the default path.
      3. It attempts a KERNEL-LEVEL (ETL) capture only when the operator asks for it AND
         elevation is available. If elevation is missing the harness SAYS SO in its output
         and in the artifact, and records the unprivileged transcript instead. It never
         fabricates a capture it did not make and never reports a missing capture as a pass.
      4. It runs the consent-OFF case FIRST and asserts the listener saw ZERO connections,
         which is the plan's "zero traffic" requirement as an observable fact.

    REDACTION BY CONSTRUCTION. The artifact writes only the request line, the header NAMES,
    the response status lines, response `Location` (a redirect target that is part of the
    trace), and the typed helper result. Header VALUES are never written, so a
    token-bearing or hostile header value cannot reach the artifact even if a future helper
    starts sending one.

.PARAMETER HelperPath
    Path to BroChoos.NetworkHelper.exe. Defaults to the Release build output. The harness
    FAILS (does not skip) when the binary is absent, so a missing build cannot masquerade
    as "no findings".

.PARAMETER OutputDirectory
    Where the artifact is written. Defaults to artifacts/evidence/network (ignored by git).

.PARAMETER SkipKernelCapture
    Force the unprivileged path even when elevation is available (useful for CI).

.PARAMETER HelperCallTimeoutMs
    Per-call budget, in milliseconds, for each helper invocation: the CASE 1 and CASE 3 calls
    (a helper still running after it is killed and recorded as a timeout) and the CASE 2 wait
    for the helper to exit after its last hop. Default 5000, which real runs should keep. The
    offline test suite raises it only for runs whose "helper" is a PowerShell stand-in, whose
    cold start under load can exceed 5 s. It does not change the 10 s CASE 2 serve deadline,
    the 3 s receive timeout, or the bounded stream and post-kill waits.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/capture-network.ps1
#>
[CmdletBinding()]
param(
    [string] $HelperPath,
    [string] $OutputDirectory,
    [switch] $SkipKernelCapture,
    [ValidateRange(1000, 600000)]
    [int] $HelperCallTimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts/evidence/network'
}

if ([string]::IsNullOrWhiteSpace($HelperPath)) {
    $candidates = @(
        (Join-Path $repoRoot 'src/BroChoos.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe'),
        (Join-Path $repoRoot 'src/BroChoos.NetworkHelper/bin/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe'),
        (Join-Path $repoRoot 'src/BroChoos.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe'),
        # `dotnet build BroChoos.sln` builds the helper under the solution's x64 platform
        # folder, which is where a fresh clean checkout's output lands.
        (Join-Path $repoRoot 'src/BroChoos.NetworkHelper/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe')
    )
    $HelperPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($HelperPath) -or -not (Test-Path -LiteralPath $HelperPath -PathType Leaf)) {
    throw 'BroChoos.NetworkHelper.exe was not found. Build it first (dotnet build BroChoos.sln -c Release) or pass -HelperPath. A missing helper is a HARD failure: it must never look like "no findings".'
}

# SETUP FAILURE before any artifact can exist: one stderr line, exit 1. (The missing-helper
# throw above stays a throw on purpose - it is the documented hard failure the tests pin.)
try {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
} catch {
    $setupInner = $_.Exception
    while ($null -ne $setupInner.InnerException) { $setupInner = $setupInner.InnerException }
    [Console]::Error.WriteLine("CAPTURE_NETWORK_ERROR=output directory could not be created: $($setupInner.GetType().Name)")
    exit 1
}
# New-Item -Force returns without error when the path already exists - including when it is a
# FILE - so the result is checked explicitly, before any helper is started.
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    [Console]::Error.WriteLine('CAPTURE_NETWORK_ERROR=output directory is not a directory')
    exit 1
}
$artifactWritten = $false
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$transcriptPath = Join-Path $OutputDirectory "w7.4-capture-network-$stamp.txt"
$forbiddenHeaders = @('Authorization', 'Cookie', 'Referer', 'Proxy-Authorization')
$artifact = [System.Collections.Generic.List[string]]::new()

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Reads one CRLF/LF-terminated ASCII line straight from the socket stream, one byte at a time.
# No StreamReader on purpose: a StreamReader reads ahead into its own buffer, and bytes it has
# pre-read (e.g. a second request PIPELINED after the first) would be invisible to the
# socket-level "extra data" check below. Returns $null at end of stream before any byte. The
# stream's ReadTimeout (the client's ReceiveTimeout) still bounds every read. Lines are capped at
# 8192 bytes; a longer one ends there, and the remainder becomes "extra data".
#
# BARE CR (RFC 9112: a CR not immediately followed by LF is invalid in a request). Such a CR does
# not end the line, so whatever follows it (e.g. "Host: x<CR>Cookie: s=1") would be folded into
# the previous header's text and never matched. It is not "fixed up": $State.BareCr is set and
# the request is reported malformed (fail closed). Only the single CR directly before the LF is
# the line terminator; any other CR - including one on a line ended by EOF or the cap - is bare.
function Read-AsciiLine {
    param([System.IO.Stream] $Stream, [hashtable] $State)
    $bytes = New-Object System.Collections.Generic.List[byte]
    $endedByLf = $false
    while ($bytes.Count -lt 8192) {
        $b = $Stream.ReadByte()
        if ($b -lt 0) { if ($bytes.Count -eq 0) { return $null } else { break } }
        if ($b -eq 10) { $endedByLf = $true; break }
        $bytes.Add([byte]$b)
    }
    if ($endedByLf -and $bytes.Count -gt 0 -and $bytes[$bytes.Count - 1] -eq 13) { $bytes.RemoveAt($bytes.Count - 1) }
    if ($bytes.Contains([byte]13)) { $State.BareCr = $true }
    return [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
}

# Reads exactly one HTTP request from a TcpClient: request line + header names/values. Returns
# $null when the request line is blank/absent, and an object with Malformed = $true when any
# line of the request carries a bare CR.
function Read-HttpRequest {
    param([System.Net.Sockets.TcpClient] $Client)
    $stream = $Client.GetStream()
    $lineState = @{ BareCr = $false }
    $requestLine = Read-AsciiLine -Stream $stream -State $lineState
    if ([string]::IsNullOrWhiteSpace($requestLine)) { return $null }
    $names = New-Object System.Collections.Generic.List[string]
    $map = @{}
    while ($true) {
        $line = Read-AsciiLine -Stream $stream -State $lineState
        if ([string]::IsNullOrEmpty($line)) { break }
        $colon = $line.IndexOf(':')
        if ($colon -gt 0) {
            $name = $line.Substring(0, $colon).Trim()
            $value = $line.Substring($colon + 1).Trim()
            $names.Add($name)
            $map[$name] = $value
        }
    }
    if ($lineState.BareCr) { return [pscustomobject]@{ Malformed = $true } }
    return [pscustomobject]@{
        Malformed = $false
        RawRequestLine = $requestLine
        Method = ($requestLine -split ' ')[0]
        HeaderNames = $names.ToArray()
        Headers = $map
    }
}

# WATCHING A KEPT CONNECTION (D17). After its one request is answered, a served CASE 2
# connection stays open and is checked on every 100 ms tick until the helper is gone. Watching
# while the peer is still ALIVE is the point - MEASURED: when a client closes with our response
# still unread in its own buffer, Windows sends RST instead of FIN, and that RST discards data
# the client sent us that we had not read yet (Available drops to 0, Read throws
# ConnectionReset). A single drain after the helper exits therefore cannot see a late pipelined
# GET from a client that never read the reply; a watcher that runs while it is alive does.
#   * data available            -> ExtraData (FAIL): anything after the one request
#   * readable with nothing     -> the peer closed (FIN): Done, clean
#   * a reset or other error    -> Reset (FAIL): an RST means the peer abandoned unread data,
#                                  which a well-behaved client that read its response never does
# Returns $true once the connection needs no more watching.
function Test-KeptConnection {
    param([object] $Kept, [int] $WaitMicroseconds)
    if ($Kept.Done) { return $true }
    try {
        if (-not $Kept.Client.Client.Poll($WaitMicroseconds, [System.Net.Sockets.SelectMode]::SelectRead)) { return $false }
        if ($Kept.Client.Available -gt 0) { $Kept.ExtraData = $true } else {
            # Poll says readable with nothing buffered: a FIN. A pending RST shows up on the read.
            $probe = New-Object byte[] 1
            $null = $Kept.Client.Client.Receive($probe, 0, 1, [System.Net.Sockets.SocketFlags]::Peek)
        }
    } catch {
        $Kept.Reset = $true
    }
    $Kept.Done = $true
    return $true
}

function Watch-KeptConnections {
    param([System.Collections.Generic.List[object]] $Kept)
    foreach ($k in $Kept) { $null = Test-KeptConnection -Kept $k -WaitMicroseconds 0 }
}

function Write-ScriptedResponse {
    param([System.Net.Sockets.TcpClient] $Client, [string] $StatusLine, [hashtable] $Headers, [object] $Request = $null)
    # The Location the harness SERVED is recorded on the captured request, so the artifact reports
    # the scripted response and never the helper's own request headers.
    if ($null -ne $Request -and $Headers.ContainsKey('Location')) {
        Add-Member -InputObject $Request -NotePropertyName ServedLocation -NotePropertyValue ([string]$Headers['Location']) -Force
    }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine($StatusLine)
    foreach ($key in $Headers.Keys) { [void]$builder.AppendLine(("{0}: {1}" -f $key, $Headers[$key])) }
    [void]$builder.AppendLine()
    $stream = $Client.GetStream()
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($builder.ToString())
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

# Kills a helper the harness is abandoning, together with any children it started, and waits
# for the exit. Works on BOTH hosts: Process.Kill(bool) (tree kill) exists only on .NET Core 3+
# (pwsh); under Windows PowerShell 5.1 / .NET Framework the call throws MethodException, which
# an empty catch used to swallow, so a hung helper was never killed. On 5.1 the tree is killed
# with taskkill /T, and Kill() is the last resort. The process is still alive when this runs
# (HasExited is checked first and the harness holds its handle), so the PID is its own.
function Stop-HelperProcess {
    param([System.Diagnostics.Process] $Process)
    try { if ($Process.HasExited) { return } } catch { return }
    $treeKill = $Process.GetType().GetMethod('Kill', [type[]]@([bool]))
    if ($null -ne $treeKill) {
        try { $Process.Kill($true) } catch { }
    } else {
        try { $null = & taskkill.exe /T /F /PID $Process.Id 2>&1 } catch { }
    }
    try { if (-not $Process.HasExited) { $Process.Kill() } } catch { }
    try { $null = $Process.WaitForExit(5000) } catch { }
}

# Collects a redirected stream's text once the child has exited, but never waits forever: a
# grandchild that inherited the pipe keeps it open after the helper itself is gone, and an
# unbounded ReadToEnd would hang the harness until that grandchild exits. The placeholder
# text is not a parseable result, so a truncated read can never read as a success.
function Get-StreamText {
    param([object] $Task, [int] $TimeoutMs = 5000)
    if ($null -eq $Task) { return '' }
    try {
        if ($Task.Wait($TimeoutMs)) { return [string]$Task.Result }
    } catch {
        return '<stream read failed>'
    }
    return "<stream still open after $TimeoutMs ms>"
}

# The innermost exception's TYPE name only (PowerShell wraps .NET failures in
# MethodInvocationException). Messages are never used: they can carry paths or URLs.
function Get-ExceptionTypeName {
    param([System.Exception] $Exception)
    $inner = $Exception
    while ($null -ne $inner.InnerException) { $inner = $inner.InnerException }
    return $inner.GetType().Name
}

# Runs the helper exactly as the App does. Returns the typed result plus stdout/stderr.
function Invoke-NetworkHelper {
    param(
        [string] $ExePath,
        [string] $Url,
        [bool] $ConsentGranted,
        [int] $TimeoutMs,
        [string] $ProxyUrl
    )

    $payload = @{ url = $Url; consentGranted = $ConsentGranted } | ConvertTo-Json -Compress
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ExePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    # PS 5.1 / .NET Framework has NO ProcessStartInfo.ArgumentList (MEASURED: the property
    # is absent, so the earlier .Add() threw InvokeMethodOnNull). Arguments is the only
    # string channel available. The helper takes exactly one fixed, harness-controlled token
    # ('resolve-short-link'), so there is no user input to quote and no injection surface --
    # the W7.4 product invariant against argv injection is about the URL, which travels on
    # stdin as JSON and never touches this string.
    $psi.Arguments = 'resolve-short-link'
    if (-not [string]::IsNullOrWhiteSpace($ProxyUrl)) {
        # The helper honours the system proxy. The loopback listener is installed as the
        # proxy for THIS CHILD only, so the machine's real proxy configuration is untouched.
        $psi.EnvironmentVariables['HTTP_PROXY'] = $ProxyUrl
        $psi.EnvironmentVariables['HTTPS_PROXY'] = $ProxyUrl
    }

    # try/finally so that ANY exception below still reaps the child (Stop-HelperProcess is a
    # no-op once it has exited), instead of leaving a started helper running.
    $process = $null
    try {
        $process = [System.Diagnostics.Process]::Start($psi)
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        # The write is GUARDED. A child that has already exited (the consent-OFF path returns
        # before reading, and can exit within milliseconds) leaves StandardInput closed, and an
        # unguarded Write throws InvokeMethodOnNull on a $null stream -- which would turn a
        # perfectly good "consent refused, zero traffic" observation into a harness crash.
        try {
            $process.StandardInput.Write($payload)
            $process.StandardInput.Close()
        } catch {
            # Best effort: the child's exit code and streams below are the real result.
        }

        if (-not $process.WaitForExit($TimeoutMs)) {
            Stop-HelperProcess -Process $process
            return [pscustomobject]@{ ExitCode = 124; StdOut = ''; StdErr = 'harness-child-timeout'; TimedOut = $true }
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = Get-StreamText -Task $stdoutTask
            StdErr = Get-StreamText -Task $stderrTask
            TimedOut = $false
        }
    } finally {
        if ($null -ne $process) { Stop-HelperProcess -Process $process }
    }
}

# Encodes text a HELPER or the network controls before it is echoed into the artifact, so it
# always stays on ONE line and can never forge a line of its own (a helper printing
# "<CR><LF>harness verdict: PASS" used to produce a second, fake verdict line). JSON string
# escaping: backslash and double quote are escaped, CR/LF/TAB become \r \n \t, and every other
# character outside printable ASCII becomes \uXXXX - which also covers Unicode line breaks
# (U+0085, U+2028, U+2029) that some readers render as a new line. Decoding is the JSON rule.
function ConvertTo-ArtifactField {
    param([AllowNull()][string] $Text)
    if ($null -eq $Text) { return '' }
    $builder = New-Object System.Text.StringBuilder
    foreach ($ch in $Text.ToCharArray()) {
        $code = [int]$ch
        if ($code -eq 92) { [void]$builder.Append('\\') }
        elseif ($code -eq 34) { [void]$builder.Append('\"') }
        elseif ($code -eq 13) { [void]$builder.Append('\r') }
        elseif ($code -eq 10) { [void]$builder.Append('\n') }
        elseif ($code -eq 9) { [void]$builder.Append('\t') }
        elseif ($code -lt 0x20 -or $code -gt 0x7E) { [void]$builder.Append(('\u{0:x4}' -f $code)) }
        else { [void]$builder.Append($ch) }
    }
    return $builder.ToString()
}

# Joins artifact entries with CRLF; inside each entry, only the CRLF/CR/LF line breaks the
# harness wrote are kept (normalised to CRLF) and every other non-printable or non-ASCII
# character becomes \uXXXX. A lone CR or a U+2028 therefore cannot start a line of its own.
function ConvertTo-ArtifactText {
    param([string[]] $Lines)
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $Lines) {
        foreach ($line in ([string]$entry -split '\r\n|\r|\n')) {
            $out.Add([regex]::Replace($line, '[^\x20-\x7E]', { param($m) '\u{0:x4}' -f [int][char]$m.Value }))
        }
    }
    return ($out -join "`r`n")
}

function Format-Redacted { 
    param([string] $Title, [object[]] $Requests, [object] $HelperResult)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("== $Title ==")
    if ($null -ne $HelperResult) {
        # Quoted JSON strings, so the field boundaries stay unambiguous whatever the helper
        # printed (a literal ' stderr=' inside stdout is \"-safe and cannot shift them).
        $lines.Add(('helper exit={0} stdout="{1}" stderr="{2}"' -f $HelperResult.ExitCode, (ConvertTo-ArtifactField $HelperResult.StdOut), (ConvertTo-ArtifactField $HelperResult.StdErr)))
    }
    $lines.Add("captured exchanges: $($Requests.Count)")
    $index = 0
    foreach ($r in $Requests) {
        $index++
        $lines.Add("--- exchange $index ---")
        $lines.Add("request line: $(ConvertTo-ArtifactField $r.RawRequestLine)")
        $lines.Add("header names: $(ConvertTo-ArtifactField ([string]::Join(', ', $r.HeaderNames)))")
        # Only the Location the scripted response served (set by Write-ScriptedResponse); a Location
        # header in the helper's REQUEST is never reported as a response header.
        $servedLocation = $r.PSObject.Properties['ServedLocation']
        if ($null -ne $servedLocation -and $null -ne $servedLocation.Value) { $lines.Add("response Location: $(ConvertTo-ArtifactField $servedLocation.Value)") }
        foreach ($forbidden in $forbiddenHeaders) {
            $present = @($r.HeaderNames | Where-Object { $_ -ieq $forbidden }).Count -gt 0
            $lines.Add(("forbidden header '{0}': {1}" -f $forbidden, $(if ($present) { 'PRESENT' } else { 'absent' })))
        }
    }
    return ($lines -join [Environment]::NewLine)
}

# Turns the CASE 2 helper result into the artifact's interpretation line. Success wording is
# emitted ONLY when the helper exited 0 AND reported an explicit null errorCode; every other
# outcome (timeout, network error, any other code, non-zero exit, unparseable stdout) is
# written as a FAILURE that names what was observed, so a failed run can never read as a
# completed redirect chain. Kept self-contained so tools/tests can exercise it offline.
# STRICT JSON VALIDATOR for helper stdout: a complete RFC 8259 recursive-descent check, so the
# harness never depends on ConvertFrom-Json's host-specific leniency (MEASURED: pwsh accepts
# trailing commas, missing values, comments, NaN; 5.1 accepts single quotes and bare keys).
# Returns @{ Ok; Reason }. Grammar (RFC 8259):
#   value  = object / array / string / number / true / false / null
#   object = '{' ws [ member *( ',' member ) ] ws '}'      member = ws string ws ':' ws value ws
#   array  = '[' ws [ value *( ',' value ) ] ws ']'
#   string = '"' *( unescaped / '\' ( '"' '\' '/' 'b' 'f' 'n' 'r' 't' / 'u' 4HEXDIG ) ) '"'
#            unescaped = any char >= U+0020 except '"' and '\'
#   number = [ '-' ] ( '0' / [1-9] *DIGIT ) [ '.' 1*DIGIT ] [ ( 'e' / 'E' ) [ '+' / '-' ] 1*DIGIT ]
#   ws     = *( SP / HTAB / CR / LF )
# Extra rules this harness needs on top of the grammar:
#   * the top-level value must be an OBJECT, followed by nothing but ws        (not-an-object,
#     trailing-content)
#   * TOP-LEVEL member names are compared after DECODING; a name that needed an escape and
#     decodes to 'errorCode' is REJECTED (escaped-top-level-key): no real helper writes it, and
#     rejecting keeps the rule simple; more than one plain 'errorCode' is duplicate-errorCode
#     (hosts would silently keep the last one). Nested errorCode members do not count.
#   * more than 64 nested objects/arrays (counted when a container OPENS, the top object being
#     the first) is too-deep; text longer than 16384 chars - counted on the text exactly as
#     received, before any whitespace is skipped - is too-long. A real helper writes a few
#     hundred bytes; the caps keep recursion and scan time bounded for a hostile one.
#   * a raw UTF-16 surrogate inside a string must be a high one followed by a low one; a lone
#     high or lone low surrogate is unpaired-surrogate (it is not valid Unicode text).
# Implementation: one shared state hashtable (text, index, reason, errorCode count) and
# index-based char comparisons only - no per-character regex, so the capped text scans
# quickly on 5.1.
function Test-StrictHelperJson {
    param([AllowNull()][string] $Text)
    $s = [string]$Text
    if ($s.Length -gt 16384) { return [pscustomobject]@{ Ok = $false; Reason = 'too-long' } }
    $st = @{ S = $s; N = $s.Length; I = 0; Why = $null; ErrorCodeKeys = 0 }

    $skipWs = {
        while ($st.I -lt $st.N) {
            $c = $st.S[$st.I]
            if ($c -eq ' ' -or $c -eq "`t" -or $c -eq "`r" -or $c -eq "`n") { $st.I++ } else { break }
        }
    }
    # Returns the DECODED string, or $null with $st.Why set. $st.I must be on the opening quote.
    $readString = {
        $st.I++
        $sb = New-Object System.Text.StringBuilder
        $hadEscape = $false
        while ($true) {
            if ($st.I -ge $st.N) { $st.Why = 'unterminated-string'; return $null }
            $c = $st.S[$st.I]
            if ($c -eq '"') { $st.I++; return [pscustomobject]@{ Value = $sb.ToString(); Escaped = $hadEscape } }
            $code = [int]$c
            if ($code -lt 0x20) { $st.Why = 'control-char-in-string'; return $null }
            if ($code -ge 0xD800 -and $code -le 0xDFFF) {
                # A raw surrogate is valid only as a high (D800-DBFF) + low (DC00-DFFF) pair.
                $low = if ($st.I + 1 -lt $st.N) { [int]$st.S[$st.I + 1] } else { -1 }
                if ($code -gt 0xDBFF -or $low -lt 0xDC00 -or $low -gt 0xDFFF) { $st.Why = 'unpaired-surrogate'; return $null }
                [void]$sb.Append($c); [void]$sb.Append($st.S[$st.I + 1]); $st.I += 2; continue
            }
            if ($c -ne '\') { [void]$sb.Append($c); $st.I++; continue }
            $hadEscape = $true
            if ($st.I + 1 -ge $st.N) { $st.Why = 'unterminated-string'; return $null }
            $e = $st.S[$st.I + 1]
            switch -CaseSensitive ([string]$e) {
                '"' { [void]$sb.Append('"'); $st.I += 2; continue }
                '\' { [void]$sb.Append('\'); $st.I += 2; continue }
                '/' { [void]$sb.Append('/'); $st.I += 2; continue }
                'b' { [void]$sb.Append([char]8); $st.I += 2; continue }
                'f' { [void]$sb.Append([char]12); $st.I += 2; continue }
                'n' { [void]$sb.Append("`n"); $st.I += 2; continue }
                'r' { [void]$sb.Append("`r"); $st.I += 2; continue }
                't' { [void]$sb.Append("`t"); $st.I += 2; continue }
                'u' {
                    if ($st.I + 6 -gt $st.N) { $st.Why = 'bad-escape'; return $null }
                    $code = 0
                    for ($k = $st.I + 2; $k -lt $st.I + 6; $k++) {
                        $h = [int]$st.S[$k]
                        if ($h -ge 48 -and $h -le 57) { $d = $h - 48 } elseif ($h -ge 65 -and $h -le 70) { $d = $h - 55 } elseif ($h -ge 97 -and $h -le 102) { $d = $h - 87 } else { $st.Why = 'bad-escape'; return $null }
                        $code = $code * 16 + $d
                    }
                    [void]$sb.Append([char]$code); $st.I += 6; continue
                }
                default { $st.Why = 'bad-escape'; return $null }
            }
        }
    }
    # Advances over one number; returns $false with $st.Why set if it is not RFC 8259.
    # Works on local copies and int char codes (48-57 = '0'-'9'), so a digit costs one compare
    # and no string or scriptblock call. Leading zeros ("01") are bad-number.
    $readNumber = {
        $str = $st.S; $n = $st.N; $i = $st.I
        if ([int]$str[$i] -eq 45) { $i++ }                                   # '-'
        $start = $i
        while ($i -lt $n -and [int]$str[$i] -ge 48 -and [int]$str[$i] -le 57) { $i++ }
        if ($i -eq $start -or ($i - $start -gt 1 -and [int]$str[$start] -eq 48)) { $st.Why = 'bad-number'; return $false }
        if ($i -lt $n -and [int]$str[$i] -eq 46) {                           # '.'
            $i++; $start = $i
            while ($i -lt $n -and [int]$str[$i] -ge 48 -and [int]$str[$i] -le 57) { $i++ }
            if ($i -eq $start) { $st.Why = 'bad-number'; return $false }
        }
        if ($i -lt $n -and ([int]$str[$i] -eq 101 -or [int]$str[$i] -eq 69)) { # 'e' / 'E'
            $i++
            if ($i -lt $n -and ([int]$str[$i] -eq 43 -or [int]$str[$i] -eq 45)) { $i++ }  # '+' / '-'
            $start = $i
            while ($i -lt $n -and [int]$str[$i] -ge 48 -and [int]$str[$i] -le 57) { $i++ }
            if ($i -eq $start) { $st.Why = 'bad-number'; return $false }
        }
        $st.I = $i
        return $true
    }
    $readLiteral = {
        foreach ($word in 'true', 'false', 'null') {
            if ($st.I + $word.Length -le $st.N -and [string]::CompareOrdinal($st.S, $st.I, $word, 0, $word.Length) -eq 0) { $st.I += $word.Length; return $true }
        }
        $st.Why = 'illegal-literal'; return $false
    }
    # value at depth $Depth: the top-level object is depth 1, a value nested in it depth 2. The
    # depth is checked when a CONTAINER OPENS, so 64 nested objects/arrays are allowed and the
    # 65th is too-deep even when it is empty. $IsTop marks the top object.
    $readValue = $null
    $readValue = {
        param([int] $Depth, [bool] $IsTop)
        & $skipWs
        if ($st.I -ge $st.N) { $st.Why = 'unbalanced'; return $false }
        $c = $st.S[$st.I]
        if (($c -eq '{' -or $c -eq '[') -and $Depth -gt 64) { $st.Why = 'too-deep'; return $false }
        if ($c -eq '{') {
            $st.I++; & $skipWs
            if ($st.I -lt $st.N -and $st.S[$st.I] -eq '}') { $st.I++; return $true }
            while ($true) {
                & $skipWs
                if ($st.I -ge $st.N) { $st.Why = 'unbalanced'; return $false }
                if ($st.S[$st.I] -ne '"') { $st.Why = 'expected-member-name'; return $false }
                $name = & $readString
                if ($null -eq $name) { return $false }
                if ($IsTop -and $name.Value -ceq 'errorCode') {
                    if ($name.Escaped) { $st.Why = 'escaped-top-level-key'; return $false }
                    $st.ErrorCodeKeys++
                }
                & $skipWs
                if ($st.I -ge $st.N -or $st.S[$st.I] -ne ':') { $st.Why = 'expected-colon'; return $false }
                $st.I++
                if (-not (& $readValue ($Depth + 1) $false)) { return $false }
                & $skipWs
                if ($st.I -ge $st.N) { $st.Why = 'unbalanced'; return $false }
                if ($st.S[$st.I] -eq ',') { $st.I++; continue }
                if ($st.S[$st.I] -eq '}') { $st.I++; return $true }
                $st.Why = 'expected-comma-or-close'; return $false
            }
        }
        if ($IsTop) { $st.Why = 'not-an-object'; return $false }
        if ($c -eq '[') {
            $st.I++; & $skipWs
            if ($st.I -lt $st.N -and $st.S[$st.I] -eq ']') { $st.I++; return $true }
            while ($true) {
                if (-not (& $readValue ($Depth + 1) $false)) { return $false }
                & $skipWs
                if ($st.I -ge $st.N) { $st.Why = 'unbalanced'; return $false }
                if ($st.S[$st.I] -eq ',') { $st.I++; continue }
                if ($st.S[$st.I] -eq ']') { $st.I++; return $true }
                $st.Why = 'expected-comma-or-close'; return $false
            }
        }
        if ($c -eq '"') { return ($null -ne (& $readString)) }
        if ($c -eq '-' -or ($c -ge '0' -and $c -le '9')) { return (& $readNumber) }
        if ($c -ceq 't' -or $c -ceq 'f' -or $c -ceq 'n') { return (& $readLiteral) }
        $st.Why = 'illegal-character'; return $false
    }

    & $skipWs
    if ($st.I -ge $st.N -or $st.S[$st.I] -ne '{') { return [pscustomobject]@{ Ok = $false; Reason = 'not-an-object' } }
    if (-not (& $readValue 1 $true)) { return [pscustomobject]@{ Ok = $false; Reason = $st.Why } }
    & $skipWs
    if ($st.I -lt $st.N) { return [pscustomobject]@{ Ok = $false; Reason = 'trailing-content' } }
    if ($st.ErrorCodeKeys -gt 1) { return [pscustomobject]@{ Ok = $false; Reason = 'duplicate-errorCode' } }
    return [pscustomobject]@{ Ok = $true; Reason = 'ok' }
}

# Reads the helper's TOP-LEVEL errorCode by PARSING its stdout as JSON - a regex over the raw
# text could take a nested "meta":{"errorCode":...} or a quoted '"errorCode":"x"' inside some
# other string field as the result. Returns one of:
#   State = 'code'        Code = the string value
#   State = 'null'        the property is PRESENT and null (the only shape a success can have)
#   State = 'unparseable' anything else: not JSON, not a single object, property absent, or a
#                         value that is neither a string nor null
#
# HOST-INDEPENDENT RULES. ConvertFrom-Json is LENIENT and differs between Windows PowerShell
# 5.1 and pwsh 7.6 (MEASURED on 5.1.26100 and 7.6.8): both accept single quotes, unquoted
# keys, NaN and Infinity, and resolve duplicate keys last-wins; pwsh also accepts // and /* */
# comments and unwraps or even completes arrays ('[{"errorCode":null}' parses on pwsh). So the
# text is first checked by Test-StrictHelperJson, which enforces strict JSON for everything
# this harness reads; only text that passes is handed to ConvertFrom-Json. Then:
#   * NAME: exactly 'errorCode', compared case-SENSITIVELY over the parsed properties; any
#     case variant ('ErrorCode') present as well makes the result unparseable.
#   * TYPE: $null or [string] only. pwsh turns a date-like string into [DateTime] unless
#     -DateKind String is passed, so it is passed whenever the parameter exists (5.1 has no
#     such conversion and no such parameter); any other type is unparseable.
# Every failure of these rules is 'unparseable', which can only ever read as FAILED.
function Get-HelperErrorCode {
    param([AllowNull()][string] $StdOut)
    $unparseable = [pscustomobject]@{ State = 'unparseable'; Code = $null }
    if ([string]::IsNullOrWhiteSpace($StdOut)) { return $unparseable }
    # The validator gets the text EXACTLY as received (so its length cap counts every char) and
    # skips JSON whitespace itself. Only JSON whitespace - space, tab, CR, LF - is trimmed for
    # ConvertFrom-Json: String.Trim() would also strip U+00A0, U+0085, U+2028/2029, U+3000,
    # VT and FF, turning text strict JSON rejects into a "completed" result.
    if (-not (Test-StrictHelperJson -Text $StdOut).Ok) { return $unparseable }
    $trimmed = $StdOut.Trim([char[]]@(' ', "`t", "`r", "`n"))
    $jsonArgs = @{ InputObject = $trimmed; ErrorAction = 'Stop' }
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $jsonArgs['DateKind'] = 'String' }
    try {
        $parsed = ConvertFrom-Json @jsonArgs
    } catch {
        return $unparseable
    }
    if ($parsed -isnot [System.Management.Automation.PSCustomObject]) { return $unparseable }
    $matching = @($parsed.PSObject.Properties | Where-Object { $_.Name -ceq 'errorCode' })
    $caseVariants = @($parsed.PSObject.Properties | Where-Object { $_.Name -ieq 'errorCode' -and $_.Name -cne 'errorCode' })
    if ($matching.Count -ne 1 -or $caseVariants.Count -gt 0) { return $unparseable }
    $value = $matching[0].Value
    if ($null -eq $value) { return [pscustomobject]@{ State = 'null'; Code = $null } }
    if ($value -isnot [string]) { return $unparseable }
    return [pscustomobject]@{ State = 'code'; Code = [string]$value }
}

function Get-Case2Interpretation {
    param([int] $ExitCode, [string] $StdOut)
    $privateTargetText = 'private-target guard refused the loopback redirect hop (expected for a loopback listener; the exchange still proves HEAD + header hygiene)'
    $parsedCode = Get-HelperErrorCode -StdOut $StdOut
    $code = $(if ($parsedCode.State -eq 'code') { $parsedCode.Code } else { $null })
    $codeIsNull = $parsedCode.State -eq 'null'
    if ($code -eq 'shortlink.private-target') { return $privateTargetText }
    if (-not [string]::IsNullOrEmpty($code)) {
        # [^"]* also matches CR/LF, so the echoed code is encoded like every other helper field.
        return "FAILED - redirect chain did NOT complete: helper errorCode=$(ConvertTo-ArtifactField $code) (exit=$ExitCode)"
    }
    if ($ExitCode -ne 0) {
        return "FAILED - redirect chain did NOT complete: helper exit=$ExitCode with no errorCode in stdout"
    }
    if ($codeIsNull) { return 'redirect chain completed' }
    return 'FAILED - redirect chain did NOT complete: helper stdout carried no parseable result'
}

# TOP-LEVEL GUARD. Everything from the artifact header to the artifact write runs inside this
# try, so an exception ANYWHERE in it (a helper whose hash cannot be read, a socket failure no
# case-level catch covers, a bug in a later edit) still produces an artifact: the lines
# collected so far plus "harness verdict: FAIL - harness exception: <TypeName>" (type name
# only - messages can carry paths or URLs), and exit 1. A failure before the output folder
# exists is handled where it happens (see the setup block above), on stderr.
$harnessExceptionType = $null
$harnessExit = 1
$elevated = $false
$kernelTraceRunning = $false
try {
    $artifact.Add('W7.4 capture-network.ps1 evidence (reproducible, redacted)')
    $artifact.Add("generated_at_utc: $((Get-Date).ToUniversalTime().ToString('o'))")
    $artifact.Add("helper: $(ConvertTo-ArtifactField ([System.IO.Path]::GetFileName($HelperPath)))")
    $artifact.Add("helper_sha256: $((Get-FileHash -LiteralPath $HelperPath -Algorithm SHA256).Hash.ToLowerInvariant())")
    $artifact.Add("powershell: $($PSVersionTable.PSVersion)")
    $artifact.Add('')

    $harnessFailures = New-Object System.Collections.Generic.List[string]

    # THE ONE PLACE A FAILURE IS RECORDED. Every check below that writes a FAIL into the artifact
    # also calls this, so the final verdict and exit code are built from the same facts as the
    # artifact text. The backstop before the verdict re-scans the artifact for failure wording no
    # check recorded, so a check that forgets to call this still cannot produce "verdict: PASS".
    function Add-HarnessFailure {
        param([string] $Reason)
        # The verdict is ONE line. Callers pass helper text already encoded; this guard turns any
        # remaining non-printable character into \uXXXX so a missed spot still cannot split it.
        $script:harnessFailures.Add([regex]::Replace($Reason, '[^\x20-\x7E]', { param($m) '\u{0:x4}' -f [int][char]$m.Value }))
    }

    # Builds the final verdict from the recorded failures, after the BACKSTOP: any harness-authored
    # failure line in the artifact must be backed by a recorded failure. If a check wrote
    # FAIL/FAILED/UNPROVEN/PRESENT but forgot Add-HarnessFailure, the run still fails and the
    # verdict names that one line, so the drift is visible instead of a quiet PASS. Takes the
    # failures as a parameter (and adds to that same list) so tools/tests can drive it offline.
    function Get-HarnessVerdict {
        param([System.Collections.Generic.List[string]] $Failures, [string[]] $ArtifactItems)
        if ($Failures.Count -eq 0) {
            $unrecordedLine = Find-UnrecordedFailureLine -Items $ArtifactItems
            if ($null -ne $unrecordedLine) {
                $Failures.Add([regex]::Replace("unrecorded failure line in artifact: $unrecordedLine", '[^\x20-\x7E]', { param($m) '\u{0:x4}' -f [int][char]$m.Value }))
            }
        }
        $exit = $(if ($Failures.Count -eq 0) { 0 } else { 1 })
        return [pscustomobject]@{
            Exit = $exit
            Line = "harness verdict: $(if ($exit -eq 0) { 'PASS' } else { 'FAIL - ' + ($Failures -join '; ') })"
        }
    }

    # The backstop's scanner. Returns the FIRST artifact LINE (not list item: one item can be a
    # multi-line block) that carries a harness-authored failure marker, trimmed and capped at 160
    # characters, or $null. Lines that ECHO helper or network data are skipped, because their
    # content is not a harness status: a helper whose stdout says "UNPROVEN", or a request with a
    # header literally named PRESENT, must not flip a clean run. PRESENT counts only on the
    # harness's own "forbidden header '<name>': PRESENT" status line.
    # A helper whose output pipe is still open 5 s after it exited left a process behind that
    # inherited it; its result could not be read in full, so the case cannot be trusted.
    function Test-HelperStreamsClosed {
        param([string] $Case, [object] $HelperResult)
        if ($null -eq $HelperResult) { return }
        foreach ($stream in @(@('stdout', $HelperResult.StdOut), @('stderr', $HelperResult.StdErr))) {
            if ([string]$stream[1] -like '<stream still open after *') {
                $artifact.Add("$Case helper $($stream[0]): still open after the helper exited (FAIL)")
                Add-HarnessFailure "${Case}: helper $($stream[0]) still open after the helper exited (a child process kept it)"
            }
        }
    }

    function Find-UnrecordedFailureLine {
        param([string[]] $Items)
        $echoPrefixes = @('helper exit=', 'request line: ', 'header names: ', 'response Location: ', 'netsh output: ')
        foreach ($item in $Items) {
            foreach ($raw in ([string]$item -split '\r\n|\r|\n')) {
                $line = $raw.Trim()
                $echoed = $false
                foreach ($prefix in $echoPrefixes) {
                    if ($line.StartsWith($prefix, [System.StringComparison]::Ordinal)) { $echoed = $true; break }
                }
                if ($echoed) { continue }
                if ($line -cmatch '\(FAIL\)|=> FAIL\b|\bFAILED\b|\bUNPROVEN\b' -or $line -cmatch "^forbidden header '[^']+': PRESENT$") {
                    if ($line.Length -gt 160) { $line = $line.Substring(0, 160) }
                    return $line
                }
            }
        }
        return $null
    }

    # ---------------------------------------------------------------------------------------
    # KERNEL TRACE START. The trace must already be running when CASE 1..3 generate traffic, so it
    # is started HERE (elevated runs only, and never with -SkipKernelCapture) and stopped after
    # CASE 3 in the KERNEL-LEVEL CAPTURE section below - or, if the run aborts first, in the
    # top-level finally. The outcome is only collected here; the artifact reports it in that
    # section. A non-zero start exit code is a recorded failure there, never a claimed capture.
    # ---------------------------------------------------------------------------------------
    $elevated = Test-Elevated
    $etl = Join-Path $OutputDirectory "w7.4-network-trace-$stamp.etl"
    $kernelStartExit = $null
    $kernelStartOutput = ''
    $kernelStartException = $null
    function Stop-KernelTrace {
        if (-not $script:kernelTraceRunning) { return $null }
        # The running flag is cleared ONLY after netsh trace stop returned exit 0. If the call
        # throws, or netsh reports a failure, the session may still be running, so the flag stays
        # set and the top-level finally retries the stop once.
        $stopOutput = & netsh trace stop 2>&1
        $stopExit = $LASTEXITCODE
        if ($stopExit -eq 0) { $script:kernelTraceRunning = $false }
        return [pscustomobject]@{ ExitCode = $stopExit; Output = [string]::Join(' ', @($stopOutput)) }
    }
    if ((-not $SkipKernelCapture) -and $elevated) {
        try {
            $netshStart = & netsh trace start capture=yes report=no tracefile="$etl" persistent=no maxsize=128 2>&1
            $kernelStartExit = $LASTEXITCODE
            $kernelStartOutput = [string]::Join(' ', @($netshStart))
            if ($kernelStartExit -eq 0) { $script:kernelTraceRunning = $true }
        } catch {
            $kernelStartException = Get-ExceptionTypeName -Exception $_.Exception
        }
    }

    # ---------------------------------------------------------------------------------------
    # CASE 1 -- consent OFF. The plan's "zero traffic" requirement, observed.
    # ---------------------------------------------------------------------------------------
    # Invoke-NetworkHelper returns only once the helper has EXITED (or, past its budget, after it
    # was killed), so every connection it could have made is already sitting in the listener's
    # backlog when the count below runs - no fixed sleep decides the verdict. Every queued
    # connection is accepted and counted. A helper that had to be killed proves nothing either
    # way, so it is a FAIL rather than a zero-traffic PASS. try/finally: an exception still stops
    # the listener (the helper is reaped inside Invoke-NetworkHelper) and becomes a recorded FAIL.
    $consentOff = $null
    $consentOffConnections = 0
    $case1Exception = $null
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $consentOff = Invoke-NetworkHelper -ExePath $HelperPath -Url 'http://bit.ly/consent-off' -ConsentGranted $false -TimeoutMs $HelperCallTimeoutMs -ProxyUrl "http://127.0.0.1:$port"
        while ($listener.Pending()) {
            $straggler = $listener.AcceptTcpClient()
            $consentOffConnections++
            $straggler.Close()
        }
    } catch {
        $case1Exception = Get-ExceptionTypeName -Exception $_.Exception
    } finally {
        $listener.Stop()
    }
    $artifact.Add((Format-Redacted -Title 'CASE 1: consent OFF (plan: zero traffic)' -Requests @() -HelperResult $consentOff))
    Test-HelperStreamsClosed -Case 'case 1' -HelperResult $consentOff
    if ($null -ne $case1Exception) {
        $consentOffVerdict = "UNPROVEN - harness exception $case1Exception (FAIL)"
    } elseif ($consentOff.TimedOut) {
        $consentOffVerdict = "UNPROVEN - helper did not exit within $HelperCallTimeoutMs ms and was killed (FAIL)"
    } elseif ($consentOffConnections -gt 0) {
        $consentOffVerdict = "$consentOffConnections (FAIL)"
    } else {
        $consentOffVerdict = '0 (PASS)'
    }
    if ($consentOffVerdict -ne '0 (PASS)') { Add-HarnessFailure "consent-off: $consentOffVerdict" }
    $artifact.Add("consent-off observed outbound connection attempts: $consentOffVerdict")
    $artifact.Add('')

    # ---------------------------------------------------------------------------------------
    # CASE 2 -- consent ON, manual HEAD redirect over the loopback listener.
    #
    # ORDERING, which is the whole trick: the helper must already be running when the first hop
    # arrives, and the hops can only be answered while it waits. So the child is STARTED FIRST
    # (asynchronously; its stdout/stderr are drained on tasks), and then this thread serves the
    # two hops the child will request. Without the async start the listener would block on
    # AcceptTcpClient forever, because nothing had connected yet.
    #
    # LIFETIME. The whole case runs inside try/finally: whatever happens (a helper that connects
    # and then sends nothing makes ReadLine throw once ReceiveTimeout passes), the client is
    # closed, a still-running helper is killed, the listener is stopped, and the exception TYPE
    # becomes a recorded FAIL - so the artifact is still written and the harness exits 1.
    # ---------------------------------------------------------------------------------------
    $hops = @(
        @{ Status = 'HTTP/1.1 302 Found'; Headers = @{ 'Location' = 'http://loopback.example/final'; 'Content-Length' = '0'; 'Connection' = 'close' } },
        @{ Status = 'HTTP/1.1 200 OK'; Headers = @{ 'Content-Length' = '0'; 'Connection' = 'close' } }
    )

    $captured = New-Object System.Collections.Generic.List[object]
    $process = $null
    $client = $null
    $stdoutTask = $null
    $stderrTask = $null
    $case2Exception = $null
    $deadlineHit = $false
    $unreadableHops = New-Object System.Collections.Generic.List[int]
    $extraDataHops = New-Object System.Collections.Generic.List[int]
    $bareCrHops = New-Object System.Collections.Generic.List[int]
    $keptClients = New-Object System.Collections.Generic.List[object]
    $resetHops = New-Object System.Collections.Generic.List[int]
    $extraConnections = 0
    $acceptTask = $null
    $accepted = $false
    $consentOnTimedOut = $false
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $HelperPath
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.Arguments = 'resolve-short-link'
        $psi.EnvironmentVariables['HTTP_PROXY'] = "http://127.0.0.1:$port"
        $psi.EnvironmentVariables['HTTPS_PROXY'] = "http://127.0.0.1:$port"
        $process = [System.Diagnostics.Process]::Start($psi)
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        try {
            $process.StandardInput.Write((@{ url = 'http://bit.ly/loopback-start'; consentGranted = $true } | ConvertTo-Json -Compress))
            $process.StandardInput.Close()
        } catch {
            # Same guard as Invoke-NetworkHelper: a fast exit must not crash the harness.
        }

        # Each hop WAITS for the helper's connection instead of sampling Pending() once. The earlier
        # single 400 ms sample gave up whenever the helper took longer than that to start (a cold
        # start under load does), which left the helper's connection unanswered in the backlog until
        # it timed out: shortlink.timeout and zero captured exchanges (MEASURED 3/5 on an idle
        # machine; 100% with a helper delayed by ~1 s). A hop now ends only when the connection is
        # accepted, the helper has exited (nothing more can arrive), or the overall deadline passes.
        # Wait() is checked BEFORE HasExited, so a connection queued just before the exit is served.
        $serveDeadline = [DateTime]::UtcNow.AddSeconds(10)
        for ($hopIndex = 0; $hopIndex -lt $hops.Count; $hopIndex++) {
            $acceptTask = $listener.AcceptTcpClientAsync()
            $accepted = $false
            while ($true) {
                if ($acceptTask.Wait(100)) { $accepted = $true; break }
                Watch-KeptConnections -Kept $keptClients
                if ($process.HasExited) { break }
                if ([DateTime]::UtcNow -ge $serveDeadline) { $deadlineHit = $true; break }
            }
            if (-not $accepted) { break }
            $client = $acceptTask.Result
            $client.ReceiveTimeout = 3000
            $request = Read-HttpRequest -Client $client
            if ($null -ne $request -and $request.Malformed) {
                # FAIL CLOSED on a bare CR (see Read-AsciiLine): the request is not captured.
                $bareCrHops.Add($hopIndex + 1)
            } elseif ($null -ne $request) {
                $captured.Add($request)
                Write-ScriptedResponse -Client $client -StatusLine $hops[$hopIndex].Status -Headers $hops[$hopIndex].Headers -Request $request
                # KEEP THE CONNECTION OPEN (D17). Anything sent after the one request this hop
                # reads - e.g. a GET with Cookie/Authorization PIPELINED behind the clean HEAD -
                # is never parsed, so its mere presence is the failure. A fixed post-response wait
                # could be outlasted by a client that sends late, so the connection is held open
                # and WATCHED on every 100 ms tick - while waiting for the next hop and while
                # waiting for the helper to exit - with one final 0.5 s look before it is closed
                # (in the finally below). Draining only once after exit cannot work: on Windows a
                # client that closes with our response still unread sends an RST, and the RST
                # discards late data already in our receive buffer before it can be read.
                $keptClients.Add([pscustomobject]@{ Hop = $hopIndex + 1; Client = $client; Done = $false; ExtraData = $false; Reset = $false })
                $client = $null
                continue
            } else {
                # FAIL CLOSED. An accepted connection whose request cannot be read (a blank first
                # line) used to be skipped silently: the hop was spent, nothing was captured, and a
                # clean HEAD on the next connection then PASSED - hiding whatever the unreadable
                # one carried (e.g. a GET with Cookie/Authorization after a leading CRLF). Leading
                # blank lines are NOT skipped to "recover" the request: any unreadable request is
                # a recorded failure, which is simpler and cannot be talked past.
                $unreadableHops.Add($hopIndex + 1)
            }
            $client.Close()
            $client = $null
        }
        # Past the deadline there is nothing left to wait for; otherwise the helper gets its
        # per-call budget (-HelperCallTimeoutMs, default 5 s) to finish on its own. A helper still
        # running after that is killed in the finally below.
        # The kept connections are WATCHED on every 100 ms tick of that wait too.
        if (-not $deadlineHit) {
            $exitDeadline = [DateTime]::UtcNow.AddMilliseconds($HelperCallTimeoutMs)
            while (-not $process.WaitForExit(100) -and [DateTime]::UtcNow -lt $exitDeadline) {
                Watch-KeptConnections -Kept $keptClients
            }
        }
    } catch {
        $case2Exception = Get-ExceptionTypeName -Exception $_.Exception
    } finally {
        if ($null -ne $client) { try { $client.Close() } catch { } }
        if ($null -ne $process) {
            $stillRunning = $true
            try { $stillRunning = -not $process.HasExited } catch { }
            if ($stillRunning) {
                $consentOnTimedOut = $true
                Stop-HelperProcess -Process $process
            }
        }
        # FINAL LOOK AT EVERY KEPT CONNECTION (D17), then close it whatever happens. The helper is
        # gone, so its last bytes are already here (or its close has arrived); a 500 ms ceiling
        # covers a socket some other process still holds open.
        foreach ($kept in $keptClients) {
            try {
                if (-not $kept.Done) { $null = Test-KeptConnection -Kept $kept -WaitMicroseconds 500000 }
            } finally {
                try { $kept.Client.Close() } catch { }
            }
        }
        foreach ($kept in $keptClients) {
            if ($kept.ExtraData) { $extraDataHops.Add($kept.Hop) }
            if ($kept.Reset) { $resetHops.Add($kept.Hop) }
        }
        # DRAIN THE BACKLOG (D16), like CASE 1. The helper has exited or been killed by now, and a
        # client's Connect only returns once the handshake has put the connection in this queue,
        # so every connection it made is already here; none can arrive later, so no grace wait is
        # needed. Anything still queued after the last served hop was never read: each one is an
        # unexpected extra connection and a FAIL (bounded at 64 so a flood cannot stall the run).
        # The pending accept of an unserved hop may already have taken one of them, so it is
        # counted too, and closed.
        try {
            if ($null -ne $acceptTask -and $acceptTask.IsCompleted -and -not $acceptTask.IsFaulted -and -not $accepted) {
                $extraConnections++
                $acceptTask.Result.Close()
            }
            while ($extraConnections -lt 64 -and $listener.Pending()) {
                $extra = $listener.AcceptTcpClient()
                $extraConnections++
                $extra.Close()
            }
        } catch {
            # Best effort: a drain failure must not hide the case's own outcome.
        }
        # Stop() also ends a still-pending AcceptTcpClientAsync; that task is abandoned, not awaited.
        $listener.Stop()
    }

    $consentOn = [pscustomobject]@{
        ExitCode = $(if ($null -eq $process) { -1 } elseif ($consentOnTimedOut) { 124 } else { $process.ExitCode })
        StdOut = Get-StreamText -Task $stdoutTask
        StdErr = $(if ($consentOnTimedOut) { 'harness-child-timeout' } else { Get-StreamText -Task $stderrTask })
        TimedOut = $consentOnTimedOut
    }

    $requests = $captured.ToArray()
    $artifact.Add((Format-Redacted -Title 'CASE 2: consent ON, manual HEAD redirect (loopback application-layer capture)' -Requests $requests -HelperResult $consentOn))
    Test-HelperStreamsClosed -Case 'case 2' -HelperResult $consentOn
    if ($null -ne $case2Exception) {
        $artifact.Add("case 2 harness exception: $case2Exception (FAIL)")
        Add-HarnessFailure "case 2: harness exception: $case2Exception"
    }
    if ($deadlineHit) {
        $artifact.Add('case 2 serve deadline: 10 s passed without the next connection (FAIL)')
        Add-HarnessFailure 'case 2: serve deadline passed without the next connection'
    }
    foreach ($unreadableHop in $unreadableHops) {
        $artifact.Add("case 2: unreadable request on hop $unreadableHop (FAIL)")
        Add-HarnessFailure "case 2: unreadable request on hop $unreadableHop"
    }
    foreach ($bareCrHop in $bareCrHops) {
        $artifact.Add("case 2: malformed line (bare CR) on hop $bareCrHop (FAIL)")
        Add-HarnessFailure "case 2: malformed line (bare CR) on hop $bareCrHop"
    }
    foreach ($resetHop in $resetHops) {
        $artifact.Add("case 2: connection reset after request on hop $resetHop (FAIL)")
        Add-HarnessFailure "case 2: connection reset after request on hop $resetHop"
    }
    foreach ($extraDataHop in $extraDataHops) {
        $artifact.Add("case 2: extra data after request on hop $extraDataHop (FAIL)")
        Add-HarnessFailure "case 2: extra data after request on hop $extraDataHop"
    }
    if ($extraConnections -gt 0) {
        $artifact.Add("case 2: $extraConnections unexpected extra connection(s) (FAIL)")
        Add-HarnessFailure "case 2: $extraConnections unexpected extra connection(s)"
    }
    $methodOk = ($requests.Count -gt 0) -and (@($requests | Where-Object { $_.Method -eq 'HEAD' }).Count -eq $requests.Count)
    $artifact.Add("redirect trace: exchanges=$($requests.Count); all HEAD=$methodOk")
    if ($requests.Count -gt 0 -and -not $methodOk) {
        # Only a plain method token is echoed into the verdict; anything else is shown as <other>.
        $badMethods = @($requests | Where-Object { $_.Method -ne 'HEAD' } | ForEach-Object { if ($_.Method -cmatch '^[A-Z]{1,16}$') { $_.Method } else { '<other>' } } | Select-Object -Unique)
        $artifact.Add("non-HEAD request methods: $($badMethods -join ', ') (FAIL)")
        Add-HarnessFailure "case 2: non-HEAD request method(s): $($badMethods -join ', ')"
    }
    $forbiddenPresent = New-Object System.Collections.Generic.List[string]
    foreach ($r in $requests) {
        foreach ($forbidden in $forbiddenHeaders) {
            if (@($r.HeaderNames | Where-Object { $_ -ieq $forbidden }).Count -gt 0 -and -not $forbiddenPresent.Contains($forbidden)) { $forbiddenPresent.Add($forbidden) }
        }
    }
    # "NONE" is a claim about captured requests, so it is only written when at least one was
    # captured; zero exchanges is a failed capture, not clean header hygiene.
    if ($requests.Count -eq 0) {
        $artifact.Add('forbidden headers present anywhere: NO EXCHANGE CAPTURED (FAIL)')
        Add-HarnessFailure 'case 2: no exchange captured'
    } elseif ($forbiddenPresent.Count -gt 0) {
        $artifact.Add("forbidden headers present anywhere: $($forbiddenPresent -join ', ') (FAIL)")
        Add-HarnessFailure "case 2: forbidden headers present: $($forbiddenPresent -join ', ')"
    } else {
        $artifact.Add('forbidden headers present anywhere: NONE')
    }

    # OBSERVED, AND WORTH READING RATHER THAN HIDING. In this case the helper returns
    # errorCode=shortlink.private-target and stops after the FIRST exchange. That is the
    # private-local-internal guard doing exactly its job: the harness serves the redirect FROM a
    # loopback listener, so the redirect target resolves inside 127.0.0.0/8 and the resolver
    # refuses to follow it. A loopback listener therefore cannot demonstrate a SUCCESSFUL public
    # redirect -- that needs a resolvable public host or the resolver's DNS seam, neither of
    # which an unprivileged harness can fabricate without becoming a mock. What the loopback
    # capture DOES prove is the part that matters and is otherwise hard to see: the FIRST
    # outbound request is a manual HEAD carrying only Host, and the guard refuses the private
    # hop. Both facts are recorded above and neither is dressed up as a successful redirect.
    $case2Interpretation = Get-Case2Interpretation -ExitCode $consentOn.ExitCode -StdOut $consentOn.StdOut
    $artifact.Add("case 2 interpretation: $case2Interpretation")
    if ($case2Interpretation -like 'FAILED*') { Add-HarnessFailure "case 2: $case2Interpretation" }
    $artifact.Add('')

    # ---------------------------------------------------------------------------------------
    # CASE 3 -- the guard chain, exercised through the REAL helper with no network at all.
    # Each of these is a rejection the plan's "Negative/security/privacy" clause names:
    #   * a non-allowlisted host            -> host-not-allowlisted
    #   * a private/loopback literal        -> host-not-allowlisted (scheme/host gate precedes it)
    #   * a file: scheme                    -> invalid-url
    # These run offline, so they are deterministic and CI-safe. Each probe is its own
    # try/catch: an exception is that probe's FAIL, and the remaining probes still run.
    # ---------------------------------------------------------------------------------------
    $guardProbes = @(
        @{ Url = 'https://example.com/not-a-shortener'; Expect = 'shortlink.host-not-allowlisted' },
        @{ Url = 'http://127.0.0.1/internal'; Expect = 'shortlink.host-not-allowlisted' },
        @{ Url = 'file:///C:/Windows/win.ini'; Expect = 'shortlink.invalid-url' }
    )
    $artifact.Add('== CASE 3: guard chain (offline, deterministic) ==')
    $guardFailures = New-Object System.Collections.Generic.List[string]
    foreach ($probe in $guardProbes) {
        try {
            $result = Invoke-NetworkHelper -ExePath $HelperPath -Url $probe.Url -ConsentGranted $true -TimeoutMs $HelperCallTimeoutMs -ProxyUrl ''
            Test-HelperStreamsClosed -Case "case 3 ($($probe.Url))" -HelperResult $result
            $probeCode = Get-HelperErrorCode -StdOut $result.StdOut
            $observed = if ($probeCode.State -eq 'code') { $probeCode.Code } elseif ($probeCode.State -eq 'null') { '<null>' } else { '<none>' }
        } catch {
            $observed = "<harness exception: $(Get-ExceptionTypeName -Exception $_.Exception)>"
        }
        $ok = $observed -eq $probe.Expect
        # The comparison uses the raw value; everything written uses the encoded one.
        $observedText = ConvertTo-ArtifactField $observed
        if (-not $ok) {
            $guardFailures.Add("$($probe.Url): expected $($probe.Expect), got $observedText")
            Add-HarnessFailure "case 3: $($probe.Url): expected $($probe.Expect), got $observedText"
        }
        $artifact.Add(("url={0} consent=on expected={1} observed={2} => {3}" -f $probe.Url, $probe.Expect, $observedText, $(if ($ok) { 'PASS' } else { 'FAIL' })))
    }
    $artifact.Add("guard chain result: $(if ($guardFailures.Count -eq 0) { 'ALL PASS' } else { $guardFailures -join '; ' })")
    $artifact.Add('')

    # ---------------------------------------------------------------------------------------
    # KERNEL-LEVEL CAPTURE: attempted only when asked for AND elevated. Recorded truthfully.
    # ---------------------------------------------------------------------------------------
    $artifact.Add('KERNEL-LEVEL CAPTURE (ETL)')
    if ($SkipKernelCapture) {
        $artifact.Add('requested: yes (-SkipKernelCapture); outcome: SKIPPED BY OPERATOR REQUEST')
    } elseif (-not $elevated) {
        $artifact.Add('requested: yes; outcome: BLOCKED - NO ADMIN ELEVATION in this shell')
        $artifact.Add('netsh trace start / pktmon start require an elevated shell; run this harness elevated to obtain an ETL.')
        $artifact.Add('The application-layer transcript above is the best honest unprivileged evidence; it is NOT a kernel capture and is not presented as one.')
    } else {
        # The trace was started before CASE 1 (see KERNEL TRACE START); this section stops it and
        # reports what actually happened at each step.
        if ($null -ne $kernelStartException) {
            $artifact.Add("kernel capture exception: netsh trace start threw $kernelStartException (FAIL)")
            Add-HarnessFailure "kernel capture: netsh trace start harness exception: $kernelStartException"
        } elseif ($kernelStartExit -ne 0) {
            $artifact.Add("netsh trace start: exit $kernelStartExit")
            $artifact.Add("netsh output: $(ConvertTo-ArtifactField $kernelStartOutput)")
            $artifact.Add('kernel capture: netsh trace start failed, so no trace ran during the cases and no ETL is claimed (FAIL)')
            Add-HarnessFailure "kernel capture: netsh trace start exited $kernelStartExit"
        } else {
            $artifact.Add("netsh trace start: exit $kernelStartExit")
            $artifact.Add("netsh output: $(ConvertTo-ArtifactField $kernelStartOutput)")
            try {
                $stopResult = Stop-KernelTrace
                $artifact.Add("netsh trace stop: exit $($stopResult.ExitCode)")
                $artifact.Add("netsh output: $(ConvertTo-ArtifactField $stopResult.Output)")
                if ($stopResult.ExitCode -ne 0) {
                    $artifact.Add('kernel capture: netsh trace stop returned a non-zero exit code (FAIL)')
                    Add-HarnessFailure "kernel capture: netsh trace stop exited $($stopResult.ExitCode)"
                }
                if (Test-Path -LiteralPath $etl) {
                    $artifact.Add("kernel capture: $([System.IO.Path]::GetFileName($etl)) ($((Get-Item -LiteralPath $etl).Length) bytes)")
                    $artifact.Add("kernel capture sha256: $((Get-FileHash -LiteralPath $etl -Algorithm SHA256).Hash.ToLowerInvariant())")
                } else {
                    $artifact.Add('kernel capture: netsh trace start exited 0 but no ETL file exists after netsh trace stop - recorded as FAILED, not as a pass')
                    Add-HarnessFailure 'kernel capture: trace started (exit 0) but no ETL was produced after stop'
                }
            } catch {
                $artifact.Add("kernel capture exception: $(Get-ExceptionTypeName -Exception $_.Exception) (FAIL)")
                Add-HarnessFailure "kernel capture: harness exception: $(Get-ExceptionTypeName -Exception $_.Exception)"
            }
        }
    }

    $artifact.Add('')
    $artifact.Add('LIMITS (explicit)')
    $artifact.Add('- The application-layer transcript is captured at the loopback listener, not in the kernel.')
    $artifact.Add('- Header VALUES are not written except response Location, which is part of the redirect trace.')
    $artifact.Add('- A kernel ETL is produced only when this harness runs elevated; otherwise its absence is stated, never faked.')
    $artifact.Add('')
    $finalVerdict = Get-HarnessVerdict -Failures $harnessFailures -ArtifactItems $artifact.ToArray()
    $harnessExit = $finalVerdict.Exit
    $artifact.Add($finalVerdict.Line)

    # FAIL-SAFE SANITISER at write time: the artifact is printable ASCII plus the CRLF line breaks
    # the harness itself puts between lines. Anything else that reached it (a field some future
    # edit forgot to encode) is written as \uXXXX instead of raw, so it can never become a new
    # line or a non-ASCII byte. Fields encoded upstream contain nothing it would touch.
    [System.IO.File]::WriteAllText($transcriptPath, (ConvertTo-ArtifactText -Lines $artifact.ToArray()), (New-Object System.Text.UTF8Encoding($false)))
    $artifactWritten = $true
} catch {
    $harnessExceptionType = Get-ExceptionTypeName -Exception $_.Exception
} finally {
    # A trace whose running flag is still set here was either never stopped (the run aborted before
    # the kernel section) or its stop did not succeed (the call threw or returned non-zero). Never
    # leave a system-wide netsh trace session behind: retry the stop once, and say so truthfully.
    if ($kernelTraceRunning) {
        $cleanupStopExit = $null
        try {
            $null = & netsh trace stop 2>&1
            $cleanupStopExit = $LASTEXITCODE
        } catch {
            $cleanupStopExit = "threw $(Get-ExceptionTypeName -Exception $_.Exception)"
        }
        if ($cleanupStopExit -eq 0) { $kernelTraceRunning = $false }
        $artifact.Add("kernel capture cleanup: netsh trace was still marked running; stop retried in cleanup (result: $cleanupStopExit); no ETL is claimed from this path")
    }
    if ($null -ne $harnessExceptionType) {
        # The verdict for an aborted run. Written here, not by the checks, because they did not
        # all run; the partial artifact above it shows how far the run got.
        $harnessExit = 1
        $artifact.Add('')
        $artifact.Add("harness verdict: FAIL - harness exception: $harnessExceptionType")
        try {
            [System.IO.File]::WriteAllText($transcriptPath, (ConvertTo-ArtifactText -Lines $artifact.ToArray()), (New-Object System.Text.UTF8Encoding($false)))
            $artifactWritten = $true
        } catch {
            [Console]::Error.WriteLine("CAPTURE_NETWORK_ERROR=artifact write failed: $(Get-ExceptionTypeName -Exception $_.Exception)")
        }
    }
}
# The artifact line is printed ONLY after a successful write, so a run that wrote nothing can
# never point at a file that does not exist. Console output stays free of user-profile paths:
# no doc or tool consumes this line (the tests locate the artifact by listing
# -OutputDirectory), so a path inside the repo is printed repo-relative and one outside it as
# the file name only - the operator chose that folder.
if ($artifactWritten) {
    $repoPrefix = $repoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $fullTranscriptPath = [System.IO.Path]::GetFullPath($transcriptPath)
    if ($fullTranscriptPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $displayPath = $fullTranscriptPath.Substring($repoPrefix.Length) -replace '\\', '/'
    } else {
        $displayPath = "<OutputDirectory>/$([System.IO.Path]::GetFileName($fullTranscriptPath))"
    }
    Write-Output "CAPTURE_NETWORK_ARTIFACT=$displayPath"
} else {
    $harnessExit = 1
}
Write-Output 'CASES=consent-off,consent-on-loopback-redirect,guard-chain'
if ($SkipKernelCapture) {
    Write-Output 'KERNEL_CAPTURE=SKIPPED-BY-OPERATOR'
} elseif ($elevated) {
    Write-Output 'KERNEL_CAPTURE=ATTEMPTED'
} else {
    Write-Output 'KERNEL_CAPTURE=UNAVAILABLE-NO-ELEVATION'
}
Write-Output "CAPTURE_NETWORK_EXIT=$harnessExit"
exit $harnessExit
