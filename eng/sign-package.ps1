#requires -Version 7.0
param([Parameter(Mandatory)][string]$Package,[switch]$Development,[string]$TimestampUrl='http://timestamp.digicert.com')
. (Join-Path $PSScriptRoot 'common.ps1')
$Package=[IO.Path]::GetFullPath($Package)
if(!(Test-Path -LiteralPath $Package)){throw 'Package does not exist.'}
if(![IO.Path]::GetFileName($Package).EndsWith('-unsigned.msix',[StringComparison]::OrdinalIgnoreCase)){throw 'Use a fresh unsigned MSIX package.'}
$work=Join-Path $RepoRoot ('artifacts\validation\sign-'+[guid]::NewGuid().ToString('N'))
& (Get-WindowsSdkTool 'makeappx.exe') unpack /p $Package /d $work /o
if($LASTEXITCODE -ne 0){throw 'Cannot read package identity.'}
[xml]$manifest=Get-Content -LiteralPath (Join-Path $work 'AppxManifest.xml') -Raw
$publisher=$manifest.Package.Identity.Publisher
$certificate=$null;$temporaryPfx=$null;$removeCertificate=$false
try {
    if($Development){
        $certificate=New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddMonths(3)
        $removeCertificate=$true
    }else{
        if(!$env:MSIX_CERTIFICATE_BASE64){throw 'Configure MSIX_CERTIFICATE_BASE64 and MSIX_CERTIFICATE_PASSWORD in the GitHub release environment before creating a version tag.'}
        $temporaryPfx=Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N')+'.pfx')
        [IO.File]::WriteAllBytes($temporaryPfx,[Convert]::FromBase64String($env:MSIX_CERTIFICATE_BASE64))
        $before=@(Get-ChildItem Cert:\CurrentUser\My | Select-Object -ExpandProperty Thumbprint)
        $password=if($env:MSIX_CERTIFICATE_PASSWORD){ConvertTo-SecureString -String $env:MSIX_CERTIFICATE_PASSWORD -AsPlainText -Force}else{[Security.SecureString]::new()}
        $certificate=Import-PfxCertificate -FilePath $temporaryPfx -CertStoreLocation 'Cert:\CurrentUser\My' -Password $password | Where-Object HasPrivateKey | Select-Object -First 1
        if(!$certificate){throw 'The signing PFX has no usable private key.'}
        $removeCertificate=$certificate.Thumbprint -notin $before
    }
    if($certificate.Subject -ne $publisher){throw 'Signing certificate subject does not exactly match the MSIX publisher. Set the repository MSIX_PUBLISHER variable before building.'}
    $tool=Get-WindowsSdkTool 'signtool.exe'
    $arguments=@('sign','/fd','SHA256','/sha1',$certificate.Thumbprint,'/s','My')
    if(!$Development){$arguments+=@('/tr',$TimestampUrl,'/td','SHA256')}
    $arguments+=$Package
    & $tool @arguments
    if($LASTEXITCODE -ne 0){throw 'MSIX signing failed.'}
    $signature=Get-AuthenticodeSignature -LiteralPath $Package
    if(!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $signature.Status -eq 'HashMismatch'){throw 'Signed package failed signature inspection.'}
    $suffix=if($Development){'-test-signed.msix'}else{'.msix'}
    $signed=[IO.Path]::Combine((Split-Path $Package),([IO.Path]::GetFileName($Package).Replace('-unsigned.msix',$suffix)))
    if($signed -eq $Package -or (Test-Path -LiteralPath $signed)){throw 'Use an unsigned package name and a fresh output destination.'}
    Move-Item -LiteralPath $Package -Destination $signed
    if($Development){Export-Certificate -Cert $certificate -FilePath ([IO.Path]::ChangeExtension($signed,'.cer')) | Out-Null;Write-Output 'Test certificate was NOT trusted or installed as a trust root. This MSIX needs explicit certificate trust before installation.'}
    else{& $tool verify /pa $signed;if($LASTEXITCODE -ne 0){throw 'Production MSIX did not validate to a trusted signing chain.'}}
    Write-PackageChecksums (Split-Path $signed)
    Write-Output $signed
}finally{
    if($temporaryPfx -and (Test-Path -LiteralPath $temporaryPfx)){Remove-Item -LiteralPath $temporaryPfx}
    if($removeCertificate -and $certificate){Remove-Item -LiteralPath ('Cert:\CurrentUser\My\'+$certificate.Thumbprint) -DeleteKey}
}
