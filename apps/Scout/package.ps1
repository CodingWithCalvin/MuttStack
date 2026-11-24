#requires -Version 7.0
param([string]$DotnetPath='dotnet',[string]$Version,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'eng/package.ps1') @PSBoundParameters
