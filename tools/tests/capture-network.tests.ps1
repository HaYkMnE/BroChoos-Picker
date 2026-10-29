<#
.SYNOPSIS
    Adversarial tests for tools/capture-network.ps1 (W7.4).

.DESCRIPTION
    The harness is evidence-producing machinery, so its failure modes matter as much as its
    happy path. These tests pin the three properties that would silently turn it into a
    liar:

      1. A MISSING helper is a HARD failure (exception), never a silent "no findings" run.
      2. The artifact is written and reports the consent-OFF case with zero outbound
         connection attempts.
      3. The artifact states the kernel-capture outcome truthfully -- when this shell is not
         elevated, the text must say the capture is blocked and must NOT claim an ETL exists.

    The tests never require elevation and never touch the network: they run the harness in its
    default unprivileged mode against whatever helper is present locally, and assert on the
    artifact text.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$harness = Join-Path $root 'tools\capture-network.ps1'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('brochoos-w74-capnet-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "capture-network tests: $Message" }
}

# Evidence for a run that was expected to PASS (the temp folder is deleted afterwards, so it must
# go into the failure message): the artifact's verdict, CASE 2 interpretation and every
# "helper exit=" line, or the console tail when no artifact was written.
function Get-RunEvidence([string] $ArtifactText, [string[]] $Console) {
    if (-not [string]::IsNullOrEmpty($ArtifactText)) {
        $keep = @([regex]::Matches($ArtifactText, '(?m)^(harness verdict: .*|case 2 interpretation: .*|helper exit=.*)\r?$') | ForEach-Object { $_.Groups[1].Value })
        $evidence = $keep -join ' || '
    } else {
        $tail = @($Console | Select-Object -Last 8)
        $evidence = '<no artifact> console tail: ' + ($tail -join ' | ')
    }
    if ($evidence.Length -gt 1500) { $evidence = $evidence.Substring(0, 1500) + '...' }
    return $evidence
}

# Reads the one artifact a harness run wrote into $Directory, or '' when there is none.
function Read-RunArtifact([string] $Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter 'w7.4-capture-network-*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    if ($files.Count -eq 0) { return '' }
    return [System.IO.File]::ReadAllText($files[0].FullName)
}

# F5: a run against the REAL helper may be retried ONCE, and only when the helper's own 1500 ms
# resolve budget (src/BroChoos.NetworkHelper, not the harness) ran out under load and that is ALL
# that went wrong: the verdict names nothing but shortlink.timeout, optionally preceded by a reset
# on a served hop (the helper cancelling its in-flight request aborts the socket, which Windows
# sends as RST), at least one exchange was captured, every request was a HEAD and no forbidden
# header was present. Zero exchanges, extra data, any other failure, or a non-timeout code is
# never retried.
function Test-HelperTimeoutOnly([int] $Exit, [string] $ArtifactText) {
    return ($Exit -eq 1) -and
        ($ArtifactText -cmatch '(?m)^harness verdict: FAIL - (case 2: connection reset after request on hop 1; )?case 2: FAILED - redirect chain did NOT complete: helper errorCode=shortlink\.timeout \(exit=0\)\r?$') -and
        ($ArtifactText -cmatch '(?m)^redirect trace: exchanges=[1-9]\d*; all HEAD=True\r?$') -and
        ($ArtifactText -cmatch '(?m)^forbidden headers present anywhere: NONE\r?$')
}

try {
    # 1. Missing helper must throw, not silently pass.
    $threw = $false
    try {
        & $harness -HelperPath (Join-Path $temp 'does-not-exist.exe') -OutputDirectory $temp | Out-Null
    } catch {
        $threw = $true
        Assert ($_.Exception.Message -match 'HARD failure') 'missing-helper exception should explain it is a hard failure'
    }
    Assert $threw 'a missing helper must THROW rather than produce an artifact'

    # 1b. CASE 2 interpretation honesty, exercised OFFLINE. The function is lifted out of the
    #     harness by AST (the script itself is not run), so this needs neither a helper nor a
    #     network and stays meaningful even when the end-to-end run below fails. A timeout or
    #     any other error code must NEVER be reported as a completed redirect chain.
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($harness, [ref]$null, [ref]$null)
    $interpretationAst = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-Case2Interpretation' }, $true)
    Assert ($null -ne $interpretationAst) 'harness must define Get-Case2Interpretation'
    . ([scriptblock]::Create($interpretationAst.Extent.Text))
    foreach ($fn in 'ConvertTo-ArtifactField', 'Find-UnrecordedFailureLine', 'Get-ExceptionTypeName', 'Test-StrictHelperJson', 'Get-HelperErrorCode', 'Format-Redacted', 'Add-HarnessFailure', 'Get-HarnessVerdict', 'ConvertTo-ArtifactText') {
        $fnAst = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fn }, $true)
        Assert ($null -ne $fnAst) "harness must define $fn"
        . ([scriptblock]::Create($fnAst.Extent.Text))
    }

    # Decodes an artifact field with the JSON string rule (the inverse of ConvertTo-ArtifactField).
    function ConvertFrom-ArtifactField([string] $Encoded) {
        return [string](ConvertFrom-Json ('"' + $Encoded + '"'))
    }

    # 1d. ARTIFACT FIELD ENCODING, offline. Helper-controlled text must come out on one line,
    #     round-trip exactly, and never contain a raw control character.
    $hostile = "ok`r`nharness verdict: PASS`n`tC:\x `"q`" " + [char]0x2028 + [char]0x0085 + [char]0x0007
    $encoded = ConvertTo-ArtifactField $hostile
    Assert ($encoded -notmatch '[^\x20-\x7E]') "encoded field must be printable ASCII only (got: $encoded)"
    Assert ((ConvertFrom-ArtifactField $encoded) -ceq $hostile) 'encoded field must round-trip exactly'
    Assert ((ConvertTo-ArtifactField $null) -eq '') 'a null field encodes to the empty string'

    # 1e. BACKSTOP SCANNER, offline. One LINE is reported, not a whole block; echoed helper or
    #     network data is not a harness status; PRESENT counts only on the status line.
    $block = "== CASE X ==`r`nfirst ok line`r`nsome check: 2 (FAIL)`r`nlater line (FAIL)"
    Assert ((Find-UnrecordedFailureLine -Items @('clean', $block)) -ceq 'some check: 2 (FAIL)') 'the backstop must report the first offending LINE of a multi-line block'
    Assert ((Find-UnrecordedFailureLine -Items @(('x' * 300) + ' (FAIL)')).Length -eq 160) 'the reported line must be capped at 160 characters'
    $echoed = @(
        'helper exit=0 stdout="UNPROVEN FAILED (FAIL) => FAIL" stderr=""',
        'request line: HEAD http://x/FAILED HTTP/1.1',
        'header names: Host, PRESENT, X-Note: PRESENT',
        'response Location: http://x/(FAIL)',
        'netsh output: FAILED'
    )
    Assert ($null -eq (Find-UnrecordedFailureLine -Items $echoed)) 'echoed helper/network fields must not count as harness failures'
    Assert ((Find-UnrecordedFailureLine -Items @("forbidden header 'Cookie': PRESENT")) -ceq "forbidden header 'Cookie': PRESENT") 'the forbidden-header PRESENT status line must count'
    Assert ($null -eq (Find-UnrecordedFailureLine -Items @('some summary: PRESENT'))) 'PRESENT outside the forbidden-header status line must not count'
    foreach ($marker in 'x (FAIL)', 'url=a => FAIL', 'interpretation: FAILED - x', 'verdict: UNPROVEN - x') {
        Assert ($null -ne (Find-UnrecordedFailureLine -Items @($marker))) "harness-authored marker must still count: $marker"
    }

    # 1g. TOP-LEVEL errorCode BY JSON PARSE, offline. A regex over the raw text would read the
    #     nested or quoted private-target below; the parse reads the real top-level timeout.
    $nestedText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"meta":{"errorCode":"shortlink.private-target"},"errorCode":"shortlink.timeout"}'
    Assert ($nestedText -match '^FAILED' -and $nestedText -match 'errorCode=shortlink\.timeout') "a nested meta.errorCode must not be taken for the top-level code (got: $nestedText)"
    $quotedText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"note":"said \"errorCode\":\"shortlink.private-target\"","errorCode":"shortlink.timeout"}'
    Assert ($quotedText -match '^FAILED' -and $quotedText -match 'errorCode=shortlink\.timeout') "an escaped errorCode inside another string must not be taken for the top-level code (got: $quotedText)"
    $absentText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"dispatch":0,"effectiveUrl":"http://x/"}'
    Assert ($absentText -match '^FAILED' -and $absentText -notmatch 'completed') "a missing errorCode property must be FAILED, never completed (got: $absentText)"
    $caseDupText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"errorCode":null,"ErrorCode":"x"}'
    Assert ($caseDupText -match '^FAILED') "keys differing only in case make the JSON unparseable, so the result must be FAILED (got: $caseDupText)"
    $nonStringText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"errorCode":5}'
    Assert ($nonStringText -match '^FAILED' -and $nonStringText -notmatch 'completed') "a non-string, non-null errorCode must be FAILED (got: $nonStringText)"
    Assert ((Get-HelperErrorCode -StdOut '{"errorCode":"a","errorCode":"b"}').State -eq 'unparseable') 'duplicate top-level errorCode keys are rejected, not resolved last-wins'
    Assert ((Get-HelperErrorCode -StdOut '[{"errorCode":null}]').State -eq 'unparseable') 'a JSON array is not a single object, so it is unparseable'
    # HOST-INDEPENDENT RULES (D3/D4). These run in whichever host runs this file; the suite is
    # run under both Windows PowerShell 5.1 and pwsh 7, where ConvertFrom-Json differs.
    $arrayText = Get-Case2Interpretation -ExitCode 0 -StdOut '[{"errorCode":null}]'
    Assert ($arrayText -match '^FAILED' -and $arrayText -notmatch 'completed') "a one-element array must be FAILED on every host (pwsh unwraps it) (got: $arrayText)"
    $paddedText = Get-Case2Interpretation -ExitCode 0 -StdOut " `r`n{`"errorCode`":null}`r`n"
    Assert ($paddedText -ceq 'redirect chain completed') "surrounding whitespace around one object must still parse (got: $paddedText)"
    $upperText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"ErrorCode":null}'
    Assert ($upperText -match '^FAILED' -and $upperText -notmatch 'completed') "the property name is case-sensitive: ErrorCode is not errorCode (got: $upperText)"
    $dateText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"errorCode":"2020-01-01T00:00:00Z"}'
    Assert ($dateText -ceq 'FAILED - redirect chain did NOT complete: helper errorCode=2020-01-01T00:00:00Z (exit=0)') "a date-like code must stay the exact string on every host (got: $dateText)"
    $commentText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"errorCode":"shortlink.timeout"} /* {"errorCode":null} */'
    Assert ($commentText -match '^FAILED' -and $commentText -notmatch 'completed') "a trailing comment must never turn into a completion on any host (got: $commentText)"

    # STRICT JSON GATE (T4/D5/D6). ConvertFrom-Json accepts every one of these on at least one
    # host (MEASURED: pwsh even completes the unterminated array); each must read as FAILED.
    $strictRejects = @(
        @('[{"errorCode":null}', 'an unterminated array'),
        @('x{"errorCode":null}', 'text before the object'),
        @('{"errorCode":"x","errorCode":null}', 'a duplicate top-level errorCode (hosts keep the last)'),
        @('{"error\u0043ode":"x","errorCode":null}', 'an escaped top-level key that decodes to a second errorCode'),
        @('{"errorCode":null /* c */}', 'a block comment'),
        @("{`"errorCode`":null // ,`"errorCode`":`"x`"`n}", 'a line comment'),
        @("{'errorCode':null}", 'a single-quoted key'),
        @('{errorCode:null}', 'an unquoted key'),
        @('{"errorCode":null,"n":NaN}', 'NaN'),
        @('{"errorCode":null,"n":Infinity}', 'Infinity'),
        @('{"errorCode":null}}', 'an extra closing brace'),
        @("{`"errorCode`":null,`"s`":`"a`tb`"}", 'a raw control character inside a string')
    )
    foreach ($reject in $strictRejects) {
        $rejectText = Get-Case2Interpretation -ExitCode 0 -StdOut $reject[0]
        Assert ($rejectText -match '^FAILED' -and $rejectText -notmatch 'completed') "strict JSON: $($reject[1]) must be FAILED (got: $rejectText)"
    }
    # The gate is STRING-AWARE: '/', "'", '//', 'NaN' and an escaped "errorCode" inside values
    # are only text, and nested errorCode keys do not count - this is valid helper JSON.
    $stringAwareText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"originalUrl":"http://a/b//c''NaN","note":"\"errorCode\":\"x\"","errorCode":null}'
    Assert ($stringAwareText -ceq 'redirect chain completed') "strict JSON must ignore /, ', NaN and escaped quotes inside strings (got: $stringAwareText)"
    $nestedKeysText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"meta":{"errorCode":"a","errorCode":"b"},"list":[{"errorCode":"c"}],"errorCode":null}'
    Assert ($nestedKeysText -ceq 'redirect chain completed') "only TOP-LEVEL errorCode keys count (got: $nestedKeysText)"
    # The REAL helper's outputs (as recorded in harness artifacts) stay accepted.
    $realPrivate = Get-Case2Interpretation -ExitCode 0 -StdOut '{"dispatch":1,"originalUrl":"http://bit.ly/loopback-start","effectiveUrl":"http://bit.ly/loopback-start","errorCode":"shortlink.private-target","fromCache":false}'
    Assert ($realPrivate -like 'private-target guard refused*') "the real helper's private-target output must be read as such (got: $realPrivate)"
    $realSuccess = Get-Case2Interpretation -ExitCode 0 -StdOut '{"dispatch":0,"originalUrl":"http://bit.ly/x","effectiveUrl":"https://example.com/final?a=1&b=-2.5e3","errorCode":null,"fromCache":false}'
    Assert ($realSuccess -ceq 'redirect chain completed') "a real success-shaped output must be completed (got: $realSuccess)"
    $reasonChecks = @(
        @('[1]', 'not-an-object'), @('{"a":1} x', 'trailing-content'), @('{"a":[1}', 'expected-comma-or-close'),
        @('{"a":1', 'unbalanced'), @('{"a":"b', 'unterminated-string'), @('{"a":/1}', 'illegal-character'),
        @('{"a":tru}', 'illegal-literal'), @('{"errorCode":1,"errorCode":2}', 'duplicate-errorCode'),
        @('{"a":01}', 'bad-number'), @('{"a":"\x"}', 'bad-escape'), @('{"a" 1}', 'expected-colon'),
        @('{,"a":1}', 'expected-member-name'), @('{"error\u0043ode":null}', 'escaped-top-level-key'),
        @('{"a":1,}', 'expected-member-name'), @('{"a":[1,]}', 'illegal-character'), @('{"a":}', 'illegal-character'),
        @('{"a":{"b":[true,false,null,-0.5,1e9]}}', 'ok'))
    foreach ($check in $reasonChecks) {
        $verdictJson = Test-StrictHelperJson -Text $check[0]
        Assert ($verdictJson.Reason -ceq $check[1]) "Test-StrictHelperJson '$($check[0])' must give $($check[1]) (got: $($verdictJson.Reason))"
    }

    # RFC 8259 GRAMMAR (D7/T5). The validator is a complete recursive-descent check, so every
    # malformed shape below is FAILED on both hosts (pwsh's ConvertFrom-Json accepted the first
    # four - trailing commas and a missing value - as success).
    $grammarRejects = @(
        '{"errorCode":null,}', '{"a":[1,2,],"errorCode":null}', '{"a":{"b":1,},"errorCode":null}', '{"errorCode":,"x":null}',
        '{"a":1,,"errorCode":null}', '{,"errorCode":null}', '{"errorCode" null}', '{"a":"b" "errorCode":null}',
        '{"a":01,"errorCode":null}', '{"a":1.,"errorCode":null}', '{"a":.5,"errorCode":null}', '{"a":+1,"errorCode":null}',
        '{"a":"\x","errorCode":null}', '{"a":"\u12","errorCode":null}',
        '{"errorCode":null', '{"a":[1},"errorCode":null}', '{"a":{]},"errorCode":null}',
        '{"a":1e,"errorCode":null}', '{"a":-,"errorCode":null}', '{"a":TRUE,"errorCode":null}'
    )
    foreach ($reject in $grammarRejects) {
        $rejectText = Get-Case2Interpretation -ExitCode 0 -StdOut $reject
        Assert ($rejectText -match '^FAILED' -and $rejectText -notmatch 'completed') "RFC 8259: '$reject' must be FAILED (got: $rejectText)"
    }
    $nest = { param($Depth) ('{"a":' * ($Depth - 1)) + '{"x":1}' + ('}' * ($Depth - 1)) }
    $tooDeep = '{"errorCode":null,"n":' + (& $nest 64) + '}'
    Assert ((Test-StrictHelperJson -Text $tooDeep).Reason -ceq 'too-deep') 'nesting 65 levels deep must be too-deep'
    Assert ((Get-Case2Interpretation -ExitCode 0 -StdOut $tooDeep) -match '^FAILED') 'nesting 65 levels deep must be FAILED'
    $grammarAccepts = @(
        '{"a":-1.5e+10,"b":0,"c":-0,"d":1E5,"e":2.25E-3,"errorCode":null}',
        "{`t`"a`":[[1,{`"b`":[true,false,null]}],{},[]],`r`n`"errorCode`" :`tnull`r`n}",
        '{"s":"\u00e9","t":"a\\","u":"\\\"","v":"\/\b\f\n\r\t","w":"\u00C9\uABcd","errorCode":null}',
        ('{"errorCode":null,"n":' + (& $nest 29) + '}')
    )
    foreach ($accept in $grammarAccepts) {
        $acceptText = Get-Case2Interpretation -ExitCode 0 -StdOut $accept
        Assert ($acceptText -ceq 'redirect chain completed') "RFC 8259: valid JSON must be completed (got: $acceptText for $($accept.Substring(0, [Math]::Min(60, $accept.Length))))"
    }
    # SIZE CAP: a 1 MB stdout is rejected without scanning it, quickly on every host.
    $hugeOutput = '{"a":"' + ('x' * (1024 * 1024)) + '","errorCode":null}'
    $hugeWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $hugeText = Get-Case2Interpretation -ExitCode 0 -StdOut $hugeOutput
    $hugeWatch.Stop()
    Assert ($hugeText -match '^FAILED') "a 1 MB stdout must be unparseable (got: $hugeText)"
    Assert ((Test-StrictHelperJson -Text $hugeOutput).Reason -ceq 'too-long') 'a 1 MB stdout must be rejected as too-long'
    Assert ($hugeWatch.Elapsed.TotalSeconds -lt 2) "a 1 MB stdout must be rejected in under 2 s (took $([int]$hugeWatch.Elapsed.TotalMilliseconds) ms)"

    # T6 / D10-D13. Each case below pins one rule the round-9 audit found loose.
    # D10: only JSON whitespace (space, tab, CR, LF) may surround the object. String.Trim()
    # would strip these Unicode spaces and turn rejected text into a "completed" result.
    foreach ($odd in @(@([char]0x00A0, 'NBSP'), @([char]0x2028, 'U+2028'), @([char]0x0085, 'U+0085'), @([char]0x3000, 'U+3000'), @([char]0x000B, 'VT'), @([char]0x000C, 'FF'))) {
        foreach ($wrapped in @(([string]$odd[0] + '{"errorCode":null}'), ('{"errorCode":null}' + [string]$odd[0]))) {
            $oddText = Get-Case2Interpretation -ExitCode 0 -StdOut $wrapped
            Assert ($oddText -match '^FAILED' -and $oddText -notmatch 'completed') "D10: $($odd[1]) around the object is not JSON whitespace, so it must be FAILED (got: $oddText)"
        }
    }
    $jsonWsText = Get-Case2Interpretation -ExitCode 0 -StdOut " `t`r`n{`"errorCode`":null} `t`r`n"
    Assert ($jsonWsText -ceq 'redirect chain completed') "D10: JSON whitespace around the object must still be accepted (got: $jsonWsText)"
    # D13: the length cap is 16384 chars, counted on the text as received.
    $capText = { param($Length) $head = '{"errorCode":null,"a":"'; $head + ('x' * ($Length - $head.Length - 2)) + '"}' }
    $atCap = & $capText 16384
    $overCap = & $capText 16385
    Assert ($atCap.Length -eq 16384 -and (Test-StrictHelperJson -Text $atCap).Ok) 'D13: exactly 16384 chars must pass the size check'
    Assert ((Test-StrictHelperJson -Text $overCap).Reason -ceq 'too-long') 'D13: 16385 chars must be too-long'
    Assert ((Test-StrictHelperJson -Text ($atCap + ' ')).Reason -ceq 'too-long') 'D13: the cap counts JSON whitespace too (it applies to the text as received)'
    $numberDense = New-Object System.Text.StringBuilder
    [void]$numberDense.Append('{"errorCode":null,"a":[')
    while ($numberDense.Length -lt 16300) { [void]$numberDense.Append('-1234567.890e+12,') }
    [void]$numberDense.Append('0]}')
    $denseWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $denseResult = Test-StrictHelperJson -Text $numberDense.ToString()
    $denseWatch.Stop()
    Assert ($denseResult.Ok) "D13: a ~16 KB number-dense valid input must validate (got: $($denseResult.Reason))"
    Assert ($denseWatch.Elapsed.TotalSeconds -lt 3) "D13: a ~16 KB number-dense input must validate in under 3 s (took $([int]$denseWatch.Elapsed.TotalMilliseconds) ms)"
    # D11: depth is checked when a container OPENS, so empty containers count.
    $emptyNest = { param($Count) '{"errorCode":null,"a":' + ('[' * $Count) + (']' * $Count) + '}' }
    Assert ((Test-StrictHelperJson -Text (& $emptyNest 63)).Ok) 'D11: the top object plus 63 nested empty arrays (64 levels) must be accepted'
    Assert ((Test-StrictHelperJson -Text (& $emptyNest 64)).Reason -ceq 'too-deep') 'D11: the top object plus 64 nested empty arrays (65 levels) must be too-deep'
    Assert ((Test-StrictHelperJson -Text ('{"errorCode":null,"a":' + ('{"b":' * 63) + '{}' + ('}' * 63) + '}')).Reason -ceq 'too-deep') 'D11: 65 levels ending in an EMPTY object must be too-deep'
    # D12: a raw surrogate must be a high one followed by a low one.
    $high = [string][char]0xD83D
    $lowS = [string][char]0xDE00
    Assert ((Test-StrictHelperJson -Text ('{"s":"' + $high + $lowS + '","errorCode":null}')).Ok) 'D12: a correct surrogate pair is valid text'
    Assert ((Test-StrictHelperJson -Text ('{"s":"' + $high + '","errorCode":null}')).Reason -ceq 'unpaired-surrogate') 'D12: a lone HIGH surrogate must be unpaired-surrogate'
    Assert ((Test-StrictHelperJson -Text ('{"s":"' + $lowS + '","errorCode":null}')).Reason -ceq 'unpaired-surrogate') 'D12: a lone LOW surrogate must be unpaired-surrogate'
    Assert ((Test-StrictHelperJson -Text ('{"s":"' + $high + 'x","errorCode":null}')).Reason -ceq 'unpaired-surrogate') 'D12: a high surrogate followed by a non-surrogate must be unpaired-surrogate'
    Assert ((Test-StrictHelperJson -Text ('{"s":"x' + $high)).Reason -ceq 'unpaired-surrogate') 'D12: a high surrogate at the very end must be unpaired-surrogate'
    # Number grammar after the D13 rewrite (leading zeros, sign, fraction, exponent).
    foreach ($badNumber in '{"a":-01}', '{"a":00}', '{"a":-}', '{"a":-x}', '{"a":1.e5}', '{"a":1e+}', '{"a":1E-}') {
        Assert ((Test-StrictHelperJson -Text $badNumber).Reason -ceq 'bad-number') "number grammar: '$badNumber' must be bad-number (got: $((Test-StrictHelperJson -Text $badNumber).Reason))"
    }
    Assert ((Test-StrictHelperJson -Text '{"a":[0,-0,10,100,0.5,-0.25e-3,7E+2],"errorCode":null}').Ok) 'number grammar: valid numbers must be accepted'

    # T7. A raw HIGH surrogate must be followed by a LOW one - two HIGH surrogates in a row
    # are not a pair. Built with [char] so this file stays ASCII.
    $twoHigh = '{"s":"' + [string][char]0xD83D + [string][char]0xD83D + '","errorCode":null}'
    Assert ((Test-StrictHelperJson -Text $twoHigh).Reason -ceq 'unpaired-surrogate') "T7: a HIGH surrogate followed by another HIGH surrogate must be unpaired-surrogate (got: $((Test-StrictHelperJson -Text $twoHigh).Reason))"
    $twoHighText = Get-Case2Interpretation -ExitCode 0 -StdOut $twoHigh
    Assert ($twoHighText -match '^FAILED' -and $twoHighText -notmatch 'completed') "T7: two HIGH surrogates must be FAILED end to end (got: $twoHighText)"

    # T8. The 16384-char cap counts the text AS RECEIVED, before any whitespace is skipped or
    # trimmed: a valid object of exactly 16384 chars plus one JSON-whitespace char is too long.
    $exactCap = & $capText 16384
    foreach ($padded in @(@(($exactCap + "`n"), 'a trailing LF'), @((' ' + $exactCap), 'a leading space'))) {
        Assert ($padded[0].Length -eq 16385) "T8: test input with $($padded[1]) must be 16385 chars long"
        Assert ((Test-StrictHelperJson -Text $padded[0]).Reason -ceq 'too-long') "T8: 16384 chars plus $($padded[1]) must be too-long (got: $((Test-StrictHelperJson -Text $padded[0]).Reason))"
        $paddedText = Get-Case2Interpretation -ExitCode 0 -StdOut $padded[0]
        Assert ($paddedText -match '^FAILED' -and $paddedText -notmatch 'completed') "T8: 16384 chars plus $($padded[1]) must be FAILED end to end (got: $paddedText)"
    }

    # T9. Only ASCII digits are JSON digits. An ASCII digit followed by an Arabic-Indic or a
    # fullwidth digit (both Unicode decimal digits) must be rejected.
    foreach ($foreignDigit in @(@([char]0x0661, 'ARABIC-INDIC DIGIT ONE'), @([char]0xFF11, 'FULLWIDTH DIGIT ONE'))) {
        $foreignText = '{"a":1' + [string]$foreignDigit[0] + ',"errorCode":null}'
        $foreignResult = Test-StrictHelperJson -Text $foreignText
        Assert (-not $foreignResult.Ok) "T9: '1' followed by $($foreignDigit[1]) must be rejected (got: $($foreignResult.Reason))"
        $foreignCase2 = Get-Case2Interpretation -ExitCode 0 -StdOut $foreignText
        Assert ($foreignCase2 -match '^FAILED' -and $foreignCase2 -notmatch 'completed') "T9: '1' followed by $($foreignDigit[1]) must be FAILED end to end (got: $foreignCase2)"
    }

    # 1h. AN ECHOED CODE CANNOT SPLIT THE LINE (N11), offline: the JSON escapes decode to real
    #     CR/LF, and the interpretation must still come out as one printable line.
    $splitText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"errorCode":"a\r\nharness verdict: PASS"}'
    Assert ($splitText -notmatch '[\r\n]' -and $splitText -notmatch '[^\x20-\x7E]') "an errorCode carrying CR/LF must stay on one printable line (got: $splitText)"
    Assert ($splitText -match '^FAILED' -and $splitText -match '\\r\\nharness verdict: PASS') "the CR/LF must be shown escaped inside the FAILED line (got: $splitText)"

    # 1i. HEADER NAMES ARE ENCODED (N8), offline: control and Unicode line-break characters in
    #     a header name must not reach the artifact raw.
    $forbiddenHeaders = @('Authorization', 'Cookie', 'Referer', 'Proxy-Authorization')
    $oddRequest = [pscustomobject]@{ RawRequestLine = 'HEAD http://x/ HTTP/1.1'; Method = 'HEAD'; HeaderNames = @('Host', ('X' + [char]0x0001 + 'Y'), ('Z' + [char]0x2028 + 'W')); Headers = @{} }
    $redacted = Format-Redacted -Title 'T' -Requests @($oddRequest) -HelperResult $null
    $namesLine = @($redacted -split "`r`n" | Where-Object { $_ -like 'header names: *' })
    Assert ($namesLine.Count -eq 1 -and $namesLine[0] -ceq 'header names: Host, X\u0001Y, Z\u2028W') "header names must be escaped (got: $namesLine)"

    # 1i-bis. RESPONSE Location IS THE SERVED ONE, offline: a Location header in the helper's REQUEST
    #     must never be reported as the response Location; the value the scripted response served
    #     (ServedLocation, set by Write-ScriptedResponse) is what the artifact records.
    $reqWithLocation = [pscustomobject]@{ RawRequestLine = 'HEAD http://x/ HTTP/1.1'; Method = 'HEAD'; HeaderNames = @('Host', 'Location'); Headers = @{ Location = 'http://attacker.invalid/from-request' } }
    $redactedNoServed = Format-Redacted -Title 'T' -Requests @($reqWithLocation) -HelperResult $null
    Assert ($redactedNoServed -notmatch 'response Location:') "a request-side Location header must not be reported as a response Location (got: $redactedNoServed)"
    $reqServed = [pscustomobject]@{ RawRequestLine = 'HEAD http://x/ HTTP/1.1'; Method = 'HEAD'; HeaderNames = @('Host', 'Location'); Headers = @{ Location = 'http://attacker.invalid/from-request' }; ServedLocation = 'http://loopback.example/final' }
    $redactedServed = Format-Redacted -Title 'T' -Requests @($reqServed) -HelperResult $null
    Assert ($redactedServed -match '(?m)^response Location: http://loopback\.example/final\r?$') "the served Location must be reported (got: $redactedServed)"
    Assert ($redactedServed -notmatch 'from-request') 'the request-side Location value must not reach the artifact'
    $harnessSource = [System.IO.File]::ReadAllText($harness)
    Assert ($harnessSource -match 'Write-ScriptedResponse -Client \$client -StatusLine \$hops\[\$hopIndex\]\.Status -Headers \$hops\[\$hopIndex\]\.Headers -Request \$request') 'the CASE 2 serve loop must pass the captured request to Write-ScriptedResponse'

    # 1i-ter. KERNEL TRACE ORDERING IS PINNED BY SOURCE TEXT. The elevated netsh path cannot run in
    #     an unelevated test, so the ordering is asserted on the harness source: the trace starts
    #     BEFORE CASE 1, stops AFTER CASE 3 (in the kernel section), and is stopped again in the
    #     top-level finally; the running flag is cleared only after a successful stop.
    $posStart = $harnessSource.IndexOf('netsh trace start capture=yes')
    $posCase1 = $harnessSource.IndexOf('# CASE 1 -- consent OFF')
    $posCase3 = $harnessSource.IndexOf("'== CASE 3: guard chain")
    $posStopCall = $harnessSource.IndexOf('$stopResult = Stop-KernelTrace')
    $posFinally = $harnessSource.IndexOf('} finally {', $posStopCall)
    $posCleanupStop = $harnessSource.IndexOf('& netsh trace stop', $posFinally)
    Assert ($posStart -ge 0 -and $posCase1 -ge 0 -and $posStart -lt $posCase1) 'netsh trace start must come BEFORE CASE 1'
    Assert ($posCase3 -ge 0 -and $posStopCall -gt $posCase3) 'the kernel trace must be stopped AFTER CASE 3'
    Assert ($posFinally -gt $posStopCall -and $posCleanupStop -gt $posFinally) 'the top-level finally must stop a still-running trace'
    Assert ($harnessSource -match '(?s)\$stopExit = \$LASTEXITCODE\s+if \(\$stopExit -eq 0\) \{ \$script:kernelTraceRunning = \$false \}') 'the running flag must be cleared only after netsh trace stop returned exit 0'
    Assert ($harnessSource -match '(?s)\$cleanupStopExit = \$LASTEXITCODE.{0,300}if \(\$cleanupStopExit -eq 0\) \{ \$kernelTraceRunning = \$false \}') 'the finally retry must clear the flag only on success'
    Assert ($harnessSource -match 'Add-HarnessFailure "kernel capture: netsh trace start exited') 'a non-zero netsh trace start exit code must be a recorded failure'

    # 1i-quater. PER-CALL HELPER BUDGET IS ONE PARAMETER. Real runs keep 5000 ms; every per-call
    #     budget (CASE 1 call, CASE 3 probes, CASE 2 exit wait) must use -HelperCallTimeoutMs, so
    #     the stand-in runs below can raise it without a hard-coded 5000 left behind.
    Assert ($harnessSource -cmatch '\[int\] \$HelperCallTimeoutMs = 5000') 'the harness must default -HelperCallTimeoutMs to 5000'
    Assert (@([regex]::Matches($harnessSource, 'Invoke-NetworkHelper [^\r\n]*-TimeoutMs \$HelperCallTimeoutMs')).Count -eq 2) 'the CASE 1 call and the CASE 3 probes must both use -HelperCallTimeoutMs'
    Assert ($harnessSource -cmatch '\$exitDeadline = \[DateTime\]::UtcNow\.AddMilliseconds\(\$HelperCallTimeoutMs\)') 'the CASE 2 helper exit wait must use -HelperCallTimeoutMs'
    Assert ($harnessSource -cnotmatch 'Invoke-NetworkHelper [^\r\n]*-TimeoutMs 5000') 'no helper call may keep a hard-coded 5000 ms budget'

    # 1j. BACKSTOP MATCHES ECHO PREFIXES AT THE START ONLY (N6), offline: a harness status line
    #     that merely CONTAINS "helper exit=" is still a harness line and its marker counts.
    Assert ((Find-UnrecordedFailureLine -Items @('case 2 interpretation: FAILED - x helper exit=124')) -ceq 'case 2 interpretation: FAILED - x helper exit=124') 'a status line containing "helper exit=" mid-line must still be reported'

    # 1k. FAILURE REASONS AND THE VERDICT ARE ONE LINE (N9), and THE BACKSTOP DECIDES THE
    #     VERDICT (M8), offline through the harness's own verdict builder.
    $harnessFailures = New-Object System.Collections.Generic.List[string]
    Add-HarnessFailure "case 2: x`r`nharness verdict: PASS"
    Assert ($harnessFailures.Count -eq 1 -and $harnessFailures[0] -notmatch '[\r\n]' -and $harnessFailures[0] -match '\\u000d\\u000aharness verdict: PASS') "Add-HarnessFailure must turn CR/LF into escapes (got: $($harnessFailures[0]))"
    $recordedVerdict = Get-HarnessVerdict -Failures $harnessFailures -ArtifactItems @('clean')
    Assert ($recordedVerdict.Exit -eq 1 -and $recordedVerdict.Line -notmatch '[\r\n]' -and $recordedVerdict.Line -like 'harness verdict: FAIL - case 2: x*') "a recorded failure must give a one-line FAIL verdict (got: $($recordedVerdict.Line))"
    $noFailures = New-Object System.Collections.Generic.List[string]
    $backstopVerdict = Get-HarnessVerdict -Failures $noFailures -ArtifactItems @('== CASE X ==', "ok line`r`nsome check: 1 (FAIL)")
    Assert ($backstopVerdict.Exit -eq 1 -and $backstopVerdict.Line -ceq 'harness verdict: FAIL - unrecorded failure line in artifact: some check: 1 (FAIL)') "an unrecorded (FAIL) line must still fail the verdict (got: $($backstopVerdict.Line))"
    $cleanVerdict = Get-HarnessVerdict -Failures (New-Object System.Collections.Generic.List[string]) -ArtifactItems @('helper exit=0 stdout="FAILED" stderr=""', 'all good')
    Assert ($cleanVerdict.Exit -eq 0 -and $cleanVerdict.Line -ceq 'harness verdict: PASS') "a clean artifact must give PASS (got: $($cleanVerdict.Line))"

    # 1l. WRITE-TIME SANITISER (D1), offline: whatever reaches the writer comes out as printable
    #     ASCII lines joined by CRLF. A lone CR or a U+2028 cannot start a line, and non-ASCII
    #     (e.g. a helper file name with U+2028 or accents) is escaped.
    $sanitised = ConvertTo-ArtifactText -Lines @(('helper: a' + [char]0x2028 + 'b.exe'), "two`r`nlines", ('caf' + [char]0x00E9))
    Assert ($sanitised -notmatch '[^\x20-\x7E\r\n]') "the sanitised artifact must be printable ASCII plus line breaks (got: $sanitised)"
    Assert ($sanitised -ceq "helper: a\u2028b.exe`r`ntwo`r`nlines`r`ncaf\u00e9") "the sanitiser must keep CRLF line breaks and escape the rest (got: $sanitised)"

    # 1m. THE HELPER FILE NAME IS ENCODED AT THE SOURCE (D1), not only by the write-time
    #     sanitiser: the `helper:` artifact line must pass the name through ConvertTo-ArtifactField.
    #     Checked on the parsed harness (the end-to-end runs use ASCII helper names, which would
    #     look identical either way).
    $helperLineAst = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and $n.Value -like 'helper: *' }, $true))
    Assert ($helperLineAst.Count -eq 1) "harness must build exactly one 'helper: ...' artifact line (found $($helperLineAst.Count))"
    Assert ($helperLineAst[0].Extent.Text -match 'ConvertTo-ArtifactField') "the 'helper:' artifact line must encode the file name with ConvertTo-ArtifactField (got: $($helperLineAst[0].Extent.Text))"

    # 1f. EXCEPTION TYPE ONLY, offline: the recorded text is a bare .NET type name.
    $wrapped = New-Object System.Management.Automation.MethodInvocationException('outer C:\Users\x', (New-Object System.IO.IOException('inner http://host/path C:\secret')))
    $typeName = Get-ExceptionTypeName -Exception $wrapped
    Assert ($typeName -ceq 'IOException') "Get-ExceptionTypeName must return the innermost type name (got: $typeName)"

    $timeoutText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"dispatch":1,"originalUrl":"http://bit.ly/x","effectiveUrl":"http://bit.ly/x","errorCode":"shortlink.timeout","fromCache":false}'
    Assert ($timeoutText -notmatch 'redirect chain completed') "timeout must not be reported as completed (got: $timeoutText)"
    Assert ($timeoutText -match 'FAILED' -and $timeoutText -match 'shortlink\.timeout') "timeout interpretation must say FAILED and name the code (got: $timeoutText)"

    $networkText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"errorCode":"shortlink.network-error"}'
    Assert ($networkText -notmatch 'completed' -and $networkText -match 'shortlink\.network-error') "network-error must be reported as a failure naming the code (got: $networkText)"

    $exitText = Get-Case2Interpretation -ExitCode 124 -StdOut ''
    Assert ($exitText -notmatch 'redirect chain completed' -and $exitText -match 'exit=124') "non-zero exit with empty stdout must be a failure (got: $exitText)"

    $privateText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"errorCode":"shortlink.private-target"}'
    Assert ($privateText -match 'private-target guard refused') "private-target wording must be preserved (got: $privateText)"

    $nullCodeBadExitText = Get-Case2Interpretation -ExitCode 3 -StdOut '{"errorCode":null}'
    Assert ($nullCodeBadExitText -notmatch 'redirect chain completed' -and $nullCodeBadExitText -match 'FAILED' -and $nullCodeBadExitText -match 'exit=3') "a null errorCode with a non-zero exit must be a failure (got: $nullCodeBadExitText)"

    $successText = Get-Case2Interpretation -ExitCode 0 -StdOut '{"dispatch":0,"originalUrl":"http://bit.ly/x","effectiveUrl":"http://loopback.example/final","errorCode":null,"fromCache":false}'
    Assert ($successText -eq 'redirect chain completed') "a real success (errorCode null, exit 0) must read as completed (got: $successText)"
    $emptyText = Get-Case2Interpretation -ExitCode 0 -StdOut ''
    Assert ($emptyText -notmatch 'redirect chain completed' -and $emptyText -match 'FAILED') "exit 0 with empty stdout must be a failure (got: $emptyText)"

    $garbageText = Get-Case2Interpretation -ExitCode 0 -StdOut 'Unhandled exception. System.InvalidOperationException: boom'
    Assert ($garbageText -notmatch 'redirect chain completed' -and $garbageText -match 'FAILED') "exit 0 with non-JSON stdout must be a failure (got: $garbageText)"
    Write-Output 'PASS: case 2 interpretation (offline)'

    # 1c. A hung helper must really be killed, child processes included, on the host running
    #     this test (Windows PowerShell 5.1 has no Process.Kill(bool); an empty catch used to
    #     swallow that MethodException, so nothing was killed). The stand-in is cmd.exe holding
    #     a ping child for ~30 s.
    $stopAst = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Stop-HelperProcess' }, $true)
    Assert ($null -ne $stopAst) 'harness must define Stop-HelperProcess'
    . ([scriptblock]::Create($stopAst.Extent.Text))
    $hungPsi = New-Object System.Diagnostics.ProcessStartInfo
    $hungPsi.FileName = $env:ComSpec
    $hungPsi.Arguments = '/c ping -n 30 127.0.0.1'
    $hungPsi.UseShellExecute = $false
    $hungPsi.RedirectStandardOutput = $true
    $hungPsi.CreateNoWindow = $true
    $hung = [System.Diagnostics.Process]::Start($hungPsi)
    # Children are tracked by PID AND creation time: Windows reuses PIDs quickly, and a PID
    # that now belongs to some unrelated new process must not count as a surviving child.
    $children = @()
    $childDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while ($children.Count -eq 0 -and [DateTime]::UtcNow -lt $childDeadline) {
        Start-Sleep -Milliseconds 100
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($hung.Id)" | ForEach-Object { [pscustomobject]@{ Id = [int]$_.ProcessId; Created = $_.CreationDate } })
    }
    Assert ($children.Count -gt 0) 'the hung stand-in must have started its ping child'
    Stop-HelperProcess -Process $hung
    Assert $hung.HasExited 'Stop-HelperProcess must terminate the hung helper'
    # Poll, do not sleep a fixed time: the tree kill is asynchronous for the children and a
    # loaded machine can take well over 300 ms to tear them down. Survivors after 5 s are real.
    $survivorDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $survivors = @($children | Where-Object { $child = $_; @(Get-CimInstance Win32_Process -Filter "ProcessId=$($child.Id)" | Where-Object { $_.CreationDate -eq $child.Created }).Count -gt 0 } | ForEach-Object { $_.Id })
        if ($survivors.Count -gt 0) { Start-Sleep -Milliseconds 100 }
    } while ($survivors.Count -gt 0 -and [DateTime]::UtcNow -lt $survivorDeadline)
    Assert ($survivors.Count -eq 0) "Stop-HelperProcess must also terminate the helper's children (alive: $($survivors -join ', '))"
    Write-Output 'PASS: hung helper is killed with its children (offline)'

    # 2. Locate a real helper; if none exists, fail loudly (do not silently skip). The
    #    candidate list mirrors the HARNESS's own list on purpose: if the two drift, this test
    #    starts failing for the right reason (the harness stopped finding a helper) rather
    #    than passing because the TEST found one the harness could not.
    $helperCandidates = @(
        (Join-Path $root 'src/BroChoos.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe'),
        (Join-Path $root 'src/BroChoos.NetworkHelper/bin/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe'),
        (Join-Path $root 'src/BroChoos.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe'),
        # The x64 platform subfolder is what `dotnet build BroChoos.sln` produces for the
        # helper when the solution's Platform is x64; a fresh clean checkout lands here.
        (Join-Path $root 'src/BroChoos.NetworkHelper/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/BroChoos.NetworkHelper.exe')
    )
    $helper = $helperCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    Assert (-not [string]::IsNullOrWhiteSpace($helper)) 'no built BroChoos.NetworkHelper.exe found; build Release first'

    $realOut = $temp
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $helper -OutputDirectory $realOut -SkipKernelCapture 2>&1
    $realExit = $LASTEXITCODE
    if (Test-HelperTimeoutOnly -Exit $realExit -ArtifactText (Read-RunArtifact -Directory $realOut)) {
        Write-Output "RETRY: real-helper run hit the helper's own shortlink.timeout with a clean capture; retrying once ($(Get-RunEvidence -ArtifactText (Read-RunArtifact -Directory $realOut) -Console @($output | ForEach-Object { "$_" })))"
        $realOut = Join-Path $temp 'real-out-retry'
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $helper -OutputDirectory $realOut -SkipKernelCapture 2>&1
        $realExit = $LASTEXITCODE
    }
    if ($realExit -ne 0) {
        # Quote the evidence so a failure here is diagnosable (the temp folder is cleaned up).
        Assert $false "harness should exit 0, got $realExit ($(Get-RunEvidence -ArtifactText (Read-RunArtifact -Directory $realOut) -Console @($output | ForEach-Object { "$_" })))"
    }
    # The console must not print the full output path (it sits under the user profile here).
    $artifactLine = @($output | ForEach-Object { "$_" } | Where-Object { $_ -like 'CAPTURE_NETWORK_ARTIFACT=*' })
    Assert ($artifactLine.Count -eq 1 -and $artifactLine[0] -match '^CAPTURE_NETWORK_ARTIFACT=<OutputDirectory>/w7\.4-capture-network-\d{8}-\d{6}\.txt$') "console must name the artifact without its folder path (got: $artifactLine)"
    Assert ((@($output) -join "`n") -notmatch [regex]::Escape($temp)) 'console output must not contain the output folder path'

    $artifactPath = @(Get-ChildItem -LiteralPath $realOut -Filter 'w7.4-capture-network-*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    Assert ($artifactPath.Count -eq 1) 'harness should write exactly one transcript artifact'
    $text = Get-Content -LiteralPath $artifactPath[0].FullName -Raw

    # 3. Consent-OFF zero-traffic fact is present and reports PASS.
    Assert ($text -match 'consent-off observed outbound connection attempts: 0 \(PASS\)') 'artifact must report zero consent-off traffic'

    # 4. Header hygiene is asserted for the exchange that was captured - and an exchange MUST
    #    have been captured, otherwise "absent" and "NONE" would be claims about nothing.
    Assert ($text -match '(?m)^redirect trace: exchanges=[1-9]') 'CASE 2 must capture at least one exchange'
    Assert ($text -match '(?m)^forbidden headers present anywhere: NONE\r?$') 'CASE 2 must report NONE for a captured exchange'
    Assert ($text -match '(?m)^harness verdict: PASS\r?$') 'a healthy run must end with harness verdict PASS'
    Assert ($text -match "forbidden header 'Authorization': absent") 'artifact must record Authorization absent'
    Assert ($text -match "forbidden header 'Cookie': absent") 'artifact must record Cookie absent'
    Assert ($text -match "forbidden header 'Referer': absent") 'artifact must record Referer absent'

    # 5. Kernel-capture honesty: with -SkipKernelCapture the text must say so and must never
    #    claim an ETL exists.
    Assert ($text -match 'SKIPPED BY OPERATOR REQUEST') '-SkipKernelCapture must be stated in the artifact'
    Assert ($text -notmatch 'kernel capture: w7\.4-network-trace') 'artifact must not claim a kernel capture that was skipped'

    # 5b. The artifact's CASE 2 interpretation line is the function's verdict on THAT run's
    #     helper exit + stdout (both are written to the artifact's `helper exit=` line), so a
    #     call site that stops using the function - e.g. the old always-"completed" branch - fails.
    #     The stdout field is an encoded JSON string on ONE line; it is decoded with the JSON rule
    #     before it is fed to the function, exactly as the harness fed it the raw value.
    $case2Helper = [regex]::Match($text, '(?m)^== CASE 2:[^\r\n]*\r?\nhelper exit=(-?\d+) stdout="((?:[^"\\]|\\.)*)" stderr="((?:[^"\\]|\\.)*)"\r?$')
    Assert $case2Helper.Success 'artifact must record the CASE 2 helper exit/stdout as one encoded line'
    $case2StdOut = ConvertFrom-ArtifactField $case2Helper.Groups[2].Value
    $case2Lines = @([regex]::Matches($text, '(?m)^case 2 interpretation: (.*?)\r?$'))
    Assert ($case2Lines.Count -eq 1) "artifact must carry exactly one case 2 interpretation line (found $($case2Lines.Count))"
    $expectedCase2 = Get-Case2Interpretation -ExitCode ([int]$case2Helper.Groups[1].Value) -StdOut $case2StdOut
    Assert ($case2Lines[0].Groups[1].Value -ceq $expectedCase2) "case 2 interpretation must match the helper result (artifact: '$($case2Lines[0].Groups[1].Value)'; expected: '$expectedCase2')"
    # The REAL helper's documented loopback outcome is fixed: the harness answers hop 1 with a
    # 302 to a non-public host, so the private-target guard must refuse it. Pinned, not merely
    # checked when present: a harness serving a different first hop would change this outcome.
    Assert ((Get-HelperErrorCode -StdOut $case2StdOut).Code -ceq 'shortlink.private-target') "the real helper must end CASE 2 in shortlink.private-target (got stdout: $case2StdOut)"
    Assert ($case2Lines[0].Groups[1].Value -match 'private-target guard refused' -and $case2Lines[0].Groups[1].Value -notmatch 'redirect chain completed') 'loopback private-target run must not be reported as completed'

    # 6. Guard chain runs offline and passes.
    Assert ($text -match 'guard chain result: ALL PASS') 'offline guard chain must pass'

    # 7. END-TO-END TIMEOUT, through the harness's REAL call site. The live loopback run above
    #    always ends in private-target, which the old always-"completed" branch also printed
    #    correctly, so it cannot catch that regression on its own. A stub helper (a batch file
    #    that ignores stdin and prints a typed timeout result) forces CASE 2 down the non-private
    #    path offline; the artifact must then say FAILED with the code and never "completed".
    #    Cases 1 and 3 are meaningless against the stub and are not asserted here.
    #
    #    PROOF THE STUB RAN. A harness that ignored -HelperPath would run the real helper, and if
    #    that one also timed out every assertion below would still pass. The stub therefore
    #    prints a per-run unique marker that only it can know, and the artifact must carry the
    #    stub's own file name and SHA-256.
    $stubDir = Join-Path $temp 'stub'
    New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
    $stubHelper = Join-Path $stubDir 'timeout-helper.cmd'
    $stubMarker = 'stub-' + [guid]::NewGuid().ToString('N')
    [System.IO.File]::WriteAllText($stubHelper, "@echo off`r`necho {`"dispatch`":1,`"originalUrl`":`"http://bit.ly/$stubMarker`",`"effectiveUrl`":`"http://bit.ly/$stubMarker`",`"errorCode`":`"shortlink.timeout`",`"fromCache`":false}`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $stubHash = (Get-FileHash -LiteralPath $stubHelper -Algorithm SHA256).Hash.ToLowerInvariant()
    $stubOut = Join-Path $temp 'stub-out'
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $stubHelper -OutputDirectory $stubOut -SkipKernelCapture 2>&1
    # The stub never connects, so CASE 2 captures nothing: the harness must FAIL (exit 1),
    # not report clean header hygiene for a capture that did not happen.
    Assert ($LASTEXITCODE -eq 1) "harness with a non-connecting stub helper must exit 1 (no exchange captured), got $LASTEXITCODE"
    $stubArtifact = @(Get-ChildItem -LiteralPath $stubOut -Filter 'w7.4-capture-network-*.txt')
    Assert ($stubArtifact.Count -eq 1) 'stub run should write exactly one transcript artifact'
    $stubText = Get-Content -LiteralPath $stubArtifact[0].FullName -Raw
    Assert ($stubText -match '(?m)^helper: timeout-helper\.cmd\r?$') 'stub artifact must name the stub helper (was -HelperPath honoured?)'
    Assert ($stubText -match "(?m)^helper_sha256: $stubHash\r?$") 'stub artifact must carry the stub helper SHA-256'
    $stubCase2 = [regex]::Match($stubText, '(?m)^== CASE 2:[^\r\n]*\r?\nhelper exit=(-?\d+) stdout="((?:[^"\\]|\\.)*)" stderr=')
    Assert ($stubCase2.Success -and (ConvertFrom-ArtifactField $stubCase2.Groups[2].Value).Contains($stubMarker)) 'CASE 2 stdout must carry the stub-only marker (proves the stub, not the real helper, answered)'
    Assert ($stubText -match '(?m)^forbidden headers present anywhere: NO EXCHANGE CAPTURED \(FAIL\)\r?$') 'zero captured exchanges must be reported as a FAIL, never as NONE'
    Assert ($stubText -match '(?m)^harness verdict: FAIL - .*no exchange captured') 'stub artifact must carry the failing harness verdict'
    $stubLine = [regex]::Match($stubText, '(?m)^case 2 interpretation: (.*?)\r?$')
    Assert $stubLine.Success 'stub artifact must carry a case 2 interpretation line'
    Assert ($stubLine.Groups[1].Value -notmatch 'redirect chain completed') "a timed-out CASE 2 must not be reported as completed (got: $($stubLine.Groups[1].Value))"
    Assert ($stubLine.Groups[1].Value -match '^FAILED' -and $stubLine.Groups[1].Value -match 'shortlink\.timeout') "a timed-out CASE 2 must say FAILED and name shortlink.timeout (got: $($stubLine.Groups[1].Value))"

    # 8. SLOW-STARTING HELPER. A wrapper waits ~1 s and then runs the REAL helper (stdin and the
    #    proxy variables are inherited), which is the cold start that used to make CASE 2 give
    #    up after one 400 ms sample and capture nothing. The capture must still succeed.
    $slowHelper = Join-Path $stubDir 'slow-helper.cmd'
    [System.IO.File]::WriteAllText($slowHelper, "@echo off`r`nping -n 2 127.0.0.1 >nul`r`n`"$helper`" %*`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $slowOut = Join-Path $temp 'slow-out'
    # Run WITHOUT -SkipKernelCapture, so the unelevated kernel-capture branch is exercised too:
    # it must be reported as BLOCKED and must not fail the run. (Skipped when this shell is
    # elevated - then the harness would really attempt netsh, which this test does not cover.)
    $isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $slowArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $harness, '-HelperPath', $slowHelper, '-OutputDirectory', $slowOut)
    if ($isElevated) { $slowArgs += '-SkipKernelCapture' }
    $slowConsole = @(& powershell @slowArgs 2>&1 | ForEach-Object { "$_" })
    $slowExit = $LASTEXITCODE
    $slowText = Read-RunArtifact -Directory $slowOut
    # F5: ONE retry, and only for the cold-start case this test is not about: the real helper's
    # own 1500 ms resolve budget (src/BroChoos.NetworkHelper, not the harness) can run out under
    # heavy load. The retry is allowed only when that is ALL that went wrong - the helper reported
    # exactly shortlink.timeout, the capture itself was clean (every request HEAD, no forbidden
    # header, no other failure) - so it cannot hide a harness or hygiene failure. It is announced
    # on the console as RETRY so a retried pass is visible. (MEASURED: 0 of 30 slow-start runs
    # under 2 and 8 CPU-burning workers needed it; the one recorded failure was not reproduced.)
    $coldStartOnly = Test-HelperTimeoutOnly -Exit $slowExit -ArtifactText $slowText
    if ($coldStartOnly) {
        Write-Output "RETRY: slow-start run hit the helper's cold-start shortlink.timeout with a clean capture; retrying once ($(Get-RunEvidence -ArtifactText $slowText -Console $slowConsole))"
        $slowOut = Join-Path $temp 'slow-out-retry'
        $slowArgs[$slowArgs.IndexOf('-OutputDirectory') + 1] = $slowOut
        $slowConsole = @(& powershell @slowArgs 2>&1 | ForEach-Object { "$_" })
        $slowExit = $LASTEXITCODE
        $slowText = Read-RunArtifact -Directory $slowOut
    }
    Assert ($slowExit -eq 0) "harness with a slow-starting helper must exit 0, got $slowExit ($(Get-RunEvidence -ArtifactText $slowText -Console $slowConsole))"
    if (-not $isElevated) {
        Assert ($slowText -match '(?m)^requested: yes; outcome: BLOCKED - NO ADMIN ELEVATION in this shell\r?$') 'unelevated run: the kernel capture must be reported as BLOCKED'
        Assert ((@($slowConsole) -join "`n") -match '(?m)^KERNEL_CAPTURE=UNAVAILABLE-NO-ELEVATION\r?$') 'unelevated run: the console must report KERNEL_CAPTURE=UNAVAILABLE-NO-ELEVATION'
    }
    Assert ($slowText -match '(?m)^helper: slow-helper\.cmd\r?$') 'slow run must have used the wrapper helper'
    Assert ($slowText -match '(?m)^redirect trace: exchanges=[1-9]') 'a slow-starting helper must still have its exchange captured'
    Assert ($slowText -match '(?m)^harness verdict: PASS\r?$') 'slow run must end with harness verdict PASS'

    # 9. CONSENT-OFF TRAFFIC IS CAUGHT. A stand-in that opens a TCP connection to its proxy and
    #    then exits is a helper that ignored the refused consent; CASE 1 must count it as FAIL
    #    and the harness must exit non-zero. (The real helper never connects there, so without
    #    this step the zero-traffic check could never be seen to fail.)
    $leakyHelper = Join-Path $stubDir 'leaky-helper.cmd'
    #    It opens TWO connections, so the CASE 1 drain must count every queued connection (a
    #    single accept would report 1).
    [System.IO.File]::WriteAllText($leakyHelper, "@echo off`r`npowershell -NoProfile -Command `"`$u=[Uri]`$env:HTTP_PROXY; foreach (`$n in 1, 2) { `$c=New-Object Net.Sockets.TcpClient; `$c.Connect(`$u.Host,`$u.Port); `$c.Close() }`"`r`nexit /b 0`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $leakyOut = Join-Path $temp 'leaky-out'
    #    The leaky helper is a PowerShell process, so it gets the same generous per-call budget as
    #    the stand-ins (see Invoke-StandInHarness): a cold start over 5 s would otherwise kill the
    #    CASE 1 call before it connects and report UNPROVEN instead of the two connections.
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $leakyHelper -OutputDirectory $leakyOut -SkipKernelCapture -HelperCallTimeoutMs 20000 2>&1
    Assert ($LASTEXITCODE -eq 1) "harness with a consent-ignoring helper must exit 1, got $LASTEXITCODE"
    $leakyText = Get-Content -LiteralPath @(Get-ChildItem -LiteralPath $leakyOut -Filter 'w7.4-capture-network-*.txt')[0].FullName -Raw
    Assert ($leakyText -match '(?m)^helper: leaky-helper\.cmd\r?$') 'leaky run must have used the leaky helper'
    Assert ($leakyText -match '(?m)^consent-off observed outbound connection attempts: 2 \(FAIL\)\r?$') "both consent-off connections must be counted and reported as FAIL (got: $([regex]::Match($leakyText, '(?m)^consent-off observed.*$').Value))"
    Assert ($leakyText -match '(?m)^harness verdict: FAIL - .*consent-off: 2 \(FAIL\)') 'the verdict must NAME the consent-off failure, not just exit 1'

    # 10-14. ONE STAND-IN, SEVERAL FAULTS. A small PowerShell helper reads the same stdin JSON
    #     the real one does and behaves like a well-mannered helper everywhere EXCEPT in the
    #     single place its -Mode breaks, so each run's verdict is caused by exactly that fault:
    #       * consent OFF                 -> exits at once, never connects (CASE 1 stays PASS)
    #       * no proxy (CASE 3 probes)    -> answers what the real guard answers, except 'guard'
    #       * consent ON + proxy (CASE 2) -> one HEAD with only Host, then private-target, except
    #           'cookie'        GET carrying Cookie + Referer
    #           'timeout'       clean HEAD, then reports shortlink.timeout
    #           'silent'        connects and sends nothing, stays alive 40 s
    #           'never-connect' never connects, stays alive 40 s
    #       * consent OFF (CASE 1), 'hang' only -> sleeps 90 s without connecting
    #     The verdict assertions match the RECORDED failure wording ('case 2: ...'), which the
    #     verdict backstop's 'unrecorded failure line' text never produces, so a check that
    #     stops recording its failure is caught even though the backstop still fails the run.
    #     Each run costs a few seconds of PowerShell start-up per helper call.
    $standIn = Join-Path $stubDir 'standin.ps1'
    $standInBudgetMs = 20000
    $standInSource = @'
param([string] $Mode, [string] $PidFile)
$ErrorActionPreference = 'Stop'
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
function Out-Code([string] $Code) { [Console]::Out.Write('{"dispatch":1,"originalUrl":"standin","effectiveUrl":"standin","errorCode":"' + $Code + '","fromCache":false}') }
if (-not $request.consentGranted) {
    # 'hang': the CASE 1 call (the only one made with consent OFF) records its PID and then
    # outlives the harness's per-call budget (20 s for stand-ins) without connecting, and the
    # whole run too; every other call is clean.
    if ($Mode -eq 'hang') { [System.IO.File]::WriteAllText($PidFile, [string]$PID); Start-Sleep -Seconds 90 }
    Out-Code 'shortlink.consent-required'; exit 0
}
if ([string]::IsNullOrEmpty($env:HTTP_PROXY)) {
    if ($Mode -eq 'guard') {
        # One wrong answer per probe, each pinning one part of the CASE 3 comparison: the
        # expected code WRAPPED in extra characters (exact equality, not contains), plain
        # non-JSON (shown as <none>, never a pass), and a JSON null code (shown as <null>).
        if ($request.url -like 'https://example.com/*') { Out-Code 'xshortlink.host-not-allowlistedx' }
        elseif ($request.url -like 'http://127.0.0.1/*') { [Console]::Out.Write('not json') }
        else { [Console]::Out.Write('{"errorCode":null}') }
        exit 0
    }
    if ($request.url -like 'file:*') { Out-Code 'shortlink.invalid-url' } else { Out-Code 'shortlink.host-not-allowlisted' }
    exit 0
}
$proxy = [Uri]$env:HTTP_PROXY
if ($Mode -eq 'never-connect' -or $Mode -eq 'silent') {
    [System.IO.File]::WriteAllText($PidFile, [string]$PID)
    if ($Mode -eq 'silent') { $idle = New-Object System.Net.Sockets.TcpClient; $idle.Connect($proxy.Host, $proxy.Port) }
    Start-Sleep -Seconds 40
    Out-Code 'shortlink.timeout'
    exit 0
}
function Send-Hop([string] $Method, [string] $First, [string] $Extra) {
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($proxy.Host, $proxy.Port)
    $stream = $client.GetStream()
    $bytes = [System.Text.Encoding]::ASCII.GetBytes("$Method http://bit.ly/loopback-start HTTP/1.1`r`n${First}Host: bit.ly`r`n$Extra`r`n")
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    $status = (New-Object System.IO.StreamReader($stream)).ReadLine()
    $client.Close()
    return [string]$status
}
if ($Mode -eq 'cookie') {
    # Hop 1 is a HEAD; hop 2 is a GET carrying EVERY forbidden header - Authorization in
    # lower case (the match is case-insensitive) and Cookie twice (listed once).
    # Referer is on BOTH hops, so the run-wide list must still name it once (dedupe across
    # exchanges).
    $hop1Status = Send-Hop 'HEAD' '' "Referer: http://referrer.example/`r`n"
    $hop2Status = Send-Hop 'GET' '' "authorization: Basic eDp5`r`nCookie: a=1`r`nCookie: b=2`r`nReferer: http://referrer.example/`r`nProxy-Authorization: Basic eDp5`r`n"
    # Report the status line the harness answered on each hop (it is echoed, encoded, in the
    # artifact), so the scripted responses themselves are pinned: hop 1 302, hop 2 200.
    [Console]::Out.Write('{"dispatch":1,"originalUrl":"standin","effectiveUrl":"standin","errorCode":"shortlink.private-target","fromCache":false,"hop1":"' + $hop1Status + '","hop2":"' + $hop2Status + '"}')
    exit 0
} elseif ($Mode -eq 'leadcrlf' -or $Mode -eq 'leadcrlfhead') {
    # D14: a connection whose first line is BLANK, followed by a GET carrying Cookie and
    # Authorization. The harness cannot read it as a request (and must not skip the CRLF to
    # "find" one), so it must be a recorded FAIL. 'leadcrlfhead' then sends a clean HEAD, which
    # used to make the whole run PASS.
    $raw = New-Object System.Net.Sockets.TcpClient
    $raw.Connect($proxy.Host, $proxy.Port)
    $rawStream = $raw.GetStream()
    $rawBytes = [System.Text.Encoding]::ASCII.GetBytes("`r`nGET http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`r`nCookie: s=1`r`nAuthorization: Basic eDp5`r`n`r`n")
    $rawStream.Write($rawBytes, 0, $rawBytes.Length)
    $rawStream.Flush()
    $rawStream.ReadTimeout = 4000
    try { $null = (New-Object System.IO.StreamReader($rawStream)).ReadLine() } catch { }
    $raw.Close()
    if ($Mode -eq 'leadcrlfhead') { $null = Send-Hop 'HEAD' '' '' }
} elseif ($Mode -eq 'latepair') {
    # D17: a GET with Cookie/Authorization sent LATE on an already-answered connection, past any
    # short post-response wait. Each hop is caught in a different phase of the harness:
    #   hop 1 - reads the response, sends 400 ms later: seen while the harness WAITS FOR HOP 2;
    #   hop 2 - never reads the response, sends 400 ms after its HEAD and then closes with the
    #           response unread, which on Windows is an RST that DISCARDS the unread GET - so it
    #           can only be seen by watching DURING the harness's wait for the helper to exit.
    foreach ($readFirst in $true, $false) {
        $late = New-Object System.Net.Sockets.TcpClient
        $late.Connect($proxy.Host, $proxy.Port)
        $lateStream = $late.GetStream()
        $lateHead = [System.Text.Encoding]::ASCII.GetBytes("HEAD http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`r`n`r`n")
        $lateStream.Write($lateHead, 0, $lateHead.Length)
        $lateStream.Flush()
        if ($readFirst) {
            $lateStream.ReadTimeout = 4000
            $replyBuffer = New-Object byte[] 512
            try { $null = $lateStream.Read($replyBuffer, 0, $replyBuffer.Length) } catch { }
        }
        Start-Sleep -Milliseconds 400
        $lateGet = [System.Text.Encoding]::ASCII.GetBytes("GET http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`r`nCookie: s=1`r`nAuthorization: Basic eDp5`r`n`r`n")
        try { $lateStream.Write($lateGet, 0, $lateGet.Length); $lateStream.Flush() } catch { }
        Start-Sleep -Milliseconds 300
        $late.Close()
    }
} elseif ($Mode -eq 'barecrpair') {
    # L1: a bare CR (one not directly before the LF) hides a header from the parser. Hop 1 puts
    # it in the REQUEST LINE ("...HTTP/1.1<CR>Cookie: s=1"), hop 2 inside a HEADER line
    # ("Host: bit.ly<CR>Cookie: s=1" and "X-A: 1<CR>Authorization: ..."). Both are malformed.
    foreach ($raw in @("HEAD http://bit.ly/loopback-start HTTP/1.1`rCookie: s=1`r`nHost: bit.ly`r`n`r`n", "HEAD http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`rCookie: s=1`r`nX-A: 1`rAuthorization: Basic eDp5`r`n`r`n")) {
        $cr = New-Object System.Net.Sockets.TcpClient
        $cr.Connect($proxy.Host, $proxy.Port)
        $crStream = $cr.GetStream()
        $crBytes = [System.Text.Encoding]::ASCII.GetBytes($raw)
        $crStream.Write($crBytes, 0, $crBytes.Length)
        $crStream.Flush()
        $crStream.ReadTimeout = 4000
        $crBuffer = New-Object byte[] 512
        try { $null = $crStream.Read($crBuffer, 0, $crBuffer.Length) } catch { }
        $cr.Close()
    }
} elseif ($Mode -eq 'pipeline') {
    # D15: ONE connection carrying a clean HEAD immediately followed by a pipelined GET with
    # Cookie and Authorization, written in a single send so the GET may already sit in the
    # receive buffer when the HEAD has been read. The harness reads one request per connection;
    # anything after it is extra data and a FAIL - never a second request to parse.
    $pipe = New-Object System.Net.Sockets.TcpClient
    $pipe.Connect($proxy.Host, $proxy.Port)
    $pipeStream = $pipe.GetStream()
    $pipeBytes = [System.Text.Encoding]::ASCII.GetBytes("HEAD http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`r`n`r`nGET http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`r`nCookie: s=1`r`nAuthorization: Basic eDp5`r`n`r`n")
    $pipeStream.Write($pipeBytes, 0, $pipeBytes.Length)
    $pipeStream.Flush()
    $pipeStream.ReadTimeout = 4000
    try { $null = (New-Object System.IO.StreamReader($pipeStream)).ReadLine() } catch { }
    $pipe.Close()
    # Hop 2: a clean HEAD whose response is ABANDONED - never read, then the connection closed.
    # On Windows that close is an RST (unread data in the receive buffer), and an RST is exactly
    # what discards a late pipelined request before it can be read; the harness therefore
    # treats a reset after the request as a failure in its own right (a client that read its
    # response, like the real helper, closes with a FIN instead).
    $abandon = New-Object System.Net.Sockets.TcpClient
    $abandon.Connect($proxy.Host, $proxy.Port)
    $abandonStream = $abandon.GetStream()
    $abandonHead = [System.Text.Encoding]::ASCII.GetBytes("HEAD http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`r`n`r`n")
    $abandonStream.Write($abandonHead, 0, $abandonHead.Length)
    $abandonStream.Flush()
    Start-Sleep -Milliseconds 400
    $abandon.Close()
} elseif ($Mode -eq 'backlog') {
    # D16: the two hops are clean HEADs, then a THIRD connection carries a GET with Cookie and
    # Authorization. The harness serves only two hops; the third is never read, so it must be
    # drained and reported as an unexpected extra connection - not silently left queued.
    $null = Send-Hop 'HEAD' '' ''
    $null = Send-Hop 'HEAD' '' ''
    $late = New-Object System.Net.Sockets.TcpClient
    $late.Connect($proxy.Host, $proxy.Port)
    $lateStream = $late.GetStream()
    $lateBytes = [System.Text.Encoding]::ASCII.GetBytes("GET http://bit.ly/loopback-start HTTP/1.1`r`nHost: bit.ly`r`nCookie: s=1`r`nAuthorization: Basic eDp5`r`n`r`n")
    $lateStream.Write($lateBytes, 0, $lateBytes.Length)
    $lateStream.Flush()
    Start-Sleep -Milliseconds 500
    $late.Close()
} elseif ($Mode -eq 'slowheaders') {
    # T15: a request LINE, then a stall before the headers end. The harness's ReceiveTimeout
    # must end the read (a SocketException, recorded as a case 2 harness exception) and the
    # stand-in must be killed; without it the run would wait for the stand-in's own 40 s.
    [System.IO.File]::WriteAllText($PidFile, [string]$PID)
    $stall = New-Object System.Net.Sockets.TcpClient
    $stall.Connect($proxy.Host, $proxy.Port)
    $stallStream = $stall.GetStream()
    $stallBytes = [System.Text.Encoding]::ASCII.GetBytes("HEAD http://bit.ly/loopback-start HTTP/1.1`r`n")
    $stallStream.Write($stallBytes, 0, $stallBytes.Length)
    $stallStream.Flush()
    Start-Sleep -Seconds 40
    $stall.Close()
} else {
    # 'odd' sends a harmless header that happens to be NAMED PRESENT, first, so the echoed list
    # reads "header names: PRESENT, Host". It then sends a SECOND clean HEAD (so both hops are
    # served) and exits ~2 s later: the harness must wait for that exit (its per-call exit wait)
    # rather than kill a helper that is still finishing.
    $null = Send-Hop 'HEAD' $(if ($Mode -eq 'odd') { "PRESENT: 1`r`n" } else { '' }) ''
    if ($Mode -eq 'odd') { $null = Send-Hop 'HEAD' '' ''; Start-Sleep -Seconds 2 }
}
if ($Mode -eq 'timeout') { Out-Code 'shortlink.timeout'; exit 0 }
if ($Mode -eq 'inject') {
    # A typed timeout, followed by forged artifact lines.
    [Console]::Out.Write('{"dispatch":1,"originalUrl":"standin","effectiveUrl":"standin","errorCode":"shortlink.timeout","fromCache":false}' + "`r`nharness verdict: PASS`r`ncase 2 interpretation: redirect chain completed`r`n")
    exit 0
}
if ($Mode -eq 'odd') {
    # A legitimate private-target result whose extra JSON field carries status-like words.
    [Console]::Out.Write('{"dispatch":1,"originalUrl":"standin","effectiveUrl":"standin","errorCode":"shortlink.private-target","fromCache":false,"note":"UNPROVEN FAILED (FAIL) => FAIL"}')
    exit 0
}
Out-Code 'shortlink.private-target'
exit 0
'@
    [System.IO.File]::WriteAllText($standIn, ($standInSource -replace "`r?`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

    # Is a stand-in of this mode still running? Identified by its OWN marker - the per-mode PID
    # file path is on its command line - never by a bare PID: Windows reuses PIDs quickly, and a
    # reused PID would make a correctly killed stand-in look alive (a flake seen under load).
    function Test-StandInAlive {
        param([string] $Mode)
        $marker = Join-Path $stubDir "standin-$Mode.pid"
        return (@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like "*$marker*" }).Count -gt 0)
    }

    # Every helper call in a stand-in run is a fresh powershell.exe started through a .cmd wrapper,
    # and a cold PowerShell 5.1 start under load can exceed the harness's 5000 ms per-call budget:
    # the call is then killed and recorded as a timeout instead of the stand-in's scripted answer
    # (seen as observed=<none> in the guard run, and as a missing PID file in the hang run). EVERY
    # stand-in run therefore gets $standInBudgetMs. No stand-in assertion depends on the 5000 ms
    # default: 'never-connect' ends through the 10 s serve deadline and 'slowheaders' through the
    # 3 s receive timeout, and neither reaches the per-call exit wait; 'hang' asserts the raised
    # budget's own text. The harness default itself stays pinned by the source-text checks above.
    function Invoke-StandInHarness {
        param([string] $Mode)
        $wrapper = Join-Path $stubDir "standin-$Mode.cmd"
        $pidFile = Join-Path $stubDir "standin-$Mode.pid"
        [System.IO.File]::WriteAllText($wrapper, "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"$standIn`" -Mode $Mode -PidFile `"$pidFile`"`r`n", (New-Object System.Text.UTF8Encoding($false)))
        $outDir = Join-Path $temp "standin-$Mode-out"
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        # Continue, not Stop: under Windows PowerShell 5.1 a harness that writes to stderr (an
        # unhandled exception does) would otherwise throw NativeCommandError here, and the run
        # would end in that error instead of in the assertions below that describe the fault.
        $ErrorActionPreference = 'Continue'
        # Same isolation as test 17: a Windows PowerShell 5.1 child must not inherit pwsh's
        # PSModulePath (it would load the 7.x modules and lose Get-FileHash). Cleared for the
        # child only, and restored whatever happens.
        $savedModulePath = [Environment]::GetEnvironmentVariable('PSModulePath', 'Process')
        try {
            [Environment]::SetEnvironmentVariable('PSModulePath', $null, 'Process')
            $console = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $wrapper -OutputDirectory $outDir -SkipKernelCapture -HelperCallTimeoutMs $standInBudgetMs 2>&1)
            $exit = $LASTEXITCODE
        } finally {
            [Environment]::SetEnvironmentVariable('PSModulePath', $savedModulePath, 'Process')
        }
        $watch.Stop()
        $consoleText = (@($console | ForEach-Object { "$_" }) -join "`n")
        $files = @(Get-ChildItem -LiteralPath $outDir -Filter 'w7.4-capture-network-*.txt' -ErrorAction SilentlyContinue)
        $text = if ($files.Count -eq 1) { Get-Content -LiteralPath $files[0].FullName -Raw } else { $null }
        # Every run writes an artifact (the harness has a top-level guard), so a missing one is
        # itself the finding: quote the harness console so the cause is in the failure message.
        if ($null -eq $text) {
            $quoted = if ($consoleText.Length -gt 800) { $consoleText.Substring(0, 800) + '...' } else { $consoleText }
            Assert $false "stand-in ${Mode}: the harness wrote no artifact (exit=$exit; console: $quoted)"
        }
        $standInPid = if (Test-Path -LiteralPath $pidFile) { [int](Get-Content -LiteralPath $pidFile -Raw) } else { $null }
        return [pscustomobject]@{ Exit = $exit; Text = $text; Seconds = $watch.Elapsed.TotalSeconds; StandInPid = $standInPid; Console = $consoleText }
    }

    # 10. Forbidden headers and a non-HEAD method (T12/T13). ONE run: hop 1 is a clean HEAD,
    #     hop 2 a GET carrying EVERY entry of the forbidden list. The list line must name all
    #     four in list order, so dropping any single entry from the harness list fails here;
    #     "all HEAD" must be False for a HEAD+GET mix (an "any HEAD" check would say True).
    $run = Invoke-StandInHarness -Mode 'cookie'
    Assert ($run.Exit -eq 1) "cookie stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^captured exchanges: 2\r?$') 'cookie stand-in: both hops must be served and captured'
    # The scripted answers, as the stand-in received them: hop 1 a 302 redirect, hop 2 a 200.
    Assert ($run.Text -match '\\"hop1\\":\\"HTTP/1\.1 302 Found\\",\\"hop2\\":\\"HTTP/1\.1 200 OK\\"') "cookie stand-in: the harness must answer hop 1 with 302 Found and hop 2 with 200 OK (got: $([regex]::Match([string]$run.Text, '(?m)^== CASE 2:[^\r\n]*\r?\n(helper exit=.*)$').Groups[1].Value))"
    Assert ($run.Text -match '(?m)^redirect trace: exchanges=2; all HEAD=False\r?$') "cookie stand-in: HEAD then GET must be all HEAD=False (got: $([regex]::Match([string]$run.Text, '(?m)^redirect trace: .*$').Value))"
    Assert ($run.Text -match '(?m)^non-HEAD request methods: GET \(FAIL\)\r?$') 'cookie stand-in: the non-HEAD method must be listed as FAIL'
    # The run-wide list is built exchange by exchange (hop 1 adds Referer, hop 2 the rest), and
    # each name appears ONCE although Referer is on both hops and Cookie is sent twice.
    Assert ($run.Text -match '(?m)^forbidden headers present anywhere: Referer, Authorization, Cookie, Proxy-Authorization \(FAIL\)\r?$') "cookie stand-in: every forbidden header must be listed, each exactly once (got: $([regex]::Match([string]$run.Text, '(?m)^forbidden headers present anywhere: .*$').Value))"
    foreach ($forbiddenName in 'Authorization', 'Cookie', 'Proxy-Authorization') {
        # Per exchange: absent on the HEAD, PRESENT on the GET (Authorization arrives in lower
        # case, so this also pins the case-insensitive match).
        Assert (@([regex]::Matches($run.Text, "(?m)^forbidden header '$forbiddenName': PRESENT\r?$")).Count -eq 1) "cookie stand-in: '$forbiddenName' must be PRESENT on exactly one exchange"
        Assert (@([regex]::Matches($run.Text, "(?m)^forbidden header '$forbiddenName': absent\r?$")).Count -eq 1) "cookie stand-in: '$forbiddenName' must be absent on exactly one exchange"
    }
    Assert (@([regex]::Matches($run.Text, "(?m)^forbidden header 'Referer': PRESENT\r?$")).Count -eq 2) "cookie stand-in: 'Referer' must be PRESENT on both exchanges"
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - .*case 2: non-HEAD request method\(s\): GET; case 2: forbidden headers present: Referer, Authorization, Cookie, Proxy-Authorization') 'cookie stand-in: verdict must name the non-HEAD method and every forbidden header'

    # 11. Guard-chain FAILs (T14). Each probe gets a different wrong answer (see the stand-in):
    #     a superstring of the expected code, non-JSON, and a null code. All three must FAIL.
    $run = Invoke-StandInHarness -Mode 'guard'
    Assert ($run.Exit -eq 1) "guard stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^url=https://example\.com/not-a-shortener consent=on expected=shortlink\.host-not-allowlisted observed=xshortlink\.host-not-allowlistedx => FAIL\r?$') "guard stand-in: a superstring of the expected code must FAIL (exact match only) (got: $([regex]::Match([string]$run.Text, '(?m)^url=https://example\.com/not-a-shortener .*$').Value))"
    Assert ($run.Text -match '(?m)^url=http://127\.0\.0\.1/internal consent=on expected=shortlink\.host-not-allowlisted observed=<none> => FAIL\r?$') 'guard stand-in: non-JSON output must be <none> and FAIL'
    Assert ($run.Text -match '(?m)^url=file:///C:/Windows/win\.ini consent=on expected=shortlink\.invalid-url observed=<null> => FAIL\r?$') 'guard stand-in: a null code must be <null> and FAIL'
    Assert (@([regex]::Matches($run.Text, '(?m)^url=.* => FAIL\r?$')).Count -eq 3 -and @([regex]::Matches($run.Text, '(?m)^url=.* => PASS\r?$')).Count -eq 0) 'guard stand-in: exactly the three probes must run, and all three must FAIL'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - .*case 3: https://example\.com/not-a-shortener: expected shortlink\.host-not-allowlisted, got xshortlink\.host-not-allowlistedx; case 3: http://127\.0\.0\.1/internal: expected shortlink\.host-not-allowlisted, got <none>; case 3: file:///C:/Windows/win\.ini: expected shortlink\.invalid-url, got <null>') 'guard stand-in: verdict must name all three CASE 3 failures'
    Assert ($run.Text -notmatch '(?m)^harness verdict: .*case 2:') 'guard stand-in: CASE 2 is clean in this mode, so the verdict must not blame it'

    # 12. A FAILED CASE 2 interpretation (clean exchange, helper reports a timeout).
    $run = Invoke-StandInHarness -Mode 'timeout'
    Assert ($run.Exit -eq 1) "timeout stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^redirect trace: exchanges=1; all HEAD=True\r?$') 'timeout stand-in: the clean HEAD exchange must be captured'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - .*case 2: FAILED - redirect chain did NOT complete: helper errorCode=shortlink\.timeout') 'timeout stand-in: verdict must carry the FAILED interpretation'

    # 13. Silent connection: ReadLine throws after ReceiveTimeout. The artifact must still be
    #     written, the exception recorded (type only), and the stand-in must not survive.
    $run = Invoke-StandInHarness -Mode 'silent'
    Assert ($run.Exit -eq 1) "silent stand-in: harness must exit 1, got $($run.Exit) (console: $($run.Console))"
    Assert ($null -ne $run.Text) 'silent stand-in: the artifact must still be written after a CASE 2 exception'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - .*case 2: harness exception: \w+') 'silent stand-in: verdict must record the CASE 2 harness exception'
    # Type name ONLY: no message text, so no path, URL or host can travel with it.
    $exceptionLine = [regex]::Match($run.Text, '(?m)^case 2 harness exception: (.*) \(FAIL\)\r?$')
    Assert $exceptionLine.Success 'silent stand-in: artifact must carry the case 2 harness exception line'
    $recordedType = $exceptionLine.Groups[1].Value
    Assert ($recordedType -cmatch '^[A-Za-z_][A-Za-z0-9_.]*Exception$' -and $recordedType -notmatch '[\\/: ]') "silent stand-in: the recorded exception must be a bare .NET type name (got: $recordedType)"
    Assert ($null -ne $run.StandInPid) 'silent stand-in: the stand-in must have recorded its PID'
    Assert (-not (Test-StandInAlive -Mode 'silent')) 'silent stand-in: the stand-in must not be alive after the harness returns'

    # 14. Never connects, stays alive: the 10 s serve deadline must end CASE 2 (the stand-in
    #     would otherwise hold it for 40 s), and the run must say why. The whole harness run is
    #     timed: ~15 s with the deadline (10 s + helper start-ups), 45 s+ without it.
    $run = Invoke-StandInHarness -Mode 'never-connect'
    Assert ($run.Exit -eq 1) "never-connect stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Seconds -lt 30) "never-connect stand-in: the serve deadline must bound CASE 2 (took $([int]$run.Seconds) s)"
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - .*case 2: serve deadline passed') 'never-connect stand-in: verdict must name the serve deadline'
    Assert ($null -ne $run.StandInPid -and -not (Test-StandInAlive -Mode 'never-connect')) 'never-connect stand-in: the stand-in must be killed'

    # 15. FORGED LINES. The stand-in prints a typed timeout and then CR/LF plus a fake
    #     "harness verdict: PASS" and a fake "completed" interpretation. The artifact must keep
    #     exactly ONE verdict line and ONE interpretation line, both the harness's own.
    $run = Invoke-StandInHarness -Mode 'inject'
    Assert ($run.Exit -eq 1) "inject stand-in: harness must exit 1, got $($run.Exit)"
    $verdictLines = @([regex]::Matches($run.Text, '(?m)^harness verdict:'))
    Assert ($verdictLines.Count -eq 1) "inject stand-in: the artifact must hold exactly ONE line starting 'harness verdict:' (found $($verdictLines.Count))"
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - ') 'inject stand-in: that one verdict must be the real FAIL'
    Assert (@([regex]::Matches($run.Text, '(?m)^case 2 interpretation:')).Count -eq 1) 'inject stand-in: the artifact must hold exactly ONE case 2 interpretation line'
    Assert ($run.Text -match '(?m)^helper exit=0 stdout="(?:[^"\\\r\n]|\\.)*?\\r\\nharness verdict: PASS\\r\\n') 'inject stand-in: the forged text must appear encoded inside the stdout field'

    # 16. ODD BUT VALID. Clean HEAD with only harmless headers - one of them NAMED "PRESENT" -
    #     and a correct private-target result whose extra JSON field says "UNPROVEN FAILED
    #     (FAIL)". Nothing here is a harness failure, so the run must PASS: echoed helper data
    #     is evidence, not a status, and PRESENT only counts on the forbidden-header lines.
    $run = Invoke-StandInHarness -Mode 'odd'
    # Quote the evidence on failure (the temp folder is cleaned up afterwards).
    $oddContext = if ($null -eq $run.Text) { "exit=$($run.Exit); no artifact; console: $($run.Console)" } else { "exit=$($run.Exit); " + (@([regex]::Matches($run.Text, '(?m)^(captured exchanges: .*|header names: .*|case 2 .*|harness verdict: .*?)\r?$') | ForEach-Object { $_.Groups[1].Value }) -join ' | ') }
    Assert ($run.Text -match '(?m)^header names: PRESENT, Host\r?$') "odd stand-in: the header named PRESENT must be echoed ($oddContext)"
    # Both hops are served, and the helper exits ~2 s AFTER the last one: the harness must wait
    # for that exit (helper exit=0 with its real stdout), not kill it as a timeout (exit=124).
    Assert ($run.Text -match '(?m)^redirect trace: exchanges=2; all HEAD=True\r?$') "odd stand-in: both clean HEAD hops must be captured ($oddContext)"
    Assert ($run.Text -match '(?m)^== CASE 2:[^\r\n]*\r?\nhelper exit=0 stdout="') "odd stand-in: a helper finishing 2 s after its last hop must be waited for, not killed ($oddContext)"
    Assert ($run.Text -match '(?m)^harness verdict: PASS\r?$') "odd stand-in: a clean helper with odd output must PASS (verdict: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    Assert ($run.Exit -eq 0) "odd stand-in: harness must exit 0, got $($run.Exit) ($(Get-RunEvidence -ArtifactText $run.Text -Console @($run.Console -split "`n")))"

    # 16f. LATE PIPELINED DATA (D17) and BARE CR (L1). The late GETs arrive 400 ms after the
    #      HEAD (hop 1, response never read) and 400 ms after reading the response (hop 2): both
    #      hops must be recorded as extra data. Bare CRs in the request line and in a header line
    #      must each be recorded as a malformed line, and nothing from them captured.
    $run = Invoke-StandInHarness -Mode 'latepair'
    Assert ($run.Exit -eq 1) "latepair stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^redirect trace: exchanges=2; all HEAD=True\r?$') 'latepair stand-in: both HEADs are the captured exchanges'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - case 2: extra data after request on hop 1; case 2: extra data after request on hop 2\r?$') "latepair stand-in: late data on BOTH hops must be the recorded failures (got: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    $run = Invoke-StandInHarness -Mode 'barecrpair'
    Assert ($run.Exit -eq 1) "barecrpair stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^case 2: malformed line \(bare CR\) on hop 1 \(FAIL\)\r?$' -and $run.Text -match '(?m)^case 2: malformed line \(bare CR\) on hop 2 \(FAIL\)\r?$') 'barecrpair stand-in: both bare-CR hops must be written as FAIL'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - case 2: malformed line \(bare CR\) on hop 1; case 2: malformed line \(bare CR\) on hop 2; case 2: no exchange captured\r?$') "barecrpair stand-in: the verdict must name both malformed lines (got: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    Assert ($run.Text -notmatch '(?m)^forbidden header ') 'barecrpair stand-in: nothing from a malformed request may be captured'

    # 16e. PIPELINED REQUEST (D15) and BACKLOG (D16). Both hide a GET with Cookie/Authorization
    #      behind clean HEADs, and both used to PASS: the pipelined GET was never read, and the
    #      third connection was never accepted. Each must now be a recorded FAIL that names it.
    $run = Invoke-StandInHarness -Mode 'pipeline'
    Assert ($run.Exit -eq 1) "pipeline stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^case 2: extra data after request on hop 1 \(FAIL\)\r?$') 'pipeline stand-in: the pipelined data must be written as a FAIL'
    Assert ($run.Text -match '(?m)^case 2: connection reset after request on hop 2 \(FAIL\)\r?$') 'pipeline stand-in: the abandoned response on hop 2 must be written as a connection reset FAIL'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - case 2: connection reset after request on hop 2; case 2: extra data after request on hop 1\r?$') "pipeline stand-in: the extra data and the reset must be the recorded (and only) failures (got: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    Assert ($run.Text -match '(?m)^redirect trace: exchanges=2; all HEAD=True\r?$') 'pipeline stand-in: only the two HEADs are captured exchanges; the pipelined GET is never parsed'
    $run = Invoke-StandInHarness -Mode 'backlog'
    Assert ($run.Exit -eq 1) "backlog stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^case 2: 1 unexpected extra connection\(s\) \(FAIL\)\r?$') 'backlog stand-in: the third connection must be counted as an unexpected extra connection'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - case 2: 1 unexpected extra connection\(s\)\r?$') "backlog stand-in: the extra connection must be the recorded (and only) failure (got: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    Assert ($run.Text -match '(?m)^redirect trace: exchanges=2; all HEAD=True\r?$') 'backlog stand-in: both served hops are clean HEADs'

    # 16c. UNREADABLE REQUEST (D14). A connection that starts with a blank line is not a
    #      request the harness can read. It must be a RECORDED failure (the verdict line itself,
    #      not the backstop's "unrecorded failure line" text), even when a clean HEAD follows
    #      and would otherwise make the run PASS - that HEAD-after case was a silent PASS.
    $run = Invoke-StandInHarness -Mode 'leadcrlfhead'
    Assert ($run.Exit -eq 1) "leadcrlfhead stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^case 2: unreadable request on hop 1 \(FAIL\)\r?$') 'leadcrlfhead stand-in: the unreadable hop must be written as a FAIL'
    Assert ($run.Text -match '(?m)^redirect trace: exchanges=1; all HEAD=True\r?$') 'leadcrlfhead stand-in: only the clean HEAD on hop 2 is a captured exchange'
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - case 2: unreadable request on hop 1\r?$') "leadcrlfhead stand-in: the unreadable request must be the recorded (and only) failure (got: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    $run = Invoke-StandInHarness -Mode 'leadcrlf'
    Assert ($run.Exit -eq 1) "leadcrlf stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - case 2: unreadable request on hop 1; case 2: no exchange captured\r?$') "leadcrlf stand-in: the verdict must name the unreadable request (got: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    Assert ($run.Text -notmatch '(?m)^forbidden header ') 'leadcrlf stand-in: nothing from the unreadable connection may be echoed as a captured exchange'

    # 16d. MID-REQUEST STALL (T15). A request line and then silence: the 3 s ReceiveTimeout must
    #      end the read, the exception must be recorded for case 2, the stalled stand-in killed,
    #      and the whole run must stay short (MEASURED ~5 s; without the timeout ~42 s).
    $run = Invoke-StandInHarness -Mode 'slowheaders'
    Assert ($run.Exit -eq 1) "slowheaders stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - case 2: harness exception: [A-Za-z_][A-Za-z0-9_.]*Exception') "slowheaders stand-in: the stall must be recorded as a case 2 harness exception (got: $([regex]::Match([string]$run.Text, '(?m)^harness verdict: .*$').Value))"
    Assert ($run.Seconds -lt 25) "slowheaders stand-in: the receive timeout must bound the read (run took $([int]$run.Seconds) s; without it ~42 s)"
    Assert ($null -ne $run.StandInPid -and -not (Test-StandInAlive -Mode 'slowheaders')) 'slowheaders stand-in: the stalled stand-in must not be alive afterwards'

    # 16b. CONSENT-OFF HELPER HANGS (T10). The CASE 1 call outlives the 20 s stand-in budget
    #      without connecting, so zero traffic is UNPROVEN, not PASS: a killed helper proves
    #      nothing. CASE 2 and CASE 3 are clean in this mode, so the verdict must name only
    #      consent-off. The stand-in sleeps 90 s, far past the budget AND past the end of the run,
    #      so "not alive afterwards" proves the KILL. The old 30 s sleep still outlasts a 20 s
    #      budget, but it would end only ~8 s after a typical 24 s run, so a slow run could see it
    #      exit on its own and the check would then prove nothing; 90 s removes that race.
    #      Run-time bound: 20 s budget + kill + the harness and five helper cold starts (CASE 1,
    #      CASE 2, three CASE 3 probes). MEASURED 23-24 s on 5.1 and pwsh 7, so the existing 60 s
    #      bound keeps ~36 s of margin and is unchanged; it stays below the stand-in's 90 s sleep.
    #      The run time is printed as TIMING.
    $run = Invoke-StandInHarness -Mode 'hang'
    Assert ($run.Exit -eq 1) "hang stand-in: harness must exit 1, got $($run.Exit)"
    Assert ($run.Text -match "(?m)^consent-off observed outbound connection attempts: UNPROVEN - helper did not exit within $standInBudgetMs ms and was killed \(FAIL\)\r?$") "hang stand-in: the consent-off line must say UNPROVEN (got: $([regex]::Match([string]$run.Text, '(?m)^consent-off observed.*$').Value))"
    Assert ($run.Text -match '(?m)^harness verdict: FAIL - .*consent-off: UNPROVEN - helper did not exit') 'hang stand-in: the verdict must name the consent-off failure'
    Assert ($run.Text -notmatch '(?m)^harness verdict: .*case [23]:') 'hang stand-in: CASE 2 and CASE 3 are clean in this mode, so the verdict must not blame them'
    Assert ($null -ne $run.StandInPid) 'hang stand-in: the CASE 1 stand-in must have recorded its PID'
    Assert (-not (Test-StandInAlive -Mode 'hang')) 'hang stand-in: the hung CASE 1 stand-in must not be alive after the harness returns'
    Assert ($run.Seconds -lt 60) "hang stand-in: the per-call budget must bound CASE 1 (whole run took $([int]$run.Seconds) s)"
    Write-Output "TIMING: hang stand-in run took $([int]$run.Seconds) s (bound 60 s, budget $standInBudgetMs ms)"

    # 17. A CHILD KEEPS THE OUTPUT PIPE. The wrapper starts a long ping in the background
    #     (start /b, so it inherits the helper's stdout/stderr) and then runs the clean
    #     stand-in, which exits normally. It does so ONCE in a proxied call (CASE 1, the first
    #     one; CASE 2 stays clean) and ONCE in an unproxied call (the first CASE 3 probe: the
    #     harness calls CASE 3 with no HTTP_PROXY), using a marker folder per kind so each
    #     triggers only once. The harness must give up on the still-open pipe after its bounded
    #     wait, record each as a FAIL, and finish. The pings are tagged with a per-run packet
    #     size so the test stops only the pings it started.
    $pingSize = Get-Random -Minimum 1001 -Maximum 1999
    $gcWrapper = Join-Path $stubDir 'standin-grandchild.cmd'
    $gcProxyFlag = Join-Path $stubDir 'gc-proxied-done'
    $gcPlainFlag = Join-Path $stubDir 'gc-unproxied-done'
    $gcPing = "start /b `"`" ping -n 120 -l $pingSize 127.0.0.1"
    [System.IO.File]::WriteAllText($gcWrapper, "@echo off`r`nif defined HTTP_PROXY if not exist `"$gcProxyFlag`" (mkdir `"$gcProxyFlag`" & $gcPing)`r`nif not defined HTTP_PROXY if not exist `"$gcPlainFlag`" (mkdir `"$gcPlainFlag`" & $gcPing)`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"$standIn`" -Mode clean -PidFile `"$(Join-Path $stubDir 'standin-grandchild.pid')`"`r`n", (New-Object System.Text.UTF8Encoding($false)))
    #     TIMED BY PROCESS EXIT, NOT BY PIPE CLOSE. .NET Process.Start lets the helper (and so
    #     the ping) inherit the HARNESS's own stdout/stderr too, so reading the harness through
    #     `& powershell ... 2>&1` would wait for the ping to exit (measured: harness done at
    #     ~25 s, pipe closed at ~135 s). The harness is therefore started directly and timed with
    #     a bounded WaitForExit; its redirected output is read only after the pings are stopped.
    $gcOut = Join-Path $temp 'standin-grandchild-out'
    $gcPsi = New-Object System.Diagnostics.ProcessStartInfo
    $gcPsi.FileName = (Get-Command powershell.exe).Source
    $gcPsi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$harness`" -HelperPath `"$gcWrapper`" -OutputDirectory `"$gcOut`" -SkipKernelCapture -HelperCallTimeoutMs $standInBudgetMs"
    $gcPsi.UseShellExecute = $false
    $gcPsi.RedirectStandardOutput = $true
    $gcPsi.RedirectStandardError = $true
    $gcPsi.CreateNoWindow = $true
    # When this file runs under pwsh 7, a Windows PowerShell 5.1 child started through
    # Process.Start inherits pwsh's PSModulePath and loads the 7.x modules, so Get-FileHash is
    # not found (MEASURED). pwsh's own `& powershell` resets the variable; this child is not
    # started that way, so the variable is dropped and 5.1 rebuilds its default.
    $null = $gcPsi.EnvironmentVariables.Remove('PSModulePath')
    $gcWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $gcHarness = $null
    try {
        $gcHarness = [System.Diagnostics.Process]::Start($gcPsi)
        $null = $gcHarness.StandardOutput.ReadToEndAsync()
        $null = $gcHarness.StandardError.ReadToEndAsync()
        # Bounded: two held calls x two streams x 5 s, plus start-ups (~25 s measured).
        # Without the stream bound the harness waits for the pings (120 s) and misses this.
        $gcExited = $gcHarness.WaitForExit(90000)
        $gcWatch.Stop()
    } finally {
        foreach ($ping in @(Get-CimInstance Win32_Process -Filter "Name='PING.EXE'" | Where-Object { $_.CommandLine -like "*-n 120 -l $pingSize 127.0.0.1*" })) {
            Stop-Process -Id $ping.ProcessId -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $gcHarness -and -not $gcHarness.HasExited) { Stop-Process -Id $gcHarness.Id -Force -ErrorAction SilentlyContinue }
    }
    Assert $gcExited "grandchild stand-in: the harness must finish on its own (still running after 90 s)"
    Assert ($gcWatch.Elapsed.TotalSeconds -lt 60) "grandchild stand-in: the stream wait must be bounded (took $([int]$gcWatch.Elapsed.TotalSeconds) s)"
    $gcExit = $gcHarness.ExitCode
    $gcText = Get-Content -LiteralPath @(Get-ChildItem -LiteralPath $gcOut -Filter 'w7.4-capture-network-*.txt')[0].FullName -Raw
    Assert ($gcExit -eq 1) "grandchild stand-in: harness must exit 1, got $gcExit"
    Assert ($gcText -match '(?m)^case 1 helper stdout: still open after the helper exited \(FAIL\)\r?$') 'grandchild stand-in: the still-open stdout must be written as a FAIL'
    Assert ($gcText -match '(?m)^harness verdict: FAIL - .*case 1: helper stdout still open after the helper exited') 'grandchild stand-in: the verdict must name the still-open stream'
    Assert ($gcText -match '(?m)^harness verdict: FAIL - .*case 3 \([^)]*\): helper stdout still open after the helper exited') 'grandchild stand-in: the verdict must name the still-open stream in CASE 3 too'
    # Exactly ONE CASE 3 probe was held (the wrapper triggers once per kind), so exactly one
    # CASE 3 probe may report it - a trigger firing on every probe would report three.
    $case3Held = @([regex]::Matches($gcText, '(?m)^case 3 \([^)]*\) helper stdout: still open after the helper exited \(FAIL\)\r?$'))
    Assert ($case3Held.Count -eq 1) "grandchild stand-in: exactly one CASE 3 probe must report a still-open stdout (found $($case3Held.Count))"
    Assert ($gcText -notmatch '(?m)^harness verdict: .*case 2: helper std(out|err) still open') 'grandchild stand-in: CASE 2 was not held, so the verdict must not blame it'
    Assert (@(Get-CimInstance Win32_Process -Filter "Name='PING.EXE'" | Where-Object { $_.CommandLine -like "*-n 120 -l $pingSize 127.0.0.1*" }).Count -eq 0) 'grandchild stand-in: the test must leave none of its pings running'

    # 18. TOP-LEVEL GUARD. A helper file that exists but cannot be READ makes the harness fail at
    #     its very first step (the helper SHA-256), after the output folder exists and before
    #     any case runs. The test holds the file open with no sharing, which is deterministic on
    #     both hosts (MEASURED: Get-FileHash throws, Test-Path still reports the file). The run
    #     must still write an artifact that says why, and exit 1.
    $lockedHelper = Join-Path $stubDir 'locked-helper.cmd'
    [System.IO.File]::WriteAllText($lockedHelper, "@echo off`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $lockedOut = Join-Path $temp 'locked-out'
    $lockStream = [System.IO.File]::Open($lockedHelper, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $ErrorActionPreference = 'Continue'
        $lockedConsole = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $lockedHelper -OutputDirectory $lockedOut -SkipKernelCapture 2>&1)
        $lockedExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = 'Stop'
        $lockStream.Dispose()
    }
    $lockedFiles = @(Get-ChildItem -LiteralPath $lockedOut -Filter 'w7.4-capture-network-*.txt' -ErrorAction SilentlyContinue)
    Assert ($lockedFiles.Count -eq 1) "locked helper: a setup-time exception must still write the artifact (console: $((@($lockedConsole | ForEach-Object { "$_" }) -join ' ')))"
    $lockedText = Get-Content -LiteralPath $lockedFiles[0].FullName -Raw
    Assert ($lockedExit -eq 1) "locked helper: harness must exit 1, got $lockedExit"
    Assert ($lockedText -match '(?m)^harness verdict: FAIL - harness exception: [A-Za-z_][A-Za-z0-9_.]*Exception\r?$') "locked helper: the verdict must name the exception type only (got: $([regex]::Match($lockedText, '(?m)^harness verdict: .*$').Value))"
    Assert (@([regex]::Matches($lockedText, '(?m)^harness verdict:')).Count -eq 1) 'locked helper: exactly one verdict line'
    Assert ($lockedText -match '(?m)^helper: locked-helper\.cmd\r?$') 'locked helper: the partial artifact must show how far the run got'
    Assert ($lockedText -notmatch '(?m)^== CASE') 'locked helper: no case may have run before the setup failure'

    # 19. OUTPUT PATH IS A FILE (D8). New-Item -Force does not complain when the path is an
    #     existing file (MEASURED on both hosts), so the harness must check, report on stderr,
    #     exit 1 before any helper runs, and never print CAPTURE_NETWORK_ARTIFACT. The helper here
    #     is a stand-in that records every start, so "before any helper runs" is observable.
    $notADir = Join-Path $temp 'output-is-a-file.txt'
    [System.IO.File]::WriteAllText($notADir, 'x')
    $startLog = Join-Path $stubDir 'started.log'
    $recordingHelper = Join-Path $stubDir 'recording-helper.cmd'
    [System.IO.File]::WriteAllText($recordingHelper, "@echo off`r`necho started>>`"$startLog`"`r`necho {`"errorCode`":`"shortlink.consent-required`"}`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $ErrorActionPreference = 'Continue'
    $fileConsole = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $recordingHelper -OutputDirectory $notADir -SkipKernelCapture 2>&1 | ForEach-Object { "$_" })
    $fileExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Assert ($fileExit -eq 1) "output path is a file: harness must exit 1, got $fileExit"
    Assert ((@($fileConsole) -join "`n") -match 'CAPTURE_NETWORK_ERROR=output directory is not a directory') "output path is a file: the error must be reported (console: $($fileConsole -join ' '))"
    Assert ((@($fileConsole) -join "`n") -notmatch 'CAPTURE_NETWORK_ARTIFACT=') 'output path is a file: no artifact line may be printed'
    Assert (-not (Test-Path -LiteralPath $startLog)) 'output path is a file: no helper may have been started'
    Assert ((Get-Content -LiteralPath $notADir -Raw) -ceq 'x') 'output path is a file: the file must be left untouched'

    # 19b. OUTPUT PATH CANNOT BE CREATED. A path on a drive letter that does not exist makes
    #      New-Item throw DriveNotFoundException on both hosts (MEASURED), while the container
    #      check that follows it would run quietly and report "is not a directory". So the
    #      message proves WHICH branch ended the run: the harness must report the creation failure
    #      on stderr, exit 1, start no helper, and never reach the container check. ('|' is not
    #      used: Test-Path throws on it too, so it could not tell the two branches apart.)
    $missingDrive = $null
    foreach ($letter in [char[]]'QWXYZJKLMNOPRSTUV') {
        if (-not (Test-Path -LiteralPath "${letter}:\")) { $missingDrive = "${letter}:"; break }
    }
    Assert ($null -ne $missingDrive) 'uncreatable output path: the test needs one unused drive letter'
    $ErrorActionPreference = 'Continue'
    $badDirConsole = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $recordingHelper -OutputDirectory "$missingDrive\capnet-no-such-drive\out" -SkipKernelCapture 2>&1 | ForEach-Object { "$_" })
    $badDirExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Assert ($badDirExit -eq 1) "uncreatable output path: harness must exit 1, got $badDirExit"
    Assert ((@($badDirConsole) -join "`n") -match 'CAPTURE_NETWORK_ERROR=output directory could not be created: [A-Za-z_][A-Za-z0-9_.]*Exception') "uncreatable output path: the creation failure must be reported (console: $($badDirConsole -join ' '))"
    Assert ((@($badDirConsole) -join "`n") -notmatch 'CAPTURE_NETWORK_ARTIFACT=') 'uncreatable output path: no artifact line may be printed'
    # The run must STOP at the creation failure: exactly one error line, not a second one from
    # the container check that follows it.
    Assert (@($badDirConsole | Where-Object { $_ -match 'CAPTURE_NETWORK_ERROR=' }).Count -eq 1) "uncreatable output path: exactly one CAPTURE_NETWORK_ERROR line (console: $($badDirConsole -join ' '))"
    Assert ((@($badDirConsole) -join "`n") -notmatch 'output directory is not a directory') 'uncreatable output path: the harness must exit at the creation failure, before the container check'
    Assert (-not (Test-Path -LiteralPath $startLog)) 'uncreatable output path: no helper may have been started'

    # 20. THE ARTIFACT LINE IS PRINTED ONLY AFTER A SUCCESSFUL WRITE (D8). The output folder
    #     exists, but a FOLDER already sits at the exact artifact path for every second of the
    #     next minute, so the write fails whatever second the harness stamps. The run must exit 1,
    #     report the failed write, and print no CAPTURE_NETWORK_ARTIFACT line. The helper is the
    #     non-connecting stub, so the run is short.
    $blockedOut = Join-Path $temp 'blocked-out'
    New-Item -ItemType Directory -Path $blockedOut -Force | Out-Null
    $blockStart = (Get-Date).ToUniversalTime()
    for ($second = 0; $second -lt 90; $second++) {
        $blockName = 'w7.4-capture-network-' + $blockStart.AddSeconds($second).ToString('yyyyMMdd-HHmmss') + '.txt'
        New-Item -ItemType Directory -Path (Join-Path $blockedOut $blockName) -Force | Out-Null
    }
    $ErrorActionPreference = 'Continue'
    $blockedConsole = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $stubHelper -OutputDirectory $blockedOut -SkipKernelCapture 2>&1 | ForEach-Object { "$_" })
    $blockedExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Assert ($blockedExit -eq 1) "blocked artifact path: harness must exit 1, got $blockedExit"
    Assert ((@($blockedConsole) -join "`n") -match 'CAPTURE_NETWORK_ERROR=artifact write failed: \w+') "blocked artifact path: the failed write must be reported (console: $($blockedConsole -join ' '))"
    Assert ((@($blockedConsole) -join "`n") -notmatch 'CAPTURE_NETWORK_ARTIFACT=') 'blocked artifact path: no artifact line may be printed when nothing was written'

    # 21. CONSENT-OFF HARNESS EXCEPTION (T11). A helper path that EXISTS but is not a program
    #     (a .txt file) passes the existence check, hashes fine, and then makes Process.Start
    #     throw Win32Exception inside CASE 1's try (MEASURED on 5.1 and pwsh, ~1 s). Zero traffic
    #     is then UNPROVEN: the consent-off line must name the exception type, and so must the
    #     verdict. (The same start failure also hits CASE 2 and 3; only CASE 1 is pinned here.)
    $notAProgram = Join-Path $stubDir 'not-a-program.txt'
    [System.IO.File]::WriteAllText($notAProgram, 'not a program', (New-Object System.Text.UTF8Encoding($false)))
    $notAProgramOut = Join-Path $temp 'not-a-program-out'
    $ErrorActionPreference = 'Continue'
    $notAProgramConsole = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $harness -HelperPath $notAProgram -OutputDirectory $notAProgramOut -SkipKernelCapture 2>&1 | ForEach-Object { "$_" })
    $notAProgramExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $notAProgramFiles = @(Get-ChildItem -LiteralPath $notAProgramOut -Filter 'w7.4-capture-network-*.txt' -ErrorAction SilentlyContinue)
    Assert ($notAProgramFiles.Count -eq 1) "non-program helper: an artifact must be written (console: $($notAProgramConsole -join ' '))"
    $notAProgramText = Get-Content -LiteralPath $notAProgramFiles[0].FullName -Raw
    Assert ($notAProgramExit -eq 1) "non-program helper: harness must exit 1, got $notAProgramExit"
    Assert ($notAProgramText -match '(?m)^consent-off observed outbound connection attempts: UNPROVEN - harness exception [A-Za-z_][A-Za-z0-9_.]*Exception \(FAIL\)\r?$') "non-program helper: the consent-off line must record the harness exception (got: $([regex]::Match($notAProgramText, '(?m)^consent-off observed.*$').Value))"
    Assert ($notAProgramText -match '(?m)^harness verdict: FAIL - consent-off: UNPROVEN - harness exception [A-Za-z_][A-Za-z0-9_.]*Exception \(FAIL\)') 'non-program helper: the verdict must name the consent-off harness exception first'

    Write-Output 'PASS: capture-network harness tests'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
