[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string]$Version,

	[Parameter(Mandatory = $true)]
	[string]$OutFile,

	# Default (repository-root CHANGELOG.md) is resolved in the body: under Windows PowerShell 5.1
	# -File, this advanced script's param default saw an empty $PSScriptRoot.
	[string]$ChangelogPath = '',

	[string]$Repository = 'HaYkMnE/BroChoos-Picker'
)

# Writes GitHub Release notes for one version: the matching CHANGELOG.md section, or a generic
# note when the file or the section is missing, followed by a fixed download/verify footer.
# Tolerant on purpose: the changelog is maintained elsewhere, and a missing section must never
# block a release. Accepted headings (levels 1-3, case-insensitive):
#   ## [1.2.3]   ## 1.2.3   ## v1.2.3   ## [v1.2.3] - 2026-10-29   ## Version 1.2.3 (date)
# The section ends at the next heading of the same or a higher level.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($ChangelogPath)) {
	$ChangelogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'
}

function Get-ChangelogSection {
	param(
		[Parameter(Mandatory = $true)]
		[AllowEmptyString()]
		[string]$Text,

		[Parameter(Mandatory = $true)]
		[string]$SectionVersion
	)

	$escaped = [regex]::Escape($SectionVersion)
	$headingPattern = '^(?<level>#{1,3})\s+(?:version\s+)?\[?v?' + $escaped + '\]?(?:[\s:(].*)?$'
	$lines = $Text -split "\r?\n"

	$start = -1
	$level = 0
	for ($index = 0; $index -lt $lines.Count; $index++) {
		$match = [regex]::Match($lines[$index].Trim(), $headingPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
		if ($match.Success) {
			$start = $index + 1
			$level = $match.Groups['level'].Value.Length
			break
		}
	}

	if ($start -lt 0) {
		return $null
	}

	$body = New-Object System.Collections.Generic.List[string]
	for ($index = $start; $index -lt $lines.Count; $index++) {
		$next = [regex]::Match($lines[$index], '^(#{1,6})\s')
		if ($next.Success -and $next.Groups[1].Value.Length -le $level) {
			break
		}
		$body.Add($lines[$index])
	}

	$section = ($body -join "`n").Trim()
	if ([string]::IsNullOrWhiteSpace($section)) {
		return $null
	}

	return $section
}

$notes = $null
if (Test-Path -LiteralPath $ChangelogPath -PathType Leaf) {
	$notes = Get-ChangelogSection -Text ([System.IO.File]::ReadAllText($ChangelogPath)) -SectionVersion $Version
	if ($null -eq $notes) {
		Write-Host "CHANGELOG.md has no section for $Version; using the generic note."
	}
	else {
		Write-Host "Using the CHANGELOG.md section for $Version."
	}
}
else {
	Write-Host "CHANGELOG.md not found at $ChangelogPath; using the generic note."
}

if ($null -eq $notes) {
	$notes = "BroChoos Picker $Version for Windows 10 and 11 (x64)."
}

# Single-quoted here-string: Markdown backticks must not be read as PowerShell escapes.
$footer = @'
## Downloads

- `BroChoos-Picker-Setup-{VERSION}-x64.exe`: per-user installer. No administrator rights needed.
- `BroChoos-Picker-{VERSION}-x64-portable.zip`: portable build. Settings stay in the `BroChoosConfig` folder next to `BroChoos.exe`.

Both include the .NET runtime, so nothing else needs to be installed.

## Verify the download

These files are not code-signed, so Windows SmartScreen may warn the first time you run them. You can check them first:

- SHA-256: compare `Get-FileHash .\<file> -Algorithm SHA256` with `SHA256SUMS.txt`.
- Build provenance: `gh attestation verify .\<file> --repo {REPO}`
'@

$footer = $footer.Replace('{VERSION}', $Version).Replace('{REPO}', $Repository)
$content = $notes + "`n`n" + $footer.Replace("`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText($OutFile, $content, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Release notes written to $OutFile"
exit 0
