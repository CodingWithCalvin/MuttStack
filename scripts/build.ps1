$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\app'
Push-Location $root
try {
    & dotnet publish 'src\Ripley\Ripley.csproj' -c Release -m:1 -o $output --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw 'Ripley build failed.' }
    Write-Output (Join-Path $output 'Ripley.exe')
} finally { Pop-Location }
