[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$installerScript = Join-Path $repoRoot 'packaging\BroChoos.Installer\BroChoos.per-user.iss'
$packagerScript = Join-Path $repoRoot 'packaging\BroChoos.Installer\package-per-user-installer.ps1'
$ownerApprovalRecord = Join-Path $repoRoot 'docs\decisions\owner-rulings\installer-toolchain-approval.json'

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Name
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "[$Name] missing required content: $Needle"
    }
}

function Assert-NotContains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Name
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "[$Name] contains forbidden content: $Needle"
    }
}

function Get-NormalizedSemver {
    param(
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $match = [regex]::Match($Text, '^(\d+\.\d+\.\d+)')
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $null
}

function Get-InstalledInnoSetupRecordForTest {
    # Independent expectation source: the uninstall registry entry written by the Inno Setup installer.
    # This intentionally does NOT reuse any packager version-detection logic (no FileVersionInfo, no
    # banner parsing, no install-location matching): it reads the canonical 'Inno Setup 6_is1' key
    # directly. The ROOTS and their ORDER are the packager's (HKCU, HKLM, HKLM\WOW6432Node): a
    # per-user-only read missed every machine-wide install, e.g. the one on hosted CI runners.
    $key = $null
    foreach ($uninstallRoot in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        $key = Get-ItemProperty -LiteralPath (Join-Path $uninstallRoot 'Inno Setup 6_is1') -ErrorAction SilentlyContinue
        if ($null -ne $key) {
            break
        }
    }

    if ($null -eq $key) {
        return $null
    }

    $versionProperty = $key.PSObject.Properties['DisplayVersion']
    if ($null -eq $versionProperty -or $null -eq $versionProperty.Value) {
        return $null
    }

    $version = ([string]$versionProperty.Value).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) {
        return $null
    }

    $locationProperty = $key.PSObject.Properties['InstallLocation']
    $installLocation = $null
    if ($null -ne $locationProperty -and $null -ne $locationProperty.Value) {
        $installLocation = ([string]$locationProperty.Value).Trim()
    }

    return @{
        Version = $version
        InstallLocation = $installLocation
    }
}

function Resolve-IsccCompilerPath {
    $command = Get-Command -Name 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source) -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    # Generic install locations only: no machine-specific user paths.
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        'C:\tools\Inno Setup 6\ISCC.exe',
        'C:\ProgramData\chocolatey\bin\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    return $null
}

function Invoke-PackagerValidateOnly {
    param(
        [string]$PackagerScriptPath,
        [AllowEmptyString()]
        [string]$ApprovalRecordPath,
        [string]$CompilerPath,
        [string]$SandboxRoot,
        [string]$CaseName,
        [string]$Configuration = 'Release',
        [string]$Runtime = 'win-x64',
        [AllowEmptyString()]
        [string]$Version = '',
        [switch]$AllowOverride,
        [switch]$EnableTestOverrideContext
    )

    $stdoutPath = Join-Path $SandboxRoot ("$CaseName.stdout.log")
    $stderrPath = Join-Path $SandboxRoot ("$CaseName.stderr.log")

    $argLine = '-NoProfile -File "{0}" -ValidateOnly -Configuration "{1}" -Runtime "{2}" -IsccPath "{3}"' -f $PackagerScriptPath, $Configuration, $Runtime, $CompilerPath
    if (-not [string]::IsNullOrWhiteSpace($ApprovalRecordPath)) {
        $argLine = "$argLine -ApprovalRecordPath `"$ApprovalRecordPath`""
    }

    if ($PSBoundParameters.ContainsKey('Version')) {
        $argLine = "$argLine -Version `"$Version`""
    }

    if ($AllowOverride) {
        $argLine = "$argLine -AllowTestApprovalRecordOverride"
    }

    $overrideEnvName = 'BROCHOOS_TEST_APPROVAL_OVERRIDE'
    $previousOverrideEnv = [Environment]::GetEnvironmentVariable($overrideEnvName, 'Process')

    try {
        if ($EnableTestOverrideContext) {
            [Environment]::SetEnvironmentVariable($overrideEnvName, '1', 'Process')
        }
        else {
            [Environment]::SetEnvironmentVariable($overrideEnvName, $null, 'Process')
        }

        $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -Wait -PassThru
    }
    finally {
        [Environment]::SetEnvironmentVariable($overrideEnvName, $previousOverrideEnv, 'Process')
    }

    $output = ((Get-Content -LiteralPath $stdoutPath -Raw -Encoding UTF8) + [Environment]::NewLine + (Get-Content -LiteralPath $stderrPath -Raw -Encoding UTF8))

    return @{
        ExitCode = [int]$proc.ExitCode
        Output = $output
        Stdout = $stdoutPath
        Stderr = $stderrPath
    }
}

function Get-CompileArgsForTest {
    # Evaluates the packager's $compileArgs expression with controlled inputs.
    # This catches the comma/`+` precedence trap: unparenthesized concatenations collapse the whole
    # array literal into a single element.
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagerScriptPath,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$PublishDir,

        [Parameter(Mandatory = $true)]
        [string]$InstallerScriptPath
    )

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($PackagerScriptPath, [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) "Packager script has PowerShell parse errors: $($parseErrors -join '; ')"

    $assignments = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$compileArgs' }, $true))
    Assert-True ($assignments.Count -eq 1) "Expected exactly one `$compileArgs assignment in the packager, found $($assignments.Count)"

    $expressionText = $assignments[0].Right.Extent.Text

    # The evaluated expression references $Version, $publishDir and $installerScript. PowerShell
    # resolves those dynamically, so bind locals with exactly those names; otherwise the probe could
    # silently read unrelated variables from an outer scope.
    $publishDir = $PublishDir
    $installerScript = $InstallerScriptPath

    # Returned inside a hashtable: PowerShell unrolls arrays returned from functions, which would
    # otherwise hide whether the expression produced one collapsed element or three distinct ones.
    $result = @(& ([scriptblock]::Create($expressionText)))
    return @{ Count = $result.Count; Items = $result }
}

function Get-PackagerFunctionText {
    # Extracts top-level function definitions from the packager by name. The packager is a standalone
    # script that executes on load, so its helpers are evaluated through the AST instead of dot-sourcing.
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagerScriptPath,

        [Parameter(Mandatory = $true)]
        [string[]]$FunctionNames
    )

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($PackagerScriptPath, [ref]$tokens, [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) "Packager script has PowerShell parse errors: $($parseErrors -join '; ')"

    $allFunctions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))

    $definitions = @()
    foreach ($functionName in $FunctionNames) {
        $matches = @($allFunctions | Where-Object { $_.Name -eq $functionName })
        Assert-True ($matches.Count -eq 1) "Expected exactly one '$functionName' definition in the packager, found $($matches.Count)"
        $definitions += $matches[0].Extent.Text
    }

    return $definitions
}

function Invoke-IsccVersionLookupForTest {
    # Runs the packager's registry version lookup against a simulated registry image. The live Inno
    # Setup uninstall entries on this machine carry InstallLocation, so the missing-InstallLocation
    # branch can only be reached with stubbed registry cmdlets; the machine registry is never touched.
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagerScriptPath,

        [Parameter(Mandatory = $true)]
        [string]$CompilerPath,

        [Parameter(Mandatory = $true)]
        [hashtable]$RegistryImage
    )

    $definitions = @(Get-PackagerFunctionText -PackagerScriptPath $PackagerScriptPath -FunctionNames @('Get-NormalizedSemver', 'Get-RegistryStringValue', 'Get-IsccVersionFromRegistry'))

    $registryStub = @'
function Test-Path {
    param([string]$LiteralPath, $PathType, $ErrorAction)
    if ($RegistryImage.ContainsKey($LiteralPath)) { return $true }
    foreach ($imageKey in @($RegistryImage.Keys)) {
        if ($imageKey.StartsWith($LiteralPath + '\', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-ChildItem {
    param([string]$LiteralPath, $ErrorAction)
    $entries = @()
    foreach ($imageKey in @($RegistryImage.Keys)) {
        if ($imageKey.StartsWith($LiteralPath + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            $entries += [pscustomobject]@{ PSPath = $imageKey }
        }
    }
    return @($entries)
}

function Get-ItemProperty {
    param([string]$LiteralPath, $ErrorAction)
    if (-not $RegistryImage.ContainsKey($LiteralPath)) { return $null }
    return [pscustomobject]$RegistryImage[$LiteralPath]
}
'@

    $quotedCompilerPath = "'" + $CompilerPath.Replace("'", "''") + "'"
    $scriptText = (@('param($RegistryImage)', $registryStub) + $definitions + @('Get-IsccVersionFromRegistry -CompilerPath ' + $quotedCompilerPath)) -join [Environment]::NewLine

    return (& ([scriptblock]::Create($scriptText)) $RegistryImage)
}

function Test-SafeTokenForTest {
    # Evaluates the packager's Test-SafeToken for one value. Unit-level coverage is required for
    # single-dash tokens: the PowerShell host itself rejects '-o' before the script is reached.
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagerScriptPath,

        [AllowEmptyString()]
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $definitions = @(Get-PackagerFunctionText -PackagerScriptPath $PackagerScriptPath -FunctionNames @('Test-SafeToken'))
    $quotedValue = "'" + $Value.Replace("'", "''") + "'"
    $scriptText = $definitions[0] + [Environment]::NewLine + 'Test-SafeToken -Value ' + $quotedValue

    return [bool](& ([scriptblock]::Create($scriptText)))
}

function Invoke-IsccCompileForTest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CompilerPath,

        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,

        [Parameter(Mandatory = $true)]
        [string]$SourceDir,

        [Parameter(Mandatory = $true)]
        [string]$AppVersion,

        [Parameter(Mandatory = $true)]
        [string]$WorkingRoot
    )

    $stdoutPath = Join-Path $WorkingRoot 'iscc-compile.stdout.log'
    $stderrPath = Join-Path $WorkingRoot 'iscc-compile.stderr.log'

    $arguments = @(
        ('/DAppPublishDir=' + $SourceDir),
        ('/DAppVersion=' + $AppVersion),
        $ScriptPath
    )

    $process = Start-Process -FilePath $CompilerPath -ArgumentList $arguments -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -Wait -PassThru -NoNewWindow

    $output = ''
    if (Test-Path -LiteralPath $stdoutPath) {
        $output += [string](Get-Content -LiteralPath $stdoutPath -Raw -Encoding UTF8)
    }

    if (Test-Path -LiteralPath $stderrPath) {
        $output += [Environment]::NewLine + [string](Get-Content -LiteralPath $stderrPath -Raw -Encoding UTF8)
    }

    return @{
        ExitCode = [int]$process.ExitCode
        Output = $output
    }
}

$sandboxRoot = $null

try {
    Assert-True (Test-Path -LiteralPath $installerScript -PathType Leaf) "Installer script not found: $installerScript"
    Assert-True (Test-Path -LiteralPath $packagerScript -PathType Leaf) "Packager script not found: $packagerScript"
    Assert-True (Test-Path -LiteralPath $ownerApprovalRecord -PathType Leaf) "Owner approval record not found: $ownerApprovalRecord"

    $iss = Get-Content -LiteralPath $installerScript -Raw -Encoding UTF8
    Assert-Contains -Text $iss -Needle 'PrivilegesRequired=lowest' -Name 'per-user-privilege'
    Assert-Contains -Text $iss -Needle 'DefaultDirName={localappdata}\Programs\BroChoos' -Name 'per-user-directory'
    Assert-Contains -Text $iss -Needle '--unregister' -Name 'uninstall-unregister'
    Assert-Contains -Text $iss -Needle 'attempting recovery cycle' -Name 'registration-recovery-log'
    Assert-Contains -Text $iss -Needle '#define AppPublishDir' -Name 'publish-dir-define'
    Assert-Contains -Text $iss -Needle 'Source: "{#AppPublishDir}\*"' -Name 'publish-source-uses-define'
    Assert-Contains -Text $iss -Needle 'skipifdoesntexist' -Name 'uninstall-run-skip-if-missing'

    # D1: [Code] is Pascal; a ';' line comment there aborts compilation ("Unknown identifier").
    $codeSectionMatch = [regex]::Match($iss, '(?ims)^\s*\[Code\]\s*(?<body>.*)\z')
    Assert-True $codeSectionMatch.Success 'Installer script must contain a [Code] section'
    $codeBody = [string]$codeSectionMatch.Groups['body'].Value
    $pascalCommentLines = @($codeBody -split "`n" | Where-Object { $_ -match '^\s*;' })
    Assert-True ($pascalCommentLines.Count -eq 0) "Installer [Code] section must not use ';' line comments (Pascal requires '//'): $($pascalCommentLines -join ' | ')"

    # D5: AppVersion must be overridable via /DAppVersion.
    Assert-True ($iss -match '(?im)^\s*#ifndef\s+AppVersion\s*$') 'Installer script must guard #define AppVersion with #ifndef so /DAppVersion is honored'

    # D7: [UninstallRun] requires RunOnceId.
    Assert-Contains -Text $iss -Needle 'RunOnceId:' -Name 'uninstall-run-once-id'

    # D8: ISCC output must not land untracked inside the repo tree.
    Assert-True ($iss -match '(?im)^\s*OutputDir\s*=\s*\S+') 'Installer script must define OutputDir so setup output does not default next to the script'

    # D9: release payload hygiene.
    Assert-True ($iss -match '(?im)Excludes:\s*"[^"]*\*\.pdb') 'Installer [Files] must exclude *.pdb from the release payload'

    # The .iss default publish path must not be a stale/mistaken layout: it must include the
    # runtime identifier segment and must not point at a pre-publish output folder.
    # The script escapes backslashes ('\\'), so normalize before matching to keep this non-vacuous.
    $issPathNormalized = $iss.Replace('\\', '\')
    Assert-NotContains -Text $issPathNormalized -Needle 'bin\Release\net10.0-windows10.0.19041.0\publish' -Name 'forbid-runtime-less-publish-path'
    Assert-Contains -Text $issPathNormalized -Needle 'net10.0-windows10.0.19041.0\win-x64\publish' -Name 'publish-dir-includes-runtime-identifier'

    Assert-True ($iss -notmatch '(?im)^\s*PrivilegesRequiredOverridesAllowed\s*=') 'Per-user installer must not allow privilege override path'

    $runSectionMatch = [regex]::Match($iss, '(?ims)^\s*\[Run\]\s*(?<body>.*?)(?=^\s*\[[^\]]+\]|\z)')
    if ($runSectionMatch.Success) {
        $runBody = [string]$runSectionMatch.Groups['body'].Value
        Assert-True ($runBody -notmatch '(?i)--register') 'Installer must not use [Run] registration path; registration is owned by [Code] only'
    }

    $registerExecMatches = [regex]::Matches($iss, [regex]::Escape('RunRegistration(''--register'''))
    Assert-True ($registerExecMatches.Count -eq 2) 'Installer must have exactly one primary register and one bounded recovery register attempt'

    Assert-Contains -Text $iss -Needle 'RaiseException(' -Name 'registration-fail-closed'
    Assert-Contains -Text $iss -Needle 'review the setup log for details' -Name 'registration-fail-closed-message-actionable'

    $packager = Get-Content -LiteralPath $packagerScript -Raw -Encoding UTF8
    Assert-Contains -Text $packager -Needle 'toolchain.version' -Name 'toolchain-version-read-from-owner-record'
    Assert-Contains -Text $packager -Needle 'Inno Setup version mismatch' -Name 'toolchain-version-mismatch-error'
    Assert-Contains -Text $packager -Needle 'FileVersionInfo' -Name 'toolchain-version-fileversioninfo'
    Assert-Contains -Text $packager -Needle 'AllowTestApprovalRecordOverride' -Name 'test-override-switch'
    Assert-Contains -Text $packager -Needle 'Blocked non-default owner approval record path' -Name 'override-fail-closed-message'
    Assert-Contains -Text $packager -Needle 'Test-IsccIdentity' -Name 'compiler-identity-guard'
    Assert-Contains -Text $packager -Needle 'Get-AuthenticodeSignature' -Name 'compiler-signature-verification'
    Assert-Contains -Text $packager -Needle 'approvedSignerAllowlist' -Name 'compiler-signer-allowlist-present'
    Assert-Contains -Text $packager -Needle 'signer certificate mismatch' -Name 'compiler-signer-mismatch-diagnostic'
    Assert-True ($packager.IndexOf('& powershell @approvalArgs | Out-Null', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) 'Owner approval validator diagnostics must not be suppressed with Out-Null'
    Assert-Contains -Text $packager -Needle '/DAppPublishDir=' -Name 'compile-app-publish-dir-define'
    Assert-Contains -Text $packager -Needle 'missing a valid toolchain.version' -Name 'missing-toolchain-version-message'
    Assert-Contains -Text $packager -Needle 'Configuration contains unsupported characters' -Name 'configuration-token-validation-message'
    Assert-Contains -Text $packager -Needle 'Runtime contains unsupported characters' -Name 'runtime-token-validation-message'
    Assert-Contains -Text $packager -Needle 'Version contains unsupported characters' -Name 'version-token-validation-message'

    # D4: the command-line compiler discriminator must be enforced, not just Inno Setup metadata.
    Assert-Contains -Text $packager -Needle 'Inno Setup Command-Line Compiler' -Name 'cli-compiler-discriminator'

    # D3: no fabricated version may be derived from a major-only banner.
    Assert-True ($packager -notmatch "'\.0\.0'") 'Packager must not fabricate a X.0.0 version from a major-only banner'

    # D2: compile arguments must be parenthesized so the array does not collapse into one element.
    $compileArgsProbeResult = Get-CompileArgsForTest -PackagerScriptPath $packagerScript -Version '1.2.3' -PublishDir 'C:\probe publish dir' -InstallerScriptPath 'C:\probe\script.iss'
    $compileArgsProbe = @($compileArgsProbeResult.Items)
    Assert-True ($compileArgsProbeResult.Count -eq 3) "Compile arguments must resolve to exactly 3 elements, got $($compileArgsProbeResult.Count): $($compileArgsProbe -join ' | ')"
    Assert-True ($compileArgsProbe[0] -eq '/DAppVersion=1.2.3') "First compile argument must be the version define only, got '$($compileArgsProbe[0])'"
    Assert-True ($compileArgsProbe[1] -eq '/DAppPublishDir=C:\probe publish dir') "Second compile argument must be the publish dir define without embedded quotes, got '$($compileArgsProbe[1])'"
    Assert-True ($compileArgsProbe[2] -eq 'C:\probe\script.iss') "Third compile argument must be the installer script path, got '$($compileArgsProbe[2])'"
    foreach ($compileArg in $compileArgsProbe) {
        Assert-True ($compileArg -notmatch '"') "Compile arguments must not embed quotes (Windows PowerShell does not escape them): '$compileArg'"
    }

    $sandboxRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('brochoos-w10.1-installer-tests-' + [guid]::NewGuid().ToString('N'))
    if (-not (Test-Path -LiteralPath $sandboxRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $sandboxRoot | Out-Null
    }

    $blockedOverrideRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-blocked-override.json'
    $missingVersionRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-missing-version.json'
    $invalidVersionRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-invalid-version.json'
    $priorStatusMismatchRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-prior-status-mismatch.json'
    $mismatchRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-mismatch.json'
    $passRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-pass.json'

    $ownerRecord = Get-Content -LiteralPath $ownerApprovalRecord -Raw -Encoding UTF8 | ConvertFrom-Json

    $ownerRecord.toolchain.version = '6.4.3'
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $blockedOverrideRecordPath -Encoding UTF8

    $isccProbePath = Join-Path $PSHOME 'powershell.exe'
    Assert-True (Test-Path -LiteralPath $isccProbePath -PathType Leaf) "Probe binary not found for deterministic identity-guard test: $isccProbePath"

    $isccRealPath = Resolve-IsccCompilerPath
    if ([string]::IsNullOrWhiteSpace($isccRealPath) -or -not (Test-Path -LiteralPath $isccRealPath -PathType Leaf)) {
        throw 'environment precondition missing: ISCC.exe is required to validate the true version-mismatch branch after identity succeeds.'
    }

    $blockedOverrideResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $blockedOverrideRecordPath -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-blocked-override'
    Assert-True ($blockedOverrideResult.ExitCode -ne 0) 'Non-canonical approval override must fail-closed without explicit test switch'
    Assert-Contains -Text $blockedOverrideResult.Output -Needle 'Blocked non-default owner approval record path' -Name 'blocked-override-diagnostic'
    Assert-Contains -Text $blockedOverrideResult.Output -Needle 'AllowTestApprovalRecordOverride' -Name 'blocked-override-guidance'

    $overrideWithoutContextResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $blockedOverrideRecordPath -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-override-without-context' -AllowOverride
    Assert-True ($overrideWithoutContextResult.ExitCode -ne 0) 'Override switch must fail-closed outside explicit test context'
    Assert-Contains -Text $overrideWithoutContextResult.Output -Needle 'AllowTestApprovalRecordOverride is test-only' -Name 'override-context-required-message'
    Assert-Contains -Text $overrideWithoutContextResult.Output -Needle 'BROCHOOS_TEST_APPROVAL_OVERRIDE=1' -Name 'override-context-required-guidance'

    $ownerRecord.toolchain.version = ''
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $missingVersionRecordPath -Encoding UTF8

    $missingVersionResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $missingVersionRecordPath -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-missing-version' -AllowOverride -EnableTestOverrideContext
    Assert-True ($missingVersionResult.ExitCode -ne 0) 'Missing toolchain.version must fail-closed with non-zero exit code'
    Assert-Contains -Text $missingVersionResult.Output -Needle 'missing a valid toolchain.version' -Name 'missing-version-diagnostic'
    Assert-Contains -Text $missingVersionResult.Output -Needle 'Set toolchain.version to a semantic version' -Name 'missing-version-guidance'

    $ownerRecord.toolchain.version = 'invalid'
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $invalidVersionRecordPath -Encoding UTF8

    $invalidVersionResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $invalidVersionRecordPath -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-invalid-version' -AllowOverride -EnableTestOverrideContext
    Assert-True ($invalidVersionResult.ExitCode -ne 0) 'Invalid toolchain.version must fail-closed with non-zero exit code'
    Assert-Contains -Text $invalidVersionResult.Output -Needle 'missing a valid toolchain.version' -Name 'invalid-version-diagnostic'

    $ownerRecord.toolchain.version = '6.4.3'
    $ownerRecord.resolutionState.priorStatus = 'CLOSED'
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $priorStatusMismatchRecordPath -Encoding UTF8

    $priorStatusMismatchResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $priorStatusMismatchRecordPath -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-prior-status-mismatch' -AllowOverride -EnableTestOverrideContext
    Assert-True ($priorStatusMismatchResult.ExitCode -ne 0) 'RequiredPriorStatus mismatch must fail-closed with non-zero exit code'
    Assert-Contains -Text $priorStatusMismatchResult.Output -Needle 'resolution.prior-status.mismatch' -Name 'prior-status-mismatch-validator-diagnostic-visible'
    Assert-Contains -Text $priorStatusMismatchResult.Output -Needle 'Owner approval record validation failed for W10.1 preflight' -Name 'prior-status-mismatch-controlled-packager-message'

    $ownerRecord.resolutionState.priorStatus = 'OPEN'

    # Independent expectation: read the installed version from the Inno Setup uninstall registry entry.
    # This deliberately does not reuse the packager's own detection logic (no FileVersionInfo, no
    # banner parsing), so a fabricated/derived value cannot make this assertion self-fulfilling.
    $installedInnoRecord = Get-InstalledInnoSetupRecordForTest
    Assert-True ($null -ne $installedInnoRecord) 'environment precondition missing: the Inno Setup uninstall registry entry (Uninstall\Inno Setup 6_is1 under HKCU, HKLM or HKLM\WOW6432Node) with DisplayVersion is required to independently validate version detection.'
    $isccExpectedVersion = [string]$installedInnoRecord.Version
    Assert-True ($isccExpectedVersion -match '^\d+\.\d+\.\d+$') "Registry DisplayVersion must be a three-part version, got '$isccExpectedVersion'."

    $isccRealVersion = $isccExpectedVersion
    Assert-True ($null -ne $isccRealVersion) "Could not determine the expected ISCC version independently of the packager: $isccRealPath"

    $ownerRecord.toolchain.version = $isccRealVersion
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $passRecordPath -Encoding UTF8

    $defaultApprovalResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-default-approval-canonical'
    Assert-True ($defaultApprovalResult.ExitCode -ne 0) 'Canonical default approval path should continue into compiler checks in validate-only mode (using deterministic non-ISCC probe)'
    Assert-NotContains -Text $defaultApprovalResult.Output -Needle 'Blocked non-default owner approval record path' -Name 'default-approval-no-override-block'
    Assert-Contains -Text $defaultApprovalResult.Output -Needle 'Selected compiler is not accepted' -Name 'default-approval-reaches-identity-check'

    $ownerRecord.toolchain.version = '0.0.0'
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $mismatchRecordPath -Encoding UTF8

    $mismatchResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $mismatchRecordPath -CompilerPath $isccRealPath -SandboxRoot $sandboxRoot -CaseName 'packager-mismatch-version' -AllowOverride -EnableTestOverrideContext
    Assert-True ($mismatchResult.ExitCode -ne 0) 'Version mismatch path must fail-closed with non-zero exit code after identity passes'
    Assert-Contains -Text $mismatchResult.Output -Needle 'Inno Setup version mismatch' -Name 'compiler-version-mismatch-runtime-message'

    # A non-ISCC binary must be rejected by the identity guard even when the approval record's pinned
    # version matches the independently known installed version (so the failure cannot be attributed
    # to a version mismatch).
    $ownerRecord.toolchain.version = $isccExpectedVersion
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $passRecordPath -Encoding UTF8

    $passResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $passRecordPath -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-pass' -AllowOverride -EnableTestOverrideContext
    Assert-True ($passResult.ExitCode -ne 0) 'Identity guard must fail for non-ISCC probe even when semver matches'
    Assert-Contains -Text $passResult.Output -Needle 'Selected compiler is not accepted' -Name 'identity-guard-non-iscc'
    Assert-Contains -Text $passResult.Output -Needle 'provide the ISCC.exe binary installed by Inno Setup' -Name 'identity-guard-guidance'

    # D4 behavioral proof: a renamed copy of the Inno Setup GUI compiler (Compil32.exe -> ISCC.exe)
    # carries valid Inno Setup metadata and a valid Authenticode signature from the allowlisted
    # signer, so only the command-line discriminator can reject it.
    $guiCompilerPath = Join-Path (Split-Path -Parent $isccRealPath) 'Compil32.exe'
    Assert-True (Test-Path -LiteralPath $guiCompilerPath -PathType Leaf) "environment precondition missing: Compil32.exe is required next to the selected ISCC.exe to prove the command-line discriminator: $guiCompilerPath"

    $guiCompilerSignature = Get-AuthenticodeSignature -FilePath $guiCompilerPath
    Assert-True ($guiCompilerSignature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) 'environment precondition missing: Compil32.exe must carry a valid Authenticode signature for this discriminator test to be meaningful'

    $renamedGuiCompilerPath = Join-Path $sandboxRoot 'ISCC.exe'
    Copy-Item -LiteralPath $guiCompilerPath -Destination $renamedGuiCompilerPath -Force

    $renamedGuiResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $passRecordPath -CompilerPath $renamedGuiCompilerPath -SandboxRoot $sandboxRoot -CaseName 'packager-renamed-gui-compiler' -AllowOverride -EnableTestOverrideContext
    Assert-True ($renamedGuiResult.ExitCode -ne 0) 'Renamed GUI compiler (Compil32.exe -> ISCC.exe) must be rejected by the identity guard'
    Assert-Contains -Text $renamedGuiResult.Output -Needle 'Selected compiler is not accepted' -Name 'renamed-gui-compiler-rejected'
    Assert-Contains -Text $renamedGuiResult.Output -Needle 'command-line compiler' -Name 'renamed-gui-compiler-discriminator-message'

    $ownerRecord.toolchain.version = $isccRealVersion
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $passRecordPath -Encoding UTF8

    $preflightPassResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $passRecordPath -CompilerPath $isccRealPath -SandboxRoot $sandboxRoot -CaseName 'packager-preflight-pass' -AllowOverride -EnableTestOverrideContext
    Assert-True ($preflightPassResult.ExitCode -eq 0) 'Validate-only preflight should succeed for approved record + trusted ISCC toolchain fixture'
    Assert-Contains -Text $preflightPassResult.Output -Needle 'W10.1_PACKAGE_PREFLIGHT=PASS' -Name 'validate-only-preflight-pass-token'

    $tamperedIsccPath = Join-Path $sandboxRoot 'ISCC.exe'
    Copy-Item -LiteralPath $isccRealPath -Destination $tamperedIsccPath -Force
    $tamperBytes = [System.Text.Encoding]::ASCII.GetBytes('BROCHOOS-TAMPER')
    $tamperStream = [System.IO.File]::Open($tamperedIsccPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    try {
        $tamperStream.Write($tamperBytes, 0, $tamperBytes.Length)
    }
    finally {
        $tamperStream.Dispose()
    }

    $tamperedSignatureResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $tamperedIsccPath -SandboxRoot $sandboxRoot -CaseName 'packager-tampered-signature'
    Assert-True ($tamperedSignatureResult.ExitCode -ne 0) 'Tampered ISCC binary must fail-closed on Authenticode verification'
    Assert-Contains -Text $tamperedSignatureResult.Output -Needle 'Authenticode signature validation failed' -Name 'tampered-signature-diagnostic'

    $invalidConfigurationResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-invalid-configuration' -Configuration '..\Release'
    Assert-True ($invalidConfigurationResult.ExitCode -ne 0) 'Unsafe Configuration token must fail-closed before compilation'
    Assert-Contains -Text $invalidConfigurationResult.Output -Needle 'Configuration contains unsupported characters' -Name 'invalid-configuration-diagnostic'

    $invalidRuntimeResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-invalid-runtime' -Runtime '..\evil'
    Assert-True ($invalidRuntimeResult.ExitCode -ne 0) 'Unsafe Runtime token must fail-closed before compilation'
    Assert-Contains -Text $invalidRuntimeResult.Output -Needle 'Runtime contains unsupported characters' -Name 'invalid-runtime-diagnostic'

    $invalidVersionTokenResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-invalid-version-token' -Version '..\evil'
    Assert-True ($invalidVersionTokenResult.ExitCode -ne 0) 'Unsafe Version token must fail-closed before publish/compile'
    Assert-Contains -Text $invalidVersionTokenResult.Output -Needle 'Version contains unsupported characters' -Name 'invalid-version-token-diagnostic'

    # F2: option-like tokens are forwarded to native command lines (dotnet/ISCC), where a leading '-'
    # is parsed as a switch and shifts the following arguments (for example -Configuration --nologo).
    # Unit coverage is required for single-dash forms: the PowerShell host rejects '-o' itself before
    # the packager is reached, so only an in-process call can exercise that branch.
    foreach ($safeToken in @('Release', 'win-x64', '0.0.0-dev', '1.2.3')) {
        Assert-True (Test-SafeTokenForTest -PackagerScriptPath $packagerScript -Value $safeToken) "Legitimate token '$safeToken' must still be accepted by Test-SafeToken"
    }

    foreach ($optionLikeToken in @('--output', '-o', '--self-contained', '-Release', '--', '-')) {
        Assert-True (-not (Test-SafeTokenForTest -PackagerScriptPath $packagerScript -Value $optionLikeToken)) "Option-like token '$optionLikeToken' must be rejected by Test-SafeToken"
    }

    $optionLikeConfigurationResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-option-like-configuration' -Configuration '--nologo'
    Assert-True ($optionLikeConfigurationResult.ExitCode -ne 0) 'Option-like Configuration token must fail-closed before publish/compile'
    Assert-Contains -Text $optionLikeConfigurationResult.Output -Needle 'Configuration contains unsupported characters' -Name 'option-like-configuration-diagnostic'

    $optionLikeRuntimeResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-option-like-runtime' -Runtime '--self-contained'
    Assert-True ($optionLikeRuntimeResult.ExitCode -ne 0) 'Option-like Runtime token must fail-closed before publish/compile'
    Assert-Contains -Text $optionLikeRuntimeResult.Output -Needle 'Runtime contains unsupported characters' -Name 'option-like-runtime-diagnostic'

    $optionLikeVersionResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath '' -CompilerPath $isccProbePath -SandboxRoot $sandboxRoot -CaseName 'packager-option-like-version' -Version '--output'
    Assert-True ($optionLikeVersionResult.ExitCode -ne 0) 'Option-like Version token must fail-closed before publish/compile'
    Assert-Contains -Text $optionLikeVersionResult.Output -Needle 'Version contains unsupported characters' -Name 'option-like-version-diagnostic'

    # D3: version detection must match the independently read registry value and must never be derived
    # from the binary's FileVersionInfo (real ISCC builds report 0.0.0.0 there) or a major-only banner.
    $versionGateRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-independent-version.json'
    $ownerRecord.toolchain.version = $isccExpectedVersion
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $versionGateRecordPath -Encoding UTF8

    $independentVersionResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $versionGateRecordPath -CompilerPath $isccRealPath -SandboxRoot $sandboxRoot -CaseName 'packager-independent-version-gate' -AllowOverride -EnableTestOverrideContext
    Assert-True ($independentVersionResult.ExitCode -eq 0) "Version gate must accept the installed Inno Setup version read independently from the registry ('$isccExpectedVersion'); output: $($independentVersionResult.Output)"
    Assert-Contains -Text $independentVersionResult.Output -Needle 'W10.1_PACKAGE_PREFLIGHT=PASS' -Name 'independent-version-preflight-pass'

    $staleVersionRecordPath = Join-Path $sandboxRoot 'installer-toolchain-approval-stale-version.json'
    $ownerRecord.toolchain.version = '6.0.1'
    $ownerRecord | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $staleVersionRecordPath -Encoding UTF8

    if ($isccExpectedVersion -ne '6.0.1') {
        $staleVersionResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $staleVersionRecordPath -CompilerPath $isccRealPath -SandboxRoot $sandboxRoot -CaseName 'packager-stale-version-gate' -AllowOverride -EnableTestOverrideContext
        Assert-True ($staleVersionResult.ExitCode -ne 0) "Version gate must reject a pinned version that differs from the installed registry version '$isccExpectedVersion'"
        Assert-Contains -Text $staleVersionResult.Output -Needle 'Inno Setup version mismatch' -Name 'stale-version-mismatch-diagnostic'
    }

    # F1: a version may only be attributed to the compiler when an uninstall entry's InstallLocation
    # matches the compiler's own directory. Inno Setup 6 and 7 can be installed side by side, so a
    # version from a key that cannot be tied to the selected compiler (InstallLocation absent) must
    # never be returned: the lookup has to fail closed instead.
    # The live uninstall entries on this machine all carry InstallLocation, so the branch is reached
    # through a simulated registry image; the pre-change packager returns '6.7.2' here while the
    # location-matched control case returns the same value, which proves the simulation discriminates.
    $foreignCompilerDir = Join-Path $sandboxRoot 'unregistered-compiler-dir'
    $foreignCompilerPath = Join-Path $foreignCompilerDir 'ISCC.exe'

    $simulatedRegistryWithLocation = @{
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1' = @{ DisplayName = 'Inno Setup 6'; DisplayVersion = '6.7.2'; InstallLocation = $foreignCompilerDir }
    }
    $simulatedRegistryWithoutLocation = @{
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1' = @{ DisplayName = 'Inno Setup 6'; DisplayVersion = '6.7.2' }
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1' = @{ DisplayName = 'Inno Setup 7.0.0-beta'; DisplayVersion = '7.0.0-beta'; InstallLocation = 'C:\Program Files\Inno Setup 7' }
    }
    $simulatedRegistryWithOtherLocation = @{
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1' = @{ DisplayName = 'Inno Setup 6'; DisplayVersion = '6.7.2'; InstallLocation = 'C:\Somewhere Else\Inno Setup 6' }
    }

    $locationMatchedLookup = Invoke-IsccVersionLookupForTest -PackagerScriptPath $packagerScript -CompilerPath $foreignCompilerPath -RegistryImage $simulatedRegistryWithLocation
    Assert-True ($locationMatchedLookup -eq '6.7.2') "Location-matched uninstall entry must still supply its DisplayVersion, got '$locationMatchedLookup' (non-vacuity control for the fail-closed assertions below)"

    $missingLocationLookup = Invoke-IsccVersionLookupForTest -PackagerScriptPath $packagerScript -CompilerPath $foreignCompilerPath -RegistryImage $simulatedRegistryWithoutLocation
    Assert-True ($null -eq $missingLocationLookup) "A foreign compiler directory must not inherit the version of an uninstall entry whose InstallLocation is absent (fail-closed), got '$missingLocationLookup'"

    $otherLocationLookup = Invoke-IsccVersionLookupForTest -PackagerScriptPath $packagerScript -CompilerPath $foreignCompilerPath -RegistryImage $simulatedRegistryWithOtherLocation
    Assert-True ($null -eq $otherLocationLookup) "A foreign compiler directory must not inherit the version of an uninstall entry installed elsewhere, got '$otherLocationLookup'"

    # End-to-end fail-closed proof: the real, trusted ISCC.exe copied outside its installation
    # directory passes the identity guard but cannot be attributed a version, so packaging must stop
    # instead of proceeding with an unverifiable toolchain.
    $neutralCompilerDir = Join-Path $sandboxRoot 'neutral-compiler'
    New-Item -ItemType Directory -Path $neutralCompilerDir -Force | Out-Null
    $neutralCompilerPath = Join-Path $neutralCompilerDir 'ISCC.exe'
    Copy-Item -LiteralPath $isccRealPath -Destination $neutralCompilerPath -Force

    $unregisteredCompilerResult = Invoke-PackagerValidateOnly -PackagerScriptPath $packagerScript -ApprovalRecordPath $passRecordPath -CompilerPath $neutralCompilerPath -SandboxRoot $sandboxRoot -CaseName 'packager-unregistered-compiler-dir' -AllowOverride -EnableTestOverrideContext
    Assert-True ($unregisteredCompilerResult.ExitCode -ne 0) 'A trusted ISCC.exe located outside its installation directory must fail closed when no uninstall entry matches its directory'
    Assert-Contains -Text $unregisteredCompilerResult.Output -Needle 'Could not determine the Inno Setup version' -Name 'unregistered-compiler-dir-version-fail-closed'

    # D8: end-to-end compile proof. The real .iss is compiled by the real ISCC in a sandbox copy.
    # Missing ISCC is an explicit environment-precondition failure, never a silent pass.
    $compileSandboxRoot = Join-Path $sandboxRoot 'compile'
    $compileScriptDir = Join-Path $compileSandboxRoot 'packaging\BroChoos.Installer'
    $compilePayloadDir = Join-Path $compileSandboxRoot 'payload'
    New-Item -ItemType Directory -Path $compileScriptDir -Force | Out-Null
    New-Item -ItemType Directory -Path $compilePayloadDir -Force | Out-Null

    Copy-Item -LiteralPath $installerScript -Destination (Join-Path $compileScriptDir 'BroChoos.per-user.iss') -Force
    # SetupIconFile is relative to the script (..\..\src\BroChoos.App\Assets\brochoos.ico), so the
    # sandbox mirrors that location.
    $compileIconDir = Join-Path $compileSandboxRoot 'src\BroChoos.App\Assets'
    New-Item -ItemType Directory -Path $compileIconDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src\BroChoos.App\Assets\brochoos.ico') -Destination $compileIconDir -Force
    Set-Content -LiteralPath (Join-Path $compilePayloadDir 'BroChoos.exe') -Value 'sandbox-payload-stub' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $compilePayloadDir 'BroChoos.pdb') -Value 'sandbox-symbol-stub' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $compilePayloadDir 'BroChoos.xml') -Value 'sandbox-doc-stub' -Encoding ASCII

    $compileVersion = '9.9.9'
    $compileResult = Invoke-IsccCompileForTest -CompilerPath $isccRealPath -ScriptPath (Join-Path $compileScriptDir 'BroChoos.per-user.iss') -SourceDir $compilePayloadDir -AppVersion $compileVersion -WorkingRoot $compileSandboxRoot
    Assert-True ($compileResult.ExitCode -eq 0) "ISCC compile of the real installer script must succeed (exit 0), got exit $($compileResult.ExitCode). ISCC output: $($compileResult.Output)"
    Assert-Contains -Text $compileResult.Output -Needle 'Successful compile' -Name 'iscc-successful-compile'

    $compiledSetupPath = Join-Path $compileSandboxRoot "artifacts\installer\BroChoos-Picker-Setup-$compileVersion-x64.exe"
    Assert-True (Test-Path -LiteralPath $compiledSetupPath -PathType Leaf) "Compiled setup executable not found at the OutputDir target: $compiledSetupPath"

    # D5: /DAppVersion must reach the produced setup executable's version resource.
    $compiledSetupVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($compiledSetupPath)
    $compiledProductVersion = ([string]$compiledSetupVersionInfo.ProductVersion).Trim()
    Assert-True ($compiledProductVersion -eq $compileVersion) "Compiled setup ProductVersion must equal /DAppVersion '$compileVersion', got '$compiledProductVersion'"

    # D9: excluded payload artifacts must not be embedded in the setup.
    $compileLogText = [string]$compileResult.Output
    Assert-True ($compileLogText -notmatch [regex]::Escape('BroChoos.pdb')) 'Excluded *.pdb payload file must not be compressed into the setup'
    Assert-True ($compileLogText -notmatch [regex]::Escape('BroChoos.xml')) 'Excluded *.xml payload file must not be compressed into the setup'
    Assert-True ($compileLogText -match [regex]::Escape('BroChoos.exe')) 'Payload BroChoos.exe must be compressed into the setup'

    'PASS: W10.1 per-user installer slice tests'
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($sandboxRoot) -and (Test-Path -LiteralPath $sandboxRoot -PathType Container)) {
        Remove-Item -LiteralPath $sandboxRoot -Recurse -Force
    }
}
