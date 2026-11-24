#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Scout','Ripley','Tucker')][string]$Application)
. (Join-Path $PSScriptRoot 'common.ps1')
$version='7.1.1'
$directory=Join-Path $StackRoot ('artifacts/tools/innosetup-'+$version)
$compiler=Join-Path $directory 'ISCC.exe'
if(!(Test-Path -LiteralPath $compiler)) {
    $download=Get-VerifiedDownload 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0' (Join-Path $StackRoot 'artifacts/downloads/innosetup-7.1.1-x64.exe')
    $log=Join-Path $StackRoot 'artifacts/tools/inno-provision.log'
    New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
    # Upstream portable mode skips uninstall registration, associations and shortcuts.
    $code=Invoke-InstallerProcess $download @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/PORTABLE=1','/NOICONS','/TASKS=""',('/DIR="'+$directory+'"'),('/LOG="'+$log+'"'))
    if($code -ne 0){throw "Inno Setup provisioning failed ($code). See $log"}
}
if(!(Test-Path -LiteralPath $compiler) -or ((& $compiler --version) -join '').Trim() -ne '7.1.1'){throw 'The pinned Inno Setup compiler is missing or has the wrong version.'}
return $compiler
