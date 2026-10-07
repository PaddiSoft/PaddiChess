# Native Windows counterpart of build.sh. Uses the same existing Zig toolchain,
# GNU target, sources and adapter; no Bash, Python or Visual Studio is required.
[CmdletBinding()]
param([ValidateSet('win-x64')][string]$Target = 'win-x64')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$herePath = $PSScriptRoot
$projectRoot = [IO.Path]::GetFullPath((Join-Path $herePath '../../..')).TrimEnd([IO.Path]::DirectorySeparatorChar)
$sourcePath = Join-Path $herePath 'upstream/src'
$outputPath = Join-Path $herePath "bin/$Target"
$zigPath = $env:PADDI_ZIG
if ([string]::IsNullOrWhiteSpace($zigPath)) {
    $command = Get-Command zig -ErrorAction SilentlyContinue
    if ($null -ne $command) { $zigPath = $command.Source }
}
if ([string]::IsNullOrWhiteSpace($zigPath) -or -not (Test-Path -LiteralPath $zigPath -PathType Leaf)) {
    throw 'Zig is required. Install Zig 0.13.0 and add zig.exe to PATH, or set PADDI_ZIG to its full executable path.'
}
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$temporaryBinary = Join-Path $outputPath "PaddiRules.$PID.tmp.exe"
$binaryPath = Join-Path $outputPath 'PaddiRules.exe'
$arguments = @('c++', '-std=c++17', '-O2', '-DNDEBUG', '-DIS_64BIT', '-flto',
    '-g0', '-s', "-ffile-prefix-map=$projectRoot=/_", "-fdebug-prefix-map=$projectRoot=/_",
    "-ffile-prefix-map=$($projectRoot.Replace('\', '/'))=/_",
    "-fdebug-prefix-map=$($projectRoot.Replace('\', '/'))=/_", '-fdebug-compilation-dir=/_',
    '-ffunction-sections', '-fdata-sections', '-target', 'x86_64-windows-gnu',
    '-include', (Join-Path $herePath 'windows_threads.h'), '-Wl,--gc-sections', "-I$sourcePath",
    (Join-Path $herePath 'main.cpp'), (Join-Path $sourcePath 'position.cpp'),
    (Join-Path $sourcePath 'movegen.cpp'), (Join-Path $sourcePath 'attacks.cpp'),
    (Join-Path $sourcePath 'bitboard.cpp'), (Join-Path $herePath 'standalone_support.cpp'),
    (Join-Path $sourcePath 'nnue/features/half_ka_v2_hm.cpp'), '-o', $temporaryBinary)
try {
    & $zigPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Native rules compilation failed with exit code $LASTEXITCODE." }
    Move-Item -LiteralPath $temporaryBinary -Destination $binaryPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryBinary) { Remove-Item -LiteralPath $temporaryBinary -Force }
}

# Include the complete corresponding helper source and licence, with portable
# UTF-8 ZIP names. Never include binaries, caches or local filesystem metadata.
# Windows PowerShell 5.1 does not load ZipArchiveMode transitively from FileSystem.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = Join-Path $herePath 'bin/PikafishRules-source.zip'
$temporaryArchive = Join-Path $herePath "bin/PikafishRules-source.$PID.tmp.zip"
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($file in (Get-ChildItem -LiteralPath $herePath -Recurse -File | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($herePath.Length + 1).Replace('\', '/')
        if ($relative.Split('/') -contains 'bin' -or $relative.Split('/') -contains '__pycache__' -or
            $file.Name -eq '.DS_Store') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName,
            $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $archive.Dispose(); $archive = $null
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
}
Write-Output 'Native Windows rules component and corresponding source archive are ready.'
