$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 & dotnet build CodingWithCalvin.Tucker.slnx -c Release -m:1
 if($LASTEXITCODE -ne 0){throw 'Fake CLI build failed'}
 $fake=Join-Path $root 'artifacts\bin\FakeCli\release\FakeCli.exe'
 & dotnet run --project tests\Tucker.Tests\Tucker.Tests.csproj -c Release --no-build -- $fake
 if($LASTEXITCODE -ne 0){throw 'Core tests failed'}
 $testRoot=Join-Path $root ('artifacts\desktop-tests\'+[guid]::NewGuid().ToString('N'))
 & dotnet run --project tests\Tucker.Desktop.Tests\Tucker.Desktop.Tests.csproj -c Release --no-build -- $fake $testRoot
 if($LASTEXITCODE -ne 0){throw 'Desktop tests failed'}
}finally{Pop-Location}
