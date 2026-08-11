[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$solutionPath = Join-Path $projectRoot "Zeitlind.slnx"
$zzzHookProject = Join-Path $projectRoot "src\Zeitlind.Hook.Zzz\Zeitlind.Hook.Zzz.csproj"
$hsrHookProject = Join-Path $projectRoot "src\Zeitlind.Hook.Hsr\Zeitlind.Hook.Hsr.csproj"
$appProject = Join-Path $projectRoot "src\Zeitlind.App\Zeitlind.App.csproj"
$versionSourcePath = Join-Path $projectRoot "src\Zeitlind.App\ApplicationBuildInfo.cs"
$intermediateOutput = Join-Path $projectRoot "artifacts\intermediate"
$zzzHookOutput = Join-Path $intermediateOutput "hook-zzz"
$hsrHookOutput = Join-Path $intermediateOutput "hook-hsr"
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

function Reset-OutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($projectRoot)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $resolvedRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    if (-not $resolvedPath.StartsWith(
        $requiredPrefix,
        [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to clean an output directory outside '$resolvedRoot'."
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolvedPath | Out-Null
}

function Read-ApplicationMetadata {
    if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
        throw "The application project was not found at '$appProject'."
    }

    [xml] $projectXml = Get-Content -LiteralPath $appProject -Raw
    $propertyNames = @(
        "AssemblyName"
        "AssemblyTitle"
        "Product"
        "Company"
        "Copyright"
    )
    $metadata = [ordered]@{}

    foreach ($propertyName in $propertyNames) {
        $propertyNodes = @(
            $projectXml.SelectNodes("/Project/PropertyGroup/$propertyName")
        )
        if ($propertyNodes.Count -ne 1) {
            throw "Expected exactly one $propertyName property in '$appProject'."
        }

        $propertyValue = $propertyNodes[0].InnerText
        if ([string]::IsNullOrWhiteSpace($propertyValue)) {
            throw "The $propertyName property in '$appProject' must not be empty."
        }

        $metadata[$propertyName] = $propertyValue
    }

    return [pscustomobject] $metadata
}

function Read-ApplicationVersion {
    if (-not (Test-Path -LiteralPath $versionSourcePath -PathType Leaf)) {
        throw "The application version source was not found at '$versionSourcePath'."
    }

    $source = Get-Content -LiteralPath $versionSourcePath -Raw
    $versionMatches = [regex]::Matches(
        $source,
        'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"\s*;'
    )
    if ($versionMatches.Count -ne 1) {
        throw "Expected exactly one ApplicationBuildInfo.Version constant in '$versionSourcePath'."
    }

    $version = $versionMatches[0].Groups[1].Value
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
        throw "Application version '$version' is not a supported semantic version."
    }

    return $version
}

function Assert-ExecutableMetadata {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $expectedValues = [ordered]@{
        FileDescription = $applicationMetadata.AssemblyTitle
        FileVersion = $windowsFileVersion
        ProductName = $applicationMetadata.Product
        ProductVersion = $applicationVersion
        CompanyName = $applicationMetadata.Company
        LegalCopyright = $applicationMetadata.Copyright
        OriginalFilename = "$($applicationMetadata.AssemblyName).exe"
        InternalName = "$($applicationMetadata.AssemblyName).exe"
    }

    $mismatches = @(
        foreach ($entry in $expectedValues.GetEnumerator()) {
            $actualValue = $versionInfo.($entry.Key)
            if (-not [string]::Equals(
                $actualValue,
                $entry.Value,
                [StringComparison]::Ordinal))
            {
                "$($entry.Key): expected '$($entry.Value)', found '$actualValue'"
            }
        }
    )
    if ($mismatches.Count -ne 0) {
        throw "Executable metadata validation failed for '$Path': $($mismatches -join '; ')."
    }
}

$applicationMetadata = Read-ApplicationMetadata
$applicationVersion = Read-ApplicationVersion
$windowsFileVersion = "$($applicationVersion -replace '-.*$', '').0"

Reset-OutputDirectory -Path $intermediateOutput
New-Item -ItemType Directory -Path $zzzHookOutput | Out-Null
New-Item -ItemType Directory -Path $hsrHookOutput | Out-Null
New-Item -ItemType Directory -Path $appOutput | Out-Null
New-Item -ItemType Directory -Path $preparedOutput | Out-Null

& dotnet restore $solutionPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Write-Host "Building ZZZ Hook (Release)..."
& dotnet publish $zzzHookProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $zzzHookOutput
if ($LASTEXITCODE -ne 0) {
    throw "Building the ZZZ Hook library failed with exit code $LASTEXITCODE."
}

$zzzHookBinary = Join-Path $zzzHookOutput "Zeitlind.Hook.Zzz.dll"
if (-not (Test-Path -LiteralPath $zzzHookBinary -PathType Leaf)) {
    throw "The ZZZ NativeAOT Hook library was not produced at '$zzzHookBinary'."
}

Write-Host "Building HSR Hook (Release)..."
& dotnet publish $hsrHookProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $hsrHookOutput
if ($LASTEXITCODE -ne 0) {
    throw "Building the HSR Hook library failed with exit code $LASTEXITCODE."
}

$hsrHookBinary = Join-Path $hsrHookOutput "Zeitlind.Hook.Hsr.dll"
if (-not (Test-Path -LiteralPath $hsrHookBinary -PathType Leaf)) {
    throw "The HSR NativeAOT Hook library was not produced at '$hsrHookBinary'."
}

foreach ($currentConfiguration in $configurations) {
    Reset-OutputDirectory -Path $appOutput
    Write-Host "Building $currentConfiguration Host..."

    & dotnet publish $appProject `
        --configuration $currentConfiguration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $appOutput `
        "-p:ZzzHookBinaryPath=$zzzHookBinary" `
        "-p:HsrHookBinaryPath=$hsrHookBinary" `
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

    $assetName = "Zeitlind_v$($applicationVersion)_$currentConfiguration.exe"
    $preparedAssetPath = Join-Path $preparedOutput $assetName
    Copy-Item -LiteralPath $appFiles[0].FullName -Destination $preparedAssetPath
    Assert-ExecutableMetadata -Path $preparedAssetPath
}

$expectedNames = @(
    $configurations | ForEach-Object {
        "Zeitlind_v$($applicationVersion)_$($_).exe"
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
    "Zeitlind_v$($applicationVersion)_Release.exe"
    "Zeitlind_v$($applicationVersion)_Debug.exe"
)
$managedAssetPattern = '^Zeitlind_v.+_(?:Release|Debug)\.exe$'
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
