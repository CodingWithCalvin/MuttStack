#requires -Version 7.0
$ErrorActionPreference='Stop'
$version=& (Join-Path $PSScriptRoot 'version.ps1')
$root=Join-Path $PSScriptRoot ('../artifacts/validation/release-'+[guid]::NewGuid().ToString('N'))
$inputRoot=Join-Path $root 'input'
foreach($app in @('Scout','Ripley','Tucker')){
    $folder=Join-Path $inputRoot "MuttStack-$app-windows"
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $name="$app-$($version.package)-win-x64-setup.exe"
    $file=Join-Path $folder $name
    Set-Content -LiteralPath $file -Value "Synthetic $app package; never executed."
    ((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()+'  '+$name) | Set-Content -LiteralPath (Join-Path $folder 'SHA256SUMS.txt')
}
& (Join-Path $PSScriptRoot 'assemble-release.ps1') -InputDirectory $inputRoot -OutputDirectory (Join-Path $root 'complete') -Tag $version.tag
if(@(Get-ChildItem -LiteralPath (Join-Path $root 'complete')).Count -ne 4){throw 'Release must contain three installers and one checksum manifest.'}
function Expect-Rejection([scriptblock]$Action){
    $rejected=$false
    try { & $Action } catch { $rejected=$true }
    if(!$rejected){throw 'Invalid release payload was accepted.'}
}
Expect-Rejection { & (Join-Path $PSScriptRoot 'version.ps1') -Tag 'v65535.0.0' }
$file=Join-Path $inputRoot "MuttStack-Tucker-windows/Tucker-$($version.package)-win-x64-setup.exe"
Add-Content -LiteralPath $file -Value 'Changed after checksumming.'
$output=Join-Path $root 'corrupt'
Expect-Rejection { & (Join-Path $PSScriptRoot 'assemble-release.ps1') -InputDirectory $inputRoot -OutputDirectory $output -Tag $version.tag }
if(Test-Path -LiteralPath $output){throw 'An invalid bundle must not emit a partial release.'}
Remove-Item -LiteralPath $file
$output=Join-Path $root 'missing'
Expect-Rejection { & (Join-Path $PSScriptRoot 'assemble-release.ps1') -InputDirectory $inputRoot -OutputDirectory $output -Tag $version.tag }
if(Test-Path -LiteralPath $output){throw 'A missing app must not emit a partial release.'}
Write-Output 'Release assembly checks passed: complete bundle, tag mismatch, changed checksum and missing app.'
