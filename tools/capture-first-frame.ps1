param(
    [string]$InputPath,
    [string]$OutputPath
)

if ([string]::IsNullOrWhiteSpace($InputPath) -or [string]::IsNullOrWhiteSpace($OutputPath)) {
    throw "Both -InputPath and -OutputPath are required."
}

if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) {
    throw "InputPath is not an existing file: $InputPath"
}

# Refuse to overwrite the input log with the JSON artifact.
$resolvedInput = (Resolve-Path -LiteralPath $InputPath).ProviderPath
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([string]::Equals($resolvedInput, $resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must differ from InputPath (would overwrite the input log): $resolvedOutput"
}

# A different PATH can still be the same FILE: a hardlink, or a symlink (the open follows it to
# its final target). Both are caught by comparing the file identity (volume serial number plus
# file index) of the two opened handles, which works the same on Windows PowerShell 5.1 and 7.
# Only an output that already exists can alias the input.
if (Test-Path -LiteralPath $resolvedOutput -PathType Leaf) {
    if (-not ('FirstFrameFileIdentity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class FirstFrameFileIdentity {
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION {
        public uint FileAttributes; public FILETIME CreationTime; public FILETIME LastAccessTime; public FILETIME LastWriteTime;
        public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks;
        public uint FileIndexHigh; public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(IntPtr handle, out BY_HANDLE_FILE_INFORMATION info);
    public static string Get(string path) {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            BY_HANDLE_FILE_INFORMATION info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out info)) {
                throw new IOException("GetFileInformationByHandle failed: " + Marshal.GetLastWin32Error());
            }
            return info.VolumeSerialNumber.ToString("x8") + ":" + info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8");
        }
    }
}
'@
    }
    if ([FirstFrameFileIdentity]::Get($resolvedInput) -ceq [FirstFrameFileIdentity]::Get($resolvedOutput)) {
        throw "OutputPath is the same file as InputPath (hardlink or symlink); refusing to overwrite the input log: $resolvedOutput"
    }
}

$content = Get-Content -LiteralPath $InputPath -ErrorAction Stop
$markers = @(
    "OPEN_WINDOW_START",
    "FIRST_FRAME_REQUESTED",
    "FIRST_FRAME_RENDERED",
    "INTERACTIVE_READY"
)

$captured = @()
foreach ($marker in $markers) {
    # '[picker]' contains wildcard brackets; escape so it matches literally.
    $pattern = '*' + [WildcardPattern]::Escape('[picker]') + '*' + [WildcardPattern]::Escape($marker) + '*'
    $line = $content | Where-Object { $_ -like $pattern } | Select-Object -First 1
    if ($null -ne $line) {
        $captured += [PSCustomObject]@{
            marker = $marker
            observed = $true
            sample = [string]$line
        }
    }
    else {
        $captured += [PSCustomObject]@{
            marker = $marker
            observed = $false
            sample = ""
        }
    }
}

$payload = [PSCustomObject]@{
    observed_at_utc = [DateTimeOffset]::UtcNow.ToString("o")
    markers = $captured
}

$payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
