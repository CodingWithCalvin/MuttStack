#requires -Version 7.0
param([string]$DotnetPath='dotnet',[string]$Version,[string]$OutputDirectory)
. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'check-independence.ps1')
if(!$Version){$Version=$PackageConfig.version}
if($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count){throw 'Package version must have four numeric components from 0 through 65535.'}
$name=$PackageConfig.name
if(!$OutputDirectory){$OutputDirectory=Join-Path $RepoRoot ('artifacts\packages\'+$Version)}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw "Output already exists; choose a new package output directory: $OutputDirectory"}
$work=Join-Path $RepoRoot ('artifacts\packaging\'+[guid]::NewGuid().ToString('N'))
$app=Join-Path $work 'app'
New-Item -ItemType Directory -Path $app,$OutputDirectory -Force | Out-Null
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
    [ordered]@{app=$name;version=$Version;commit=$commit;workingTreeDirty=$dirty;packageFormat='inno-setup';databaseMigrationPerformed=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app 'build-info.json') -Encoding utf8
    $compiler=& (Join-Path $PSScriptRoot 'get-inno.ps1')
    Invoke-InnoCompiler $compiler $app $Version $OutputDirectory
    $installer=Join-Path $OutputDirectory "$name-$Version-win-x64-setup.exe"
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -PublishedApp $app -Installer $installer
    Write-PackageChecksums $OutputDirectory
    $downloads=@(Get-ChildItem -LiteralPath $OutputDirectory -File)
    if($downloads.Count -ne 2 -or @($downloads | Where-Object { $_.Name -notin @([IO.Path]::GetFileName($installer),'SHA256SUMS.txt') }).Count){throw 'Distribution must contain only the installer and its checksum file.'}
    [ordered]@{app=$name;version=$Version;directory=$OutputDirectory;publishedApp=$app;installer=$installer;appId=$PackageConfig.appId;signing='unsigned';installed=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding utf8
    Write-Output "Packages: $OutputDirectory"
}finally{Pop-Location}
