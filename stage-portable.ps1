param(
    [Parameter(Mandatory=$true)]
    [string]$zipPath,

    [Parameter(Mandatory=$true)]
    [string]$nupkgPath,

    [Parameter(Mandatory=$true)]
    [string]$publishDir
)

Write-Host "[stage-portable] Processing portable archive: $zipPath" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $zipPath)) {
    Write-Warning "[stage-portable] Target ZIP archive was not found: $zipPath"
    exit 0
}

# Both assemblies are required in Windows PowerShell 5.1
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Update)

    # 1. Stage the base full .nupkg into packages/ inside the portable zip for delta updates
    if (Test-Path -LiteralPath $nupkgPath) {
        $nupkgName = [System.IO.Path]::GetFileName($nupkgPath)
        $entryName = "packages/" + $nupkgName
        $existing = $zip.GetEntry($entryName)
        if ($existing) {
            $existing.Delete()
        }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $nupkgPath, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        Write-Host "[SUCCESS] Staged $entryName inside portable zip for delta updates." -ForegroundColor Green
    } else {
        Write-Warning "[stage-portable] Base .nupkg not found at $nupkgPath; delta updates will not be seeded."
    }

    # 2. Mirror GeoIP/*.mmdb into root GeoIP/ inside the zip
    $geoIpSourceDir = Join-Path $publishDir "GeoIP"
    if (Test-Path -LiteralPath $geoIpSourceDir) {
        $mmdbFiles = Get-ChildItem -LiteralPath $geoIpSourceDir -Filter "*.mmdb"
        foreach ($file in $mmdbFiles) {
            $targetEntry = "GeoIP/" + $file.Name
            $existing = $zip.GetEntry($targetEntry)
            if (-not $existing) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $targetEntry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        Write-Host "[SUCCESS] Verified root GeoIP/*.mmdb files in portable zip." -ForegroundColor Green
    }

    # 3. Mirror *.pdb files into the root of the zip
    if (Test-Path -LiteralPath $publishDir) {
        $pdbFiles = Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb"
        foreach ($pdb in $pdbFiles) {
            $targetEntry = $pdb.Name
            $existing = $zip.GetEntry($targetEntry)
            if (-not $existing) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $pdb.FullName, $targetEntry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        Write-Host "[SUCCESS] Verified root .pdb symbol files in portable zip." -ForegroundColor Green
    }

    # 4. Normalize file timestamps (fixes the 01/01/1980 blank date issue in Windows Explorer)
    $now = [DateTimeOffset]::Now
    $normalizedCount = 0
    foreach ($entry in $zip.Entries) {
        if ($entry.LastWriteTime.Year -lt 1990 -or $entry.LastWriteTime.Year -gt 2100) {
            $entry.LastWriteTime = $now
            $normalizedCount++
        }
    }

    $zip.Dispose()
    Write-Host "[SUCCESS] Normalized $normalizedCount file timestamps for Windows Explorer." -ForegroundColor Green
}
catch {
    Write-Error "[stage-portable] Failed updating portable zip: $_"
    exit 1
}