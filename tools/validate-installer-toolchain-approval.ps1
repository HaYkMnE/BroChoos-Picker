[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Record,

    [Parameter(Mandatory = $true)]
    [string]$SubjectId,

    [string]$RequiredPriorStatus
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Add-ValidationError {
    param(
        [System.Collections.Generic.List[string]]$Errors,
        [string]$Code,
        [string]$Detail
    )

    $Errors.Add("$Code :: $Detail") | Out-Null
}

function Test-PlaceholderValue {
    param([object]$Value)

    if ($null -eq $Value) { return $true }

    $s = [string]$Value
    if ([string]::IsNullOrWhiteSpace($s)) { return $true }

    foreach ($token in @('TBD','TODO','PLACEHOLDER','example.invalid','changeme','dummy','latest')) {
        if ($s.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Test-PinnedVersion {
    param([string]$Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        return $false
    }

    if ($Version -match '(?i)\blatest\b') { return $false }
    if ($Version -match '[*xX]') { return $false }
    if ($Version -match '[><=~^]') { return $false }
    if ($Version -match '\s') { return $false }
    if ($Version -match ',') { return $false }
    if ($Version -match '\|\|') { return $false }
    if ($Version -match '-') { return $false }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { return $false }

    return $true
}

function Test-IsJsonObject {
    param([object]$Value)

    return ($Value -is [System.Management.Automation.PSCustomObject] -or $Value -is [hashtable])
}

function Test-JsonDateTimeFormat {
    param([object]$Value)

    if ($null -eq $Value) {
        return $false
    }

    # PowerShell 7 ConvertFrom-Json converts ISO timestamps into [DateTime] before this function
    # sees them, and the original text is gone (a [string] cast gives '10/27/2026 00:00:00', which
    # no RFC 3339 pattern matches). Windows PowerShell 5.1 keeps the string. For a [DateTime] the
    # Kind says what the text had: Utc ('Z') or Local (a numeric offset) means an offset was
    # present, which RFC 3339 requires; Unspecified means it had none, so it is rejected, the same
    # as the string path rejects it.
    if ($Value -is [System.DateTimeOffset]) {
        return $true
    }
    if ($Value -is [System.DateTime]) {
        return ($Value.Kind -ne [System.DateTimeKind]::Unspecified)
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $false
    }

    if ($text -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+\-]\d{2}:\d{2})$') {
        return $false
    }

    $parsed = [System.DateTimeOffset]::MinValue
    return [System.DateTimeOffset]::TryParse(
        $text,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsed
    )
}

function Test-ObjectSchema {
    param(
        [System.Collections.Generic.List[string]]$Errors,
        [string]$Path,
        [object]$Value,
        [string[]]$Required,
        [string[]]$Allowed
    )

    if (-not (Test-IsJsonObject -Value $Value)) {
        Add-ValidationError -Errors $Errors -Code 'schema.type' -Detail "Field '$Path' must be an object"
        return
    }

    $propertyNames = @($Value.PSObject.Properties.Name)

    foreach ($requiredName in $Required) {
        if (-not $propertyNames.Contains($requiredName)) {
            Add-ValidationError -Errors $Errors -Code 'schema.required' -Detail "Missing required field: $Path.$requiredName"
        }
    }

    foreach ($actualName in $propertyNames) {
        if (-not $Allowed.Contains($actualName)) {
            Add-ValidationError -Errors $Errors -Code 'schema.additional-properties' -Detail "Unexpected field: $Path.$actualName"
        }
    }
}

$errors = [System.Collections.Generic.List[string]]::new()

if (-not (Test-Path -LiteralPath $Record -PathType Leaf)) {
    Add-ValidationError -Errors $errors -Code 'record.missing' -Detail "Record not found: $Record"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$schemaPath = Join-Path $repoRoot 'docs\decisions\installer-toolchain-approval.schema.json'
if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
    Add-ValidationError -Errors $errors -Code 'schema.missing' -Detail "Schema not found: $schemaPath"
}

$json = $null
if ($errors.Count -eq 0) {
    try {
        $json = Get-Content -LiteralPath $Record -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Add-ValidationError -Errors $errors -Code 'record.invalid-json' -Detail $_.Exception.Message
    }
}

$topAllowed = @(
    'schemaVersion','recordVersion','subjectId','resolutionState','toolchain',
    'distribution','scope','constraints','authority','validation','notes'
)
$requiredTop = @(
    'schemaVersion','recordVersion','subjectId','resolutionState','toolchain',
    'distribution','scope','constraints','authority','validation'
)

if ($errors.Count -eq 0) {
    foreach ($name in $requiredTop) {
        if (-not $json.PSObject.Properties.Name.Contains($name)) {
            Add-ValidationError -Errors $errors -Code 'schema.required' -Detail "Missing top-level field: $name"
        }
    }

    foreach ($name in $json.PSObject.Properties.Name) {
        if (-not $topAllowed.Contains($name)) {
            Add-ValidationError -Errors $errors -Code 'schema.additional-properties' -Detail "Unexpected top-level field: $name"
        }
    }
}

if ($errors.Count -eq 0) {
    Test-ObjectSchema -Errors $errors -Path 'resolutionState' -Value $json.resolutionState -Required @('disposition','priorStatus') -Allowed @('disposition','priorStatus')
    Test-ObjectSchema -Errors $errors -Path 'toolchain' -Value $json.toolchain -Required @('product','version') -Allowed @('product','version')
    Test-ObjectSchema -Errors $errors -Path 'distribution' -Value $json.distribution -Required @('sourceIdentity','integrityReference') -Allowed @('sourceIdentity','integrityReference')
    Test-ObjectSchema -Errors $errors -Path 'authority' -Value $json.authority -Required @('source','approver','approvedAtUtc') -Allowed @('source','approver','approvedAtUtc')
    Test-ObjectSchema -Errors $errors -Path 'validation' -Value $json.validation -Required @('status','reviewedBy','reviewedAtUtc') -Allowed @('status','reviewedBy','reviewedAtUtc')

    if ($json.PSObject.Properties.Name.Contains('notes')) {
        if ($null -eq $json.notes) {
            Add-ValidationError -Errors $errors -Code 'schema.type' -Detail "Field 'notes' must be an array when provided"
        }
        elseif (-not ($json.notes -is [System.Array])) {
            Add-ValidationError -Errors $errors -Code 'schema.type' -Detail "Field 'notes' must be an array when provided"
        }
        else {
            foreach ($item in $json.notes) {
                if ($null -eq $item -or -not ($item -is [string])) {
                    Add-ValidationError -Errors $errors -Code 'schema.type' -Detail "Field 'notes' must contain only string items"
                    break
                }
            }
        }
    }
}

if ($errors.Count -eq 0) {
    foreach ($stringField in @(
        @{ Path = 'schemaVersion'; Value = $json.schemaVersion },
        @{ Path = 'recordVersion'; Value = $json.recordVersion },
        @{ Path = 'subjectId'; Value = $json.subjectId },
        @{ Path = 'resolutionState.disposition'; Value = $json.resolutionState.disposition },
        @{ Path = 'resolutionState.priorStatus'; Value = $json.resolutionState.priorStatus },
        @{ Path = 'toolchain.product'; Value = $json.toolchain.product },
        @{ Path = 'toolchain.version'; Value = $json.toolchain.version },
        @{ Path = 'distribution.sourceIdentity'; Value = $json.distribution.sourceIdentity },
        @{ Path = 'distribution.integrityReference'; Value = $json.distribution.integrityReference },
        @{ Path = 'constraints'; Value = $json.constraints },
        @{ Path = 'authority.source'; Value = $json.authority.source },
        @{ Path = 'authority.approver'; Value = $json.authority.approver },
        @{ Path = 'validation.status'; Value = $json.validation.status },
        @{ Path = 'validation.reviewedBy'; Value = $json.validation.reviewedBy }
    )) {
        if ([string]::IsNullOrWhiteSpace([string]$stringField.Value)) {
            Add-ValidationError -Errors $errors -Code 'schema.min-length' -Detail "Field '$($stringField.Path)' must be a non-empty string"
        }
    }

    if (-not ([string]$json.subjectId -cmatch '^[A-Z0-9._-]+\z')) {
        Add-ValidationError -Errors $errors -Code 'schema.pattern' -Detail "subjectId must match ^[A-Z0-9._-]+$; got '$($json.subjectId)'"
    }

    if (-not (Test-JsonDateTimeFormat -Value $json.authority.approvedAtUtc)) {
        Add-ValidationError -Errors $errors -Code 'schema.format' -Detail "authority.approvedAtUtc must be RFC3339 date-time; got '$($json.authority.approvedAtUtc)'"
    }

    if (-not (Test-JsonDateTimeFormat -Value $json.validation.reviewedAtUtc)) {
        Add-ValidationError -Errors $errors -Code 'schema.format' -Detail "validation.reviewedAtUtc must be RFC3339 date-time; got '$($json.validation.reviewedAtUtc)'"
    }

    if (-not [string]::Equals([string]$json.subjectId, [string]$SubjectId, [System.StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Code 'subject.mismatch' -Detail "Expected '$SubjectId', got '$($json.subjectId)'"
    }

    if (-not ($json.scope -is [string]) -or -not [string]::Equals([string]$json.scope, 'per-user packaging for this phase', [System.StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Code 'scope.invalid' -Detail "Expected scope 'per-user packaging for this phase'"
    }

    if ($PSBoundParameters.ContainsKey('RequiredPriorStatus') -and -not [string]::Equals([string]$json.resolutionState.priorStatus, [string]$RequiredPriorStatus, [System.StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Code 'resolution.prior-status.mismatch' -Detail "Expected prior status '$RequiredPriorStatus', got '$($json.resolutionState.priorStatus)'"
    }

    $validDispositions = @('approved','rejected','deferred')
    if ($validDispositions -cnotcontains [string]$json.resolutionState.disposition) {
        Add-ValidationError -Errors $errors -Code 'resolution.disposition.invalid' -Detail "Unsupported disposition '$($json.resolutionState.disposition)'"
    }

    if ([string]$json.resolutionState.disposition -cne 'approved') {
        Add-ValidationError -Errors $errors -Code 'resolution.nonfinal' -Detail "disposition must be approved for preflight pass; got '$($json.resolutionState.disposition)'"
    }

    if ([string]$json.validation.status -cne 'PASS') {
        Add-ValidationError -Errors $errors -Code 'validation.status.nonpass' -Detail "validation.status must be PASS; got '$($json.validation.status)'"
    }

    $fieldsDisallowingPlaceholder = @(
        $json.toolchain.product,
        $json.toolchain.version,
        $json.distribution.sourceIdentity,
        $json.distribution.integrityReference,
        $json.constraints,
        $json.authority.source,
        $json.authority.approver,
        $json.validation.reviewedBy
    )

    foreach ($field in $fieldsDisallowingPlaceholder) {
        if (Test-PlaceholderValue -Value $field) {
            Add-ValidationError -Errors $errors -Code 'placeholder.disallowed' -Detail "Placeholder value is not allowed: '$field'"
        }
    }

    if (-not (Test-PinnedVersion -Version ([string]$json.toolchain.version))) {
        Add-ValidationError -Errors $errors -Code 'version.unpinned' -Detail "toolchain.version must be a single immutable version token; got '$($json.toolchain.version)'"
    }

    if (-not [string]::Equals([string]$json.toolchain.product, 'Inno Setup', [System.StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Code 'toolchain.product.invalid' -Detail "toolchain.product must be 'Inno Setup' for W10.1; got '$($json.toolchain.product)'"
    }
}

if ($errors.Count -gt 0) {
    foreach ($errorLine in $errors) {
        [Console]::Error.WriteLine($errorLine)
    }

    exit 1
}

'INSTALLER_TOOLCHAIN_APPROVAL_VALIDATION=PASS'
exit 0
