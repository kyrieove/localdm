param(
    [string]$BaseUrl = 'http://127.0.0.1:5099/',
    [string]$PlaywrightModule = $env:LOCALDM_PLAYWRIGHT_MODULE,
    [string]$OutputDirectory = 'artifacts/tablet/codex-reader-acceptance'
)
$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'verify-reader.mjs'), '--url', $BaseUrl, '--output', $OutputDirectory)
if ($PlaywrightModule) { $arguments += @('--playwright-module', $PlaywrightModule) }
& node @arguments
if ($LASTEXITCODE -ne 0) { throw 'Reader acceptance failed. See results.json in the output directory.' }
