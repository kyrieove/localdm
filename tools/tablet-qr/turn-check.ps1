param(
    [string]$BaseUrl = 'http://127.0.0.1:5099/',
    [string]$PlaywrightModule = $env:LOCALDM_PLAYWRIGHT_MODULE
)
$ErrorActionPreference = 'Stop'
# Printing an index alone is not a pass; run the asserted browser acceptance.
& (Join-Path $PSScriptRoot 'verify-reader.ps1') -BaseUrl $BaseUrl -PlaywrightModule $PlaywrightModule
