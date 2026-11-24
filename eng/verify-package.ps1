#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Scout','Ripley','Tucker')][string]$Application,[Parameter(Mandatory)][string]$PublishedApp,[Parameter(Mandatory)][string]$Installer)
. (Join-Path $PSScriptRoot 'common.ps1')
$name=$PackageConfig.name
foreach($file in @("$name.exe","$name.dll",'hostfxr.dll','hostpolicy.dll','LICENSE.txt','build-info.json')){if(!(Test-Path -LiteralPath (Join-Path $PublishedApp $file))){throw "Missing self-contained payload file: $file"}}
$assembly=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $PublishedApp "$name.dll"))
$info=Get-Content -LiteralPath (Join-Path $PublishedApp 'build-info.json') -Raw | ConvertFrom-Json
if($assembly.Version.ToString() -ne $StackVersion.package -or $info.version -ne $StackVersion.package -or $info.suite -ne 'Mutt Stack' -or $info.app -ne $name){throw 'App assembly, package and Mutt Stack version must agree.'}
$payload=@(Get-ChildItem -LiteralPath $PublishedApp -File -Recurse)
$private=@($payload | Where-Object { $_.Name -match '\.db(-wal|-shm)?$|^credentials|^storage\.json$' -or $_.Extension -in @('.pfx','.p12','.key','.gguf','.mkv','.mp4') })
if($private.Count){throw 'Package unexpectedly contains private state, signing keys, models or media.'}
if($PackageConfig.handbrake -and !(Test-Path -LiteralPath (Join-Path $PublishedApp 'handbrake/source/HandBrake-1.11.2-source.tar.bz2'))){throw 'HandBrake source archive is missing.'}
if(!(Test-Path -LiteralPath $Installer) -or (Get-Item -LiteralPath $Installer).VersionInfo.FileDescription.Trim() -ne "Mutt Stack $name Setup"){throw 'Installer identity is incorrect.'}
if((Get-AuthenticodeSignature -LiteralPath $Installer).Status -ne 'NotSigned'){throw 'This workflow expects an unsigned installer; review signing before changing the release contract.'}
Write-Output "Verified $name installer identity and unsigned status, self-contained payload, and exclusion of private state."
