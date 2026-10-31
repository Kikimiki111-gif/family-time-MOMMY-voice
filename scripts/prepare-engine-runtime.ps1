param([string]$Dotnet = (Join-Path $PSScriptRoot '..\.tools\dotnet\dotnet.exe'), [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsDir = Join-Path $taskRoot '.tools'
$archive = Join-Path $toolsDir 'unity-full-runtime-6000.2.12.zip'
$unpacked = Join-Path $toolsDir 'unity-full-runtime-6000.2.12'
$runtime = Join-Path $toolsDir 'engine-runtime'
$expected = '072C64A8E60A3B806E2CB899A6579B477DBD150CCBAC83B97E3B234AFBE18D7E'
New-Item -ItemType Directory -Path $toolsDir,$runtime -Force | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri 'https://unity.bepinex.dev/libraries/6000.2.12.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    throw 'Unity runtime archive checksum mismatch. Inspect the dependency before proceeding.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $unpacked -Force
$modules = @('UnityEngine.SharedInternalsModule.dll','UnityEngine.CoreModule.dll','UnityEngine.AudioModule.dll','UnityEngine.IMGUIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.ImageConversionModule.dll')
$manifest = foreach ($name in $modules) {
    $source = Join-Path $unpacked $name
    Copy-Item -LiteralPath $source -Destination (Join-Path $runtime $name) -Force
    [ordered]@{ file=$name; sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $runtime 'manifest.json') -Encoding utf8
if (!$SkipBuild) {
    & $Dotnet build (Join-Path $taskRoot 'preloader\FamilyTimeVoice.EngineRestoration.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Engine restoration patcher build failed.' }
}
Write-Output ('Engine wrappers ready: ' + $runtime)
if (!$SkipBuild) { Write-Output ('Patcher ready: ' + (Join-Path $taskRoot 'preloader\bin\Release\netstandard2.0\FamilyTimeVoice.EngineRestoration.dll')) }
