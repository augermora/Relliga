#Requires -Version 7.0
[CmdletBinding()]
param([string] $OutputRoot = '', [ValidateSet('win-x64','linux-x64','osx-arm64')] [string[]] $RuntimeIdentifiers = @('win-x64'))
. (Join-Path $PSScriptRoot 'Distribution.Common.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repository 'artifacts/distribution' }
$OutputRoot = Assert-ChildPath $OutputRoot (Join-Path $repository 'artifacts')
$version = Get-RelligaVersion $repository
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $parseTokens = $null; $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$parseTokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "PowerShell syntax errors in $($script.Name): $parseErrors" }
}
$scratch = Assert-ChildPath (Join-Path $repository ('artifacts/packaging-tests-' + [guid]::NewGuid().ToString('N'))) (Join-Path $repository 'artifacts')
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    $rejected = $false
    try { $null = Assert-ChildPath (Join-Path $scratch '../outside') $scratch } catch { $rejected = $true }
    if (-not $rejected) { throw 'Output-boundary protection failed.' }
    $sample = Join-Path $scratch "Relliga-$version-synthetic.zip"
    [IO.File]::WriteAllText($sample, 'Synthetic packaging checksum fixture')
    Write-ArtifactChecksums $scratch $version
    Write-ArtifactChecksums $scratch $version # Replacement must work too, including PowerShell null-string binding.
    $hash = (Get-FileHash -LiteralPath $sample).Hash.ToLowerInvariant()
    if ((Get-Content -LiteralPath (Join-Path $scratch 'SHA256SUMS.txt') -Raw) -notmatch $hash) { throw 'Checksum writer failed.' }
}
finally { $null = Assert-ChildPath $scratch (Join-Path $repository 'artifacts'); Remove-Item -LiteralPath $scratch -Recurse -Force }

$release = Join-Path $OutputRoot $version
foreach ($rid in $RuntimeIdentifiers) {
    $name = "Relliga-$version-$rid-portable"
    $directory = Join-Path $release $name
    $required = switch ($rid) {
        'win-x64' { @('Relliga.exe', 'coreclr.dll', 'hostfxr.dll', 'e_sqlite3.dll') }
        'linux-x64' { @('Relliga', 'libcoreclr.so', 'libhostfxr.so', 'libe_sqlite3.so') }
        'osx-arm64' { @('Relliga', 'libcoreclr.dylib', 'libhostfxr.dylib', 'libe_sqlite3.dylib') }
    }
    foreach ($file in $required + @('LICENSE','THIRD-PARTY-NOTICES.md','dependency-inventory.json','relliga-package.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $file))) { throw "Missing $rid payload: $file" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $directory 'Relliga.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($runtime.runtimeOptions.ContainsKey('framework') -or $runtime.runtimeOptions.ContainsKey('frameworks')) { throw 'Framework-dependent output detected.' }
    $metadata = Get-Content -LiteralPath (Join-Path $directory 'relliga-package.json') -Raw | ConvertFrom-Json
    if ($metadata.version -ne $version -or -not $metadata.selfContained -or $metadata.trimmed -or $metadata.aot -or $metadata.singleFile) { throw 'Unsafe or inconsistent publish metadata.' }
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $release ($name + '.zip')))
    try {
        if ($archive.Entries.FullName -match '(?i)\.db($|-)|\.git/|TestResults/|Relliga.Tests|synthetic-spotify|synthetic-calendar|synthetic-telegram|\.dpapi|Connections/Vault') { throw 'Private/test data leaked into the portable archive.' }
        foreach ($file in $required) { if ($null -eq $archive.GetEntry($file)) { throw "Missing archive runtime file: $file" } }
    }
    finally { $archive.Dispose() }
    Write-Output "$rid payload, runtime, license, archive and configuration checks passed."
}
foreach ($line in Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-f0-9]{64})  (Relliga-[^/\\]+)$') { throw 'Invalid checksum line.' }
    $expected = $Matches[1]; $file = Join-Path $release $Matches[2]
    if ((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -ne $expected) { throw "Checksum mismatch: $file" }
}
Write-Output 'All distribution script and checksum checks passed. This does not claim GUI or installer execution.'
