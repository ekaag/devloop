<#
.SYNOPSIS
  Creates a dedicated Git workspace from examples/sample-target for a DevLoop Codex run.
.EXAMPLE
  .\examples\New-SampleWorkspace.ps1 -Destination C:\code\devloop-sample
#>
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'

# Native tools report progress on stderr; judge them by exit code only.
function Invoke-Native([string]$Description, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE) { throw "$Description failed with exit code $LASTEXITCODE." }
}

if (Test-Path $Destination) { throw "Destination already exists: $Destination" }
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = [IO.Path]::GetFullPath($Destination)
if ($target.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase)) { throw 'Create the workspace outside the DevLoop repository.' }

Copy-Item (Join-Path $PSScriptRoot 'sample-target') $target -Recurse
Push-Location $target
try {
    Invoke-Native 'git init' { git init -q }
    Invoke-Native 'git add' { git add . }
    Invoke-Native 'git commit (configure git user.name and user.email first)' { git commit -q -m 'Baseline sample workspace' }
    # Restore needs the network, so it happens here, once, before the sandboxed loop runs.
    Invoke-Native 'dotnet restore' { dotnet restore SampleTarget.slnx }
    Invoke-Native 'dotnet build' { dotnet build SampleTarget.slnx --no-restore }
}
finally { Pop-Location }
Write-Host "Workspace ready: $target"
