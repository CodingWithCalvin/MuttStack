#requires -Version 7.0
param([string]$Tag='')
$ErrorActionPreference='Stop'
[xml]$props=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../build/MuttStack.props') -Raw
$version=[string]$props.Project.PropertyGroup.MuttStackVersion
if($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or @($version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count){throw 'Invalid root Mutt Stack version.'}
if($Tag -and $Tag -cne "v$version"){throw "Release tag must match the root version: v$version"}
[pscustomobject]@{display=$version;package="$version.0";tag="v$version"}
