#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet(10000, 100000, 1000000)] [int[]] $Sizes = @(10000, 100000),
    [ValidatePattern('^[a-zA-Z0-9-]+$')] [string] $Label = 'measurement',
    [switch] $SkipLargeArtifacts
)
. (Join-Path $PSScriptRoot 'Distribution.Common.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repository 'tests/Relliga.Tests/Relliga.Tests.csproj'
& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
$arguments = @((Join-Path $repository 'tests/Relliga.Tests/bin/Release/net10.0/Relliga.Tests.dll'), '--benchmark', '--sizes', ($Sizes -join ','), '--label', $Label,
    '--output', (Join-Path $repository 'artifacts/benchmarks'))
if ($SkipLargeArtifacts) { $arguments += '--skip-large-artifacts' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Benchmark failed with exit code $LASTEXITCODE. No success claim was generated." }
