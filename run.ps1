$ErrorActionPreference = 'Stop'
$taskExecutable = Join-Path $PSScriptRoot 'artifacts\AudioRouter-win-x64\AudioRouter.exe'
if (-not (Test-Path -LiteralPath $taskExecutable)) { & (Join-Path $PSScriptRoot 'build.ps1') -Publish }
# This is the interactive application the user is launching.
Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path $taskExecutable) -WindowStyle Normal
