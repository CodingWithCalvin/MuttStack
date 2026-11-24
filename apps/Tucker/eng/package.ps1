#requires -Version 7.0
# Compatibility entry point; all build logic is owned by Mutt Stack.
& (Join-Path $PSScriptRoot '../../../eng/package.ps1') -Application 'Tucker' @args
