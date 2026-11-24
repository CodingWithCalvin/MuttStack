#requires -Version 7.0
# Compatibility entry point; all build logic is owned by Mutt Stack.
& (Join-Path $PSScriptRoot '../../../eng/verify-package.ps1') -Application 'Scout' @args
