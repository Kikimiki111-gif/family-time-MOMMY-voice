param([string]$Dotnet = (Join-Path $PSScriptRoot '..\.tools\dotnet\dotnet.exe'))
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
& $Dotnet restore (Join-Path $taskRoot 'buildtools\JsonIsolation\JsonIsolation.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
$inputDll = Join-Path $packageRoot 'newtonsoft.json\13.0.3\lib\netstandard2.0\Newtonsoft.Json.dll'
$outputDll = Join-Path $taskRoot '.tools\json\FamilyTimeVoice.Json.dll'
& $Dotnet run --project (Join-Path $taskRoot 'buildtools\JsonIsolation\JsonIsolation.csproj') -c Release -- $inputDll $outputDll
if ($LASTEXITCODE -ne 0) { throw 'JSON dependency isolation failed.' }
Get-FileHash -Algorithm SHA256 -LiteralPath $inputDll,$outputDll | Format-List
