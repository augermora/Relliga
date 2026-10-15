#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')] [string] $RuntimeIdentifier = 'win-x64',
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string] $OutputRoot = '',
    [switch] $Replace
)
. (Join-Path $PSScriptRoot 'Distribution.Common.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repository 'artifacts'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $artifacts 'distribution' }
$OutputRoot = Assert-ChildPath $OutputRoot $artifacts
$version = Get-RelligaVersion $repository
$release = Join-Path $OutputRoot $version
[IO.Directory]::CreateDirectory($release) | Out-Null
$name = "Relliga-$version-$RuntimeIdentifier-portable"
$destination = Assert-ChildPath (Join-Path $release $name) $release
$zip = Assert-ChildPath ($destination + '.zip') $release
if (-not $Replace -and ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zip))) {
    throw 'Output already exists. Use -Replace to replace these generated distribution artifacts.'
}
$staging = Assert-ChildPath (Join-Path $release ('.publish-' + [guid]::NewGuid().ToString('N'))) $release
$temporaryZip = Assert-ChildPath (Join-Path $release ('.archive-' + [guid]::NewGuid().ToString('N') + '.tmp')) $release
try {
    $project = Join-Path $repository 'src/Relliga.Desktop/Relliga.Desktop.csproj'
    & dotnet publish $project -c $Configuration -r $RuntimeIdentifier --self-contained true '-p:PublishProfile=Portable' -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    $executable = if ($RuntimeIdentifier -eq 'win-x64') { 'Relliga.exe' } else { 'Relliga' }
    foreach ($required in @($executable, 'Relliga.dll', 'Relliga.runtimeconfig.json', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required) -PathType Leaf)) { throw "Missing publish output: $required" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $staging 'Relliga.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($runtime.runtimeOptions.ContainsKey('framework') -or $runtime.runtimeOptions.ContainsKey('frameworks')) { throw 'Output depends on a separately installed runtime.' }
    Copy-DependencyNotices (Join-Path $repository 'src/Relliga.Desktop/obj/project.assets.json') $staging
    [ordered]@{ application = 'Relliga'; version = $version; runtimeIdentifier = $RuntimeIdentifier; configuration = $Configuration;
        selfContained = $true; trimmed = $false; aot = $false; singleFile = $false; sdk = (& dotnet --version); unsigned = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'relliga-package.json') -Encoding utf8NoBOM
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    if (Test-Path -LiteralPath $destination) {
        $marker = Join-Path $destination 'relliga-package.json'
        if (-not (Test-Path -LiteralPath $marker) -or ((Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).application -ne 'Relliga')) { throw 'Existing directory is not owned by this packaging script.' }
        $null = Assert-ChildPath $destination $release
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $destination
    if (Test-Path -LiteralPath $zip) { [IO.File]::Replace($temporaryZip, $zip, [NullString]::Value) } else { [IO.File]::Move($temporaryZip, $zip) }
    Write-ArtifactChecksums $release $version
    Write-Output "Portable directory: $destination"
    Write-Output "Portable archive: $zip"
    Write-Output "Checksums: $(Join-Path $release 'SHA256SUMS.txt')"
}
finally {
    if (Test-Path -LiteralPath $staging) { $null = Assert-ChildPath $staging $release; Remove-Item -LiteralPath $staging -Recurse -Force }
    if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip }
}
