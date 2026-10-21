[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
. (Join-Path $projectRoot 'tools\build-common.ps1')

$hooks = @(
    @{ Project = 'Zeitlind.Hook.Zzz'; Directory = 'hook-zzz' }
    @{ Project = 'Zeitlind.Hook.Hsr'; Directory = 'hook-hsr' }
    @{ Project = 'Zeitlind.Hook.Genshin'; Directory = 'hook-ys' }
)
foreach ($hook in $hooks) {
    $project = Join-Path $projectRoot "src\$($hook.Project)\$($hook.Project).csproj"
    $output = Join-Path $projectRoot "artifacts\intermediate\$($hook.Directory)"
    Reset-OutputDirectory -ProjectRoot $projectRoot -Path $output
    Write-Host "Building $($hook.Project) (Release)..."
    & dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $output
    if ($LASTEXITCODE -ne 0) {
        throw "Building $($hook.Project) failed with exit code $LASTEXITCODE."
    }
    $binary = Join-Path $output "$($hook.Project).dll"
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "The NativeAOT Hook library was not produced at '$binary'."
    }
}
Write-Host 'All three Release native hooks are ready.'
