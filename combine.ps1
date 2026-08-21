# combine.ps1
# Crawls all subfolders relative to the script's location and combines
# every file's text content into a single output file: combined_files.txt

# ─── Configuration ────────────────────────────────────────────────────────────

# Safely resolve script root directory across direct execution and various environments
$scriptDir = if ($null -ne $PSScriptRoot -and $PSScriptRoot -ne "") {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue
}
if ($null -eq $scriptDir -or $scriptDir -eq "") {
    $scriptDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(".")
}

$outputFile = Join-Path $scriptDir "combined_files.txt"

# Safely resolve current script file path to prevent self-inclusion
$scriptPath = if ($null -ne $MyInvocation -and $null -ne $MyInvocation.MyCommand) {
    $MyInvocation.MyCommand.Path
} else {
    $null
}

# Centralized list to collect and display failures at the end
$scriptErrors = [System.Collections.Generic.List[string]]::new()

# Base folders to completely bypass across all project types
$baseSkipDirectories = @(
    '.git', '.github', '.vs', '.vscode', '.idea', 'node_modules', 'vendor'
)

# Extensions to skip (binary / non-text files)
$skipExtensions = @(
    # Images & Vectors
    '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.ico', '.icns', '.svg', '.webp', '.tiff', '.tif', '.raw', '.psd', '.ai', '.sketch', '.fig', '.blend',
    # Executables & compiled binaries
    '.exe', '.dll', '.so', '.dylib', '.bin', '.com', '.msi', '.app', '.apk', '.aar', '.jar', '.war', '.dill', '.flat', '.arsc', '.pdb',
    # Caches, database & metadata (including MaxMind .mmdb)
    '.suo', '.user', '.sln.docstates', '.lock', '.class', '.o', '.obj', '.pyc', '.pyo', '.db', '.sqlite', '.sqlite3', '.db3', '.mmdb', '.hprof', '.keystore', '.jks',
    # Archives & compressed
    '.zip', '.tar', '.gz', '.tgz', '.rar', '.7z', '.bz2', '.xz', '.cab', '.pack', '.idx',
    # Audio / Video
    '.mp3', '.mp4', '.wav', '.avi', '.mov', '.mkv', '.flac', '.aac', '.ogg', '.wmv', '.webm',
    # Documents (binary format)
    '.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.rtf',
    # Fonts
    '.ttf', '.otf', '.woff', '.woff2', '.eot'
)

# ─── Logging Helpers ──────────────────────────────────────────────────────────

function Log-Section {
    param([string]$title)
    $line = '-' * 60
    Write-Host ""
    Write-Host $line -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host $line -ForegroundColor DarkGray
}

function Log-Step {
    param([string]$message)
    Write-Host "  >> " -NoNewline -ForegroundColor DarkCyan
    Write-Host $message -ForegroundColor White
}

function Log-Info {
    param([string]$label, [string]$value)
    Write-Host "    " -NoNewline
    Write-Host "${label}: " -NoNewline -ForegroundColor Gray
    Write-Host $value -ForegroundColor Yellow
}

function Log-FileIncluded {
    param([string]$relPath, [long]$bytes, [int]$current, [int]$total)
    $kb     = [math]::Round($bytes / 1KB, 2)
    $padded = $current.ToString().PadLeft($total.ToString().Length)
    Write-Host "    " -NoNewline
    Write-Host "[$padded/$total] " -NoNewline -ForegroundColor DarkGray
    Write-Host "OK  " -NoNewline -ForegroundColor Green
    Write-Host $relPath -NoNewline -ForegroundColor White
    Write-Host " ($kb KB)" -ForegroundColor DarkGray
}

function Log-FileSkipped {
    param([string]$relPath, [string]$reason)
    Write-Host "    " -NoNewline
    Write-Host "SKIP  " -NoNewline -ForegroundColor DarkYellow
    Write-Host $relPath -NoNewline -ForegroundColor DarkGray
    Write-Host " [$reason]" -ForegroundColor DarkGray
}

function Log-FileError {
    param([string]$relPath, [string]$err)
    Write-Host "    " -NoNewline
    Write-Host "ERROR " -NoNewline -ForegroundColor Red
    Write-Host $relPath -NoNewline -ForegroundColor White
    Write-Host " -- $err" -ForegroundColor Red
}

function Log-Stat {
    param([string]$label, [string]$value)
    Write-Host "    " -NoNewline
    Write-Host $label.PadRight(28) -NoNewline -ForegroundColor Gray
    Write-Host $value -ForegroundColor Cyan
}

# ─── Helpers ──────────────────────────────────────────────────────────────────

function Get-RelativePath {
    param([string]$fullPath)
    if ($fullPath -eq $scriptDir) { return "" }
    return $fullPath.Substring($scriptDir.Length).TrimStart('\', '/')
}

# Recursive folder crawler that outputs directly to the pipeline stream and isolates errors
function Get-FilesToProcess {
    param(
        [string]$dir,
        [System.Collections.Generic.HashSet[string]]$skipDirs
    )
    
    $entries = @()
    try {
        $entries = [System.IO.Directory]::GetFileSystemEntries($dir)
    } catch {
        $errMessage = "Access failure in folder '$dir': $($_.Exception.Message)"
        $script:scriptErrors.Add($errMessage)
        return
    }

    foreach ($entry in $entries) {
        $name = [System.IO.Path]::GetFileName($entry)
        
        $isDir = $false
        try {
            $isDir = [System.IO.Directory]::Exists($entry)
        } catch {
            $script:scriptErrors.Add("Failed to inspect file system entry '$entry': $($_.Exception.Message)")
            continue
        }

        if ($isDir) {
            # Skip list check
            if ($skipDirs.Contains($name)) {
                continue
            }

            # Safe Symlink/Junction check to avoid infinite folder loops
            try {
                $dirInfo = [System.IO.DirectoryInfo]::new($entry)
                if ($dirInfo.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
                    continue
                }
            } catch {
                $script:scriptErrors.Add("Failed to check directory attributes for '$entry': $($_.Exception.Message)")
            }

            # Safe recursion (failures in sibling folders won't discard the rest of the branch)
            try {
                Get-FilesToProcess -dir $entry -skipDirs $skipDirs
            } catch {
                $script:scriptErrors.Add("Error recursing into '$entry': $($_.Exception.Message)")
            }
        } else {
            try {
                [System.IO.FileInfo]::new($entry)
            } catch {
                $script:scriptErrors.Add("Failed to access file properties for '$entry': $($_.Exception.Message)")
            }
        }
    }
}

# ─── Script Start ─────────────────────────────────────────────────────────────

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "         combine.ps1  --  File Combiner                    " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Prompt the user to select the project type
Write-Host "Select the project type to customize exclusions:" -ForegroundColor Cyan
Write-Host "  [1] Flutter / Dart Project" -ForegroundColor Yellow
Write-Host "      (Skips .dart_tool, build, ephemeral, native platform folders, .g.dart, etc.)" -ForegroundColor Gray
Write-Host "  [2] C# Project" -ForegroundColor Yellow
Write-Host "      (Skips bin, obj, packages, .designer.cs, .g.cs, etc.)" -ForegroundColor Gray
Write-Host "  [3] React Native Project" -ForegroundColor Yellow
Write-Host "      (Skips android, ios, .expo, .metro, pods, build, package manager lockfiles, etc.)" -ForegroundColor Gray
Write-Host "  [4] General / Default" -ForegroundColor Yellow
Write-Host "      (Skips standard build directories and temporary files)" -ForegroundColor Gray
Write-Host ""

$choice = ""
while ($choice -notmatch '^[1-4]$') {
    $choice = (Read-Host "Enter selection (1, 2, 3, or 4) [Default: 4]").Trim()
    if ($choice -eq "") { $choice = "4"; break }
}

$projectType = "General"
$skipDirectories = @() + $baseSkipDirectories

# Dynamic Configuration Adjustment based on Selection
if ($choice -eq "1") {
    $projectType = "Flutter / Dart"
    # Includes Dart-specific caches and explicit platform folders
    $skipDirectories += @(
        '.dart_tool', 'build', 'pods', 'ephemeral', '.fvm', '.pub-cache', '.gradle',
        'android', 'ios', 'linux', 'macos', 'web', 'windows'
    )
} elseif ($choice -eq "2") {
    $projectType = "C#"
    $skipDirectories += @('bin', 'obj', 'packages', 'TestResults', 'AppPackages', '.cr')
} elseif ($choice -eq "3") {
    $projectType = "React Native"
    # Skips native platforms, caches, and test output directories
    $skipDirectories += @(
        'android', 'ios', '.expo', '.expo-shared', 'web-build', 'pods', 
        'deriveddata', '.gradle', '.metro', 'build', 'dist', 'coverage', 
        '.yarn', '.pnpm-store'
    )
} else {
    $projectType = "General"
    $skipDirectories += @('.dart_tool', '.gradle', 'bin', 'obj', 'build', 'dist', 'out', 'pods', 'ephemeral', 'deriveddata', '.expo', '.next', 'packages')
}

# Build HashSets for rapid, case-insensitive lookups
$skipDirectoriesLower = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($d in $skipDirectories) { [void]$skipDirectoriesLower.Add($d) }

$skipExtensionsLower = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($e in $skipExtensions) { [void]$skipExtensionsLower.Add($e) }

Write-Host ""
Log-Info "Root directory" $scriptDir
Log-Info "Output file   " $outputFile
Log-Info "Project type  " $projectType

# ─── Phase 1: Discovery ───────────────────────────────────────────────────────

Log-Section "PHASE 1 of 3 -- Discovering Files"
Log-Step "Scanning directory tree recursively (bypassing excluded folders)..."

$discoveredRaw = Get-FilesToProcess -dir $scriptDir -skipDirs $skipDirectoriesLower
$allDiscovered = @()
if ($null -ne $discoveredRaw) {
    $allDiscovered = @($discoveredRaw) | Sort-Object FullName
}

Log-Info "Total items found on disk (after directory filter)" $allDiscovered.Count.ToString()

Write-Host ""
Log-Step "Filtering files (checking each against skip rules)..."
Write-Host ""

$skippedOutput    = 0
$skippedBinary    = 0
$skippedGenerated = 0
$allFiles         = [System.Collections.Generic.List[System.IO.FileInfo]]::new()

foreach ($file in $allDiscovered) {
    $rel = Get-RelativePath $file.FullName

    # Explicit self-reference filters (skipping output file and combine.ps1 itself)
    if ($file.FullName -eq $outputFile) {
        Log-FileSkipped $rel "output file -- excluded to prevent self-reference"
        $skippedOutput++
        continue
    }

    if (($null -ne $scriptPath -and $file.FullName -eq $scriptPath) -or $file.Name -eq "combine.ps1") {
        Log-FileSkipped $rel "script file -- excluded to prevent self-reference"
        $skippedOutput++
        continue
    }

    if ($skipExtensionsLower.Contains($file.Extension)) {
        Log-FileSkipped $rel "binary/non-text extension '$($file.Extension)'"
        $skippedBinary++
        continue
    }

    # Project-specific file exclusions (skipping generated/mock files)
    if ($choice -eq "1") {
        if ($file.Name -like "*.g.dart" -or $file.Name -like "*.freezed.dart" -or $file.Name -like "*.mocks.dart" -or $file.Name -eq "generated_plugin_registrant.dart") {
            Log-FileSkipped $rel "generated/mock file (Flutter/Dart)"
            $skippedGenerated++
            continue
        }
    } elseif ($choice -eq "2") {
        if ($file.Name -like "*.designer.cs" -or $file.Name -like "*.g.cs" -or $file.Name -like "*.g.i.cs") {
            Log-FileSkipped $rel "generated designer/markup file (C#)"
            $skippedGenerated++
            continue
        }
    } elseif ($choice -eq "3") {
        # Skips lockfiles and compiled output maps which are typically large and lack helpful LLM context
        if ($file.Name -like "*.map" -or $file.Name -like "*.js.map" -or $file.Name -like "*.min.js" -or $file.Name -eq "yarn-error.log" -or $file.Name -eq "package-lock.json" -or $file.Name -eq "yarn.lock" -or $file.Name -eq "pnpm-lock.yaml") {
            Log-FileSkipped $rel "lockfile/minified/map file (React Native)"
            $skippedGenerated++
            continue
        }
    }

    Write-Host "    " -NoNewline
    Write-Host "QUEUED  " -NoNewline -ForegroundColor DarkCyan
    Write-Host $rel -ForegroundColor White
    $allFiles.Add($file)
}

$totalFiles = $allFiles.Count

Write-Host ""
Write-Host "    ----------------------------------------" -ForegroundColor DarkGray
Log-Info "  Included files " $totalFiles.ToString()
Log-Info "  Skipped (output/script)" $skippedOutput.ToString()
Log-Info "  Skipped (binary ext) " $skippedBinary.ToString()
if ($choice -eq "1" -or $choice -eq "2" -or $choice -eq "3") {
    Log-Info "  Skipped (generated)  " $skippedGenerated.ToString()
}
Write-Host "    ----------------------------------------" -ForegroundColor DarkGray

# ─── Phase 2: Building Tree ───────────────────────────────────────────────────

Log-Section "PHASE 2 of 3 -- Building Directory Tree"
Log-Step "Mapping folder/file hierarchy..."

function Build-Tree {
    param([System.Collections.Generic.List[System.IO.FileInfo]]$files)

    $folderMap = [System.Collections.Specialized.OrderedDictionary]::new()

    foreach ($file in $files) {
        $relFolder = Get-RelativePath $file.DirectoryName
        if (-not $folderMap.Contains($relFolder)) {
            $folderMap[$relFolder] = [System.Collections.Generic.List[string]]::new()
        }
        $folderMap[$relFolder].Add((Split-Path $file.FullName -Leaf))
    }

    $allFolders = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $folderMap.Keys) {
        $parts = $key -split '\\'
        $cum   = ''
        foreach ($part in $parts) {
            if ($part -eq '') { continue }
            $cum = if ($cum -eq '') { $part } else { "$cum\$part" }
            [void]$allFolders.Add($cum)
        }
    }

    $lines = [System.Collections.Generic.List[string]]::new()

    function Render-Node {
        param([string]$currentFolder, [string]$prefix)

        $childFolders = $allFolders | Where-Object {
            if ($currentFolder -eq '') { $_ -notmatch '\\' }
            else { $_ -match ('^' + [regex]::Escape($currentFolder) + '\\[^\\]+$') }
        } | Sort-Object

        $directFiles = if ($folderMap.Contains($currentFolder)) {
            $folderMap[$currentFolder] | Sort-Object
        } else { @() }

        $items = @($childFolders) + @($directFiles)
        $total = $items.Count

        for ($i = 0; $i -lt $total; $i++) {
            $item     = $items[$i]
            $isLast   = ($i -eq $total - 1)
            $isFolder = $childFolders -contains $item
            $conn     = if ($isLast) { '└── ' } else { '├── ' }
            $childPfx = if ($isLast) { '    ' } else { '│   ' }

            if ($isFolder) {
                $lines.Add("${prefix}${conn}${item}\")
                Render-Node -currentFolder $item -prefix ($prefix + $childPfx)
            } else {
                $lines.Add("${prefix}${conn}${item}")
            }
        }
    }

    $rootFolders = $allFolders | Where-Object { $_ -notmatch '\\' } | Sort-Object
    $rootFiles   = if ($folderMap.Contains('')) { $folderMap[''] | Sort-Object } else { @() }
    $rootItems   = @($rootFolders) + @($rootFiles)
    $rootTotal   = $rootItems.Count

    for ($i = 0; $i -lt $rootTotal; $i++) {
        $item     = $rootItems[$i]
        $isLast   = ($i -eq $rootTotal - 1)
        $isFolder = $rootFolders -contains $item
        $conn     = if ($isLast) { '└── ' } else { '├── ' }
        $childPfx = if ($isLast) { '    ' } else { '│   ' }

        if ($isFolder) {
            $lines.Add("${conn}${item}\")
            Render-Node -currentFolder $item -prefix $childPfx
        } else {
            $lines.Add("${conn}${item}")
        }
    }

    return $lines
}

$treeLines = @()
$uniqueFolders = 0
try {
    $treeLines     = Build-Tree -files $allFiles
    $uniqueFolders = ($allFiles | Select-Object -ExpandProperty DirectoryName -Unique).Count
    Log-Info "Unique folders mapped  " $uniqueFolders.ToString()
    Log-Info "Tree lines generated   " $treeLines.Count.ToString()

    Write-Host ""
    Write-Host "    Tree preview:" -ForegroundColor DarkGray
    foreach ($line in $treeLines) {
        Write-Host "      $line" -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "    OK  Tree structure built successfully" -ForegroundColor Green
} catch {
    $errMessage = "Failed to build tree hierarchy mapping: $($_.Exception.Message)"
    $scriptErrors.Add($errMessage)
    Write-Host "    WARNING: Tree representation building encountered problems. Proceeding without preview." -ForegroundColor Yellow
}

# ─── Phase 3: Writing Output ──────────────────────────────────────────────────

Log-Section "PHASE 3 of 3 -- Writing Output File"
Log-Step "Opening output stream: $outputFile"

$writer     = $null
$fsOutput   = $null
$totalBytes = 0
$fileIndex  = 0
$errorCount = 0

try {
    # Open file stream and writer with explicit share parameters to allow read-access by other processes
    $fsOutput = [System.IO.FileStream]::new($outputFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    $writer = [System.IO.StreamWriter]::new($fsOutput, [System.Text.Encoding]::UTF8)
} catch {
    $errMessage = "Failed to open output file '$outputFile' for writing: $($_.Exception.Message)"
    $scriptErrors.Add($errMessage)
    Write-Host "    ERROR: $errMessage" -ForegroundColor Red
}

if ($null -ne $writer) {
    try {
        # Write header + tree
        Write-Host ""
        Log-Step "Writing tree header to file..."
        $writer.WriteLine(" Root: $scriptDir")
        $writer.WriteLine("PROJECT DIRECTORY CONTENTS:")
        $writer.WriteLine("│")
        foreach ($line in $treeLines) { $writer.WriteLine($line) }
        $writer.WriteLine("---")
        $writer.WriteLine("Total files found: $totalFiles")
        $writer.WriteLine("---")
        Log-Info "  Header lines written" ($treeLines.Count + 5).ToString()

        # Write file contents
        Write-Host ""
        Log-Step "Appending file contents one by one..."
        Write-Host ""

        foreach ($file in $allFiles) {
            $fileIndex++
            $relPath = Get-RelativePath $file.FullName

            $writer.WriteLine("---")
            $writer.WriteLine(" FILE: $relPath")

            $fsInput = $null
            $reader  = $null

            try {
                # Open with ReadAccess and ReadWrite sharing to maximize compatibility with locked files
                $fsInput = [System.IO.FileStream]::new($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                
                # Auto-detect encoding based on BOM; fall back to UTF-8
                $reader  = [System.IO.StreamReader]::new($fsInput, [System.Text.Encoding]::UTF8, $true)

                $buffer = [char[]]::new(4096)
                $bytesRead = 0
                $lastChar = [char]0
                $fileBytesWritten = 0

                # Efficient chunk-by-chunk copy avoids memory allocation spikes and prevents content truncation
                while (($bytesRead = $reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $writer.Write($buffer, 0, $bytesRead)
                    $lastChar = $buffer[$bytesRead - 1]
                    $fileBytesWritten += [System.Text.Encoding]::UTF8.GetByteCount($buffer, 0, $bytesRead)
                }

                # Ensure the file ends with a trailing newline if it didn't already
                if ($lastChar -ne [char]10 -and $lastChar -ne [char]13 -and $lastChar -ne [char]0) {
                    $writer.WriteLine()
                }

                $totalBytes += $fileBytesWritten
                Log-FileIncluded $relPath $fileBytesWritten $fileIndex $totalFiles
            } catch {
                # Log the read failure inside the destination file and register the failure details
                $writer.WriteLine("[Could not read file: $($_.Exception.Message)]")
                Log-FileError $relPath $_.Exception.Message
                
                $errMessage = "Failed to read file '$relPath': $($_.Exception.Message)"
                $scriptErrors.Add($errMessage)
                $errorCount++
            } finally {
                # Guarantee file handles are released immediately
                if ($null -ne $reader)  { $reader.Close() }
                if ($null -ne $fsInput) { $fsInput.Close() }
            }

            $writer.WriteLine("---")
        }

        # Footer
        Write-Host ""
        Log-Step "Writing footer..."
        $writer.WriteLine("---")
        $writer.WriteLine(" END OF COMBINED FILES")
        $writer.WriteLine("---")
        Write-Host "    OK  Footer written" -ForegroundColor Green

    } finally {
        # Safely flush and release the output file handles
        $writer.Flush()
        $writer.Close()
        $writer.Dispose()
        if ($null -ne $fsOutput) { $fsOutput.Dispose() }
        Log-Step "Output file stream closed"
    }
} else {
    Log-Step "Skipping compilation loop due to write failure on output file."
}

# ─── Summary ──────────────────────────────────────────────────────────────────

$stopwatch.Stop()
$elapsed    = $stopwatch.Elapsed
$outputSize = 0
if (Test-Path $outputFile) {
    $outputSize = (Get-Item $outputFile).Length
}
$elapsedStr = "{0:D2}:{1:D2}.{2:D3}" -f $elapsed.Minutes, $elapsed.Seconds, $elapsed.Milliseconds

Log-Section "DONE -- Summary"
Log-Stat "Files combined"           "$totalFiles"
Log-Stat "Files with read errors"   "$errorCount"
Log-Stat "Files skipped (binary)"   "$skippedBinary"
if ($choice -eq "1" -or $choice -eq "2" -or $choice -eq "3") {
    Log-Stat "Files skipped (generated)" "$skippedGenerated"
}
Log-Stat "Files skipped (output/script)" "$skippedOutput"
Log-Stat "Total content size"       "$([math]::Round($totalBytes / 1KB, 2)) KB"
Log-Stat "Output file size"         "$([math]::Round($outputSize / 1KB, 2)) KB"
Log-Stat "Time elapsed"             $elapsedStr
Log-Stat "Saved to"                 $outputFile

# Display failure log details if any errors occurred
if ($scriptErrors.Count -gt 0) {
    Write-Host ""
    Write-Host "------------------------------------------------------------" -ForegroundColor Red
    Write-Host "  ⚠️  ERRORS & FAILURES REPORT ($($scriptErrors.Count))" -ForegroundColor Red
    Write-Host "------------------------------------------------------------" -ForegroundColor Red
    foreach ($err in $scriptErrors) {
        Write-Host "  * $err" -ForegroundColor Yellow
    }
    Write-Host "------------------------------------------------------------" -ForegroundColor Red
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  All done! Open combined_files.txt to review the output.  " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""