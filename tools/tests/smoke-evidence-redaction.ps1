[CmdletBinding()]
param(
    [switch] $ExpectedRed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('brochoos-execution-temp-W0.2-' + [Guid]::NewGuid().ToString('N'))
$captureDir = Join-Path $tempRoot ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $captureDir -Force

$scripts = @(
    (Join-Path $repoRoot 'tools\registration-smoke.ps1')
    (Join-Path $repoRoot 'tools\url-activation-sim.ps1')
    (Join-Path $repoRoot 'tools\smoke-launch.ps1')
)

$sentinel = @{
    repoRoot   = 'C:\Users\SENTINEL_ACCOUNT\Repo\SENTINEL_PROFILE\Project'
    Url        = 'https://example.com/path?token=SENTINEL_TOKEN_QUERY&profile=SENTINEL_PROFILE'
    exePath    = 'C:\Users\SENTINEL_ACCOUNT\AppData\Local\BroChoos\BroChoos.exe'
    StdOut     = 'stdout includes SENTINEL_TOKEN_STDOUT and path C:\Users\SENTINEL_ACCOUNT\Logs\run.txt'
    StdErr     = 'stderr includes SENTINEL_TOKEN_STDERR and UserChoice ProgId=[ChromeHTML] Hash=[Zm9vYmFyaGFzaA==]'
    logPath    = 'C:\Users\SENTINEL_ACCOUNT\AppData\Local\BroChoos\logs\system_changes.log'
    backupPath = 'C:\Users\SENTINEL_ACCOUNT\AppData\Local\BroChoos\backups\registration-SENTINEL_TOKEN_BACKUP.json'
    tempPath   = 'C:\Users\SENTINEL_ACCOUNT\AppData\Local\Temp\SENTINEL_TOKEN_TEMP'
    # Ordinary values on purpose: they must NOT match the separate SENTINEL_ token pattern, so only the
    # UserChoice redaction can remove them.
    UserChoice = 'http=[ChromeHTML|Zm9vYmFyaGFzaA==],https=[FirefoxURL-308046B0AF4A39CB|YmF6cXV4aGFzaA==] ; .url=[ChromeHTML|dXJsaGFzaHZhbHVl]'
    account    = 'SENTINEL_ACCOUNT'
    profile    = 'SENTINEL_PROFILE'
    token      = 'SENTINEL_TOKEN_PAYLOAD'
}

$userChoiceLeakPattern = 'ChromeHTML|FirefoxURL-308046B0AF4A39CB|Zm9vYmFyaGFzaA|YmF6cXV4aGFzaA|dXJsaGFzaHZhbHVl'

function Test-ContainsLeak {
    param([string] $Text)
    if ($Text -match 'SENTINEL_') { return $true }
    if ($Text -match $userChoiceLeakPattern) { return $true }
    if ($Text -match '[A-Za-z]:\\Users\\') { return $true }
    if ($Text -match '\?token=') { return $true }
    return $false
}

function Get-LeakClassMap {
    param([string] $Text)
    return [ordered]@{
        AbsoluteUserPath = ($Text -match '[A-Za-z]:\\Users\\')
        UrlQueryToken    = ($Text -match '\?token=')
        UserChoice       = ($Text -match $userChoiceLeakPattern)
        AccountProfile   = ($Text -match 'SENTINEL_ACCOUNT|SENTINEL_PROFILE')
        TokenSentinel    = ($Text -match 'SENTINEL_TOKEN_')
    }
}

function Assert-RenderOnlyBranchBeforeSideEffects {
    param([string] $ScriptPath)
    $raw = Get-Content -Raw -Path $ScriptPath

    if ($raw -notmatch '\[switch\]\s*\$RenderEvidenceOnly') {
        throw "RenderEvidenceOnly parameter missing in $ScriptPath"
    }

    $renderIdx = $raw.IndexOf('if ($RenderEvidenceOnly)')
    if ($renderIdx -lt 0) {
        throw "RenderEvidenceOnly branch missing in $ScriptPath"
    }

    $ops = @('Add-Type', 'dotnet ', 'Start-Process', 'Get-ItemProperty', 'Set-ItemProperty', 'Remove-Item Env:', 'Invoke-BroChoos')
    foreach ($op in $ops) {
        $idx = $raw.IndexOf($op)
        if ($idx -ge 0 -and $idx -lt $renderIdx) {
            throw "Render-only branch is after side-effect token '$op' in $ScriptPath"
        }
    }
}

try {
    foreach ($script in $scripts) {
        if (-not (Test-Path -LiteralPath $script)) {
            throw "Script missing: $script"
        }

        Assert-RenderOnlyBranchBeforeSideEffects -ScriptPath $script

        $name = [IO.Path]::GetFileNameWithoutExtension($script)
        $stdoutFile = Join-Path $captureDir ("$name.stdout.log")
        $stderrFile = Join-Path $captureDir ("$name.stderr.log")

        $args = @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $script,
            '-RenderEvidenceOnly'
        )

        if ($ExpectedRed) {
            $args += '-UnsafePassThrough'
        }

        $args += @(
            '-SyntheticRepoRoot', $sentinel.repoRoot,
            '-SyntheticUrl', $sentinel.Url,
            '-SyntheticExePath', $sentinel.exePath,
            '-SyntheticStdOut', $sentinel.StdOut,
            '-SyntheticStdErr', $sentinel.StdErr,
            '-SyntheticLogPath', $sentinel.logPath,
            '-SyntheticBackupPath', $sentinel.backupPath,
            '-SyntheticTempPath', $sentinel.tempPath,
            '-SyntheticUserChoice', $sentinel.UserChoice,
            '-SyntheticAccount', $sentinel.account,
            '-SyntheticProfile', $sentinel.profile,
            '-SyntheticToken', $sentinel.token
        )

        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & powershell.exe @args 1> $stdoutFile 2> $stderrFile
        $procExit = $LASTEXITCODE
        $ErrorActionPreference = $oldPreference
        if ($procExit -ne 0) {
            throw "Render branch process failed for $name with exit $procExit"
        }

        $out = ''
        $err = ''
        if (Test-Path -LiteralPath $stdoutFile) { $out = Get-Content -Raw -Path $stdoutFile }
        if (Test-Path -LiteralPath $stderrFile) { $err = Get-Content -Raw -Path $stderrFile }
        $combined = "$out`n$err"

        $hasLeak = Test-ContainsLeak -Text $combined
        $classMap = Get-LeakClassMap -Text $combined

        if ($ExpectedRed) {
            foreach ($k in $classMap.Keys) {
                if (-not $classMap[$k]) {
                    throw "Expected RED leak class '$k' was not observed for $name"
                }
            }
        }
        else {
            if ($hasLeak) {
                throw "Unexpected sensitive leakage observed for $name"
            }

            if ($combined -notmatch '<redacted:user-path>' -or $combined -notmatch '<redacted-query>' -or $combined -notmatch '<redacted:token>') {
                throw "Expected redaction labels missing for $name"
            }
        }
    }

    if ($ExpectedRed) {
        exit 1
    }

    exit 0
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $tempRoot) {
        throw "Post-cleanup assertion failed: temp root still exists: $tempRoot"
    }
}
