[CmdletBinding()]
param(
    [string]$GameDir = '',
    [string]$LoaderDir = '',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
. (Join-Path $PSScriptRoot 'common.ps1')
$GameDir = Assert-Game $GameDir
if (!$LoaderDir) { $LoaderDir = Join-Path $script:Workspace '.tools\bepinex' }
$dotnet = Get-Dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
if (!(Test-Path -LiteralPath (Join-Path $script:Workspace '.tools\json\FamilyTimeVoice.Json.dll'))) {
    & (Join-Path $PSScriptRoot 'prepare-json-dependency.ps1') -Dotnet $dotnet
}
if (!(Test-Path -LiteralPath (Join-Path $script:Workspace '.tools\unity-full-runtime-6000.2.12\UnityEngine.AudioModule.dll'))) {
    & (Join-Path $PSScriptRoot 'prepare-engine-runtime.ps1') -Dotnet $dotnet
}
& $dotnet build (Join-Path $script:Workspace 'FamilyTimeVoice.csproj') --configuration $Configuration "-p:GameDir=$GameDir" "-p:LoaderDir=$LoaderDir"
if ($LASTEXITCODE) { throw "Build failed with exit code $LASTEXITCODE" }
$patcherProject = Join-Path $script:Workspace 'preloader\FamilyTimeVoice.EngineRestoration.csproj'
if (Test-Path -LiteralPath $patcherProject) {
    & $dotnet build $patcherProject --configuration $Configuration "-p:GameDir=$GameDir" "-p:LoaderDir=$LoaderDir"
    if ($LASTEXITCODE) { throw "Preloader build failed with exit code $LASTEXITCODE" }
}
Write-Host (Join-Path $script:Workspace "bin\$Configuration\netstandard2.1\FamilyTimeVoice.dll")
