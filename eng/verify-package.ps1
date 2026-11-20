#requires -Version 7.0
param([Parameter(Mandatory)][string]$PublishedApp,[Parameter(Mandatory)][string]$Installer,[Parameter(Mandatory)][string]$PortableZip)
. (Join-Path $PSScriptRoot 'common.ps1')
$name=$PackageConfig.name
foreach($file in @("$name.exe","$name.dll",'hostfxr.dll','hostpolicy.dll','LICENSE.txt','build-info.json')){if(!(Test-Path -LiteralPath (Join-Path $PublishedApp $file))){throw "Missing self-contained payload file: $file"}}
$payload=@(Get-ChildItem -LiteralPath $PublishedApp -File -Recurse)
$private=@($payload | Where-Object { $_.Name -match '\.db(-wal|-shm)?$|^credentials|^storage\.json$' -or $_.Extension -in @('.pfx','.p12','.key','.gguf','.mkv','.mp4') })
if($private.Count){throw 'Package unexpectedly contains private state, signing keys, models or media.'}
if($PackageConfig.handbrake -and !(Test-Path -LiteralPath (Join-Path $PublishedApp 'handbrake/source/HandBrake-1.11.2-source.tar.bz2'))){throw 'HandBrake source archive is missing.'}
if(!(Test-Path -LiteralPath $Installer) -or (Get-Item -LiteralPath $Installer).VersionInfo.FileDescription.Trim() -ne "$name Setup"){throw 'Installer identity is incorrect.'}
if((Get-AuthenticodeSignature -LiteralPath $Installer).Status -ne 'NotSigned'){throw 'This workflow expects an unsigned installer; review signing before changing the release contract.'}
$zip=[IO.Compression.ZipFile]::OpenRead($PortableZip)
try {
    $entries=@($zip.Entries | Where-Object { $_.Name })
    if($entries.Count -ne $payload.Count){throw 'Portable ZIP and installer payload have different file counts.'}
    foreach($file in $payload) {
        $relative=[IO.Path]::GetRelativePath($PublishedApp,$file.FullName).Replace('\','/')
        $entry=$zip.GetEntry($relative);if(!$entry){throw "Portable ZIP is missing $relative"}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Portable ZIP payload mismatch: $relative"}
    }
}finally{$zip.Dispose()}
Write-Output "Verified $name installer identity and unsigned status, self-contained payload, portable ZIP parity, and exclusion of private state."
