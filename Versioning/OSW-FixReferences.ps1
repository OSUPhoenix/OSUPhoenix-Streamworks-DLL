# =====================================================================
#  OSWTools - Versioning/OSW-FixReferences.ps1                NEW FILE
#
#  Points every Streamer.bot action reference to OSWTools.dll at the
#  new location:  <Streamer.bot folder>\dlls\OSWTools.dll
#
#  WHY THIS EXISTS:
#    Each "Execute C# Code" sub-action saves its references as FULL
#    paths (e.g. C:\Streamer.bot\OSWTools.dll). When the DLL moves to
#    dlls\, those paths point at nothing and the actions stop compiling.
#    This script rewrites those saved paths, so users never have to
#    re-add the reference by hand.
#
#  WHEN IT RUNS:
#    Automatically - DllInstaller's swap script runs it after
#    Streamer.bot has closed and OSWTools.dll is safely in dlls\.
#    It MUST run while Streamer.bot is closed: Streamer.bot keeps its
#    actions in memory and writes them back to disk, which would undo
#    our edit.
#
#  MANUAL USE (Streamer.bot closed):
#    powershell -ExecutionPolicy Bypass -File OSW-FixReferences.ps1 -SbFolder "C:\Streamer.bot"
#
#  SAFETY:
#    - Only touches .json files directly in <SB folder>\data.
#    - Only changes JSON text values that are paths ending in \OSWTools.dll.
#    - Backs up every file it changes before writing.
#    - Writes to a temp file and swaps it in, so a crash mid-write never
#      leaves a half-written actions file.
#    - Refuses to run while this Streamer.bot is open.
#
#  This file is plain ASCII on purpose: Windows PowerShell 5.1 reads
#  scripts without a BOM as the ANSI code page, so fancy characters
#  (em dashes, arrows) could turn into garbage.
# =====================================================================

param(
    [Parameter(Mandatory = $true)][string]$SbFolder,
    [string]$LogFile,
    [string]$BackupDir
)

$ErrorActionPreference = 'Stop'

$SbFolder = $SbFolder.TrimEnd('\')
$dataDir  = Join-Path $SbFolder 'data'
$newPath  = Join-Path $SbFolder 'dlls\OSWTools.dll'
$stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $LogFile)   { $LogFile   = Join-Path $SbFolder 'OSWData\Updater\install.log' }
if (-not $BackupDir) { $BackupDir = Join-Path $SbFolder ('OSWData\Updater\backup-' + $stamp) }

function Write-Log([string]$msg) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' [refs] ' + $msg
    try {
        $logDir = Split-Path -Parent $LogFile
        if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Add-Content -LiteralPath $LogFile -Value $line
    } catch { }
    Write-Host $line
}

# ---- Is THIS Streamer.bot still running? --------------------------------
# Compare process paths so a second, separate Streamer.bot install doesn't
# block us.
$running = Get-Process -Name 'Streamer.bot' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($SbFolder + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Log 'Streamer.bot is still running - close it and run this again. Nothing changed.'
    exit 1
}

if (-not (Test-Path -LiteralPath $newPath)) {
    Write-Log ('OSWTools.dll is not in ' + $newPath + ' yet - nothing changed.')
    exit 1
}
if (-not (Test-Path -LiteralPath $dataDir)) {
    Write-Log ('No data folder at ' + $dataDir + ' - nothing changed.')
    exit 0
}

# ---- What we look for ----------------------------------------------------
# Inside JSON text every backslash is written twice:  C:\SB\x.dll -> "C:\\SB\\x.dll"
# We want every JSON string value that is a path ending in \OSWTools.dll or
# /OSWTools.dll, wherever it used to point.
#
# Find-References does this without a regex over the whole file (that was
# slow on big actions files): jump to each "OSWTools.dll" + closing quote with
# IndexOf, check there's a path separator before the name, then step back to
# the opening quote. Paths can't contain " so the nearest one is the start.
# DllInstaller.FindDllReferences() in the C# code uses the same steps.
function Find-References([string]$text) {
    $needle = 'OSWTools.dll"'
    $found  = New-Object System.Collections.Generic.List[object]
    $pos    = 0
    while ($true) {
        $hit = $text.IndexOf($needle, $pos, [StringComparison]::OrdinalIgnoreCase)
        if ($hit -lt 0) { break }
        $pos = $hit + $needle.Length

        $slash     = ($hit -ge 1 -and $text[$hit - 1] -eq '/')
        $backslash = ($hit -ge 2 -and $text[$hit - 1] -eq '\' -and $text[$hit - 2] -eq '\')
        if (-not ($slash -or $backslash)) { continue }

        $open = $text.LastIndexOf([char]'"', $hit - 1)   # [char] = fast character search, not culture-aware string search
        if ($open -lt 0) { continue }
        if ($open -ge 1 -and $text[$open - 1] -eq '\') { continue }   # escaped quote, not a value start

        # Remember where the value starts and how long it is (quotes included).
        # A small named object, not an array: PowerShell likes to "unroll"
        # arrays when passing them around, which would scramble the pairs.
        $found.Add([pscustomobject]@{ Start = $open; Length = ($pos - $open) })
    }
    return ,$found   # the comma stops PowerShell from unrolling the list
}

# The replacement: our new path, JSON-escaped, in quotes.
$newJson = '"' + $newPath.Replace('\', '\\') + '"'

$changedFiles = 0
$failedFiles  = 0
$changedRefs  = 0

foreach ($file in Get-ChildItem -LiteralPath $dataDir -Filter '*.json' -File) {
    $tmp = $null
    try {
        # Read raw bytes so we can keep the file's BOM (or lack of one)
        # exactly as Streamer.bot wrote it.
        $bytes  = [IO.File]::ReadAllBytes($file.FullName)
        $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $enc    = New-Object System.Text.UTF8Encoding($hasBom)
        $offset = 0
        if ($hasBom) { $offset = 3 }
        $text   = $enc.GetString($bytes, $offset, $bytes.Length - $offset)

        # Find references; keep only the ones NOT already correct.
        $stale = New-Object System.Collections.Generic.List[object]
        foreach ($r in (Find-References $text)) {
            $value = $text.Substring($r.Start, $r.Length)
            if (-not [string]::Equals($value, $newJson, [StringComparison]::OrdinalIgnoreCase)) { $stale.Add($r) }
        }
        if ($stale.Count -eq 0) { continue }

        # Rebuild the text: copy everything between references unchanged,
        # and put the new path where each stale reference was.
        $sb   = New-Object System.Text.StringBuilder
        $last = 0
        foreach ($r in $stale) {
            [void]$sb.Append($text, $last, $r.Start - $last)
            [void]$sb.Append($newJson)
            $last = $r.Start + $r.Length
        }
        [void]$sb.Append($text, $last, $text.Length - $last)
        $newText = $sb.ToString()

        # Back up, then write to a temp file beside the original and swap it
        # in. File.Replace is an atomic swap on NTFS and saves the original
        # as the backup in the same step.
        if (-not (Test-Path -LiteralPath $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
        $tmp    = $file.FullName + '.oswtmp'
        $backup = Join-Path $BackupDir $file.Name
        [IO.File]::WriteAllText($tmp, $newText, $enc)
        [IO.File]::Replace($tmp, $file.FullName, $backup)

        $changedFiles++
        $changedRefs += $stale.Count
        Write-Log ('Updated ' + $stale.Count + ' reference(s) in ' + $file.Name + ' (backup: ' + $backup + ')')
    }
    catch {
        $failedFiles++
        Write-Log ('Could not update ' + $file.Name + ': ' + $_.Exception.Message + ' - left as it was.')
        if ($tmp -and (Test-Path -LiteralPath $tmp)) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

if ($changedRefs -eq 0) {
    Write-Log 'No old OSWTools.dll references found - nothing to change.'
} else {
    Write-Log ('Done: ' + $changedRefs + ' reference(s) in ' + $changedFiles + ' file(s) now point to ' + $newPath)
}
# Exit code tells the swap script how it went: 0 = all good, 2 = at least
# one file couldn't be updated (counted as a failed run by the DLL).
if ($failedFiles -gt 0) { exit 2 }
exit 0
