[CmdletBinding()]
param([string]$OutputPath = '', [switch]$AllowIncomplete)
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'public-files.ps1')
$plugin = Join-Path $script:Workspace 'bin\Release\netstandard2.1\FamilyTimeVoice.dll'
if (!(Test-Path -LiteralPath $plugin)) { throw 'Build Release before packaging.' }
$binaryVersion = [Reflection.AssemblyName]::GetAssemblyName($plugin).Version
if (!$AllowIncomplete -and $binaryVersion -ne [version]($script:ReleaseVersion + '.0')) { throw "Stale plugin binary $binaryVersion; build release $($script:ReleaseVersion) before packaging." }
foreach ($required in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','profiles\wife.json','docs\installation.md','docs\testing.md')) {
    if (!$AllowIncomplete -and !(Test-Path -LiteralPath (Join-Path $script:Workspace $required))) { throw "Release file missing: $required" }
}
$releaseDir = Join-Path $script:Workspace 'dist'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
if (!$OutputPath) { $OutputPath = Join-Path $releaseDir ('FamilyTimeVoice-' + $script:ReleaseVersion + '.zip') }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$currentArchive = [IO.Path]::GetFullPath((Join-Path $releaseDir ('FamilyTimeVoice-' + $script:ReleaseVersion + '.zip')))
$namedRelease = [regex]::Match([IO.Path]::GetFileName($OutputPath), '^FamilyTimeVoice-(\d+\.\d+\.\d+)\.zip$', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($namedRelease.Success -and [version]$namedRelease.Groups[1].Value -lt [version]$script:ReleaseVersion) { throw 'Preserved older release archives cannot be overwritten by an upgrade package.' }
if ((Test-Path -LiteralPath $OutputPath) -and !$OutputPath.Equals($currentArchive,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to overwrite an existing archive other than the current release output.' }
$staging = Join-Path $releaseDir ('package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
function Add-ReleaseFile([string]$Source,[string]$Relative) {
    $target = Resolve-ChildPath $staging $Relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $target
}
# Historical private reports and local audition helpers never enter a public
# release. This is the same allowlist used for the public source repository.
foreach ($relative in @(Get-PublicSourcePaths $script:Workspace)) {
    Add-ReleaseFile (Join-Path $script:Workspace $relative) $relative
}
Add-ReleaseFile $plugin 'bin\Release\netstandard2.1\FamilyTimeVoice.dll'
Add-ReleaseFile (Join-Path $script:Workspace 'bin\Release\netstandard2.1\FamilyTimeVoice.Json.dll') 'bin\Release\netstandard2.1\FamilyTimeVoice.Json.dll'
Add-ReleaseFile (Join-Path $script:Workspace 'preloader\bin\Release\netstandard2.0\FamilyTimeVoice.EngineRestoration.dll') 'preloader\bin\Release\netstandard2.0\FamilyTimeVoice.EngineRestoration.dll'
$loader = Join-Path $script:Workspace '.tools\BepInEx_win_x64_5.4.23.6.zip'
if (Test-Path -LiteralPath $loader) { Add-ReleaseFile $loader 'redistributables\BepInEx_win_x64_5.4.23.6.zip' }
$fileList = @(Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($staging.Length+1).Replace('\','/');sha256=Get-SHA256 $_.FullName}
})
$fileList | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'SHA256SUMS.json') -Encoding UTF8
# An explicit allowlist controls the package; protected storage, game files, keys,
# ledger, logs, .git, .tools, and proprietary Unity DLLs are never copied.
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $OutputPath -Force
Write-Host "Package: $([IO.Path]::GetFullPath($OutputPath))"
Write-Host "SHA256: $(Get-SHA256 $OutputPath)"
