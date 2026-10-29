[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Hermetic by construction: the test never writes the real canonical authority
# record. Instead it builds a miniature repo inside a per-run %TEMP% dir whose
# canonical record path resolves inside the sandbox, and runs a fresh copy of
# the real validator from that miniature repo. The real record is only ever
# READ (final production-state assertion).
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$realValidator = Join-Path $repoRoot 'tools\validate-owner-ruling.ps1'
$realSchema = Join-Path $repoRoot 'docs\decisions\owner-ruling.schema.json'
$realCanonicalRecord = Join-Path $repoRoot 'docs\decisions\owner-rulings\dev-runner-ipc-trust-anchor.json'

$sandboxRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('brochoos-owner-ruling-tests-' + [guid]::NewGuid().ToString('N'))
$sandboxRepo = Join-Path $sandboxRoot 'repo'
$sandboxTools = Join-Path $sandboxRepo 'tools'
$sandboxDecisions = Join-Path $sandboxRepo 'docs\decisions'
$sandboxRulings = Join-Path $sandboxDecisions 'owner-rulings'
$null = New-Item -ItemType Directory -Path $sandboxTools -Force
$null = New-Item -ItemType Directory -Path $sandboxRulings -Force
Copy-Item -LiteralPath $realValidator -Destination (Join-Path $sandboxTools 'validate-owner-ruling.ps1') -Force
Copy-Item -LiteralPath $realSchema -Destination (Join-Path $sandboxDecisions 'owner-ruling.schema.json') -Force

$sandboxValidator = Join-Path $sandboxTools 'validate-owner-ruling.ps1'
$sandboxCanonicalRecord = Join-Path $sandboxRulings 'dev-runner-ipc-trust-anchor.json'

function Invoke-Validator {
    param(
        [string]$Record,
        [string]$Subject,
        [string]$RequiredPriorStatus,
        [string]$ValidatorPath = $sandboxValidator,
        [string]$HostExe = 'powershell'
    )

    $args = @('-NoProfile', '-File', $ValidatorPath, '-Record', $Record, '-SubjectId', $Subject)
    if ($RequiredPriorStatus) {
        $args += @('-RequiredPriorStatus', $RequiredPriorStatus)
    }

    $stdoutPath = Join-Path $sandboxRoot ('stdout-' + [guid]::NewGuid().ToString('N') + '.txt')
    $stderrPath = Join-Path $sandboxRoot ('stderr-' + [guid]::NewGuid().ToString('N') + '.txt')
    $prev = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $HostExe @args 1> $stdoutPath 2> $stderrPath
    }
    finally {
        $ErrorActionPreference = $prev
    }

    $code = $LASTEXITCODE
    $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw } else { '' }
    $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }
    return [pscustomobject]@{ ExitCode = [int]$code; Output = ([string]$stdout + "`n" + [string]$stderr) }
}

function New-RecordJson {
    param(
        [string]$SubjectId = 'DEV-RUNNER-IPC-TRUST-ANCHOR',
        [string]$Disposition = 'approved',
        [string]$ValidationStatus = 'PASS',
        [bool]$TestOnly = $false,
        [bool]$IncludePublicTrust = $true,
        [bool]$IncludeLifecycle = $true,
        [bool]$IncludeConstraints = $true,
        [bool]$IncludePriorStatus = $true,
        [string]$SignerIdentityModel = 'x509-chain',
        [string]$UndeclaredTopLevelField = '',
        [string]$Scope = 'local Dev Runner IPC only',
        [string]$ApprovedAtUtc = '2026-09-12T00:00:00Z',
        [string]$ReviewedAtUtc = '2026-09-12T00:00:00Z'
    )

    $base = [ordered]@{
        schemaVersion = '1.0.0'
        recordVersion = '1.0.0'
        subjectId = $SubjectId
        resolutionState = [ordered]@{
            disposition = $Disposition
        }
        scope = $Scope
        signerIdentityModel = $SignerIdentityModel
        authority = [ordered]@{
            source = 'DH-2026-09-11'
            approver = 'owner@brochoos'
            approvedAtUtc = $ApprovedAtUtc
        }
        validation = [ordered]@{
            status = $ValidationStatus
            reviewedBy = 'security-review@brochoos'
            reviewedAtUtc = $ReviewedAtUtc
        }
    }

    if ($IncludePriorStatus) {
        $base.resolutionState.priorStatus = 'OPEN'
    }

    if ($IncludePublicTrust) {
        $base.publicTrustMetadata = [ordered]@{
            testOnly = $TestOnly
        }
    }

    if ($IncludeLifecycle) {
        $base.trustAnchorLifecycle = [ordered]@{
            provisioning = 'manual owner-controlled provisioning'
            storage = 'local machine trust store reference only'
            rotation = 'owner-authorized rotation with overlap window'
            revocation = 'explicit revocation list and deny on mismatch'
        }
        if ($IncludeConstraints) {
            $base.trustAnchorLifecycle.constraints = 'no fixture fallback; local Dev Runner IPC only'
        }
    }

    if ($UndeclaredTopLevelField) {
        $base[$UndeclaredTopLevelField] = 'undeclared-value'
    }

    return $base | ConvertTo-Json -Depth 8
}

function Assert-ExitCode {
    param(
        [int]$Actual,
        [int]$Expected,
        [string]$Name
    )

    if ($Actual -ne $Expected) {
        throw "[$Name] expected exit code $Expected, got $Actual"
    }
}

function Assert-OutputContains {
    param(
        [object]$Result,
        [string]$Needle,
        [string]$Name
    )

    $hay = ($Result.Output -replace '\s+', '')
    $target = ($Needle -replace '\s+', '')
    if ($hay.IndexOf($target, [System.StringComparison]::Ordinal) -lt 0) {
        throw "[$Name] expected output to contain '$Needle' but got:`n$($Result.Output)"
    }
}

function Assert-OutputExcludes {
    param(
        [object]$Result,
        [string]$Needle,
        [string]$Name
    )

    $hay = ($Result.Output -replace '\s+', '')
    $target = ($Needle -replace '\s+', '')
    if ($hay.IndexOf($target, [System.StringComparison]::Ordinal) -ge 0) {
        throw "[$Name] expected output to NOT contain '$Needle' but got:`n$($Result.Output)"
    }
}

function Invoke-CanonicalScenario {
    param(
        [string]$JsonContent,
        [string]$Subject = 'DEV-RUNNER-IPC-TRUST-ANCHOR',
        [string]$RequiredPriorStatus = 'OPEN'
    )

    $JsonContent | Set-Content -LiteralPath $sandboxCanonicalRecord -Encoding UTF8
    return Invoke-Validator -Record $sandboxCanonicalRecord -Subject $Subject -RequiredPriorStatus $RequiredPriorStatus
}

try {
    # Positive: fully valid approved record at the sandbox canonical path.
    $result = Invoke-CanonicalScenario (New-RecordJson)
    Assert-ExitCode -Actual $result.ExitCode -Expected 0 -Name 'valid-approved-canonical-path'
    Assert-OutputContains -Result $result -Needle 'OWNER_RULING_VALIDATION=PASS' -Name 'valid-approved-canonical-path'

    # Negative: same valid content at a non-canonical path for the Dev Runner subject.
    $wrongPathRecord = Join-Path $sandboxRoot 'wrong-path-valid.json'
    (New-RecordJson) | Set-Content -LiteralPath $wrongPathRecord -Encoding UTF8
    $result = Invoke-Validator -Record $wrongPathRecord -Subject 'DEV-RUNNER-IPC-TRUST-ANCHOR' -RequiredPriorStatus 'OPEN'
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-wrong-record-path'
    Assert-OutputContains -Result $result -Needle 'record.path.invalid' -Name 'reject-wrong-record-path'

    # Negative: wrong subject.
    $result = Invoke-CanonicalScenario (New-RecordJson -SubjectId 'ANOTHER-SUBJECT')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-wrong-subject'
    Assert-OutputContains -Result $result -Needle 'subject.mismatch' -Name 'reject-wrong-subject'

    # Negative: placeholder value.
    $result = Invoke-CanonicalScenario (New-RecordJson -SignerIdentityModel 'TBD_PLACEHOLDER')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-placeholder'
    Assert-OutputContains -Result $result -Needle 'placeholder.disallowed' -Name 'reject-placeholder'

    # Negative: epoch placeholder timestamps in authority/validation.
    $result = Invoke-CanonicalScenario (New-RecordJson -ApprovedAtUtc '1970-01-01T00:00:00Z')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-placeholder-approvedAtUtc'
    Assert-OutputContains -Result $result -Needle 'placeholder.disallowed' -Name 'reject-placeholder-approvedAtUtc'

    $result = Invoke-CanonicalScenario (New-RecordJson -ReviewedAtUtc '1970-01-01T00:00:00Z')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-placeholder-reviewedAtUtc'
    Assert-OutputContains -Result $result -Needle 'placeholder.disallowed' -Name 'reject-placeholder-reviewedAtUtc'

    # Negative: non-final disposition.
    $result = Invoke-CanonicalScenario (New-RecordJson -Disposition 'deferred')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-deferred'
    Assert-OutputContains -Result $result -Needle 'resolution.nonfinal' -Name 'reject-deferred'

    # Negative: lifecycle object absent entirely.
    $result = Invoke-CanonicalScenario (New-RecordJson -IncludeLifecycle:$false)
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-missing-lifecycle'
    Assert-OutputContains -Result $result -Needle 'schema.required' -Name 'reject-missing-lifecycle'

    # Negative: fixture record used as production (testOnly=true).
    $result = Invoke-CanonicalScenario (New-RecordJson -TestOnly:$true)
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-fixture-as-production'
    Assert-OutputContains -Result $result -Needle 'trust.fixture-forbidden' -Name 'reject-fixture-as-production'

    # Negative: publicTrustMetadata absent -> clean coded exit 1 with NEXT_STEPS, no raw exception.
    $result = Invoke-CanonicalScenario (New-RecordJson -IncludePublicTrust:$false)
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-missing-publicTrustMetadata'
    Assert-OutputContains -Result $result -Needle 'trust.metadata.missing' -Name 'reject-missing-publicTrustMetadata'
    Assert-OutputContains -Result $result -Needle 'NEXT_STEPS' -Name 'reject-missing-publicTrustMetadata'
    Assert-OutputExcludes -Result $result -Needle 'PropertyNotFoundException' -Name 'reject-missing-publicTrustMetadata'

    # Negative: lifecycle missing constraints -> clean coded exit 1.
    $result = Invoke-CanonicalScenario (New-RecordJson -IncludeConstraints:$false)
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-missing-constraints'
    Assert-OutputContains -Result $result -Needle 'trustAnchorLifecycle.constraints' -Name 'reject-missing-constraints'
    Assert-OutputContains -Result $result -Needle 'NEXT_STEPS' -Name 'reject-missing-constraints'
    Assert-OutputExcludes -Result $result -Needle 'PropertyNotFoundException' -Name 'reject-missing-constraints'

    # Negative: priorStatus absent (even without -RequiredPriorStatus) -> coded exit 1.
    $result = Invoke-CanonicalScenario (New-RecordJson -IncludePriorStatus:$false) -RequiredPriorStatus ''
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-missing-priorStatus'
    Assert-OutputContains -Result $result -Needle 'resolutionState.priorStatus' -Name 'reject-missing-priorStatus'

    # Negative: undeclared top-level field violates schema additionalProperties:false.
    $result = Invoke-CanonicalScenario (New-RecordJson -UndeclaredTopLevelField 'smuggled')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-additional-property'
    Assert-OutputContains -Result $result -Needle 'schema.additional-property' -Name 'reject-additional-property'

    # Read-only production-state assertion: the real shipped canonical record must
    # validate PASS against the real validator (never written by this test).
    $result = Invoke-Validator -Record $realCanonicalRecord -Subject 'DEV-RUNNER-IPC-TRUST-ANCHOR' -ValidatorPath $realValidator
    Assert-ExitCode -Actual $result.ExitCode -Expected 0 -Name 'real-canonical-record-is-production-valid'
    Assert-OutputContains -Result $result -Needle 'OWNER_RULING_VALIDATION=PASS' -Name 'real-canonical-record-is-production-valid'

    # Case-sensitive comparisons: case variants of canonical tokens must be rejected.
    $result = Invoke-CanonicalScenario (New-RecordJson -Disposition 'Approved')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-disposition-case-variant'
    Assert-OutputContains -Result $result -Needle 'resolution.disposition.invalid' -Name 'reject-disposition-case-variant'

    $result = Invoke-CanonicalScenario (New-RecordJson -ValidationStatus 'pass')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-validation-status-case-variant'
    Assert-OutputContains -Result $result -Needle 'validation.status.nonpass' -Name 'reject-validation-status-case-variant'

    $result = Invoke-CanonicalScenario (New-RecordJson -Scope 'Local Dev Runner IPC Only')
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-scope-case-variant'
    Assert-OutputContains -Result $result -Needle 'scope.invalid' -Name 'reject-scope-case-variant'

    $result = Invoke-CanonicalScenario (New-RecordJson) -RequiredPriorStatus 'open'
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-prior-status-case-variant'
    Assert-OutputContains -Result $result -Needle 'resolution.prior-status.mismatch' -Name 'reject-prior-status-case-variant'

    $result = Invoke-CanonicalScenario (New-RecordJson) -Subject 'dev-runner-ipc-trust-anchor'
    Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'reject-subject-case-variant'
    Assert-OutputContains -Result $result -Needle 'subject.mismatch' -Name 'reject-subject-case-variant'

    # PowerShell 7 host: ConvertFrom-Json turns ISO timestamps into [DateTime] there, so the
    # epoch-placeholder check must not depend on the original string. Skipped (loudly) if absent.
    if ($null -ne (Get-Command pwsh -ErrorAction SilentlyContinue)) {
        (New-RecordJson) | Set-Content -LiteralPath $sandboxCanonicalRecord -Encoding UTF8
        $result = Invoke-Validator -Record $sandboxCanonicalRecord -Subject 'DEV-RUNNER-IPC-TRUST-ANCHOR' -RequiredPriorStatus 'OPEN' -HostExe 'pwsh'
        Assert-ExitCode -Actual $result.ExitCode -Expected 0 -Name 'pwsh-valid-approved-canonical-path'

        (New-RecordJson -ApprovedAtUtc '1970-01-01T00:00:00Z') | Set-Content -LiteralPath $sandboxCanonicalRecord -Encoding UTF8
        $result = Invoke-Validator -Record $sandboxCanonicalRecord -Subject 'DEV-RUNNER-IPC-TRUST-ANCHOR' -RequiredPriorStatus 'OPEN' -HostExe 'pwsh'
        Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'pwsh-reject-placeholder-approvedAtUtc'
        Assert-OutputContains -Result $result -Needle 'placeholder.disallowed' -Name 'pwsh-reject-placeholder-approvedAtUtc'

        (New-RecordJson -ReviewedAtUtc '1970-01-01T00:00:00Z') | Set-Content -LiteralPath $sandboxCanonicalRecord -Encoding UTF8
        $result = Invoke-Validator -Record $sandboxCanonicalRecord -Subject 'DEV-RUNNER-IPC-TRUST-ANCHOR' -RequiredPriorStatus 'OPEN' -HostExe 'pwsh'
        Assert-ExitCode -Actual $result.ExitCode -Expected 1 -Name 'pwsh-reject-placeholder-reviewedAtUtc'
        Assert-OutputContains -Result $result -Needle 'placeholder.disallowed' -Name 'pwsh-reject-placeholder-reviewedAtUtc'

        $result = Invoke-Validator -Record $realCanonicalRecord -Subject 'DEV-RUNNER-IPC-TRUST-ANCHOR' -ValidatorPath $realValidator -HostExe 'pwsh'
        Assert-ExitCode -Actual $result.ExitCode -Expected 0 -Name 'pwsh-real-canonical-record-is-production-valid'
    }
    else {
        Write-Output 'SKIPPED: pwsh (PowerShell 7) is not installed; the pwsh-host cases did not run.'
    }

    'ALL_VALIDATE_OWNER_RULING_TESTS_PASSED'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $sandboxRoot) {
        Remove-Item -LiteralPath $sandboxRoot -Recurse -Force
    }
}
