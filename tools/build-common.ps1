function Reset-OutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $ProjectRoot,
        [Parameter(Mandatory)]
        [string] $Path
    )

    $intermediateRoot = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts\intermediate'))
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $intermediateRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an output directory outside '$intermediateRoot' or the entire intermediate root."
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedPath -Force | Out-Null
}

function Read-ApplicationVersion {
    param([Parameter(Mandatory)][string] $ProjectRoot)

    $versionSourcePath = Join-Path $ProjectRoot 'src\Zeitlind.Runtime\ApplicationBuildInfo.cs'
    $source = Get-Content -LiteralPath $versionSourcePath -Raw
    $versionMatches = [regex]::Matches($source, 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"\s*;')
    if ($versionMatches.Count -ne 1) {
        throw "Expected exactly one ApplicationBuildInfo.Version constant in '$versionSourcePath'."
    }
    $version = $versionMatches[0].Groups[1].Value
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
        throw "Application version '$version' is not a supported semantic version."
    }
    return $version
}

function Read-ApplicationMetadata {
    param([Parameter(Mandatory)][string] $ProjectPath)

    [xml] $projectXml = Get-Content -LiteralPath $ProjectPath -Raw
    $metadata = [ordered]@{}
    foreach ($name in @('AssemblyName', 'AssemblyTitle', 'Product', 'Company', 'Copyright')) {
        $nodes = @($projectXml.SelectNodes("/Project/PropertyGroup/$name"))
        if ($nodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($nodes[0].InnerText)) {
            throw "Expected exactly one nonempty $name property in '$ProjectPath'."
        }
        $metadata[$name] = $nodes[0].InnerText
    }
    return [pscustomobject] $metadata
}

function Assert-ExecutableMetadata {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][psobject] $Metadata,
        [Parameter(Mandatory)][string] $Version,
        [ValidateSet('.exe', '.dll')][string] $ManagedTargetExtension = '.exe'
    )

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $expected = [ordered]@{
        FileDescription = $Metadata.AssemblyTitle
        FileVersion = "$($Version -replace '-.*$', '').0"
        ProductName = $Metadata.Product
        ProductVersion = $Version
        CompanyName = $Metadata.Company
        LegalCopyright = $Metadata.Copyright
        OriginalFilename = "$($Metadata.AssemblyName)$ManagedTargetExtension"
        InternalName = "$($Metadata.AssemblyName)$ManagedTargetExtension"
    }
    $mismatches = @(
        foreach ($entry in $expected.GetEnumerator()) {
            $actual = $versionInfo.($entry.Key)
            if (-not [string]::Equals($actual, $entry.Value, [StringComparison]::Ordinal)) {
                "$($entry.Key): expected '$($entry.Value)', found '$actual'"
            }
        }
    )
    if ($mismatches.Count -ne 0) {
        throw "Executable metadata validation failed for '$Path': $($mismatches -join '; ')."
    }
}

function Get-EmbeddedHookPaths {
    param([Parameter(Mandatory)][string] $ProjectRoot)

    $paths = [ordered]@{
        Zzz = Join-Path $ProjectRoot 'artifacts\intermediate\hook-zzz\Zeitlind.Hook.Zzz.dll'
        Hsr = Join-Path $ProjectRoot 'artifacts\intermediate\hook-hsr\Zeitlind.Hook.Hsr.dll'
        Genshin = Join-Path $ProjectRoot 'artifacts\intermediate\hook-ys\Zeitlind.Hook.Genshin.dll'
    }
    foreach ($path in $paths.Values) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing native Hook '$path'. Run .\build-hooks.ps1 first, or omit -SkipHookBuild."
        }
    }
    return [pscustomobject] $paths
}
