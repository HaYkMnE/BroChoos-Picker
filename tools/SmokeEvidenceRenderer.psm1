Set-StrictMode -Version Latest

function Protect-SmokeEvidenceValue {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string] $Value,
        [switch] $UnsafePassThrough
    )

    if ($null -eq $Value) {
        return ''
    }

    if ($UnsafePassThrough) {
        return $Value
    }

    $text = $Value

    # Absolute user and machine-local path redaction.
    $text = [regex]::Replace($text, '[A-Za-z]:\\Users\\[^\\\r\n]+(?:\\[^\r\n]*)?', '<redacted:user-path>')
    $text = [regex]::Replace($text, '[A-Za-z]:\\[^\r\n]*', '<redacted:path>')

    # URL query/token redaction while preserving scheme/host/path.
    $text = [regex]::Replace($text, '(?i)(https?://[^?\s]+)\?[^\s\r\n]+', '$1?<redacted-query>')

    # UserChoice snapshot redaction: ProgId=[..], Hash=[..], and every key the smoke scripts'
    # Read-UserChoiceSnapshot emits: http=[ProgId|Hash], https=[ProgId|Hash], .url=[ProgId|Hash]
    # (registration-smoke.ps1 builds all three; url-activation-sim.ps1 builds http and https).
    # The key is kept, the bracketed value is replaced.
    $text = [regex]::Replace($text, '(?i)(?<!\w)(progid|hash|https?|\.url)=\[[^\]]*\]', '$1=[<redacted-userchoice>]')
    # Token-like sentinel redaction.
    $text = [regex]::Replace($text, '(?i)SENTINEL_[A-Z0-9_\-]+', '<redacted:token>')

    return $text
}

function Write-SmokeEvidenceRender {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Fields,
        [switch] $UnsafePassThrough
    )

    foreach ($key in @($Fields.Keys | Sort-Object)) {
        $safe = Protect-SmokeEvidenceValue -Value ([string]$Fields[$key]) -UnsafePassThrough:$UnsafePassThrough
        Write-Host ("{0}: {1}" -f $key, $safe)
    }
}

Export-ModuleMember -Function Protect-SmokeEvidenceValue, Write-SmokeEvidenceRender
