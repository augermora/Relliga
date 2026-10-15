#Requires -Version 7.0
[CmdletBinding()]
param([string] $CompilerPath = '', [string] $OutputRoot = '', [switch] $Replace)
. (Join-Path $PSScriptRoot 'Distribution.Common.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $IsWindows) { throw 'Compile the Windows installer on Windows with NSIS 3.x.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repository 'artifacts/distribution' }
$OutputRoot = Assert-ChildPath $OutputRoot (Join-Path $repository 'artifacts')
$version = Get-RelligaVersion $repository
$release = Join-Path $OutputRoot $version
$publish = Join-Path $release "Relliga-$version-win-x64-portable"
if (-not (Test-Path -LiteralPath (Join-Path $publish 'relliga-package.json'))) { throw 'Run Publish-Portable.ps1 -RuntimeIdentifier win-x64 first.' }
$package = Get-Content -LiteralPath (Join-Path $publish 'relliga-package.json') -Raw | ConvertFrom-Json
if ($package.version -ne $version -or $package.runtimeIdentifier -ne 'win-x64' -or -not $package.selfContained) { throw 'The published package does not match this installer.' }
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { $CompilerPath = $command.Source }
    else {
        foreach ($candidate in @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe")) {
            if (Test-Path -LiteralPath $candidate) { $CompilerPath = $candidate; break }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'NSIS 3.x compiler unavailable. Install NSIS from https://nsis.sourceforge.io/Download or pass -CompilerPath to its makensis.exe. Portable output is still usable; no installer was created.'
}
$directCompiler = Join-Path (Split-Path -Parent $CompilerPath) 'Bin/makensis.exe'
if (Test-Path -LiteralPath $directCompiler -PathType Leaf) { $CompilerPath = $directCompiler }
$compilerVersion = & $CompilerPath /VERSION
if ($LASTEXITCODE -ne 0 -or "$compilerVersion" -notmatch '^v?3\.') { throw "Unsupported NSIS compiler: $compilerVersion" }
$installer = Assert-ChildPath (Join-Path $release "Relliga-$version-win-x64-installer.exe") $release
if ((Test-Path -LiteralPath $installer) -and -not $Replace) { throw 'Installer exists. Use -Replace to replace generated output.' }
$staging = Assert-ChildPath (Join-Path $release ('.installer-' + [guid]::NewGuid().ToString('N'))) $release
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    # Delete only the exact files shipped by this package. Never recursively remove an installation directory.
    $removal = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($publish, $file.FullName).Replace('/', '\')
        if ($relative -match '[\r\n"$]') { throw 'Unexpected packaged filename.' }
        $removal.Add('Delete "$INSTDIR\' + $relative + '"')
    }
    foreach ($directory in Get-ChildItem -LiteralPath $publish -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
        $relative = [IO.Path]::GetRelativePath($publish, $directory.FullName).Replace('/', '\')
        if ($relative -match '[\r\n"$]') { throw 'Unexpected packaged directory.' }
        $removal.Add('RMDir "$INSTDIR\' + $relative + '"')
    }
    $include = Join-Path $staging 'uninstall-files.nsh'
    [IO.File]::WriteAllLines($include, $removal, [Text.UTF8Encoding]::new($false))
    $temporary = Join-Path $staging 'Relliga-installer.exe'
    & $CompilerPath /NOCONFIG /INPUTCHARSET UTF8 /V3 "/DVERSION=$version" "/DPUBLISH_DIR=$publish" "/DOUTPUT_FILE=$temporary" "/DUNINSTALL_FILES=$include" (Join-Path $repository 'packaging/windows/Relliga.nsi')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $temporary)) { throw 'NSIS compilation failed; no installer was published.' }
    if (Test-Path -LiteralPath $installer) { [IO.File]::Replace($temporary, $installer, [NullString]::Value) } else { [IO.File]::Move($temporary, $installer) }
    Write-ArtifactChecksums $release $version
    Write-Output "Unsigned installer: $installer"
}
finally { $null = Assert-ChildPath $staging $release; Remove-Item -LiteralPath $staging -Recurse -Force }
