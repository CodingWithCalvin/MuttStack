#requires -Version 7.0
param([ValidateSet('Scout','Ripley','Tucker')][string]$Application)
$ErrorActionPreference='Stop'
$stack=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sharedProps=[IO.Path]::GetFullPath((Join-Path $stack 'build/MuttStack.props'))
[xml]$shared=Get-Content -LiteralPath $sharedProps -Raw
if($shared.SelectNodes('/Project/*[not(self::PropertyGroup)]').Count){throw 'Shared build properties must not import application code or targets.'}
$apps=if($Application){@($Application)}else{@('Scout','Ripley','Tucker')}
foreach($name in $apps){
    $root=[IO.Path]::GetFullPath((Join-Path $stack ('apps/'+$name)))
    $paths=& git -C $root ls-files --cached --others --exclude-standard -- .
    if($LASTEXITCODE -ne 0){throw 'Run this check in a repository checkout.'}
    $count=0
    foreach($relative in $paths | Sort-Object -Unique){
        if($relative -notmatch '\.(csproj|props|targets)$'){continue}
        $path=Join-Path $root $relative;if(!(Test-Path -LiteralPath $path)){continue}
        [xml]$document=Get-Content -LiteralPath $path -Raw
        foreach($node in $document.SelectNodes('//ProjectReference[@Include]|//Compile[@Include]|//Content[@Include]|//Import[@Project]|//HintPath')){
            $value=if($node.Name -eq 'HintPath'){$node.InnerText}elseif($node.Name -eq 'Import'){$node.GetAttribute('Project')}else{$node.GetAttribute('Include')}
            if($value.Contains('$(')){throw "Unresolved build input in ${relative}: $value"}
            foreach($part in $value.Split(';')){
                $resolved=[IO.Path]::GetFullPath((Join-Path (Split-Path $path) $part))
                if($node.Name -eq 'Import' -and $resolved -eq $sharedProps -and $path -eq (Join-Path $root 'Directory.Build.props')){continue}
                if(!$resolved.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Cross-app build input in ${name}/${relative}: $value"}
                $count++
            }
        }
    }
    Write-Output "Verified $count app-local build inputs for $name."
}
