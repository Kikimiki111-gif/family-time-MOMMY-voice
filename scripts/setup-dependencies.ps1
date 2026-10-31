[CmdletBinding()]
param([switch]$WithDecompiler, [switch]$BuildOnly, [switch]$InstallOnly, [string]$EditorDir = '')
. (Join-Path $PSScriptRoot 'common.ps1')
if ($InstallOnly -and ($BuildOnly -or $WithDecompiler)) { throw '-InstallOnly cannot be combined with -BuildOnly or -WithDecompiler.' }
if ($InstallOnly) {
    foreach ($relative in @('bin\Release\netstandard2.1\FamilyTimeVoice.dll','bin\Release\netstandard2.1\FamilyTimeVoice.Json.dll','preloader\bin\Release\netstandard2.0\FamilyTimeVoice.EngineRestoration.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $script:Workspace $relative))) { throw 'Compiled release files are missing. Download and extract the release ZIP, or use setup-dependencies.ps1 followed by build.ps1 for a source checkout.' }
    }
    if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $script:Workspace 'bin\Release\netstandard2.1\FamilyTimeVoice.dll')).Version -ne [version]($script:ReleaseVersion + '.0')) { throw 'The compiled plugin does not match this release. Extract a complete release ZIP or rebuild the source.' }
}
$toolRoot = Join-Path $script:Workspace '.tools'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
$sdkDir = Join-Path $toolRoot 'dotnet'
if (!$InstallOnly -and !(Test-Path -LiteralPath (Join-Path $sdkDir 'sdk\8.0.427\MSBuild.dll'))) {
    $sdkArchive = Join-Path $toolRoot 'dotnet-sdk-8.0.427-win-x64.zip'
    $sdkUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.427/dotnet-sdk-8.0.427-win-x64.zip'
    $sdkHash = 'A4DD6EB2BF2A2F6FB10997D3E044A229B23AD96A5C86D20494687E34EE73B537D7BD14488CE14CDED6917F6778DA4D66360E2734020B7EF487D0F29AB144489D'
    if (!(Test-Path -LiteralPath $sdkArchive)) { Invoke-WebRequest -Uri $sdkUrl -OutFile $sdkArchive }
    if ((Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash -ne $sdkHash) { throw 'SDK download checksum mismatch; archive was preserved for inspection.' }
    Expand-Archive -LiteralPath $sdkArchive -DestinationPath $sdkDir -Force
}
$loaderArchive = Join-Path $toolRoot 'BepInEx_win_x64_5.4.23.6.zip'
$bundledLoader = Join-Path $script:Workspace 'redistributables\BepInEx_win_x64_5.4.23.6.zip'
if (!(Test-Path -LiteralPath $loaderArchive)) {
    if (Test-Path -LiteralPath $bundledLoader) { Copy-Item -LiteralPath $bundledLoader -Destination $loaderArchive }
    else { Invoke-WebRequest -Uri 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6/BepInEx_win_x64_5.4.23.6.zip' -OutFile $loaderArchive }
}
if ((Get-SHA256 $loaderArchive) -ne '6C57A714A892EFB4E9E1FEF63D608A3391F30EC984031C465404E4EE381A0755') { throw 'Loader checksum mismatch.' }
Expand-Archive -LiteralPath $loaderArchive -DestinationPath (Join-Path $toolRoot 'bepinex') -Force
if (!$InstallOnly) {
    $dotnet = Join-Path $sdkDir 'dotnet.exe'
    $env:DOTNET_ROOT = $sdkDir
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    & $dotnet --version
    if ($LASTEXITCODE) { throw 'Portable SDK did not start.' }
    if ($WithDecompiler -and !(Test-Path -LiteralPath (Join-Path $toolRoot 'ilspy\ilspycmd.exe'))) {
        & $dotnet tool install ilspycmd --version 9.1.0.7988 --tool-path (Join-Path $toolRoot 'ilspy')
        if ($LASTEXITCODE) { throw 'ILSpy installation failed.' }
    }
    & (Join-Path $PSScriptRoot 'prepare-json-dependency.ps1') -Dotnet $dotnet
    & (Join-Path $PSScriptRoot 'prepare-engine-runtime.ps1') -Dotnet $dotnet
} else {
    & (Join-Path $PSScriptRoot 'prepare-engine-runtime.ps1') -SkipBuild
}
if (!$BuildOnly) { & (Join-Path $PSScriptRoot 'prepare-windows-corlibs.ps1') -EditorDir $EditorDir }
if ($InstallOnly) { Write-Host 'Pinned installation dependencies are ready. No SDK/build or game-file changes were needed.' }
else { Write-Host 'Pinned build dependencies are ready. No game files were changed.' }
