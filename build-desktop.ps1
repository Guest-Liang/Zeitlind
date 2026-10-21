[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $IncludeCliDebug,
    [switch] $SkipHookBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
. (Join-Path $projectRoot 'tools\build-common.ps1')
$desktopProject = Join-Path $projectRoot "src\Zeitlind.Desktop\Zeitlind.Desktop.csproj"
$version = Read-ApplicationVersion -ProjectRoot $projectRoot
$applicationMetadata = Read-ApplicationMetadata -ProjectPath $desktopProject
$fileVersion = ($version -split '-')[0] + '.0'

# Build native hooks independently; the desktop-only path does not publish the CLI.
if (-not $SkipHookBuild) {
    & (Join-Path $projectRoot 'build-hooks.ps1')
}
$hookPaths = Get-EmbeddedHookPaths -ProjectRoot $projectRoot
if ($IncludeCliDebug) {
    & (Join-Path $projectRoot 'build.ps1') -SkipHookBuild
}

$intermediate = Join-Path $projectRoot "artifacts\intermediate"
$staging = Join-Path $intermediate "desktop-$Configuration"
Reset-OutputDirectory -ProjectRoot $projectRoot -Path $staging

Write-Host "Publishing WPF .NET 10 self-contained single EXE ($Configuration)..."
& dotnet publish $desktopProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $staging `
    "-p:ZzzHookBinaryPath=$($hookPaths.Zzz)" `
    "-p:HsrHookBinaryPath=$($hookPaths.Hsr)" `
    "-p:GenshinHookBinaryPath=$($hookPaths.Genshin)" `
    "-p:RequireEmbeddedHook=true" `
    "-p:Version=$version" `
    "-p:AssemblyVersion=$fileVersion" `
    "-p:FileVersion=$fileVersion" `
    "-p:InformationalVersion=$version" `
    "-p:CopyOutputSymbolsToPublishDirectory=false"
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the WPF application failed with exit code $LASTEXITCODE."
}

$files = @(Get-ChildItem -LiteralPath $staging -Recurse -File)
if ($files.Count -ne 1 -or $files[0].Name -ne "Zeitlind.Desktop.exe") {
    throw "Expected exactly one Zeitlind.Desktop.exe, found: $($files.Name -join ', ')"
}
Assert-ExecutableMetadata -Path $files[0].FullName -Metadata $applicationMetadata -Version $version -ManagedTargetExtension '.dll'

# Only replace this named build artifact; preserve any user exports or logs beside it.
$outputDirectory = Join-Path $projectRoot "artifacts\desktop"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$output = Join-Path $outputDirectory "Zeitlind_v$($version)_$($Configuration)_desktop.exe"
Copy-Item -LiteralPath $files[0].FullName -Destination $output -Force
$currentVersionNames = @(
    "Zeitlind_v$($version)_Release_desktop.exe"
    "Zeitlind_v$($version)_Debug_desktop.exe"
)
$managedAssetPattern = '^Zeitlind_v.+_(?:WPF_(?:Release|Debug)|(?:Release|Debug)_desktop)\.exe$'
foreach ($oldBuild in Get-ChildItem -LiteralPath $outputDirectory -File | Where-Object {
    $_.Name -match $managedAssetPattern -and $_.Name -notin $currentVersionNames
}) {
    try {
        Remove-Item -LiteralPath $oldBuild.FullName -Force
        Write-Host "Removed old desktop build: $($oldBuild.FullName)"
    }
    catch [System.IO.IOException] {
        Write-Warning "Could not remove old desktop build '$($oldBuild.FullName)': $($_.Exception.Message)"
    }
}
$hash = Get-FileHash -LiteralPath $output -Algorithm SHA256
Write-Host "Built single EXE: $output"
Write-Host "Size: $([math]::Round((Get-Item -LiteralPath $output).Length / 1MB, 2)) MiB"
Write-Host "SHA-256: $($hash.Hash)"
