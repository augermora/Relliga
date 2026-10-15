#Requires -Version 7.0
[CmdletBinding()]
param([string] $Executable = (Join-Path $PSScriptRoot '../src/Relliga.Desktop/bin/Debug/net10.0/Relliga.exe'))
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Live credentials currently require Windows DPAPI.' }
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Build Relliga first, or pass -Executable pointing to the published Relliga.exe.' }
$applicationId = Read-Host 'Publisher/developer Telegram API ID (not a phone number)'
if ($applicationId -notmatch '^[1-9][0-9]{0,9}$') { throw 'Invalid application ID.' }
$protectedHash = Read-Host 'Publisher/developer Telegram API hash (hidden; never written to a file)' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($protectedHash)
$previousId = $env:RELLIGA_TELEGRAM_API_ID
$previousHash = $env:RELLIGA_TELEGRAM_API_HASH
try {
    $applicationHash = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ($applicationHash -notmatch '^[a-fA-F0-9]{32}$') { throw 'Invalid application hash.' }
    $env:RELLIGA_TELEGRAM_API_ID = $applicationId
    $env:RELLIGA_TELEGRAM_API_HASH = $applicationHash
    Write-Output 'Opening Relliga. Connect in Connected accounts to protect the application registration with DPAPI. This developer setup does not authenticate a Telegram user.'
    Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -Wait
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    $protectedHash.Dispose()
    $applicationHash = $null
    $env:RELLIGA_TELEGRAM_API_ID = $previousId
    $env:RELLIGA_TELEGRAM_API_HASH = $previousHash
}
