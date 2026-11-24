#requires -Version 7.0
param([string]$DotnetPath='dotnet',[switch]$NoBuild)
. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'check-independence.ps1')
Push-Location $RepoRoot
try {
    if(!$NoBuild){Invoke-Dotnet @('build',$PackageConfig.solution,'-c','Release','-m:1')}
    $fake=$null
    if($PackageConfig.fake){$target=& $DotnetPath msbuild $PackageConfig.fake '-getProperty:TargetPath' '-p:Configuration=Release' '-nologo';if($LASTEXITCODE -ne 0){throw 'Cannot find fake test CLI.'};$fake=[IO.Path]::ChangeExtension(($target -join '').Trim(),'.exe')}
    foreach($project in $PackageConfig.tests) {
        $arguments=@('run','--project',$project,'-c','Release','--no-build','--')
        if($fake -and $project -notlike '*Handoff.Tests*'){$arguments+= $fake}
        if($PackageConfig.name -eq 'Tucker' -and $project -like '*Desktop.Tests*'){
            $state=Join-Path $RepoRoot ('artifacts\validation\desktop-'+[guid]::NewGuid().ToString('N'));$renders=Join-Path $state 'renders';New-Item -ItemType Directory -Path $renders -Force | Out-Null;$arguments+=@($state,$renders)
        }
        Invoke-Dotnet $arguments
    }
    foreach($tool in $PackageConfig.tools){Invoke-Dotnet @('build',$tool,'-c','Release','-m:1')}
    if($PackageConfig.name -eq 'Scout') {
        $state=Join-Path $RepoRoot ('.build\handoff-rehearsal-'+[guid]::NewGuid().ToString('N'))
        foreach($phase in @('prepare','send','accept','append','accept','verify')){Invoke-Dotnet @('run','--project','tools/Scout.HandoffRehearsal','-c','Release','--no-build','--',$phase,$state)}
    }
}finally{Pop-Location}
