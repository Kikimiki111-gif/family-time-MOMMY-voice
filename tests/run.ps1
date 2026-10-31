param([string]$GameDir = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $taskRoot 'scripts\common.ps1')
$GameDir = Assert-Game $GameDir
& (Join-Path $PSScriptRoot 'GamePathTests.ps1')
$dotnetPath = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$pluginPath = Join-Path $taskRoot 'bin\Release\netstandard2.1\FamilyTimeVoice.dll'
if (!(Test-Path -LiteralPath $pluginPath) -or [Reflection.AssemblyName]::GetAssemblyName($pluginPath).Version -ne [version]($script:ReleaseVersion + '.0')) { throw "Build the current $($script:ReleaseVersion) Release plugin before running the full suite." }
foreach ($project in @('AudioTests','PlaybackTests','TransportTests','PersonaMemoryTests','CaptionAudibilityTests','UpgradeUsageTests')) {
    & $dotnetPath run --project (Join-Path $PSScriptRoot ($project + '.csproj')) -c Release --no-launch-profile
    if ($LASTEXITCODE) { throw "$project failed." }
}
& $dotnetPath run --project (Join-Path $PSScriptRoot 'PluginLifecycleTests.csproj') -c Release --no-launch-profile -- $taskRoot $GameDir --prepared-only
if ($LASTEXITCODE) { throw 'PluginLifecycleTests failed.' }
& (Join-Path $taskRoot 'scripts\test-engine-restoration.ps1') -GameDir $GameDir
& (Join-Path $PSScriptRoot 'InstallerTests.ps1') -GameDir $GameDir
Write-Host 'Offline verification complete. No paid API connection or physical listening check was performed.'
