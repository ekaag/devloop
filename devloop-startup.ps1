$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$devloopSdk = if (Test-Path .\.dotnet\dotnet.exe) { (Resolve-Path .\.dotnet\dotnet.exe).Path } else { 'dotnet' }
$env:DEVLOOP_Workspace = $PSScriptRoot
$env:DEVLOOP_DotnetExecutable = $devloopSdk
# Restore is a separate, explicit network-capable operator action: dotnet restore devloop.sln.
& $devloopSdk run --project devloop.Orchestrator --no-restore -- @args
exit $LASTEXITCODE
