param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$taskLocalDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $taskLocalDotnet) { $taskDotnet = $taskLocalDotnet }
else {
    $taskCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $taskCommand) { throw 'Install the .NET 10 SDK, then run this script again. See README.md.' }
    $taskDotnet = $taskCommand.Source
}
Push-Location $PSScriptRoot
try {
    & $taskDotnet build AudioRouter.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test) {
        & $taskDotnet test tests/AudioRouter.Tests/AudioRouter.Tests.csproj -c $Configuration --no-build --no-restore --nologo --logger trx --results-directory artifacts/test-results
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    if ($Publish) {
        $taskOutput = Join-Path $PSScriptRoot 'artifacts\AudioRouter-win-x64'
        & $taskDotnet publish src/AudioRouter.App/AudioRouter.App.csproj -c $Configuration -r win-x64 --self-contained true -o $taskOutput --nologo '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false'
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $taskOutput
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'QUICKSTART.txt') -Destination $taskOutput
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $taskOutput -Recurse -Force
        $taskCacheLine = & $taskDotnet nuget locals global-packages --list
        $taskCache = $taskCacheLine.Substring($taskCacheLine.IndexOf(': ') + 2).Trim()
        # Match notices to the runtime versions actually bundled, not other versions in the cache.
        $taskTargetFramework = ([xml](Get-Content 'src/AudioRouter.App/AudioRouter.App.csproj' -Raw)).Project.PropertyGroup.TargetFramework
        $taskRuntimeConfigPath = "src/AudioRouter.App/bin/$Configuration/$taskTargetFramework/win-x64/AudioRouter.runtimeconfig.json"
        $taskFrameworks = (Get-Content -LiteralPath $taskRuntimeConfigPath -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
        $taskRuntimeNames = @{
            'Microsoft.NETCore.App' = 'microsoft.netcore.app.runtime.win-x64'
            'Microsoft.WindowsDesktop.App' = 'microsoft.windowsdesktop.app.runtime.win-x64'
        }
        foreach ($taskFramework in $taskFrameworks) {
            $taskRuntime = $taskRuntimeNames[$taskFramework.name]
            if (-not $taskRuntime) { continue }
            $taskRuntimeRoot = Join-Path $taskCache ($taskRuntime + '/' + $taskFramework.version)
            if (-not (Test-Path -LiteralPath $taskRuntimeRoot)) { throw "Runtime notices not found: $taskRuntimeRoot" }
            foreach ($taskNotice in (Get-ChildItem -LiteralPath $taskRuntimeRoot -File | Where-Object { $_.Name -match 'license|third.*notice' })) {
                Copy-Item -LiteralPath $taskNotice.FullName -Destination (Join-Path $taskOutput ('licenses\' + $taskRuntime + '-' + $taskNotice.Name)) -Force
            }
        }
        $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskOutput 'AudioRouter.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256.txt') -Value ($taskHash + '  AudioRouter.exe') -Encoding ascii
        $taskArchive = Join-Path $PSScriptRoot 'artifacts\AudioRouter-win-x64.zip'
        Compress-Archive -LiteralPath $taskOutput -DestinationPath $taskArchive -Force
        Write-Output "Portable application: $taskOutput\AudioRouter.exe"
        Write-Output "Archive: $taskArchive"
    }
} finally { Pop-Location }
