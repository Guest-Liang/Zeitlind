[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path

& (Join-Path $projectRoot 'build-hooks.ps1')
& (Join-Path $projectRoot 'build.ps1') -SkipHookBuild
& (Join-Path $projectRoot 'build-desktop.ps1') -Configuration Release -SkipHookBuild
