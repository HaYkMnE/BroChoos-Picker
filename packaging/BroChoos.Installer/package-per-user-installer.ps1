[CmdletBinding()]
param(
    [switch]$ValidateOnly,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.0.0-dev',
    [string]$ApprovalRecordPath,
    [switch]$AllowTestApprovalRecordOverride,
    [string]$IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$installerScript = Join-Path $PSScriptRoot 'BroChoos.per-user.iss'
$canonicalApprovalRecord = Join-Path $repoRoot 'docs\decisions\owner-rulings\installer-toolchain-approval.json'
$approvalRecord = $canonicalApprovalRecord
$approvalValidator = Join-Path $repoRoot 'tools\validate-installer-toolchain-approval.ps1'
$approvedSignerAllowlist = @(
    @{ CommonName = 'Pyrsys B.V.'; Thumbprint = 'E0AB19C8D38CBF9C44709925122A7A02F8C70CB7' }
)
$publishDir = Join-Path $repoRoot "src\BroChoos.App\bin\$Configuration\net10.0-windows10.0.19041.0\$Runtime\publish"

function Test-SafeToken {
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    # Option-like tokens are rejected: these values are forwarded to native command lines (dotnet/ISCC),
    # where a leading '-' would be parsed as a switch and shift the following arguments.
    if ($Value.StartsWith('-')) {
        return $false
    }

    if ($Value.Contains('..')) {
        return $false
    }

    if ($Value.IndexOfAny(@([char]'\', [char]'/', [char]':', [char]'"', [char]'''', [char]0)) -ge 0) {
        return $false
    }

    return ($Value -match '^[A-Za-z0-9._-]+$')
}

if (-not (Test-SafeToken -Value $Configuration)) {
    [Console]::Error.WriteLine("Configuration contains unsupported characters: '$Configuration'.")
    [Console]::Error.WriteLine('Use a simple build token (letters, digits, dot, underscore, dash) without path separators or traversal sequences.')
    exit 1
}

if (-not (Test-SafeToken -Value $Runtime)) {
    [Console]::Error.WriteLine("Runtime contains unsupported characters: '$Runtime'.")
    [Console]::Error.WriteLine('Use a Runtime Identifier token (for example win-x64) without path separators or traversal sequences.')
    exit 1
}

if (-not (Test-SafeToken -Value $Version)) {
    [Console]::Error.WriteLine("Version contains unsupported characters: '$Version'.")
    [Console]::Error.WriteLine('Use a simple version token (letters, digits, dot, underscore, dash) without path separators or traversal sequences.')
    exit 1
}

if (-not [string]::IsNullOrWhiteSpace($ApprovalRecordPath)) {
    $canonicalApprovalFullPath = [System.IO.Path]::GetFullPath($canonicalApprovalRecord)
    $requestedApprovalFullPath = [System.IO.Path]::GetFullPath($ApprovalRecordPath)
    $isCanonicalRequest = [string]::Equals($canonicalApprovalFullPath, $requestedApprovalFullPath, [System.StringComparison]::OrdinalIgnoreCase)

    if (-not $isCanonicalRequest -and -not $AllowTestApprovalRecordOverride) {
        [Console]::Error.WriteLine("Blocked non-default owner approval record path: '$ApprovalRecordPath'.")
        [Console]::Error.WriteLine('Use the default owner approval record, or rerun with -AllowTestApprovalRecordOverride for test-only execution.')
        exit 1
    }

    if (-not $isCanonicalRequest -and $AllowTestApprovalRecordOverride) {
        $testOverrideEnvName = 'BROCHOOS_TEST_APPROVAL_OVERRIDE'
        $testOverrideEnvValue = [Environment]::GetEnvironmentVariable($testOverrideEnvName, 'Process')
        $isTestOverrideContextEnabled = -not [string]::IsNullOrWhiteSpace($testOverrideEnvValue) -and [string]::Equals($testOverrideEnvValue.Trim(), '1', [System.StringComparison]::Ordinal)

        if (-not $ValidateOnly -or -not $isTestOverrideContextEnabled) {
            [Console]::Error.WriteLine('AllowTestApprovalRecordOverride is test-only and blocked outside explicit test context.')
            [Console]::Error.WriteLine('Requirements: -ValidateOnly and process env BROCHOOS_TEST_APPROVAL_OVERRIDE=1.')
            exit 1
        }
    }

    $approvalRecord = $requestedApprovalFullPath
}

function Get-NormalizedSemver {
    param(
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $match = [regex]::Match($Text.Trim(), '^(\d+\.\d+\.\d+)')
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $null
}

function Test-ThreePartSemver {
    param(
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    return ($Text.Trim() -match '^\d+\.\d+\.\d+$')
}

function Get-RegistryStringValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$KeyPath,

        [Parameter(Mandatory = $true)]
        [string]$ValueName
    )

    $key = Get-ItemProperty -LiteralPath $KeyPath -ErrorAction SilentlyContinue
    if ($null -eq $key) {
        return $null
    }

    $property = $key.PSObject.Properties[$ValueName]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $null
    }

    return ([string]$property.Value).Trim()
}

function Get-IsccVersionFromRegistry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CompilerPath
    )

    $compilerDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($CompilerPath))
    if ([string]::IsNullOrWhiteSpace($compilerDirectory)) {
        return $null
    }

    $compilerDirectory = $compilerDirectory.TrimEnd('\')

    $uninstallRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    foreach ($root in $uninstallRoots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            $displayName = Get-RegistryStringValue -KeyPath $key.PSPath -ValueName 'DisplayName'
            if ([string]::IsNullOrWhiteSpace($displayName) -or $displayName -notmatch '(?i)Inno\s+Setup') {
                continue
            }

            $displayVersion = Get-NormalizedSemver -Text (Get-RegistryStringValue -KeyPath $key.PSPath -ValueName 'DisplayVersion')
            if ($null -eq $displayVersion) {
                continue
            }

            $installLocation = Get-RegistryStringValue -KeyPath $key.PSPath -ValueName 'InstallLocation'
            if ([string]::IsNullOrWhiteSpace($installLocation)) {
                continue
            }

            $normalizedLocation = $null
            try {
                $normalizedLocation = [System.IO.Path]::GetFullPath($installLocation).TrimEnd('\')
            }
            catch {
                continue
            }

            if ([string]::Equals($normalizedLocation, $compilerDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $displayVersion
            }
        }
    }

    # No fallback to a well-known uninstall key: a key whose InstallLocation is absent cannot be tied
    # to the selected compiler, and Inno Setup 6 and 7 can be installed side by side, so accepting
    # such a version would attribute the wrong release to the compiler (fail-closed instead).
    return $null
}

function Invoke-IsccBannerProbe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CompilerPath,

        [int]$TimeoutSeconds = 20
    )

    # Bounded probe: a non-CLI binary (for example the GUI Compil32.exe) can block forever on '/?'.
    $stdoutPath = Join-Path ([System.IO.Path]::GetTempPath()) ('brochoos-iscc-probe-' + [guid]::NewGuid().ToString('N') + '.out')
    $stderrPath = Join-Path ([System.IO.Path]::GetTempPath()) ('brochoos-iscc-probe-' + [guid]::NewGuid().ToString('N') + '.err')

    try {
        $probeProcess = Start-Process -FilePath $CompilerPath -ArgumentList '/?' -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru

        if (-not $probeProcess.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $probeProcess.Kill()
                $probeProcess.WaitForExit(5000) | Out-Null
            }
            catch {
            }

            return $null
        }

        $banner = ''
        if (Test-Path -LiteralPath $stdoutPath) {
            $banner += [string](Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue)
        }

        if (Test-Path -LiteralPath $stderrPath) {
            $banner += [Environment]::NewLine + [string](Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue)
        }

        # Only a full X.Y.Z banner is accepted. The pre-compile banner of real builds carries the
        # major version only, so nothing is fabricated from it.
        $bannerMatch = [regex]::Match($banner, '(?im)\bInno\s+Setup\s+(\d+\.\d+\.\d+)\b')
        if ($bannerMatch.Success) {
            return $bannerMatch.Groups[1].Value
        }

        return $null
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-IsccVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CompilerPath
    )

    # Authoritative source: the Inno Setup uninstall registry entry (DisplayVersion). Real ISCC
    # builds report ProductVersion/FileVersion 0.0.0.0, and the '/?' banner exposes the major
    # version only, so no version is inferred from those: when nothing reliable is available the
    # result is $null (fail-closed) rather than a fabricated value.
    $registryVersion = Get-IsccVersionFromRegistry -CompilerPath $CompilerPath
    if ($null -ne $registryVersion) {
        return $registryVersion
    }

    $bannerVersion = Invoke-IsccBannerProbe -CompilerPath $CompilerPath
    if ($null -ne $bannerVersion) {
        return $bannerVersion
    }

    return $null
}

function Test-IsccIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CompilerPath,

        [Parameter(Mandatory = $true)]
        [object[]]$SignerAllowlist
    )

    $compilerLeafName = [System.IO.Path]::GetFileName($CompilerPath)
    if (-not [string]::Equals($compilerLeafName, 'ISCC.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
        return @{ IsValid = $false; Reason = "Selected compiler file name must be ISCC.exe, but was '$compilerLeafName'." }
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($CompilerPath)

    # CLI discriminator: the GUI compiler (Compil32.exe) carries FileDescription "Inno Setup Compiler".
    # A renamed copy of it must not pass as the command-line compiler even though metadata, signature
    # and signer allowlist all match.
    $fileDescription = ([string]$versionInfo.FileDescription).Trim()
    if (-not [string]::Equals($fileDescription, 'Inno Setup Command-Line Compiler', [System.StringComparison]::Ordinal)) {
        return @{ IsValid = $false; Reason = "Selected compiler is not the Inno Setup command-line compiler (FileDescription='$fileDescription'). Choose the real ISCC.exe from an Inno Setup installation." }
    }

    $metadataFields = @(
        [string]$versionInfo.ProductName,
        [string]$versionInfo.FileDescription,
        [string]$versionInfo.OriginalFilename,
        [string]$versionInfo.InternalName
    )

    $hasInnoSetupMetadata = $false
    foreach ($field in $metadataFields) {
        if (-not [string]::IsNullOrWhiteSpace($field) -and $field -match '(?i)Inno\s+Setup') {
            $hasInnoSetupMetadata = $true
            break
        }
    }

    if (-not $hasInnoSetupMetadata) {
        return @{ IsValid = $false; Reason = 'Selected compiler metadata does not identify Inno Setup. Choose the real ISCC.exe from an Inno Setup installation.' }
    }

    $signature = Get-AuthenticodeSignature -FilePath $CompilerPath
    $signatureStatus = [string]$signature.Status
    $signatureSigner = ''
    if ($null -ne $signature.SignerCertificate) {
        $signatureSigner = [string]$signature.SignerCertificate.Subject
    }

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        return @{ IsValid = $false; Reason = "Authenticode signature validation failed with status '$signatureStatus'. Signer='$signatureSigner'. Use a trusted ISCC.exe from an official Inno Setup installation." }
    }

    if ([string]::IsNullOrWhiteSpace($signatureSigner)) {
        return @{ IsValid = $false; Reason = 'Authenticode signature signer certificate subject is missing. Use a trusted ISCC.exe from an official Inno Setup installation.' }
    }

    $signerCertificate = $signature.SignerCertificate
    $signerCommonName = ''
    if ($signatureSigner -match '(?i)(?:^|,)\s*CN\s*=\s*([^,]+)') {
        $signerCommonName = $matches[1].Trim()
    }

    if ([string]::IsNullOrWhiteSpace($signerCommonName)) {
        return @{ IsValid = $false; Reason = "Signer certificate subject does not contain a parseable CN. Subject='$signatureSigner'." }
    }

    $signerThumbprint = ''
    if ($null -ne $signerCertificate -and -not [string]::IsNullOrWhiteSpace([string]$signerCertificate.Thumbprint)) {
        $signerThumbprint = ([string]$signerCertificate.Thumbprint).Trim().ToUpperInvariant()
    }

    if ([string]::IsNullOrWhiteSpace($signerThumbprint)) {
        return @{ IsValid = $false; Reason = 'Signer certificate thumbprint is missing. Use a trusted ISCC.exe from an official Inno Setup installation.' }
    }

    $allowlisted = $false
    foreach ($allowedSigner in $SignerAllowlist) {
        $allowedCn = ([string]$allowedSigner.CommonName).Trim()
        $allowedThumbprint = ([string]$allowedSigner.Thumbprint).Trim().ToUpperInvariant()

        if ([string]::Equals($signerCommonName, $allowedCn, [System.StringComparison]::Ordinal) -and
            [string]::Equals($signerThumbprint, $allowedThumbprint, [System.StringComparison]::Ordinal)) {
            $allowlisted = $true
            break
        }
    }

    if (-not $allowlisted) {
        $allowlistSummary = ($SignerAllowlist | ForEach-Object { "CN='$($_.CommonName)', Thumbprint='$($_.Thumbprint)'" }) -join '; '
        return @{ IsValid = $false; Reason = "Signer certificate mismatch (fail-closed). Observed CN='$signerCommonName', Thumbprint='$signerThumbprint'. Approved signer allowlist: $allowlistSummary." }
    }

    return @{ IsValid = $true; Reason = $null }
}

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    [Console]::Error.WriteLine("Missing installer script: $installerScript")
    exit 1
}

if (-not (Test-Path -LiteralPath $approvalRecord -PathType Leaf)) {
    [Console]::Error.WriteLine("Missing owner approval record: $approvalRecord")
    exit 1
}

if (-not (Test-Path -LiteralPath $approvalValidator -PathType Leaf)) {
    [Console]::Error.WriteLine("Missing owner approval validator: $approvalValidator")
    exit 1
}

try {
    $ownerRecord = Get-Content -LiteralPath $approvalRecord -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    [Console]::Error.WriteLine("Could not read the owner approval record: $approvalRecord")
    [Console]::Error.WriteLine('Fix JSON formatting and retry.')
    exit 1
}

# Read toolchain.version defensively: a missing record, toolchain or version leaves the value empty
# so the semver check below prints its remediation message instead of a property-access error.
$pinnedIsccVersionRaw = ''
if ($null -ne $ownerRecord) {
    $toolchainProperty = $ownerRecord.PSObject.Properties['toolchain']
    if ($null -ne $toolchainProperty -and $null -ne $toolchainProperty.Value) {
        $versionProperty = $toolchainProperty.Value.PSObject.Properties['version']
        if ($null -ne $versionProperty -and $null -ne $versionProperty.Value) {
            $pinnedIsccVersionRaw = [string]$versionProperty.Value
        }
    }
}
if (-not (Test-ThreePartSemver -Text $pinnedIsccVersionRaw)) {
    [Console]::Error.WriteLine("Owner approval record is missing a valid toolchain.version value: '$pinnedIsccVersionRaw'.")
    [Console]::Error.WriteLine('Set toolchain.version to a semantic version like 6.4.3 in the owner approval record (docs/decisions/owner-rulings/installer-toolchain-approval.json) and retry.')
    exit 1
}
$pinnedIsccVersion = $pinnedIsccVersionRaw.Trim()

$approvalArgs = @(
    '-NoProfile',
    '-File', $approvalValidator,
    '-Record', $approvalRecord,
    '-SubjectId', 'INSTALLER-TOOLCHAIN-APPROVAL',
    '-RequiredPriorStatus', 'OPEN'
)

& powershell @approvalArgs
if ([int]$LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('Owner approval record validation failed for W10.1 preflight (diagnostics shown above).')
    exit 1
}

$compilerPath = $null
if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
    $compilerPath = $IsccPath
}
else {
    $compiler = (Get-Command -Name 'ISCC.exe' -ErrorAction SilentlyContinue)
    if ($null -ne $compiler) {
        $compilerPath = $compiler.Source
    }
}

if ([string]::IsNullOrWhiteSpace($compilerPath) -or -not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    [Console]::Error.WriteLine("ISCC.exe was not found. Install Inno Setup $pinnedIsccVersion and make ISCC.exe available on PATH, or pass -IsccPath with the full path.")
    exit 1
}

$identityCheck = Test-IsccIdentity -CompilerPath $compilerPath -SignerAllowlist $approvedSignerAllowlist
if (-not [bool]$identityCheck.IsValid) {
    [Console]::Error.WriteLine("Selected compiler is not accepted: $($identityCheck.Reason)")
    [Console]::Error.WriteLine('Next step: provide the ISCC.exe binary installed by Inno Setup.')
    exit 1
}

$actualIsccVersion = Get-IsccVersion -CompilerPath $compilerPath

if ($null -eq $actualIsccVersion) {
    [Console]::Error.WriteLine("Could not determine the Inno Setup version from '$compilerPath'.")
    [Console]::Error.WriteLine('Use the ISCC.exe that comes with Inno Setup and retry.')
    exit 1
}

if ($actualIsccVersion -ne $pinnedIsccVersion) {
    [Console]::Error.WriteLine("Inno Setup version mismatch. Required: $pinnedIsccVersion (owner approval record). Found: $actualIsccVersion at '$compilerPath'.")
    [Console]::Error.WriteLine('Install/select the required version and retry.')
    exit 1
}

if ($ValidateOnly) {
    'W10.1_PACKAGE_PREFLIGHT=PASS'
    exit 0
}

$publishProject = Join-Path $repoRoot 'src\BroChoos.App\BroChoos.App.csproj'
# SELF-CONTAINED: the shipped app must start on a machine with no .NET runtime installed.
# '--self-contained true' covers BroChoos.exe only, because MSBuild strips SelfContained from
# ProjectReference builds. BroChoosSelfContained=true is the custom global property the App and
# all four helper projects read (Directory.Build.props), so the helper exes copied next to
# BroChoos.exe are self-contained too and share the runtime in the publish folder.
# Folder layout, not single-file: the App locates its helpers as loose exes next to itself.
# --output pins the folder the ISCC step below packages, whatever Platform the caller uses.
$publishArgs = @(
    'publish',
    $publishProject,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '--output', $publishDir,
    '/p:BroChoosSelfContained=true',
    '/p:PublishSingleFile=false',
    "/p:Version=$Version"
)

& dotnet @publishArgs
if ([int]$LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('Application publish failed (dotnet publish). Fix build errors and retry packaging.')
    exit 1
}

if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    [Console]::Error.WriteLine("Publish directory missing: $publishDir")
    exit 1
}

$compileArgs = @(
    ('/DAppVersion=' + $Version),
    ('/DAppPublishDir=' + $publishDir),
    $installerScript
)

& $compilerPath @compileArgs
if ([int]$LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('Installer build failed (ISCC). Review ISCC output above, then fix and retry.')
    exit 1
}

'W10.1_PACKAGE_BUILD=PASS'
exit 0
