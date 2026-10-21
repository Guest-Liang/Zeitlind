[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration,
    [switch] $SkipHookBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
. (Join-Path $projectRoot 'tools\build-common.ps1')
$appProject = Join-Path $projectRoot "src\Zeitlind.App\Zeitlind.App.csproj"
$intermediateOutput = Join-Path $projectRoot "artifacts\intermediate\cli"
$appOutput = Join-Path $intermediateOutput "app"
$preparedOutput = Join-Path $intermediateOutput "prepared"
$buildOutput = Join-Path $projectRoot "artifacts\build"
$configurations =
    if ($PSBoundParameters.ContainsKey("Configuration")) {
        @($Configuration)
    }
    else {
        @("Release", "Debug")
    }

$applicationMetadata = Read-ApplicationMetadata -ProjectPath $appProject
$applicationVersion = Read-ApplicationVersion -ProjectRoot $projectRoot
$windowsFileVersion = "$($applicationVersion -replace '-.*$', '').0"

if (-not $SkipHookBuild) {
    & (Join-Path $projectRoot 'build-hooks.ps1')
}
$hookPaths = Get-EmbeddedHookPaths -ProjectRoot $projectRoot

Reset-OutputDirectory -ProjectRoot $projectRoot -Path $intermediateOutput
New-Item -ItemType Directory -Path $appOutput | Out-Null
New-Item -ItemType Directory -Path $preparedOutput | Out-Null

& dotnet restore $appProject
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

foreach ($currentConfiguration in $configurations) {
    Reset-OutputDirectory -ProjectRoot $projectRoot -Path $appOutput
    Write-Host "Building $currentConfiguration Host..."

    & dotnet publish $appProject `
        --configuration $currentConfiguration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $appOutput `
        "-p:ZzzHookBinaryPath=$($hookPaths.Zzz)" `
        "-p:HsrHookBinaryPath=$($hookPaths.Hsr)" `
        "-p:GenshinHookBinaryPath=$($hookPaths.Genshin)" `
        "-p:RequireEmbeddedHook=true" `
        "-p:Version=$applicationVersion" `
        "-p:AssemblyVersion=$windowsFileVersion" `
        "-p:FileVersion=$windowsFileVersion" `
        "-p:InformationalVersion=$applicationVersion" `
        "-p:IncludeSourceRevisionInInformationalVersion=false" `
        "-p:UseExecutableManagedTargetForNativeAot=true" `
        "-p:DebugSymbols=false" `
        "-p:DebugType=None" `
        "-p:CopyOutputSymbolsToPublishDirectory=false"
    if ($LASTEXITCODE -ne 0) {
        throw "Building the $currentConfiguration Host failed with exit code $LASTEXITCODE."
    }

    $appFiles = @(Get-ChildItem -LiteralPath $appOutput -File)
    if ($appFiles.Count -ne 1 -or $appFiles[0].Name -ne "Zeitlind.exe") {
        $names = $appFiles.Name -join ", "
        throw "Expected exactly one $currentConfiguration Host file named Zeitlind.exe, found: $names"
    }

    $assetName = "Zeitlind_v$($applicationVersion)_$($currentConfiguration)_cli.exe"
    $preparedAssetPath = Join-Path $preparedOutput $assetName
    Copy-Item -LiteralPath $appFiles[0].FullName -Destination $preparedAssetPath
    Assert-ExecutableMetadata -Path $preparedAssetPath -Metadata $applicationMetadata -Version $applicationVersion
}

$expectedNames = @(
    $configurations | ForEach-Object {
        "Zeitlind_v$($applicationVersion)_$($_)_cli.exe"
    }
)
$preparedFiles = @(Get-ChildItem -LiteralPath $preparedOutput -File)
$unexpectedPreparedNames = @(
    $preparedFiles.Name | Where-Object {
        $_ -notin $expectedNames
    }
)
$missingPreparedNames = @(
    $expectedNames | Where-Object {
        $_ -notin $preparedFiles.Name
    }
)
if (
    $preparedFiles.Count -ne $expectedNames.Count -or
    $unexpectedPreparedNames.Count -ne 0 -or
    $missingPreparedNames.Count -ne 0
) {
    $message = "Prepared build validation failed. Expected: $($expectedNames -join ', '); found: $($preparedFiles.Name -join ', ')."
    throw $message
}

New-Item -ItemType Directory -Path $buildOutput -Force | Out-Null

foreach ($preparedFile in $preparedFiles) {
    $assetPath = Join-Path $buildOutput $preparedFile.Name
    Copy-Item -LiteralPath $preparedFile.FullName -Destination $assetPath -Force
    Write-Host "Built: $assetPath"
}

$currentVersionNames = @(
    "Zeitlind_v$($applicationVersion)_Release_cli.exe"
    "Zeitlind_v$($applicationVersion)_Debug_cli.exe"
)
$managedAssetPattern = '^Zeitlind_v.+_(?:Release|Debug)(?:_cli)?\.exe$'
$oldBuildFiles = @(
    Get-ChildItem -LiteralPath $buildOutput -File | Where-Object {
        $_.Name -match $managedAssetPattern -and
        $_.Name -notin $currentVersionNames
    }
)
foreach ($oldBuildFile in $oldBuildFiles) {
    Remove-Item -LiteralPath $oldBuildFile.FullName -Force
    Write-Host "Removed old build: $($oldBuildFile.FullName)"
}

$builtFiles = @(Get-ChildItem -LiteralPath $buildOutput -File)
$missingNames = @(
    $expectedNames | Where-Object {
        $_ -notin $builtFiles.Name
    }
)
$remainingOldNames = @(
    $builtFiles.Name | Where-Object {
        $_ -match $managedAssetPattern -and
        $_ -notin $currentVersionNames
    }
)
if ($missingNames.Count -ne 0 -or $remainingOldNames.Count -ne 0) {
    $message = "Build output validation failed. Missing requested files: $($missingNames -join ', '); remaining old files: $($remainingOldNames -join ', ')."
    throw $message
}

Write-Host "Build complete: $buildOutput"
