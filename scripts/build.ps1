param([string]$HandBrakeCli)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 & dotnet publish src\Tucker\Tucker.csproj -c Release -o artifacts\app --no-self-contained
 if($LASTEXITCODE -ne 0){throw 'Tucker publish failed'}
 if($HandBrakeCli){
  $source=(Resolve-Path -LiteralPath $HandBrakeCli).Path
  New-Item -ItemType Directory -Force artifacts\app\handbrake | Out-Null
  Copy-Item -LiteralPath $source -Destination artifacts\app\handbrake\HandBrakeCLI.exe
  $docs=Join-Path (Split-Path $source) 'doc'
  if(Test-Path -LiteralPath $docs){Copy-Item -LiteralPath $docs -Destination artifacts\app\handbrake -Recurse -Force}
 }
 Write-Host "Built $root\artifacts\app\Tucker.exe"
}finally{Pop-Location}
