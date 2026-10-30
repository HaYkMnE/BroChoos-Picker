[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string]$Version,

	[Parameter(Mandatory = $true)]
	[string]$OutputDir,

	[string]$IsccPath
)

# Release build used by .github/workflows/release.yml and by local dry runs (same commands).
#
#   1. publish + installer: the existing fail-closed packager
#      (packaging/BroChoos.Installer/package-per-user-installer.ps1) publishes SELF-CONTAINED win-x64
#      and compiles the Inno Setup installer with the same /DAppVersion.
#   2. proves the publish folder is self-contained: runtime files present, every shipped exe has a
#      self-contained runtimeconfig, and BroChoos.exe plus each helper start in self-contained mode.
#   3. builds the portable zip with the .portable marker next to BroChoos.exe.
#   4. writes SHA256SUMS.txt.
#
# Release assets land in -OutputDir; diagnostics land in -OutputDir\logs.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$configuration = 'Release'
$runtime = 'win-x64'
$packager = Join-Path $repoRoot 'packaging\BroChoos.Installer\package-per-user-installer.ps1'
# The folder the packager publishes to and ISCC packages from (packager: $publishDir).
$publishDir = Join-Path $repoRoot "src\BroChoos.App\bin\$configuration\net10.0-windows10.0.19041.0\$runtime\publish"
# OutputDir in BroChoos.per-user.iss, relative to the script: <repo>\artifacts\installer.
$installerBuildDir = Join-Path $repoRoot 'artifacts\installer'
# Must equal StorageRootResolver.PortableMarkerFileName (ReleasePipelineTests pins both).
$portableMarker = '.portable'
$appExecutable = 'BroChoos.exe'
$helperExecutables = @(
	'BroChoos.LaunchHelper.exe',
	'BroChoos.StorageHelper.exe',
	'BroChoos.NetworkHelper.exe',
	'BroChoos.UpdateHelper.exe'
)
# Same payload hygiene as the installer's [Files] Excludes.
$excludedExtensions = @('.pdb', '.xml')

# X.Y.Z only. A prerelease suffix is not supported yet: the Inno version resource of such a build
# has not been verified.
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
	throw "Version '$Version' is not X.Y.Z."
}

$installerName = "BroChoos-Picker-Setup-$Version-x64.exe"
$portableName = "BroChoos-Picker-$Version-x64-portable.zip"
$sumsName = 'SHA256SUMS.txt'

# A stale publish folder (for example an older framework-dependent publish) would leak files into
# the release, so the build refuses to reuse one instead of deleting it.
if (Test-Path -LiteralPath $publishDir) {
	throw "Publish folder already exists: $publishDir. Remove it, then rerun so the release is built from a clean publish."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
if (@(Get-ChildItem -LiteralPath $OutputDir -Force).Count -gt 0) {
	throw "OutputDir must be empty: $OutputDir"
}

$logDir = Join-Path $OutputDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Invoke-SmokeRun {
	param(
		[Parameter(Mandatory = $true)]
		[string]$ExecutablePath,

		[string[]]$Arguments = @(),

		[Parameter(Mandatory = $true)]
		[string]$Name
	)

	$stdoutPath = Join-Path $logDir "$Name.stdout.txt"
	$stderrPath = Join-Path $logDir "$Name.stderr.txt"
	$stdinPath = Join-Path $logDir "$Name.stdin.txt"
	$tracePath = Join-Path $logDir "$Name.host-trace.txt"
	[System.IO.File]::WriteAllText($stdinPath, '')

	# Host tracing records how the apphost resolved the runtime. A self-contained app must report
	# self-contained mode and must not go looking for a globally installed .NET.
	$env:COREHOST_TRACE = '1'
	$env:COREHOST_TRACEFILE = $tracePath
	try {
		$startArgs = @{
			FilePath = $ExecutablePath
			NoNewWindow = $true
			PassThru = $true
			RedirectStandardInput = $stdinPath
			RedirectStandardOutput = $stdoutPath
			RedirectStandardError = $stderrPath
		}
		if ($Arguments.Count -gt 0) {
			$startArgs['ArgumentList'] = $Arguments
		}

		$process = Start-Process @startArgs
		$null = $process.Handle
		if (-not $process.WaitForExit(60000)) {
			$process.Kill()
			throw "$Name did not exit within 60 s."
		}
	}
	finally {
		Remove-Item Env:\COREHOST_TRACE -ErrorAction SilentlyContinue
		Remove-Item Env:\COREHOST_TRACEFILE -ErrorAction SilentlyContinue
	}

	$trace = [System.IO.File]::ReadAllText($tracePath)
	$modeLine = @($trace -split "\r?\n" | Where-Object { $_ -match '(?i)self-contained|framework-dependent' } | Select-Object -First 1)
	if ($modeLine.Count -eq 0 -or $modeLine[0] -notmatch '(?i)self-contained') {
		throw "$Name did not start in self-contained mode (host trace: $tracePath)."
	}

	return @{
		ExitCode = [int]$process.ExitCode
		Stdout = [System.IO.File]::ReadAllText($stdoutPath)
		ModeLine = $modeLine[0].Trim()
	}
}

# 1. Self-contained publish + installer through the existing packager.
$packagerArgs = @(
	'-NoProfile', '-ExecutionPolicy', 'Bypass',
	'-File', $packager,
	'-Configuration', $configuration,
	'-Runtime', $runtime,
	'-Version', $Version
)
if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
	$packagerArgs += @('-IsccPath', $IsccPath)
}

# A PowerShell 7 parent leaks its PSModulePath, and the Windows PowerShell 5.1 child then cannot
# autoload its own modules (Microsoft.PowerShell.Security). Cleared, 5.1 rebuilds its default.
$parentModulePath = $env:PSModulePath
try {
	$env:PSModulePath = $null
	& powershell @packagerArgs
}
finally {
	$env:PSModulePath = $parentModulePath
}
if ($LASTEXITCODE -ne 0) {
	throw "Packager failed with exit code $LASTEXITCODE."
}

# 2. Self-contained proof.
foreach ($runtimeFile in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
	if (-not (Test-Path -LiteralPath (Join-Path $publishDir $runtimeFile) -PathType Leaf)) {
		throw "Publish folder is missing $runtimeFile, so it is not self-contained."
	}
}

# Excluded on purpose in BroChoos.App.csproj (ExcludeCreateDumpFromPublish); the smoke runs below
# prove the exe and the helpers start without it.
if (Test-Path -LiteralPath (Join-Path $publishDir 'createdump.exe')) {
	throw 'Publish folder contains createdump.exe; the ExcludeCreateDumpFromPublish target did not run.'
}

foreach ($executable in @($appExecutable) + $helperExecutables) {
	if (-not (Test-Path -LiteralPath (Join-Path $publishDir $executable) -PathType Leaf)) {
		throw "Publish folder is missing $executable."
	}

	$runtimeConfigPath = Join-Path $publishDir ([System.IO.Path]::ChangeExtension($executable, '.runtimeconfig.json'))
	$runtimeOptions = (Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json).runtimeOptions
	$names = @($runtimeOptions.PSObject.Properties | ForEach-Object { $_.Name })
	if ($names -notcontains 'includedFrameworks' -or $names -contains 'framework' -or $names -contains 'frameworks') {
		throw "$executable has a framework-dependent runtimeconfig ($runtimeConfigPath)."
	}
}

$help = Invoke-SmokeRun -ExecutablePath (Join-Path $publishDir $appExecutable) -Arguments @('--help') -Name 'app-help'
if ($help.ExitCode -ne 0 -or $help.Stdout -notmatch 'Usage:') {
	throw "BroChoos.exe --help exited $($help.ExitCode) or printed no usage text."
}
Write-Host "SMOKE BroChoos.exe --help exit=0 mode='$($help.ModeLine)'"

# Each helper is started with no arguments: it must reach its own Main (an argument-error exit code
# in 0..255), not fail inside the host with a 0x8000xxxx "runtime not found" code.
foreach ($helper in $helperExecutables) {
	$result = Invoke-SmokeRun -ExecutablePath (Join-Path $publishDir $helper) -Name ([System.IO.Path]::GetFileNameWithoutExtension($helper))
	if ($result.ExitCode -lt 0 -or $result.ExitCode -gt 255) {
		throw "$helper failed in the .NET host (exit $($result.ExitCode))."
	}
	Write-Host "SMOKE $helper exit=$($result.ExitCode) mode='$($result.ModeLine)'"
}

# 3. Installer asset.
$builtInstaller = Join-Path $installerBuildDir $installerName
if (-not (Test-Path -LiteralPath $builtInstaller -PathType Leaf)) {
	throw "Installer not found: $builtInstaller"
}
$installerPath = Join-Path $OutputDir $installerName
Copy-Item -LiteralPath $builtInstaller -Destination $installerPath
$installerVersion = ([string][System.Diagnostics.FileVersionInfo]::GetVersionInfo($installerPath).ProductVersion).Trim()
if ($installerVersion -ne $Version) {
	throw "Installer ProductVersion '$installerVersion' does not equal '$Version'."
}

# 4. Portable zip: the publish folder (minus *.pdb/*.xml) at the archive root, plus the marker.
# Entries are written one by one so names always use '/' separators.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portablePath = Join-Path $OutputDir $portableName
$publishRoot = (Resolve-Path -LiteralPath $publishDir).Path.TrimEnd('\') + '\'
$zipStream = [System.IO.File]::Open($portablePath, [System.IO.FileMode]::CreateNew)
$archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
	foreach ($file in @(Get-ChildItem -LiteralPath $publishDir -Recurse -File | Sort-Object FullName)) {
		if ($excludedExtensions -contains $file.Extension.ToLowerInvariant()) {
			continue
		}
		if ($file.Name -eq $portableMarker) {
			throw "The publish folder already contains a $portableMarker marker; the installed build must not be portable."
		}

		$entryName = $file.FullName.Substring($publishRoot.Length).Replace('\', '/')
		$null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
	}

	$markerEntry = $archive.CreateEntry($portableMarker)
	$markerWriter = New-Object System.IO.StreamWriter($markerEntry.Open(), (New-Object System.Text.UTF8Encoding($false)))
	try {
		$markerWriter.Write("BroChoos Picker portable mode: settings are kept in the BroChoosConfig folder next to BroChoos.exe.`r`n")
	}
	finally {
		$markerWriter.Dispose()
	}
}
finally {
	$archive.Dispose()
	$zipStream.Dispose()
}

$readBack = [System.IO.Compression.ZipFile]::OpenRead($portablePath)
try {
	$entryNames = @($readBack.Entries | ForEach-Object { $_.FullName })
}
finally {
	$readBack.Dispose()
}
foreach ($required in @($portableMarker, $appExecutable, 'hostfxr.dll', 'coreclr.dll') + $helperExecutables) {
	if ($entryNames -notcontains $required) {
		throw "Portable zip is missing $required at its root."
	}
}

# The extracted zip must start on its own.
$extractDir = Join-Path $OutputDir 'smoke-portable'
[System.IO.Compression.ZipFile]::ExtractToDirectory($portablePath, $extractDir)
$portableHelp = Invoke-SmokeRun -ExecutablePath (Join-Path $extractDir $appExecutable) -Arguments @('--help') -Name 'portable-help'
if ($portableHelp.ExitCode -ne 0) {
	throw "Extracted portable BroChoos.exe --help exited $($portableHelp.ExitCode)."
}
Write-Host "SMOKE portable BroChoos.exe --help exit=0 mode='$($portableHelp.ModeLine)'"

# 5. SHA256SUMS.txt in sha256sum format (lowercase hex, two spaces, file name, LF).
$sumLines = foreach ($asset in @($installerName, $portableName)) {
	$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $OutputDir $asset)).Hash.ToLowerInvariant()
	"$hash  $asset"
}
[System.IO.File]::WriteAllText((Join-Path $OutputDir $sumsName), (($sumLines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

foreach ($asset in @($installerName, $portableName, $sumsName)) {
	$item = Get-Item -LiteralPath (Join-Path $OutputDir $asset)
	$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash.ToLowerInvariant()
	Write-Host ("ASSET {0} bytes={1} sha256={2}" -f $item.Name, $item.Length, $hash)
}

'RELEASE_BUILD=PASS'
exit 0
