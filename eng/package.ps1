#requires -Version 7.0
param([string]$DotnetPath='dotnet',[string]$Version,[string]$Publisher='CN=Coding With Calvin',[string]$OutputDirectory)
. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'check-independence.ps1')
if(!$Version){$Version=$PackageConfig.version}
if($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count){throw 'MSIX version must have four numeric components from 0 through 65535.'}
if([string]::IsNullOrWhiteSpace($Publisher)){throw 'A stable MSIX publisher is required.'}
$name=$PackageConfig.name
if(!$OutputDirectory){$OutputDirectory=Join-Path $RepoRoot ('artifacts\packages\'+$Version)}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw "Output already exists; choose a new package output directory: $OutputDirectory"}
$work=Join-Path $RepoRoot ('artifacts\packaging\'+[guid]::NewGuid().ToString('N'))
$app=Join-Path $work 'app';$stage=Join-Path $work 'msix'
New-Item -ItemType Directory -Path $app,$stage,$OutputDirectory -Force | Out-Null
Push-Location $RepoRoot
try {
    Invoke-Dotnet @('publish',$PackageConfig.project,'-c','Release','-r','win-x64','--self-contained','true','-m:1',('-p:Version='+$Version),'-p:DebugType=None','-p:DebugSymbols=false','-o',$app)
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'LICENSE') -Destination (Join-Path $app 'LICENSE.txt')
    $sdkRoot=Split-Path (Get-Command $DotnetPath).Source
    foreach($notice in @('LICENSE.txt','ThirdPartyNotices.txt')){if(Test-Path -LiteralPath (Join-Path $sdkRoot $notice)){Copy-Item -LiteralPath (Join-Path $sdkRoot $notice) -Destination (Join-Path $app ('DOTNET-'+$notice))}}
    if($PackageConfig.handbrake) {
        $cache=Join-Path $RepoRoot 'artifacts\downloads';$hbVersion='1.11.2'
        $archive=Get-VerifiedDownload 'https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrakeCLI-1.11.2-win-x86_64.zip' '80bfe8d5f5d11cc3ef76b834add3ed4e82dee6523ffeb435c283f88b1a21f09d' (Join-Path $cache 'HandBrakeCLI-1.11.2-win-x86_64.zip')
        $source=Get-VerifiedDownload 'https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrake-1.11.2-source.tar.bz2' '12b046350f2422dc28783ff94229aff4ba5fe5e683431e057355d36163b2593a' (Join-Path $cache 'HandBrake-1.11.2-source.tar.bz2')
        $hb=Join-Path $app 'handbrake';Expand-Archive -LiteralPath $archive -DestinationPath $hb
        if(!(Test-Path -LiteralPath (Join-Path $hb 'HandBrakeCLI.exe'))){throw 'Unexpected HandBrake archive layout.'}
        New-Item -ItemType Directory -Path (Join-Path $hb 'source') | Out-Null;Copy-Item -LiteralPath $source -Destination (Join-Path $hb 'source')
        'HandBrake '+$hbVersion+' is distributed with its upstream license notices and corresponding source archive. https://handbrake.fr/' | Set-Content -LiteralPath (Join-Path $hb 'SOURCE-NOTICE.txt')
    }
    $commit=(& git rev-parse HEAD).Trim();$dirty=@(& git status --porcelain).Count -gt 0
    [ordered]@{app=$name;version=$Version;commit=$commit;workingTreeDirty=$dirty;publisher=$Publisher;databaseMigrationPerformed=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app 'build-info.json') -Encoding utf8
    [IO.Compression.ZipFile]::CreateFromDirectory($app,(Join-Path $OutputDirectory "$name-$Version-win-x64.zip"),[IO.Compression.CompressionLevel]::Optimal,$false)
    Get-ChildItem -LiteralPath $app | Copy-Item -Destination $stage -Recurse
    $assets=Join-Path $stage 'Assets';New-Item -ItemType Directory -Path $assets -Force | Out-Null
    Add-Type -AssemblyName System.Drawing
    $original=[Drawing.Image]::FromFile((Join-Path $RepoRoot $PackageConfig.logo))
    try{foreach($size in @(44,50,150)){$bitmap=[Drawing.Bitmap]::new($size,$size);$graphics=[Drawing.Graphics]::FromImage($bitmap);try{$graphics.Clear([Drawing.Color]::Transparent);$graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$scale=[Math]::Min($size/$original.Width,$size/$original.Height);$width=[int]($original.Width*$scale);$height=[int]($original.Height*$scale);$graphics.DrawImage($original,[int](($size-$width)/2),[int](($size-$height)/2),$width,$height);$bitmap.Save((Join-Path $assets "Logo$size.png"),[Drawing.Imaging.ImageFormat]::Png)}finally{$graphics.Dispose();$bitmap.Dispose()}}}finally{$original.Dispose()}
    $publisherXml=[Security.SecurityElement]::Escape($Publisher)
    $manifest=@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6" IgnorableNamespaces="uap rescap desktop6">
  <Identity Name="CodingWithCalvin.$name" Publisher="$publisherXml" Version="$Version" ProcessorArchitecture="x64" />
  <Properties><DisplayName>$name</DisplayName><PublisherDisplayName>Coding With Calvin</PublisherDisplayName><Description>$name - Doggo app collection</Description><Logo>Assets\Logo50.png</Logo><desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization></Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Resources><Resource Language="en-us" /></Resources>
  <Applications><Application Id="App" Executable="$name.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="$name" Description="$name - Doggo app collection" BackgroundColor="transparent" Square44x44Logo="Assets\Logo44.png" Square150x150Logo="Assets\Logo150.png" /></Application></Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /><rescap:Capability Name="unvirtualizedResources" /></Capabilities>
</Package>
"@
    $manifest | Set-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Encoding utf8
    $msix=Join-Path $OutputDirectory "$name-$Version-win-x64-unsigned.msix"
    & (Get-WindowsSdkTool 'makeappx.exe') pack /d $stage /p $msix /o
    if($LASTEXITCODE -ne 0){throw 'MSIX validation or packing failed.'}
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -Package $msix
    Write-PackageChecksums $OutputDirectory
    [ordered]@{app=$name;version=$Version;directory=$OutputDirectory;publishedApp=$app;msix=$msix;signing='unsigned';installed=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding utf8
    Write-Output "Packages: $OutputDirectory"
    Write-Output "Published app for isolated tests: $app"
}finally{Pop-Location}
