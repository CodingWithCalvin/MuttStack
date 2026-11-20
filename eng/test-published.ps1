#requires -Version 7.0
param([string]$DotnetPath='dotnet',[string]$AppDirectory)
. (Join-Path $PSScriptRoot 'common.ps1')
$result=Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'artifacts/packaging') -Filter result.json -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if(!$result){throw 'Package the application before testing its published payload.'}
$package=Get-Content -LiteralPath $result.FullName -Raw | ConvertFrom-Json
$app=if($AppDirectory){[IO.Path]::GetFullPath($AppDirectory)}else{$package.publishedApp}
if(!(Test-Path -LiteralPath $app)){throw 'Published payload is missing.'}
Push-Location $RepoRoot
try {
    $project=$PackageConfig.tests | Where-Object { $_ -like '*Desktop.Tests*' } | Select-Object -First 1
    Invoke-Dotnet @('build',$project,'-c','Release','-m:1',('-p:Version='+$package.version))
    $target=& $DotnetPath msbuild $project '-getProperty:TargetPath' '-p:Configuration=Release' '-nologo'
    if($LASTEXITCODE -ne 0){throw 'Cannot locate desktop test harness.'}
    $testFolder=Split-Path (($target -join '').Trim());$testName=[IO.Path]::GetFileNameWithoutExtension(($target -join '').Trim())
    $folder=Join-Path $RepoRoot ('artifacts/validation/published-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    Get-ChildItem -LiteralPath $testFolder -File | Copy-Item -Destination $folder
    Get-ChildItem -LiteralPath $app | Copy-Item -Destination $folder -Recurse -Force
    # Run the isolated harness using the published app's own runtime and dependency graph.
    foreach($suffix in @('.runtimeconfig.json','.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $app ($PackageConfig.name+$suffix)) -Destination (Join-Path $folder ($testName+$suffix)) -Force
    }
    $arguments=@()
    if($PackageConfig.fake) {
        $fakeTarget=& $DotnetPath msbuild $PackageConfig.fake '-getProperty:TargetPath' '-p:Configuration=Release' '-nologo'
        if($LASTEXITCODE -ne 0){throw 'Cannot locate fake CLI.'}
        $arguments+=[IO.Path]::ChangeExtension(($fakeTarget -join '').Trim(),'.exe')
    }
    if($PackageConfig.name -eq 'Tucker') {
        $state=Join-Path $folder 'state';$renders=Join-Path $state 'renders'
        New-Item -ItemType Directory -Path $renders -Force | Out-Null;$arguments+=@($state,$renders)
    }
    & (Join-Path $folder ($testName+'.exe')) @arguments
    if($LASTEXITCODE -ne 0){throw 'Published application desktop tests failed.'}
}finally{Pop-Location}
