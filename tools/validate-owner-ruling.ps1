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

function Add-Error {
    param(
        [System.Collections.Generic.List[string]]$Errors,
        [string]$Code,
        [string]$Detail
    )
    $Errors.Add("$Code :: $Detail") | Out-Null
}

function Get-NextStepsSummary {
    param(
        [System.Collections.Generic.List[string]]$Errors,
        [string]$SubjectId
    )

    if ($Errors.Count -eq 0) {
        return $null
    }

    $codes = @{}
    foreach ($e in $Errors) {
        $parts = $e -split '::', 2
        $code = $parts[0].Trim()
        if (-not [string]::IsNullOrWhiteSpace($code)) {
            $codes[$code] = $true
        }
    }

    $actions = [System.Collections.Generic.List[string]]::new()

    if ($codes.ContainsKey('record.missing')) {
        $actions.Add('Create the owner-ruling record file at the canonical location.') | Out-Null
    }
    if ($codes.ContainsKey('record.path.invalid')) {
        $actions.Add('Use the canonical record path for this subject (no alternate filename).') | Out-Null
    }
    if ($codes.ContainsKey('record.invalid-json')) {
        $actions.Add('Fix JSON syntax and rerun validator.') | Out-Null
    }
    if ($codes.ContainsKey('schema.required')) {
        $actions.Add('Add all schema-required top-level fields and rerun validator.') | Out-Null
    }
    if ($codes.ContainsKey('schema.additional-property')) {
        $actions.Add('Remove fields not declared in owner-ruling.schema.json (additionalProperties is false).') | Out-Null
    }
    if ($codes.ContainsKey('subject.mismatch')) {
        $actions.Add('Set subjectId to match the validator SubjectId input exactly.') | Out-Null
    }
    if ($codes.ContainsKey('scope.invalid')) {
        $actions.Add('Set scope exactly to local Dev Runner IPC only for this subject.') | Out-Null
    }
    if ($codes.ContainsKey('authority.source.missing') -or $codes.ContainsKey('authority.approver.missing') -or $codes.ContainsKey('authority.source.unapproved')) {
        $actions.Add('Provide valid authority metadata: source DH-2026-09-11 and non-placeholder approver identity.') | Out-Null
    }
    if ($codes.ContainsKey('resolution.prior-status.mismatch')) {
        $actions.Add('Set resolutionState.priorStatus to the required status supplied to validator.') | Out-Null
    }
    if ($codes.ContainsKey('resolution.disposition.invalid') -or $codes.ContainsKey('resolution.nonfinal')) {
        $actions.Add('Set resolutionState.disposition according to intended gate state; production-unblock requires approved.') | Out-Null
    }
    if ($codes.ContainsKey('validation.status.nonpass')) {
        $actions.Add('Set validation.status to PASS only after independent authority review is complete.') | Out-Null
    }
    if ($codes.ContainsKey('placeholder.disallowed')) {
        $actions.Add('Replace placeholder values with final owner-authorized values.') | Out-Null
    }
    if ($codes.ContainsKey('trust.fixture-forbidden')) {
        $actions.Add('Set publicTrustMetadata.testOnly=false for production trust usage records.') | Out-Null
    }
    if ($codes.ContainsKey('trust.metadata.missing')) {
        $actions.Add('Add publicTrustMetadata with explicit testOnly=false for production trust usage records.') | Out-Null
    }
    if ($codes.ContainsKey('trust.metadata.invalid') -or $codes.ContainsKey('schema.type')) {
        $actions.Add('Use a JSON boolean (true/false) for publicTrustMetadata.testOnly.') | Out-Null
    }

    if ($actions.Count -eq 0) {
        $actions.Add('Resolve reported validation errors and rerun validator.') | Out-Null
    }

    $deduped = $actions | Select-Object -Unique
    return "NEXT_STEPS :: Subject=$SubjectId :: " + ($deduped -join ' | ')
}

# Host-independent text for a JSON scalar. PowerShell 7 ConvertFrom-Json turns ISO timestamps
# into [DateTime]; a plain [string] cast would then produce a culture-specific form
# ('09/12/2026 00:00:00') that no placeholder token can match, while Windows PowerShell 5.1 keeps
# the original string. DateTime/DateTimeOffset are therefore rendered as ISO UTC, which is the
# form the placeholder tokens (and the record files) use.
function ConvertTo-StableText {
    param([object]$Value)

    if ($null -eq $Value) { return '' }
    if ($Value -is [System.DateTimeOffset]) {
        return $Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [System.DateTime]) {
        $utc = if ($Value.Kind -eq [System.DateTimeKind]::Unspecified) { [System.DateTime]::SpecifyKind($Value, [System.DateTimeKind]::Utc) } else { $Value.ToUniversalTime() }
        return $utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    return [string]$Value
}

function Test-Placeholder {
    param([object]$Value)

    if ($null -eq $Value) { return $true }
    $s = ConvertTo-StableText -Value $Value
    if ([string]::IsNullOrWhiteSpace($s)) { return $true }

    $tokens = @(
        'TBD',
        'TODO',
        'PLACEHOLDER',
        'example.invalid',
        '1970-01-01T00:00:00Z',
        'changeme',
        'dummy'
    )

    foreach ($token in $tokens) {
        if ($s.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

# Safe property access under Set-StrictMode -Version Latest: reading a missing
# property directly (e.g. $json.publicTrustMetadata) throws PropertyNotFoundException.
# These helpers return $null / $false instead so missing optional fields yield
# coded diagnostics rather than raw exceptions.
function Get-PropertyValue {
    param([object]$Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Test-HasProperty {
    param([object]$Object, [string]$Name)

    if ($null -eq $Object) { return $false }
    return ($null -ne $Object.PSObject.Properties[$Name])
}

function Test-ObjectNode {
    param([object]$Value)

    return ($null -ne $Value -and $Value -is [System.Management.Automation.PSCustomObject])
}

$errors = [System.Collections.Generic.List[string]]::new()
$devRunnerSubject = 'DEV-RUNNER-IPC-TRUST-ANCHOR'

if (-not (Test-Path -LiteralPath $Record -PathType Leaf)) {
    Add-Error -Errors $errors -Code 'record.missing' -Detail "Record not found: $Record"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$schemaPath = Join-Path $repoRoot 'docs\decisions\owner-ruling.schema.json'
if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
    Add-Error -Errors $errors -Code 'schema.missing' -Detail "Schema not found: $schemaPath"
}

if ($errors.Count -eq 0 -and $SubjectId -eq $devRunnerSubject) {
    $expectedRecordPath = Join-Path $repoRoot 'docs\decisions\owner-rulings\dev-runner-ipc-trust-anchor.json'
    $expectedNormalized = [System.IO.Path]::GetFullPath($expectedRecordPath.Replace('/', '\'))
    $recordNormalized = [System.IO.Path]::GetFullPath($Record.Replace('/', '\'))

    if (-not [string]::Equals($recordNormalized, $expectedNormalized, [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-Error -Errors $errors -Code 'record.path.invalid' -Detail "Expected canonical record path '$expectedNormalized', got '$recordNormalized'"
    }
}

if ($errors.Count -eq 0) {
    try {
        $jsonRaw = Get-Content -LiteralPath $Record -Raw -Encoding UTF8
        $json = $jsonRaw | ConvertFrom-Json
    }
    catch {
        Add-Error -Errors $errors -Code 'record.invalid-json' -Detail $_.Exception.Message
    }
}

if ($errors.Count -eq 0) {
    $requiredTop = @('schemaVersion','recordVersion','subjectId','resolutionState','scope','signerIdentityModel','trustAnchorLifecycle','authority','validation')
    foreach ($name in $requiredTop) {
        if (-not (Test-HasProperty -Object $json -Name $name)) {
            Add-Error -Errors $errors -Code 'schema.required' -Detail "Missing top-level field: $name"
        }
    }
}

# Enforce the schema's additionalProperties:false declarations
# (docs/decisions/owner-ruling.schema.json). Top-level plus the declared nested
# objects; unknown keys are rejected so records cannot smuggle undeclared state.
if ($errors.Count -eq 0) {
    $allowedTop = @('schemaVersion','recordVersion','subjectId','resolutionState','scope','signerIdentityModel','trustAnchorLifecycle','authority','validation','publicTrustMetadata','notes')
    foreach ($prop in @($json.PSObject.Properties.Name)) {
        if ($allowedTop -cnotcontains [string]$prop) {
            Add-Error -Errors $errors -Code 'schema.additional-property' -Detail "Undeclared top-level field: $prop"
        }
    }

    $nestedSchema = @{
        resolutionState    = @('disposition','priorStatus')
        trustAnchorLifecycle = @('provisioning','storage','rotation','revocation','constraints')
        authority          = @('source','approver','approvedAtUtc')
        validation         = @('status','reviewedBy','reviewedAtUtc')
    }
    foreach ($nodeName in $nestedSchema.Keys) {
        $node = Get-PropertyValue -Object $json -Name $nodeName
        if (Test-ObjectNode -Value $node) {
            foreach ($prop in @($node.PSObject.Properties.Name)) {
                if ($nestedSchema[$nodeName] -cnotcontains [string]$prop) {
                    Add-Error -Errors $errors -Code 'schema.additional-property' -Detail "Undeclared field in ${nodeName}: $prop"
                }
            }
        }
    }
}

# Nested required fields per schema (clean coded diagnostics when absent).
if ($errors.Count -eq 0) {
    $nestedRequired = @{
        resolutionState      = @('disposition','priorStatus')
        trustAnchorLifecycle = @('provisioning','storage','rotation','revocation','constraints')
        authority            = @('source','approver','approvedAtUtc')
        validation           = @('status','reviewedBy','reviewedAtUtc')
    }
    foreach ($nodeName in $nestedRequired.Keys) {
        $node = Get-PropertyValue -Object $json -Name $nodeName
        if (-not (Test-ObjectNode -Value $node)) {
            Add-Error -Errors $errors -Code 'schema.required' -Detail "Missing required object: $nodeName"
            continue
        }
        foreach ($field in $nestedRequired[$nodeName]) {
            if (-not (Test-HasProperty -Object $node -Name $field)) {
                Add-Error -Errors $errors -Code 'schema.required' -Detail "Missing required field: $nodeName.$field"
            }
        }
    }
}

if ($errors.Count -eq 0) {
    # NOTE: local names must not collide with script parameters. PowerShell
    # variable names are case-insensitive, so a local `$subjectId` would
    # overwrite the `$SubjectId` parameter and silently defeat this check.
    $recordSubjectId = [string](Get-PropertyValue -Object $json -Name 'subjectId')
    if (-not [string]::Equals($recordSubjectId, [string]$SubjectId, [System.StringComparison]::Ordinal)) {
        Add-Error -Errors $errors -Code 'subject.mismatch' -Detail "Expected '$SubjectId', got '$recordSubjectId'"
    }

    $recordScope = [string](Get-PropertyValue -Object $json -Name 'scope')
    # The subject DETECTION stays case-insensitive on purpose (a case variant of the dev-runner
    # subject must still be held to the dev-runner scope); the scope comparison itself is ordinal.
    if ($recordSubjectId -eq $devRunnerSubject -and -not [string]::Equals($recordScope, 'local Dev Runner IPC only', [System.StringComparison]::Ordinal)) {
        Add-Error -Errors $errors -Code 'scope.invalid' -Detail "Expected scope 'local Dev Runner IPC only'"
    }

    $authorityNode = Get-PropertyValue -Object $json -Name 'authority'
    $authoritySource = [string](Get-PropertyValue -Object $authorityNode -Name 'source')
    $authorityApprover = [string](Get-PropertyValue -Object $authorityNode -Name 'approver')

    if ([string]::IsNullOrWhiteSpace($authoritySource)) {
        Add-Error -Errors $errors -Code 'authority.source.missing' -Detail 'authority.source is required'
    }

    if ([string]::IsNullOrWhiteSpace($authorityApprover)) {
        Add-Error -Errors $errors -Code 'authority.approver.missing' -Detail 'authority.approver is required'
    }

    $resolutionNode = Get-PropertyValue -Object $json -Name 'resolutionState'
    $disposition = [string](Get-PropertyValue -Object $resolutionNode -Name 'disposition')

    $validDispositions = @('approved','rejected','deferred')
    if ($validDispositions -cnotcontains $disposition) {
        Add-Error -Errors $errors -Code 'resolution.disposition.invalid' -Detail "Unsupported disposition '$disposition'"
    }

    if ($PSBoundParameters.ContainsKey('RequiredPriorStatus')) {
        $priorStatus = [string](Get-PropertyValue -Object $resolutionNode -Name 'priorStatus')
        if (-not [string]::Equals($priorStatus, [string]$RequiredPriorStatus, [System.StringComparison]::Ordinal)) {
            Add-Error -Errors $errors -Code 'resolution.prior-status.mismatch' -Detail "Expected prior status '$RequiredPriorStatus', got '$priorStatus'"
        }
    }

    $lifecycleNode = Get-PropertyValue -Object $json -Name 'trustAnchorLifecycle'
    $validationNode = Get-PropertyValue -Object $json -Name 'validation'

    foreach ($field in @(
        (Get-PropertyValue -Object $json -Name 'signerIdentityModel'),
        (Get-PropertyValue -Object $lifecycleNode -Name 'provisioning'),
        (Get-PropertyValue -Object $lifecycleNode -Name 'storage'),
        (Get-PropertyValue -Object $lifecycleNode -Name 'rotation'),
        (Get-PropertyValue -Object $lifecycleNode -Name 'revocation'),
        (Get-PropertyValue -Object $lifecycleNode -Name 'constraints'),
        $authorityApprover,
        (Get-PropertyValue -Object $authorityNode -Name 'approvedAtUtc'),
        (Get-PropertyValue -Object $validationNode -Name 'reviewedBy'),
        (Get-PropertyValue -Object $validationNode -Name 'reviewedAtUtc')
    )) {
        if (Test-Placeholder -Value $field) {
            Add-Error -Errors $errors -Code 'placeholder.disallowed' -Detail "Placeholder value is not allowed: '$(ConvertTo-StableText -Value $field)'"
        }
    }

    $validationStatus = [string](Get-PropertyValue -Object $validationNode -Name 'status')
    if ($validationStatus -cne 'PASS') {
        Add-Error -Errors $errors -Code 'validation.status.nonpass' -Detail "validation.status must be PASS for production use; got '$validationStatus'"
    }

    if ($disposition -cne 'approved') {
        Add-Error -Errors $errors -Code 'resolution.nonfinal' -Detail "disposition must be approved for production use; got '$disposition'"
    }

    # publicTrustMetadata is optional in the JSON schema, but this validator
    # enforces the production-trust requirement from the owner input packet:
    # an explicit testOnly=false must be present, otherwise a stripped fixture
    # record could pass silently. Missing/invalid values yield coded errors.
    $publicTrustNode = Get-PropertyValue -Object $json -Name 'publicTrustMetadata'
    if (-not (Test-ObjectNode -Value $publicTrustNode)) {
        Add-Error -Errors $errors -Code 'trust.metadata.missing' -Detail 'publicTrustMetadata with explicit testOnly=false is required for production trust usage'
    }
    elseif (-not (Test-HasProperty -Object $publicTrustNode -Name 'testOnly')) {
        Add-Error -Errors $errors -Code 'trust.metadata.missing' -Detail 'publicTrustMetadata.testOnly is required for production trust usage'
    }
    else {
        $testOnlyValue = Get-PropertyValue -Object $publicTrustNode -Name 'testOnly'
        if ($testOnlyValue -isnot [bool]) {
            Add-Error -Errors $errors -Code 'trust.metadata.invalid' -Detail "publicTrustMetadata.testOnly must be a boolean; got '$testOnlyValue'"
        }
        elseif ($testOnlyValue) {
            Add-Error -Errors $errors -Code 'trust.fixture-forbidden' -Detail 'testOnly trust metadata cannot be used for production trust provider selection'
        }
    }

    if ($authoritySource -cne 'DH-2026-09-11') {
        Add-Error -Errors $errors -Code 'authority.source.unapproved' -Detail "Expected authority source DH-2026-09-11; got '$authoritySource'"
    }
}

if ($errors.Count -gt 0) {
    foreach ($e in $errors) {
        [Console]::Error.WriteLine($e)
    }
    $next = Get-NextStepsSummary -Errors $errors -SubjectId $SubjectId
    if ($next) {
        [Console]::Error.WriteLine($next)
    }
    exit 1
}

"OWNER_RULING_VALIDATION=PASS"
exit 0
