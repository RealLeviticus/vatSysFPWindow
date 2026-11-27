<#
.SYNOPSIS
    Builds the plugin, merges its dependencies into one assembly, and makes the zip archive.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 1.2.3
    .\build.ps1 -VatSysPath "D:\vatSys"
#>
param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.0.0-local',

    # The vatSys installation directory (the directory that has "bin"). The default is the path in the registry.
    [string]$VatSysPath
)

$ErrorActionPreference = 'Stop'

$plugin = 'FlightPlanWindowPlugin'
$project = Join-Path $PSScriptRoot "source\$plugin\$plugin.csproj"
$outputDirectory = Join-Path $PSScriptRoot "artifacts\$plugin"
$zipPath = Join-Path $PSScriptRoot "artifacts\$plugin.$Version.zip"

if (-not (Test-Path (Join-Path $PSScriptRoot 'external\vatSysToolbox\source'))) {
    git -C $PSScriptRoot submodule update --init
    if ($LASTEXITCODE -ne 0) { throw 'Cannot get the vatSysToolbox submodule' }
}

if (Test-Path $outputDirectory) { Remove-Item -Recurse -Force $outputDirectory }

$majorMinorPatch = $Version.Split('-')[0]
$arguments = @(
    'build', $project,
    '-c', $Configuration,
    '-o', $outputDirectory,
    "-p:Version=$Version",
    "-p:AssemblyVersion=$majorMinorPatch",
    "-p:FileVersion=$majorMinorPatch",
    '-p:Repack=true',
    '-nologo'
)
if ($VatSysPath) { $arguments += "-p:VatSysPath=$VatSysPath" }

dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'The build failed' }

# The archive has the plugin and Harmony only
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Get-ChildItem $outputDirectory -File |
    Where-Object { $_.Extension -ne '.pdb' } |
    Compress-Archive -DestinationPath $zipPath

Write-Host "Plugin: $outputDirectory"
Write-Host "Archive: $zipPath"
