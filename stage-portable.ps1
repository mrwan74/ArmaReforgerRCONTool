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

    # -------------------------------------------------------------------------
    # 1. Clean up redundant root duplicates & native 3rd-party PDBs
    #    (The application runs out of current/, so root duplicates are bloat)
    # -------------------------------------------------------------------------
    $entriesToRemove = @($zip.Entries | Where-Object {
        # Remove any 3rd-party native Skia/HarfBuzz symbols anywhere in the archive
        $_.Name -like "libSkiaSharp*.pdb" -or 
        $_.Name -like "libHarfBuzzSharp*.pdb" -or
        # Remove duplicate PDBs at the zip root (app & Sentry load PDBs from current/)
        ($_.FullName -notlike "*/*" -and $_.Name -like "*.pdb") -or
        # Remove duplicate GeoIP at the zip root (app reads GeoIP from current/GeoIP/)
        ($_.FullName -like "GeoIP/*")
    })

    if ($entriesToRemove.Count -gt 0) {
        foreach ($entry in $entriesToRemove) {
            $entry.Delete()
        }
        Write-Host "[SUCCESS] Removed $($entriesToRemove.Count) redundant/unmanaged duplicate files from archive root." -ForegroundColor Green
    }

    # -------------------------------------------------------------------------
    # 2. Stage the base full .nupkg into packages/ for delta updates
    # -------------------------------------------------------------------------
    if (Test-Path -LiteralPath $nupkgPath) {
        $nupkgName = [System.IO.Path]::GetFileName($nupkgPath)
        $entryName = "packages/" + $nupkgName
        $existing = $zip.GetEntry($entryName)
        if ($existing) {
            $existing.Delete()
        }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, 
            $nupkgPath, 
            $entryName, 
            [System.IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
        Write-Host "[SUCCESS] Staged $entryName inside portable zip for delta updates." -ForegroundColor Green
    } else {
        Write-Warning "[stage-portable] Base .nupkg not found at $nupkgPath; delta updates will not be seeded."
    }

    # -------------------------------------------------------------------------
    # 3. Normalize file timestamps (fixes blank/1980 dates in Windows Explorer)
    # -------------------------------------------------------------------------
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