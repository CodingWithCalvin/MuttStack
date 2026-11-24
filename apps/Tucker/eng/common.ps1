#requires -Version 7.0
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$script:RepoRoot=Split-Path $PSScriptRoot -Parent
$script:PackageConfig=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package.json') -Raw | ConvertFrom-Json
function Invoke-Dotnet([string[]]$Arguments) {
    & $DotnetPath @Arguments
    if($LASTEXITCODE -ne 0){throw ('dotnet failed: '+($Arguments -join ' '))}
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

function Invoke-InstallerProcess([string]$File,[string[]]$Arguments,[int]$TimeoutSeconds=180) {
    $process=Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if(!$process.WaitForExit($TimeoutSeconds*1000)){$process.Kill($true);throw "Installer timed out: $File"}
        return $process.ExitCode
    }finally{$process.Dispose()}
}
function Invoke-InnoCompiler([string]$Compiler,[string]$Source,[string]$Version,[string]$Output,[string]$ValidationRoot='',[string]$ValidationId='') {
    $arguments=@('--quiet','--no-ide-signtools','--no-signing',('--define=AppSource='+$Source),('--define=AppVersion='+$Version),('--define=OutputRoot='+$Output))
    if($ValidationRoot){$arguments+=@(('--define=ValidationRoot='+$ValidationRoot),('--define=ValidationId='+$ValidationId))}
    $arguments+=Join-Path $RepoRoot 'installer/app.iss'
    & $Compiler @arguments
    if($LASTEXITCODE -ne 0){throw 'Inno Setup compilation failed.'}
}
