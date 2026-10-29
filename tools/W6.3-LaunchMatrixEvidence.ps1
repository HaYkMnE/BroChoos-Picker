<#
.SYNOPSIS
  W6.3 live four-browser launch-matrix evidence runner (EXPLICIT, MANUAL ONLY).

.DESCRIPTION
  Produces the four-browser live launch-matrix evidence the W6.3 plan and Gate 0B
  criterion 0B-1 require (Chrome, Edge, Firefox, Brave).

  This script is NEVER invoked by `dotnet test`. It opens REAL browsers, so it is
  gated behind explicit switches:
    - -WhatIf   : reports availability + planned argv WITHOUT launching anything.
    - -Execute  : actually launches each present browser (opens visible windows).

  Honesty rules (P4):
    * A browser that is not present on this machine is recorded as
      `not-available-on-machine` and is NEVER reported as launched.
    * A present browser whose launch returns non-Success is recorded with its real
      typed status, never massaged to Success.
    * The script never fabricates a row and never claims a family passed when its
      executable was absent.

  The launch itself goes through the product's own structured-argv contract
  (ProcessStartInfo.ArgumentList, UseShellExecute=false), matching WindowsBrowserLauncher /
  LaunchHelper. It runs against the REAL browser executables; user data keeps its
  default profile unless -ProfileId / -Private are supplied.

.EXAMPLE
  pwsh -File tools/W6.3-LaunchMatrixEvidence.ps1 -WhatIf
  pwsh -File tools/W6.3-LaunchMatrixEvidence.ps1 -Execute -OutFile artifacts/evidence/live-four-browser-launch-matrix.txt
#>
[CmdletBinding()]
param(
    [switch]$WhatIf,
    [switch]$Execute,
    [string]$Url = 'https://example.com/',
    [string]$ProfileId,
    [switch]$Private,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

if (-not $WhatIf -and -not $Execute) {
    Write-Error 'Refusing to run: pass -WhatIf (report only) or -Execute (open real browsers). Nothing was executed.'
}

# Declared Gate 0B families, each pinned to its canonical stable executable. Same
# templates the product's WindowsBrowserDetector uses for its "known-path" tier.
$families = @(
    [pscustomobject]@{
        Family      = 'chrome'
        DisplayName = 'Google Chrome'
        Channel     = 'stable'
        Templates   = @('%ProgramFiles%\Google\Chrome\Application\chrome.exe',
                         '%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe',
                         '%LocalAppData%\Google\Chrome\Application\chrome.exe')
    }
    [pscustomobject]@{
        Family      = 'edge'
        DisplayName = 'Microsoft Edge'
        Channel     = 'stable'
        Templates   = @('%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe',
                         '%ProgramFiles%\Microsoft\Edge\Application\msedge.exe')
    }
    [pscustomobject]@{
        Family      = 'firefox'
        DisplayName = 'Mozilla Firefox'
        Channel     = 'stable'
        Templates   = @('%ProgramFiles%\Mozilla Firefox\firefox.exe',
                         '%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe')
    }
    [pscustomobject]@{
        Family      = 'brave'
        DisplayName = 'Brave'
        Channel     = 'stable'
        Templates   = @('%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe',
                         '%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe',
                         '%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe')
    }
)

function Resolve-Template([string]$template) {
    $expanded = [Environment]::ExpandEnvironmentVariables($template)
    if ($expanded -eq $template) { return $null }
    return $expanded
}

function Get-BrowserVersion([string]$path) {
    try { return (Get-Item -LiteralPath $path).VersionInfo.FileVersion } catch { return $null }
}

function Get-Argv([string]$family, [string]$targetUrl, [string]$profile, [bool]$isPrivate) {
    # Mirrors src/BroChoos.Core/Abstractions/LaunchHelperArguments.TryBuild, per browser family:
    #   chrome/brave : [--profile-directory=<dir>] [--incognito] <url>
    #   edge         : [--profile-directory=<dir>] [--inprivate] <url>
    #   firefox      : NO profile argument (Gecko); private = '-private-window <url>' (the URL is the
    #                  flag's parameter, not repeated as a positional argument)
    #   other        : private mode refused (returns $null) - never silently open a normal window
    # Each item is a distinct argv entry and the URL is LAST (or the -private-window parameter),
    # so a URL can never be reparsed as an option.
    $isChromium = @('chrome', 'edge', 'brave') -contains $family
    $isGecko = $family -eq 'firefox'

    $privateSwitch = $null
    if ($isPrivate) {
        if ($isChromium) { $privateSwitch = if ($family -eq 'edge') { '--inprivate' } else { '--incognito' } }
        elseif ($isGecko) { $privateSwitch = '-private-window' }
        else { return $null }
    }

    $argv = @()
    if ($isChromium -and -not [string]::IsNullOrWhiteSpace($profile)) { $argv += "--profile-directory=$profile" }
    if ($null -ne $privateSwitch) { $argv += $privateSwitch }
    $argv += $targetUrl
    return $argv
}

$modeSwitch = if ($WhatIf) { '-WhatIf' } else { '-Execute' }
$modeText = if ($WhatIf) { 'WHAT-IF (no browser opened)' } else { 'EXECUTE (real browsers opened)' }

$startedUtc = (Get-Date).ToUniversalTime().ToString('o')
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add("COMMAND:  tools/W6.3-LaunchMatrixEvidence.ps1 $modeSwitch -Url `"$Url`"")
$lines.Add("UTC_START: $startedUtc")
$lines.Add("HOST_OS: $([Environment]::OSVersion.VersionString)")
$lines.Add("MODE: $modeText")
$lines.Add("")
$lines.Add('FAMILY  | AVAILABILITY        | OUTCOME | VERSION           | EXE')
$lines.Add('--------|---------------------|---------|-------------------|----')

$anyAbsent = $false
$anyNonSuccess = $false

foreach ($f in $families) {
    $exe = $null
    foreach ($t in $f.Templates) {
        $candidate = Resolve-Template $t
        if ($null -ne $candidate -and (Test-Path -LiteralPath $candidate)) { $exe = $candidate; break }
    }

    if ($null -eq $exe) {
        $anyAbsent = $true
        $lines.Add(('{0,-7} | {1,-19} | {2,-7} | {3,-17} | {4}' -f $f.Family, 'not-available-on-machine', 'n/a', 'n/a', 'absent'))
        continue
    }

    $version = Get-BrowserVersion $exe
    if ($WhatIf) {
        $plannedArgv = Get-Argv $f.Family $Url $ProfileId ([bool]$Private)
        $lines.Add(('{0,-7} | {1,-19} | {2,-7} | {3,-17} | {4}' -f $f.Family, 'available', 'planned', $version, $exe))
        if ($null -eq $plannedArgv) {
            $lines.Add('        | argv: refused (launch.private-unsupported)')
        } else {
            $lines.Add(('        | argv: {0} {1}' -f $exe, (@($plannedArgv) -join ' ')))
        }
        continue
    }

    # EXECUTE: real launch through the structured-argv contract.
    $status = 'Unknown'
    $detail = ''
    try {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $exe
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = [System.IO.Path]::GetDirectoryName($exe)
        $launchArgv = Get-Argv $f.Family $Url $ProfileId ([bool]$Private)
        if ($null -eq $launchArgv) { throw [System.InvalidOperationException]::new('launch.private-unsupported') }
        foreach ($a in @($launchArgv)) { $psi.ArgumentList.Add($a) }

        $proc = [System.Diagnostics.Process]::Start($psi)
        if ($null -ne $proc) {
            $status = 'Success'
            $detail = "pid=$($proc.Id)"
            $proc.Dispose()
        } else {
            $status = 'Failed'
            $detail = 'process-object-null'
        }
    } catch {
        $status = 'Failed'
        $detail = $_.Exception.GetType().Name
    }

    if ($status -ne 'Success') { $anyNonSuccess = $true }
    $lines.Add(('{0,-7} | {1,-19} | {2,-7} | {3,-17} | {4}' -f $f.Family, 'available', $status, $version, $exe))
    if ($detail) { $lines.Add(('        | detail: {0}' -f $detail)) }
}

$lines.Add('')
$lines.Add("SUMMARY: absentFamilies=$($anyAbsent.ToString().ToLowerInvariant()) anyNonSuccess=$($anyNonSuccess.ToString().ToLowerInvariant())")
$lines.Add('NOTE: An absent family is recorded as not-available-on-machine and is NOT a pass.')
$lines.Add('NOTE: Only an EXECUTE run on a host with all four families present can evidence the full 0B-1 four-browser matrix.')
$lines.Add("UTC_END: $((Get-Date).ToUniversalTime().ToString('o'))")

$text = ($lines -join [Environment]::NewLine) + [Environment]::NewLine

if ($OutFile) {
    $dir = [System.IO.Path]::GetDirectoryName($OutFile)
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText($OutFile, $text)
    Write-Output "Wrote $OutFile"
} else {
    Write-Output $text
}
