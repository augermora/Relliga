Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ChildPath([string] $Path, [string] $Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside generated output root: $resolved"
    }
    $cursor = [IO.DirectoryInfo]::new($resolved)
    while ($null -ne $cursor) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked output directories are not allowed: $($cursor.FullName)" }
        $cursor = $cursor.Parent
    }
    if ([IO.File]::Exists($resolved) -and ([IO.File]::GetAttributes($resolved) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked output files are not allowed.' }
    return $resolved
}

function Get-RelligaVersion([string] $Repository) {
    [xml] $properties = Get-Content -LiteralPath (Join-Path $Repository 'Directory.Build.props') -Raw
    $version = [string] $properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part release version.' }
    return $version
}

function Write-ArtifactChecksums([string] $Directory, [string] $Version) {
    $lines = @(Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { $_.Name -like "Relliga-$Version-*" -and $_.Extension -in '.zip', '.exe' } |
        Sort-Object Name | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
        })
    $target = Join-Path $Directory 'SHA256SUMS.txt'
    $temporary = $target + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllLines($temporary, $lines, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $target) }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function Copy-DependencyNotices([string] $AssetsPath, [string] $PublishDirectory) {
    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
    $licenses = Join-Path $PublishDirectory 'licenses'
    [IO.Directory]::CreateDirectory($licenses) | Out-Null
    $inventory = @()
    foreach ($entry in $assets.libraries.GetEnumerator() | Sort-Object Key) {
        if ($entry.Value.type -ne 'package') { continue }
        $parts = $entry.Key.Split('/')
        if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[a-zA-Z0-9_.-]+$' -or $parts[1] -notmatch '^[a-zA-Z0-9_.+-]+$') { throw 'Invalid package identity.' }
        $package = $null
        foreach ($folder in $assets.packageFolders.Keys) {
            $candidate = Join-Path $folder $entry.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $package = $candidate; break }
        }
        if ($null -eq $package) { throw "Cannot locate restored package $($entry.Key) for license notices." }
        $destination = Join-Path $licenses ($parts[0] + '.' + $parts[1])
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        $noticeFiles = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '(?i)license|notice|copyright|\.nuspec$' })
        foreach ($notice in $noticeFiles) { Copy-Item -LiteralPath $notice.FullName -Destination $destination }
        $nuspec = Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File | Select-Object -First 1
        if ($null -eq $nuspec) { throw "Missing package metadata for $($entry.Key)." }
        [xml] $metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
        $licenseNode = $metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
        $license = if ($null -eq $licenseNode) { 'See included package notices/nuspec' } else { $licenseNode.InnerText }
        $copyrightNode = $metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='copyright']")
        $authorsNode = $metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='authors']")
        $attribution = @("Package: $($entry.Key)", "Declared license: $license")
        if ($null -ne $copyrightNode) { $attribution += $copyrightNode.InnerText }
        if ($null -ne $authorsNode) { $attribution += 'Authors: ' + $authorsNode.InnerText }
        [IO.File]::WriteAllLines((Join-Path $destination 'ATTRIBUTION.txt'), $attribution, [Text.UTF8Encoding]::new($false))
        if ($license -in 'MIT', 'Apache-2.0') {
            $terms = Join-Path $PSScriptRoot "../packaging/licenses/$license.txt"
            Copy-Item -LiteralPath $terms -Destination (Join-Path $destination 'LICENSE-declared.txt')
        }
        if ($null -ne $licenseNode -and $licenseNode.GetAttribute('type') -eq 'file') {
            $licensePath = Assert-ChildPath (Join-Path $package $license) $package
            if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw "Missing declared license: $($entry.Key)" }
            Copy-Item -LiteralPath $licensePath -Destination $destination -Force
        }
        $inventory += [ordered]@{ id = $parts[0]; version = $parts[1]; license = $license }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../packaging/licenses') -Destination (Join-Path $licenses 'upstream') -Recurse
    $inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PublishDirectory 'dependency-inventory.json') -Encoding utf8NoBOM
}
