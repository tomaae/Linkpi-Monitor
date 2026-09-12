param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$pauseOnExit = -not $NoPause -and
    [Environment]::UserInteractive -and
    (-not [Console]::IsInputRedirected)

try {

$workspaceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$solutionPath = Join-Path $workspaceRoot "Linkpi Monitor.slnx"
$projectPath = Join-Path $workspaceRoot "Linkpi Monitor\Linkpi Monitor.csproj"
$runtimeIdentifier = "win-x64"
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot "artifacts"))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot "LinkpiMonitor"))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot "LinkpiMonitor.staging"))
$archivePath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "LinkpiMonitor-win-x64.zip"))
$temporaryArchivePath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "LinkpiMonitor-win-x64.pending.zip"))
$checksumPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "LinkpiMonitor-win-x64.zip.sha256"))
$temporaryChecksumPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot "LinkpiMonitor-win-x64.zip.pending.sha256"))
$artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar

$validatedArtifactNames = @(
    "LinkpiMonitor",
    "LinkpiMonitor.staging",
    "LinkpiMonitor-win-x64.zip",
    "LinkpiMonitor-win-x64.pending.zip",
    "LinkpiMonitor-win-x64.zip.sha256",
    "LinkpiMonitor-win-x64.zip.pending.sha256"
)

foreach ($artifactPath in @(
    $publishRoot,
    $stagingRoot,
    $archivePath,
    $temporaryArchivePath,
    $checksumPath,
    $temporaryChecksumPath
)) {
    if (-not $artifactPath.StartsWith(
        $artifactPrefix,
        [StringComparison]::OrdinalIgnoreCase
    ) -or (Split-Path -Leaf $artifactPath) -notin $validatedArtifactNames) {
        throw "Artifact path validation failed: $artifactPath"
    }
}

Write-Host "Restoring project..."
& dotnet restore $solutionPath --runtime $runtimeIdentifier
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Write-Host "Running Release tests..."
& dotnet test $solutionPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Release tests failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse
}

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

Write-Host "Publishing LinkPi Monitor single-file package..."
& dotnet publish $projectPath `
    --configuration Release `
    --no-restore `
    --runtime $runtimeIdentifier `
    --self-contained false `
    --output $stagingRoot
if ($LASTEXITCODE -ne 0) {
    throw "LinkPi Monitor publish failed with exit code $LASTEXITCODE."
}

$stagingPrefix = $stagingRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
$unneededVlcLibraries = @(
    (Join-Path $stagingRoot "libvlc\win-x64\libvlc.lib"),
    (Join-Path $stagingRoot "libvlc\win-x64\libvlccore.lib")
)

foreach ($libraryPath in $unneededVlcLibraries) {
    $validatedLibraryPath = [IO.Path]::GetFullPath($libraryPath)
    if (-not $validatedLibraryPath.StartsWith(
        $stagingPrefix,
        [StringComparison]::OrdinalIgnoreCase
    ) -or [IO.Path]::GetExtension($validatedLibraryPath) -ne ".lib") {
        throw "LibVLC cleanup path validation failed: $validatedLibraryPath"
    }

    if (Test-Path -LiteralPath $validatedLibraryPath -PathType Leaf) {
        Remove-Item -LiteralPath $validatedLibraryPath
    }
}

Copy-Item -LiteralPath (Join-Path $workspaceRoot "LICENSE") -Destination $stagingRoot
Copy-Item -LiteralPath (Join-Path $workspaceRoot "THIRD-PARTY-NOTICES.txt") -Destination $stagingRoot

$forbiddenPackagedConfigPath = Join-Path $stagingRoot "config.json"
if (Test-Path -LiteralPath $forbiddenPackagedConfigPath) {
    throw "Release packages must not contain config.json."
}

$expectedFiles = @(
    "Linkpi Monitor.exe",
    "LICENSE",
    "THIRD-PARTY-NOTICES.txt"
)

$publishedFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse)
$unexpectedFiles = @($publishedFiles | Where-Object {
    $relativePath = [IO.Path]::GetRelativePath($stagingRoot, $_.FullName)
    $_.DirectoryName -ne $stagingRoot -and
        -not $relativePath.StartsWith("libvlc\win-x64\", [StringComparison]::OrdinalIgnoreCase)
})
$unexpectedRootFiles = @($publishedFiles | Where-Object {
    $_.DirectoryName -eq $stagingRoot -and $_.Name -notin $expectedFiles
})
$missingFiles = @($expectedFiles |
    Where-Object { -not (Test-Path -LiteralPath (Join-Path $stagingRoot $_) -PathType Leaf) })
$requiredVlcFiles = @(
    "libvlc\win-x64\libvlc.dll",
    "libvlc\win-x64\libvlccore.dll",
    "libvlc\win-x64\plugins\access\liblive555_plugin.dll",
    "libvlc\win-x64\plugins\codec\libavcodec_plugin.dll",
    "libvlc\win-x64\plugins\video_output\libdirect3d11_plugin.dll"
)
$missingVlcFiles = @($requiredVlcFiles |
    Where-Object { -not (Test-Path -LiteralPath (Join-Path $stagingRoot $_) -PathType Leaf) })

if ($unexpectedFiles.Count -gt 0 -or $unexpectedRootFiles.Count -gt 0) {
    $unexpectedPaths = @($unexpectedFiles.FullName) + @($unexpectedRootFiles.FullName)
    throw "Unexpected publish files: $($unexpectedPaths -join ', ')"
}

if ($missingFiles.Count -gt 0) {
    throw "Missing publish files: $($missingFiles -join ', ')"
}

if ($missingVlcFiles.Count -gt 0) {
    throw "Missing LibVLC runtime files: $($missingVlcFiles -join ', ')"
}

if (Test-Path -LiteralPath $temporaryArchivePath) {
    Remove-Item -LiteralPath $temporaryArchivePath
}

Compress-Archive `
    -Path (Join-Path $stagingRoot "*") `
    -DestinationPath $temporaryArchivePath `
    -CompressionLevel Optimal

if (-not (Test-Path -LiteralPath $temporaryArchivePath -PathType Leaf)) {
    throw "Temporary package archive was not created: $temporaryArchivePath"
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath
}

Move-Item -LiteralPath $temporaryArchivePath -Destination $archivePath

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $temporaryChecksumPath `
    -Value "$archiveHash *$([IO.Path]::GetFileName($archivePath))" `
    -Encoding ascii `
    -NoNewline
Move-Item -LiteralPath $temporaryChecksumPath -Destination $checksumPath -Force

$publishRootLocked = $false
if (Test-Path -LiteralPath $publishRoot) {
    foreach ($publishedFile in $publishedFiles) {
        $relativePath = [IO.Path]::GetRelativePath($stagingRoot, $publishedFile.FullName)
        $existingFilePath = Join-Path $publishRoot $relativePath
        if (-not (Test-Path -LiteralPath $existingFilePath -PathType Leaf)) {
            continue
        }

        try {
            $stream = [IO.File]::Open(
                $existingFilePath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None
            )
            $stream.Dispose()
        }
        catch [UnauthorizedAccessException] {
            $publishRootLocked = $true
            break
        }
        catch [IO.IOException] {
            $publishRootLocked = $true
            break
        }
    }
}

if ($publishRootLocked) {
    $currentExtractedPackage = $stagingRoot
    Write-Warning "The existing extracted package is in use. Its folder was left untouched; the current package is available in LinkpiMonitor.staging and the ZIP was updated."
}
else {
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse
    }

    Move-Item -LiteralPath $stagingRoot -Destination $publishRoot
    $currentExtractedPackage = $publishRoot
}

Write-Host "Publish completed and audited: $currentExtractedPackage"
$packagedFiles = @(Get-ChildItem -LiteralPath $currentExtractedPackage -File -Recurse)
$packageSize = ($packagedFiles | Measure-Object -Property Length -Sum).Sum
[PSCustomObject]@{
    Files = $packagedFiles.Count
    SizeMB = [Math]::Round($packageSize / 1MB, 1)
}
Write-Host "Package archive: $archivePath"
Get-Item -LiteralPath $archivePath |
    Select-Object Name, Length
Write-Host "SHA-256 checksum: $checksumPath"
Get-Content -LiteralPath $checksumPath

}
finally {
    if ($pauseOnExit) {
        [void](Read-Host "Press Enter to close this window")
    }
}
