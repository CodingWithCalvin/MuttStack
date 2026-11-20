#requires -Version 7.0
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$script:RepoRoot=Split-Path $PSScriptRoot -Parent
$script:PackageConfig=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package.json') -Raw | ConvertFrom-Json
function Invoke-Dotnet([string[]]$Arguments) {
    & $DotnetPath @Arguments
    if($LASTEXITCODE -ne 0){throw ('dotnet failed: '+($Arguments -join ' '))}
}
function Get-WindowsSdkTool([string]$Name) {
    if($env:WindowsSdkBin){$candidate=Join-Path $env:WindowsSdkBin $Name;if(Test-Path -LiteralPath $candidate){return $candidate}}
    $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $versions=Get-ChildItem -LiteralPath $sdk -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending
    foreach($version in $versions){$candidate=Join-Path $version.FullName ('x64\'+$Name);if(Test-Path -LiteralPath $candidate){return $candidate}}
    throw "Windows SDK tool $Name was not found. Install the Windows SDK or set WindowsSdkBin."
}
function Get-VerifiedDownload([string]$Url,[string]$Hash,[string]$Path) {
    if(!(Test-Path -LiteralPath $Path)){New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null;Invoke-WebRequest -Uri $Url -OutFile $Path}
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash){throw "SHA256 mismatch: $Path"}
    return $Path
}
function Write-PackageChecksums([string]$Directory) {
    $lines=Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -NE 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.Name }
    $lines | Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt') -Encoding utf8
}
