#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$paths=& git -C $root ls-files --cached --others --exclude-standard
if($LASTEXITCODE -ne 0){throw 'Run this check in a repository checkout.'}
$count=0
foreach($relative in $paths | Sort-Object -Unique) {
    if($relative -notmatch '\.(csproj|props|targets)$'){continue}
    $path=Join-Path $root $relative;if(!(Test-Path -LiteralPath $path)){continue}
    [xml]$document=Get-Content -LiteralPath $path -Raw
    foreach($node in $document.SelectNodes('//ProjectReference[@Include]|//Compile[@Include]|//Content[@Include]|//Import[@Project]|//HintPath')) {
        $value=if($node.Name -eq 'HintPath'){$node.InnerText}elseif($node.Name -eq 'Import'){$node.GetAttribute('Project')}else{$node.GetAttribute('Include')}
        if($value.Contains('$(')){throw "Unresolved build input in ${relative}: $value. Make this input explicitly repository-local."}
        foreach($part in $value.Split(';')) {
            $resolved=[IO.Path]::GetFullPath((Join-Path (Split-Path $path) $part))
            if(!$resolved.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Cross-repository build input in ${relative}: $value"}
            $count++
        }
    }
}
Write-Output "Verified $count repository-local build inputs."
