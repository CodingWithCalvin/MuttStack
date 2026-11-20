#requires -Version 7.0
param([Parameter(Mandatory)][string]$Package)
. (Join-Path $PSScriptRoot 'common.ps1')
$directory=Join-Path $RepoRoot ('artifacts\validation\msix-'+[guid]::NewGuid().ToString('N'))
& (Get-WindowsSdkTool 'makeappx.exe') unpack /p ([IO.Path]::GetFullPath($Package)) /d $directory /o
if($LASTEXITCODE -ne 0){throw 'MSIX extraction/validation failed.'}
[xml]$manifest=Get-Content -LiteralPath (Join-Path $directory 'AppxManifest.xml') -Raw
$ns=[Xml.XmlNamespaceManager]::new($manifest.NameTable);$ns.AddNamespace('a','http://schemas.microsoft.com/appx/manifest/foundation/windows10');$ns.AddNamespace('d','http://schemas.microsoft.com/appx/manifest/desktop/windows10/6');$ns.AddNamespace('r','http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
if($manifest.SelectSingleNode('/a:Package/a:Properties/d:FileSystemWriteVirtualization',$ns).InnerText -ne 'disabled'){throw 'MSIX would isolate the shared database under package-local AppData.'}
foreach($capability in @('runFullTrust','unvirtualizedResources')){if(!$manifest.SelectSingleNode("/a:Package/a:Capabilities/r:Capability[@Name='$capability']",$ns)){throw "Missing $capability capability."}}
$name=$PackageConfig.name
foreach($file in @("$name.exe","$name.dll",'hostfxr.dll','hostpolicy.dll','LICENSE.txt','build-info.json')){if(!(Test-Path -LiteralPath (Join-Path $directory $file))){throw "Missing self-contained payload file: $file"}}
$private=Get-ChildItem -LiteralPath $directory -File -Recurse | Where-Object { $_.Name -match '^(doggo|scout)\.db(-wal|-shm)?$|^credentials|^storage\.json$' -or $_.Extension -in @('.pfx','.key','.gguf','.mkv','.mp4') }
if($private){throw 'Package unexpectedly contains private state, signing keys, models or media.'}
if($PackageConfig.handbrake -and !(Test-Path -LiteralPath (Join-Path $directory 'handbrake\source\HandBrake-1.11.2-source.tar.bz2'))){throw 'HandBrake source archive is missing.'}
Write-Output "Verified $name MSIX identity, runtime payload, shared-database access configuration, and exclusion of private data. No app was installed or launched."
