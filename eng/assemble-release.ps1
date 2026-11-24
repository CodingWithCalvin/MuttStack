#requires -Version 7.0
param([Parameter(Mandatory)][string]$InputDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$version=& (Join-Path $PSScriptRoot 'version.ps1') -Tag $Tag
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new release output directory.'}
$verified=@()
foreach($app in @('Scout','Ripley','Tucker')){
    $folder=Join-Path $InputDirectory "MuttStack-$app-windows"
    $name="$app-$($version.package)-win-x64-setup.exe"
    $entries=@(Get-ChildItem -LiteralPath $folder)
    if($entries.Count -ne 2 -or @($entries | Where-Object { $_.PSIsContainer -or $_.Name -notin @($name,'SHA256SUMS.txt') }).Count){throw "Incomplete or unexpected $app release payload."}
    $lines=@(Get-Content -LiteralPath (Join-Path $folder 'SHA256SUMS.txt'))
    if($lines.Count -ne 1 -or $lines[0] -cnotmatch ('^([a-f0-9]{64})  '+[regex]::Escape($name)+'$')){throw "Invalid $app checksum manifest."}
    $expected=$Matches[1];$file=Join-Path $folder $name
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw "$app release checksum mismatch."}
    $verified+=@([pscustomobject]@{source=$file;name=$name;checksum=$expected})
}
# Nothing is emitted until every app and version has been verified.
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
foreach($asset in $verified){Copy-Item -LiteralPath $asset.source -Destination (Join-Path $OutputDirectory $asset.name)}
$verified | Sort-Object name | ForEach-Object { $_.checksum+'  '+$_.name } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Verified all three Mutt Stack $($version.display) installers."
