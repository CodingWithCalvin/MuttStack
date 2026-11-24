#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Scout','Ripley','Tucker')][string]$Application,[string]$DotnetPath='dotnet',[switch]$Production)
. (Join-Path $PSScriptRoot 'common.ps1')
if($Production -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')){throw 'Production installer registration is permitted only on a clean GitHub-hosted runner. Omit -Production for an isolated local rehearsal.'}
$result=Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'artifacts/packaging') -Filter result.json -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if(!$result){throw 'Package the app before testing its installer.'}
$package=Get-Content -LiteralPath $result.FullName -Raw | ConvertFrom-Json
$name=$PackageConfig.name;$version=$package.version
$root=Join-Path $RepoRoot ('artifacts/validation/installer-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$identity=if($Production){$PackageConfig.appId}else{'MuttStack.InstallerTest.'+[guid]::NewGuid().ToString('N')}
$install=if($Production){Join-Path $env:LOCALAPPDATA "Programs/Coding With Calvin/$name"}else{Join-Path $root 'app with spaces'}
$menu=if($Production){Join-Path ([Environment]::GetFolderPath('Programs')) $name}else{Join-Path $root 'shortcuts'}
$registry="HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/${identity}_is1"
if((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $menu) -or (Test-Path -LiteralPath $registry)){throw 'Refusing to modify an existing app installation, shortcut folder or registration.'}
$compiler=& (Join-Path $PSScriptRoot 'get-inno.ps1') -Application $Application
$validationRoot=if($Production){''}else{$root}
$baseSetup=$package.installer
if(!$Production) {
    $baseOutput=Join-Path $root 'base';New-Item -ItemType Directory -Path $baseOutput | Out-Null
    Invoke-InnoCompiler $compiler $package.publishedApp $version $baseOutput $validationRoot $identity
    $baseSetup=Join-Path $baseOutput "$name-$version-win-x64-setup.exe"
}
$parts=@($version.Split('.') | ForEach-Object { [int]$_ });$incremented=$false
for($index=3;$index -ge 0;$index--){if($parts[$index] -lt 65535){$parts[$index]++;$incremented=$true;break};$parts[$index]=0}
if(!$incremented){throw 'No higher test version is available.'};$nextVersion=$parts -join '.'
$upgradeSource=Join-Path $root 'upgrade-payload';New-Item -ItemType Directory -Path $upgradeSource | Out-Null
Get-ChildItem -LiteralPath $package.publishedApp | Copy-Item -Destination $upgradeSource -Recurse
'New-version payload fixture.' | Set-Content -LiteralPath (Join-Path $upgradeSource 'installer-upgrade-fixture.txt')
$upgradeOutput=Join-Path $root 'upgrade';New-Item -ItemType Directory -Path $upgradeOutput | Out-Null
Invoke-InnoCompiler $compiler $upgradeSource $nextVersion $upgradeOutput $validationRoot $identity
$upgradeSetup=Join-Path $upgradeOutput "$name-$nextVersion-win-x64-setup.exe"
$state=if($Production){$env:LOCALAPPDATA}else{Join-Path $root 'state'}
$sentinels=@((Join-Path $state 'Doggo/doggo.db'),(Join-Path $state "$name/settings.json"),(Join-Path $root 'media/keep.mkv'))
foreach($path in $sentinels){if(Test-Path -LiteralPath $path){throw 'Refusing to overwrite existing app data.'};New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null;[IO.File]::WriteAllText($path,'Synthetic preservation fixture: '+[guid]::NewGuid())}
$hashes=@{};foreach($path in $sentinels){$hashes[$path]=(Get-FileHash -LiteralPath $path).Hash}
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};Write-Output ('PASS '+$Message)}
function Preserved { foreach($path in $sentinels){Check ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path).Hash -eq $hashes[$path]) ('Preserved fixture '+[IO.Path]::GetFileName($path))} }
function Run-Setup([string]$File,[string]$Label) {
    return Invoke-InstallerProcess $File @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="'+(Join-Path $root ($Label+'.log'))+'"'))
}
$uninstaller=Join-Path $install 'unins000.exe';$installed=$false
try {
    Check ((Run-Setup $baseSetup 'install') -eq 0) 'Fresh installer succeeds without elevation or launching the app'
    $installed=$true
    Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $version) 'Per-user uninstall registration has the package version'
    $shell=New-Object -ComObject WScript.Shell
    try {$shortcut=$shell.CreateShortcut((Join-Path $menu "$name.lnk"));Check ($shortcut.TargetPath -eq (Join-Path $install "$name.exe")) 'Start menu shortcut targets the installed app'}finally{[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)}
    foreach($file in Get-ChildItem -LiteralPath $package.publishedApp -File -Recurse) {
        $target=Join-Path $install ([IO.Path]::GetRelativePath($package.publishedApp,$file.FullName))
        if(!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw 'Installed payload differs from the published app.'}
    }
    Check $true 'Installed files match the published payload exactly'
    Preserved
    & (Join-Path $PSScriptRoot 'test-published.ps1') -Application $Application -DotnetPath $DotnetPath -AppDirectory $install
    $unknown=Join-Path $install 'user-kept-file.txt';[IO.File]::WriteAllText($unknown,'User-owned file, not an installer payload.')
    $lock=[IO.File]::Open((Join-Path $install "$name.dll"),'Open','Read','Read')
    try {
        Check ((Run-Setup $upgradeSetup 'locked-upgrade') -ne 0) 'Upgrade refuses locked files instead of stopping processing'
        Check ((Run-Setup $uninstaller 'locked-uninstall') -ne 0) 'Uninstall refuses locked files instead of stopping processing'
        Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $version) 'Refused operations retain the previous installed version'
    }finally{$lock.Dispose()}
    Check ((Run-Setup $upgradeSetup 'upgrade') -eq 0) 'Newer version upgrades the existing installation'
    Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $nextVersion) 'Upgrade keeps one registration with the newer version'
    Check (Test-Path -LiteralPath (Join-Path $install 'installer-upgrade-fixture.txt')) 'Upgrade installs the new version payload'
    Check ((Run-Setup $baseSetup 'downgrade') -ne 0) 'Older installers cannot silently downgrade an existing installation'
    Check ((Run-Setup $upgradeSetup 'repair') -eq 0) 'Reinstalling the current version succeeds'
    Preserved
    Check ((Run-Setup $uninstaller 'uninstall') -eq 0) 'Uninstall completes without restarting Windows'
    $installed=$false
    Check (!(Test-Path -LiteralPath $registry) -and !(Test-Path -LiteralPath (Join-Path $menu "$name.lnk"))) 'Uninstall removes only its registration and shortcut'
    Check (!(Test-Path -LiteralPath (Join-Path $install "$name.exe")) -and (Test-Path -LiteralPath $unknown)) 'Uninstall removes managed app files and preserves unknown user files'
    Preserved
    [ordered]@{app=$name;passed=$true;productionInstaller=[bool]$Production;root=$root;identity=$identity;appLaunched=$false;installedUiTested=$true;statePreserved=$true;uninstalled=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'installer-report.json') -Encoding utf8
}finally {
    if($installed -and (Test-Path -LiteralPath $uninstaller)){[void](Run-Setup $uninstaller 'cleanup')}
}
