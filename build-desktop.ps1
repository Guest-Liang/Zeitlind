[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $IncludeCliDebug
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$desktopProject = Join-Path $projectRoot "src\Zeitlind.Desktop\Zeitlind.Desktop.csproj"
$versionSource = Get-Content -LiteralPath (Join-Path $projectRoot "src\Zeitlind.Runtime\ApplicationBuildInfo.cs") -Raw
$versionMatches = [regex]::Matches($versionSource, 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"\s*;')
if ($versionMatches.Count -ne 1) {
    throw "Expected exactly one ApplicationBuildInfo.Version constant."
}
$version = $versionMatches[0].Groups[1].Value
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Unsupported application version '$version'."
}
$fileVersion = ($version -split '-')[0] + '.0'

# Build the existing CLI and all three native hooks before bundling the WPF application.
if ($IncludeCliDebug) {
    & (Join-Path $projectRoot "build.ps1")
}
else {
    & (Join-Path $projectRoot "build.ps1") -Configuration $Configuration
}
if ($LASTEXITCODE -ne 0) {
    throw "Building the CLI and native hooks failed with exit code $LASTEXITCODE."
}

$intermediate = Join-Path $projectRoot "artifacts\intermediate"
$staging = Join-Path $intermediate ("desktop-" + [guid]::NewGuid().ToString('N'))
$zzzHook = Join-Path $intermediate "hook-zzz\Zeitlind.Hook.Zzz.dll"
$hsrHook = Join-Path $intermediate "hook-hsr\Zeitlind.Hook.Hsr.dll"
$genshinHook = Join-Path $intermediate "hook-ys\Zeitlind.Hook.Genshin.dll"

Write-Host "Publishing WPF .NET 10 self-contained single EXE ($Configuration)..."
& dotnet publish $desktopProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $staging `
    "-p:ZzzHookBinaryPath=$zzzHook" `
    "-p:HsrHookBinaryPath=$hsrHook" `
    "-p:GenshinHookBinaryPath=$genshinHook" `
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
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($files[0].FullName)
if ($metadata.FileVersion -ne $fileVersion -or $metadata.ProductVersion -ne $version) {
    throw "Published desktop version resources do not match source version $version."
}

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
