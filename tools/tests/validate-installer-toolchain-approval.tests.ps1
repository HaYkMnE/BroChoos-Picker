[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$validator = Join-Path $repoRoot 'tools\validate-installer-toolchain-approval.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('brochoos-installer-toolchain-validator-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

function Invoke-Validator {
    param(
        [string]$Record,
        [string]$Subject,
        [string]$RequiredPriorStatus,
        [string]$HostExe = 'powershell'
    )

    $args = @('-NoProfile', '-File', $validator, '-Record', $Record, '-SubjectId', $Subject)
    if ($RequiredPriorStatus) {
        $args += @('-RequiredPriorStatus', $RequiredPriorStatus)
    }

    & $HostExe @args | Out-Null
    return [int]$LASTEXITCODE
}

function Assert-Exit {
    param(
        [int]$Actual,
        [int]$Expected,
        [string]$Name
    )

    if ($Actual -ne $Expected) {
        throw "[$Name] expected exit $Expected, got $Actual"
    }
}

function New-ApprovalRecord {
    param(
        [string]$Disposition = 'approved',
        [string]$PriorStatus = 'OPEN',
        [string]$ValidationStatus = 'PASS',
        [string]$Version = '6.0.1',
        [string]$Product = 'Inno Setup',
        [string]$SubjectId = 'INSTALLER-TOOLCHAIN-APPROVAL',
        [string]$Scope = 'per-user packaging for this phase',
        [string]$IntegrityReference = 'sha256:9d8b4dca3fc4e6f9c10a8f7b5b4f96d034588ea601f7a8d7a98c8e98dbb241cf',
        [string]$AuthorityApprovedAtUtc = '2026-09-15T12:00:00Z',
        [string]$ValidationReviewedAtUtc = '2026-09-15T12:15:00Z',
        [switch]$OmitAuthorityApprovedAtUtc,
        [switch]$AddToolchainUnexpectedProperty,
        [switch]$AddTopLevelUnexpectedProperty,
        [object]$Notes
    )

    $toolchain = [ordered]@{
        product = $Product
        version = $Version
    }

    if ($AddToolchainUnexpectedProperty) {
        $toolchain.unexpected = 'not-allowed-by-schema'
    }

    $authority = [ordered]@{
        source = 'DH-2026-09-15'
        approver = 'owner@brochoos'
    }

    if (-not $OmitAuthorityApprovedAtUtc) {
        $authority.approvedAtUtc = $AuthorityApprovedAtUtc
    }

    $record = [ordered]@{
        schemaVersion = '1.0.0'
        recordVersion = '1.0.0'
        subjectId = $SubjectId
        resolutionState = [ordered]@{
            disposition = $Disposition
            priorStatus = $PriorStatus
        }
        toolchain = $toolchain
        distribution = [ordered]@{
            sourceIdentity = 'https://github.com/wixtoolset/wix'
            integrityReference = $IntegrityReference
        }
        scope = $Scope
        constraints = 'Per-user package only; no HKLM or UserChoice writes.'
        authority = $authority
        validation = [ordered]@{
            status = $ValidationStatus
            reviewedBy = 'authority-reviewer@brochoos'
            reviewedAtUtc = $ValidationReviewedAtUtc
        }
    }

    if ($PSBoundParameters.ContainsKey('Notes')) {
        $record.notes = $Notes
    }

    if ($AddTopLevelUnexpectedProperty) {
        $record.unexpectedTopLevel = 'not-allowed-by-schema'
    }

    return ($record | ConvertTo-Json -Depth 8)
}

try {
    $subject = 'INSTALLER-TOOLCHAIN-APPROVAL'
    $recordPath = Join-Path $tempRoot 'installer-toolchain-approval.json'

    $code = Invoke-Validator -Record (Join-Path $tempRoot 'missing.json') -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'missing-record-fails'

    '{ bad json' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'invalid-json-fails'

    New-ApprovalRecord | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject -RequiredPriorStatus 'OPEN'
    Assert-Exit -Actual $code -Expected 0 -Name 'approved-record-passes'

    New-ApprovalRecord -PriorStatus 'CLOSED' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject -RequiredPriorStatus 'OPEN'
    Assert-Exit -Actual $code -Expected 1 -Name 'prior-status-mismatch-fails'

    New-ApprovalRecord -SubjectId 'WRONG-SUBJECT' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'wrong-subject-fails'

    New-ApprovalRecord -Disposition 'deferred' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'deferred-fails'

    New-ApprovalRecord -Disposition 'approved-now' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'invalid-disposition-token-fails'

    New-ApprovalRecord -Disposition 'rejected' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'rejected-fails'

    New-ApprovalRecord -Version 'latest' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'latest-version-fails'

    New-ApprovalRecord -Version '>=6.0.0' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'ranged-version-fails'

    New-ApprovalRecord -Version '6.4' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'non-semver-version-fails'

    New-ApprovalRecord -Product 'WiX Toolset' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'wrong-toolchain-product-fails'

    New-ApprovalRecord -IntegrityReference 'TBD' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'placeholder-integrity-fails'

    New-ApprovalRecord -ValidationStatus 'PENDING' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'nonpass-validation-fails'

    New-ApprovalRecord -Scope 'wrong scope' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'wrong-scope-fails'

    New-ApprovalRecord -OmitAuthorityApprovedAtUtc | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'missing-nested-required-field-fails'

    New-ApprovalRecord -AddToolchainUnexpectedProperty | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'additional-properties-fail'

    New-ApprovalRecord -AddTopLevelUnexpectedProperty | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'unexpected-top-level-property-fails'

    New-ApprovalRecord -Notes 'single string note' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'notes-non-array-fails'

    New-ApprovalRecord -Notes @('ok', 42) | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'notes-non-string-item-fails'

    New-ApprovalRecord -AuthorityApprovedAtUtc '2026-09-15 12:00:00' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'authority-date-time-format-fails'

    New-ApprovalRecord -ValidationReviewedAtUtc '2026-09-15 12:15:00' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'validation-date-time-format-fails'

    # Case-sensitive comparisons: case variants of canonical tokens must be rejected.
    New-ApprovalRecord -SubjectId $subject.ToLowerInvariant() | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject.ToLowerInvariant()
    Assert-Exit -Actual $code -Expected 1 -Name 'lowercase-subject-format-fails'

    New-ApprovalRecord | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject.ToLowerInvariant()
    Assert-Exit -Actual $code -Expected 1 -Name 'subject-case-mismatch-fails'

    New-ApprovalRecord -PriorStatus 'open' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject -RequiredPriorStatus 'OPEN'
    Assert-Exit -Actual $code -Expected 1 -Name 'prior-status-case-mismatch-fails'

    New-ApprovalRecord -Disposition 'Approved' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'disposition-case-variant-fails'

    New-ApprovalRecord -ValidationStatus 'pass' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'validation-status-case-variant-fails'

    New-ApprovalRecord -Scope 'Per-User Packaging For This Phase' | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $code = Invoke-Validator -Record $recordPath -Subject $subject
    Assert-Exit -Actual $code -Expected 1 -Name 'scope-case-variant-fails'

    # PowerShell 7 host: ConvertFrom-Json turns ISO timestamps into [DateTime] there, so the
    # RFC 3339 check must not depend on the original string. Skipped (loudly) if pwsh is absent.
    if ($null -ne (Get-Command pwsh -ErrorAction SilentlyContinue)) {
        New-ApprovalRecord | Set-Content -LiteralPath $recordPath -Encoding UTF8
        $code = Invoke-Validator -Record $recordPath -Subject $subject -RequiredPriorStatus 'OPEN' -HostExe 'pwsh'
        Assert-Exit -Actual $code -Expected 0 -Name 'pwsh-approved-record-passes'

        New-ApprovalRecord -AuthorityApprovedAtUtc '2026-09-15 12:00:00' | Set-Content -LiteralPath $recordPath -Encoding UTF8
        $code = Invoke-Validator -Record $recordPath -Subject $subject -HostExe 'pwsh'
        Assert-Exit -Actual $code -Expected 1 -Name 'pwsh-authority-date-time-format-fails'

        New-ApprovalRecord -ValidationReviewedAtUtc '2026-09-15T12:15:00' | Set-Content -LiteralPath $recordPath -Encoding UTF8
        $code = Invoke-Validator -Record $recordPath -Subject $subject -HostExe 'pwsh'
        Assert-Exit -Actual $code -Expected 1 -Name 'pwsh-offsetless-date-time-fails'

        $realRecord = Join-Path $repoRoot 'docs\decisions\owner-rulings\installer-toolchain-approval.json'
        $code = Invoke-Validator -Record $realRecord -Subject $subject -RequiredPriorStatus 'OPEN' -HostExe 'pwsh'
        Assert-Exit -Actual $code -Expected 0 -Name 'pwsh-real-record-passes'
    }
    else {
        Write-Output 'SKIPPED: pwsh (PowerShell 7) is not installed; the pwsh-host cases did not run.'
    }

    'ALL_VALIDATE_INSTALLER_TOOLCHAIN_APPROVAL_TESTS_PASSED'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
