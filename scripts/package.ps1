$ErrorActionPreference = 'Stop'
$cleanerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cleanerManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$cleanerPackage = Get-Content -LiteralPath (Join-Path $cleanerRoot 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($cleanerPackage.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
& node (Join-Path $PSScriptRoot 'check.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Source checks failed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

function Assert-CleanerPath([string]$relative, [switch]$AllowMissing) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.?(?:[\\/]|$)' -or $relative -match ':') { throw 'Unsafe package path.' }
    $target = [IO.Path]::GetFullPath((Join-Path $cleanerRoot $relative))
    if (-not $target.StartsWith($cleanerRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path is outside the project.' }
    # Inspect every existing component, including project ancestors, for reparse points.
    $current = $target
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked path is not allowed: ' + $current) }
        } elseif (-not $AllowMissing -and $current -eq $target) { throw ('Missing release file: ' + $relative) }
        $parent = [IO.Directory]::GetParent($current)
        $current = if ($parent) { $parent.FullName } else { $null }
    }
    return $target
}

$cleanerDist = Assert-CleanerPath 'dist' -AllowMissing
New-Item -ItemType Directory -Path $cleanerDist -Force | Out-Null

function Get-CleanerDigest($stream) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}

function Get-CleanerFileDigest([string]$file) {
    $stream = [IO.File]::OpenRead($file)
    try { return Get-CleanerDigest $stream } finally { $stream.Dispose() }
}

function New-CleanerArchive([string]$kind, [string[]]$inputs) {
    $fileName = 'CodexCleaner-' + $kind + '-v' + $cleanerPackage.version + '.zip'
    $output = Assert-CleanerPath ('dist/' + $fileName) -AllowMissing
    $entries = @($inputs | Sort-Object -Unique)
    if ($entries.Count -ne $inputs.Count) { throw 'Duplicate package entries.' }
    $archiveStream = [IO.File]::Open($output, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($relative in $entries) {
            $source = Assert-CleanerPath $relative
            $entry = $archive.CreateEntry(('CodexCleaner/' + $relative), [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputStream = [IO.File]::OpenRead($source)
            $entryStream = $entry.Open()
            try { $inputStream.CopyTo($entryStream) }
            finally { $entryStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $archive.Dispose(); $archiveStream.Dispose() }

    $verified = [IO.Compression.ZipFile]::OpenRead($output)
    try {
        if ($verified.Entries.Count -ne $entries.Count) { throw 'Archive entry count mismatch.' }
        foreach ($relative in $entries) {
            $entry = $verified.GetEntry('CodexCleaner/' + $relative)
            if (-not $entry) { throw ('Missing archive entry: ' + $relative) }
            $entryStream = $entry.Open()
            try { $digest = Get-CleanerDigest $entryStream } finally { $entryStream.Dispose() }
            $source = Assert-CleanerPath $relative
            if ($digest -ne (Get-CleanerFileDigest $source)) { throw ('Archive content mismatch: ' + $relative) }
        }
    } finally { $verified.Dispose() }
    Write-Host ('Verified ' + $fileName + ' (' + $entries.Count + ' files)')
    return ((Get-CleanerFileDigest $output) + '  ' + $fileName)
}

$cleanerChecksums = @()
$cleanerChecksums += New-CleanerArchive 'Windows' $cleanerManifest.runtime
$cleanerChecksums += New-CleanerArchive 'Source' (@($cleanerManifest.runtime) + @($cleanerManifest.sourceOnly))
$cleanerChecksumFile = Assert-CleanerPath 'dist/SHA256SUMS.txt' -AllowMissing
[IO.File]::WriteAllText($cleanerChecksumFile, (($cleanerChecksums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host ('Release archives and SHA256SUMS.txt are in ' + $cleanerDist)
