[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string]$Version,

	# A local SHA256SUMS.txt (for example from a dry run). When omitted, the file is downloaded from
	# the GitHub Release v<Version>.
	[string]$Sha256SumsPath,

	[string]$Repository = 'HaYkMnE/BroChoos-Picker',

	[string]$OutputDir
)

# Fills the winget manifest templates in packaging/winget with the version, release URLs and
# SHA-256 hashes of one release, and writes a ready-to-submit manifest set.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$templateDir = Join-Path $repoRoot 'packaging\winget'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
	throw "Version '$Version' is not X.Y.Z."
}
if ($Repository -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') {
	throw "Repository '$Repository' is not owner/name."
}

# The locale manifest links blob/v<Version>/LICENSE, so that file must exist in the tag.
# Fail closed when git can answer; warn and continue when it cannot (no git, not a repository,
# or the tag is not created or fetched yet).
$licenseObject = "v$($Version):LICENSE"

# Runs git for its exit code only. Under $ErrorActionPreference = 'Stop', Windows PowerShell 5.1
# turns any stderr line of a native command (redirected with 2> or *>) into a terminating
# NativeCommandError, so a plain "not a repository" or "missing object" answer would throw
# before $LASTEXITCODE could be read. EAP is relaxed for this call only and restored afterwards.
function Invoke-GitQuiet {
	param(
		[Parameter(Mandatory = $true)]
		[string]$GitPath,

		[Parameter(Mandatory = $true)]
		[string[]]$Arguments
	)

	$savedPreference = $ErrorActionPreference
	try {
		$ErrorActionPreference = 'Continue'
		& $GitPath @Arguments 2>&1 | Out-Null
		return [int]$LASTEXITCODE
	}
	finally {
		$ErrorActionPreference = $savedPreference
	}
}

$gitCommand = Get-Command -Name 'git' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $gitCommand) {
	Write-Warning "git not found; cannot check that $licenseObject exists. Check it before submitting."
}
elseif ((Invoke-GitQuiet -GitPath $gitCommand.Source -Arguments @('-C', $repoRoot, 'rev-parse', '--git-dir')) -ne 0) {
	Write-Warning "$repoRoot is not a git repository; cannot check that $licenseObject exists."
}
elseif ((Invoke-GitQuiet -GitPath $gitCommand.Source -Arguments @('-C', $repoRoot, 'rev-parse', '--verify', '--quiet', "refs/tags/v$Version")) -ne 0) {
	Write-Warning "Tag v$Version not found locally; cannot check that $licenseObject exists. Fetch the tag and rerun to check."
}
elseif ((Invoke-GitQuiet -GitPath $gitCommand.Source -Arguments @('-C', $repoRoot, 'cat-file', '-e', $licenseObject)) -ne 0) {
	# Written directly (not thrown) so the message reaches stderr as one unwrapped line.
	[Console]::Error.WriteLine("Tag v$Version has no LICENSE at the repository root, but the manifest links $licenseObject. Release a tag that contains LICENSE.")
	exit 1
}
else {
	Write-Host "OK: $licenseObject exists."
}

$installerName = "BroChoos-Picker-Setup-$Version-x64.exe"
$portableName = "BroChoos-Picker-$Version-x64-portable.zip"
$baseUrl = "https://github.com/$Repository/releases/download/v$Version"

if ([string]::IsNullOrWhiteSpace($Sha256SumsPath)) {
	$sumsText = (Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/SHA256SUMS.txt").Content
	if ($sumsText -is [byte[]]) {
		$sumsText = [System.Text.Encoding]::UTF8.GetString($sumsText)
	}
}
else {
	$sumsText = [System.IO.File]::ReadAllText($Sha256SumsPath)
}

$hashes = @{}
foreach ($line in ($sumsText -split "\r?\n")) {
	$match = [regex]::Match($line, '^(?<hash>[0-9A-Fa-f]{64})\s+\*?(?<name>\S.*)$')
	if ($match.Success) {
		$hashes[$match.Groups['name'].Value.Trim()] = $match.Groups['hash'].Value.ToUpperInvariant()
	}
}
foreach ($name in @($installerName, $portableName)) {
	if (-not $hashes.ContainsKey($name)) {
		throw "SHA256SUMS.txt has no entry for $name."
	}
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
	$OutputDir = Join-Path $repoRoot "artifacts\winget\$Version"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$values = [ordered]@{
	'__VERSION__' = $Version
	'__INSTALLER_URL__' = "$baseUrl/$installerName"
	'__INSTALLER_SHA256__' = $hashes[$installerName]
	'__PORTABLE_URL__' = "$baseUrl/$portableName"
	'__PORTABLE_SHA256__' = $hashes[$portableName]
}

$templates = @(Get-ChildItem -LiteralPath $templateDir -Filter '*.yaml' -File)
if ($templates.Count -ne 3) {
	throw "Expected 3 winget templates in $templateDir, found $($templates.Count)."
}

foreach ($template in $templates) {
	$text = [System.IO.File]::ReadAllText($template.FullName)
	foreach ($key in $values.Keys) {
		$text = $text.Replace($key, $values[$key])
	}
	$left = [regex]::Match($text, '__[A-Z0-9_]+__')
	if ($left.Success) {
		throw "Unfilled placeholder $($left.Value) in $($template.Name)."
	}

	$target = Join-Path $OutputDir $template.Name
	[System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($false)))
	Write-Host "Wrote $target"
}

exit 0
