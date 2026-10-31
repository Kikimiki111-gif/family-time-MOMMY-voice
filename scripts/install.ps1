[CmdletBinding()]
param(
    [string]$GameDir = '',
    [string]$LoaderDir = '',
    [string]$RuntimeDir = '',
    [string]$PluginPath = '',
    [switch]$LoaderSmokeTest,
    [switch]$AllowDiagnosticRuntime
)
. (Join-Path $PSScriptRoot 'common.ps1')
$GameDir = Assert-Game $GameDir
Assert-GameStopped $GameDir
if (!$LoaderDir) { $LoaderDir = Join-Path $script:Workspace '.tools\bepinex' }
if (!$RuntimeDir) { $RuntimeDir = Join-Path $script:Workspace '.tools\unity-windows-corlibs-6000.2.12' }
if (!$PluginPath) { $PluginPath = Join-Path $script:Workspace 'bin\Release\netstandard2.1\FamilyTimeVoice.dll' }
if (!(Test-Path -LiteralPath $PluginPath -PathType Leaf)) { throw 'Compiled plugin missing. Run scripts/build.ps1 first.' }
$newPluginVersion = [Reflection.AssemblyName]::GetAssemblyName($PluginPath).Version.ToString()
if (!$LoaderSmokeTest -and [version]$newPluginVersion -ne [version]($script:ReleaseVersion + '.0')) { throw "Build release $($script:ReleaseVersion) before installing; selected binary is $newPluginVersion." }
if (!(Test-Path -LiteralPath (Join-Path $RuntimeDir 'mscorlib.dll'))) { throw 'Matching unstripped Unity corlibs are missing. Run scripts/setup-dependencies.ps1.' }
$runtimeManifestPath = Join-Path $RuntimeDir 'manifest.json'
if (Test-Path -LiteralPath $runtimeManifestPath) {
    $runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json
    if ($runtimeManifest.unityVersion -ne '6000.2.12f1' -or $runtimeManifest.platform -ne 'Windows') { throw 'Runtime manifest is not the matching Unity 6000.2.12f1 Windows profile.' }
    foreach ($runtimeRecord in @($runtimeManifest.files)) {
        $runtimeSource = Resolve-ChildPath $RuntimeDir $runtimeRecord.file
        if (!(Test-Path -LiteralPath $runtimeSource) -or (Get-SHA256 $runtimeSource) -ne $runtimeRecord.sha256) { throw "Prepared runtime hash mismatch: $($runtimeRecord.file)" }
    }
    if ((Get-SHA256 (Join-Path $RuntimeDir 'mscorlib.dll')) -ne '44C8BA3B568C49A6A635680E72770CD9E3230369B60A357A37799D7921AC1539') { throw 'Wrong Windows mscorlib build; rerun exact dependency preparation.' }
} elseif (!$AllowDiagnosticRuntime) {
    throw 'Verified Windows runtime manifest missing. Prepare exact dependencies before installing. Diagnostic donor profiles require an explicit diagnostic switch.'
}
$personaSource = Join-Path $script:Workspace 'profiles\wife.json'
if (!$LoaderSmokeTest -and !(Test-Path -LiteralPath $personaSource -PathType Leaf)) { throw 'profiles/wife.json is missing.' }
$jsonDependency = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PluginPath))) 'FamilyTimeVoice.Json.dll'
$patcher = Join-Path $script:Workspace 'preloader\bin\Release\netstandard2.0\FamilyTimeVoice.EngineRestoration.dll'
$engineDir = Join-Path $script:Workspace '.tools\engine-runtime'
if (!$LoaderSmokeTest) {
    if (!(Test-Path -LiteralPath $jsonDependency)) { throw 'Private JSON dependency missing; rebuild the project.' }
    if (!(Test-Path -LiteralPath $patcher)) { throw 'Engine restoration preloader missing; run build.ps1.' }
    if (!(Test-Path -LiteralPath $engineDir)) { throw 'Matching engine runtime modules missing; run setup-dependencies.ps1.' }
}
$manifestPath = Resolve-ChildPath $GameDir 'BepInEx\FamilyTimeVoice-install.json'
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.pluginGuid -ne $script:PluginGuid) { throw 'Unrecognized installation manifest; refusing to overwrite.' }
} else {
    $manifest = [pscustomobject]@{
        schemaVersion=1; pluginGuid=$script:PluginGuid; gameDir=$GameDir;
        gameExeSHA256=$script:ExpectedExeHash; gameAssemblySHA256=$script:ExpectedAssemblyHash;
        firstInstallUtc=[DateTime]::UtcNow.ToString('o'); lastInstallUtc=[DateTime]::UtcNow.ToString('o');
        backupId=[Guid]::NewGuid().ToString('N'); files=@()
    }
}

# Keep an executable previous-mod snapshot when its binary changes. This is
# separate from first-original backups used by uninstall. Never snapshot the key
# or usage ledger, and never restore a historical ledger during rollback.
$installedPlugin = Resolve-ChildPath $GameDir 'BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.dll'
if ((Test-Path -LiteralPath $installedPlugin) -and (Get-SHA256 $installedPlugin) -ne (Get-SHA256 $PluginPath)) {
    $oldPluginRecord = @($manifest.files | Where-Object { $_.path -eq 'BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.dll' })
    if ($oldPluginRecord.Count -and (Get-SHA256 $installedPlugin) -ne $oldPluginRecord[0].installedSha256) { throw 'File changed outside this installer; installed plugin preserved and no upgrade snapshot made.' }
    $snapshotId = 'before-' + $newPluginVersion + '-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff')
    $snapshotRoot = 'BepInEx\FamilyTimeVoice-upgrades\' + $snapshotId
    $snapshot = [pscustomobject]@{schemaVersion=1;pluginGuid=$script:PluginGuid;fromVersion=$null;toVersion=$newPluginVersion;createdUtc=[DateTime]::UtcNow.ToString('o');files=@()}
    try { $snapshot.fromVersion = [Reflection.AssemblyName]::GetAssemblyName($installedPlugin).Version.ToString() } catch { $snapshot.fromVersion = 'unrecognized-previous-binary' }
    foreach ($relative in @('BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.dll','BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.Json.dll','BepInEx\patchers\FamilyTimeVoice.EngineRestoration.dll','BepInEx\config\local.familytime.wifevoice.cfg','BepInEx\config\FamilyTimeVoice\wife.json')) {
        $source = Resolve-ChildPath $GameDir $relative
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $backupRelative = $snapshotRoot + '\files\' + $relative
        $backup = Resolve-ChildPath $GameDir $backupRelative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($backup)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $backup
        $snapshot.files += [pscustomobject]@{path=$relative;backup=$backupRelative;sha256=Get-SHA256 $source;preference=($relative -like 'BepInEx\config\*')}
    }
    $snapshotManifest = Resolve-ChildPath $GameDir ($snapshotRoot + '\manifest.json')
    Write-Manifest $snapshotManifest $snapshot
    $manifest | Add-Member -NotePropertyName lastUpgradeSnapshot -NotePropertyValue ($snapshotRoot + '\manifest.json') -Force
    Write-Manifest $manifestPath $manifest
    Write-Host "Previous mod baseline preserved: $snapshotManifest"
}
$manifest | Add-Member -NotePropertyName pluginVersion -NotePropertyValue $newPluginVersion -Force

function Install-OwnedFile([string]$Source,[string]$Relative,[string]$Kind='binary') {
    $destination = Resolve-ChildPath $GameDir $Relative
    $existing = @($manifest.files | Where-Object { $_.path -eq $Relative })
    if ($existing.Count -gt 1) { throw "Duplicate manifest record: $Relative" }
    if ($Kind -eq 'preference' -and (Test-Path -LiteralPath $destination)) {
        Write-Host "Preserved existing preference: $Relative"
        return
    }
    if ($existing.Count) {
        $record = $existing[0]
        if ((Test-Path -LiteralPath $destination) -and (Get-SHA256 $destination) -ne $record.installedSha256) {
            throw "File changed outside this installer; preserved: $Relative. Resolve this file before upgrading."
        }
    } else {
        $originalExisted = Test-Path -LiteralPath $destination -PathType Leaf
        $backupRelative = $null
        $originalHash = $null
        if ($originalExisted) {
            $originalHash = Get-SHA256 $destination
            $backupRelative = 'BepInEx\FamilyTimeVoice-backups\' + $manifest.backupId + '\' + $Relative
            $backupPath = Resolve-ChildPath $GameDir $backupRelative
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($backupPath)) | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backupPath
        }
        $record = [pscustomobject]@{path=$Relative;kind=$Kind;originalExisted=[bool]$originalExisted;originalBackup=$backupRelative;originalSha256=$originalHash;installedSha256=$null}
        $manifest.files = @($manifest.files) + @($record)
    }
    $record.installedSha256 = Get-SHA256 $Source
    Write-Manifest $manifestPath $manifest
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    if ((Get-SHA256 $destination) -ne $record.installedSha256) { throw "Copy verification failed: $Relative" }
    Write-Host "Installed: $Relative"
}

$existingLoader = Resolve-ChildPath $GameDir 'BepInEx\core\BepInEx.dll'
if (Test-Path -LiteralPath $existingLoader) {
    $loaderVersion = [Reflection.AssemblyName]::GetAssemblyName($existingLoader).Version
    if ($loaderVersion.Major -ne 5 -or $loaderVersion -lt [version]'5.4.23.0') { throw "Existing loader $loaderVersion requires compatibility review; no files overwritten." }
    if (!(Test-Path -LiteralPath (Resolve-ChildPath $GameDir 'winhttp.dll'))) { throw 'Existing BepInEx has no winhttp.dll entry point. Review its loader setup.' }
    $doorstop = Resolve-ChildPath $GameDir 'doorstop_config.ini'
    if (!(Test-Path -LiteralPath $doorstop)) { throw 'Existing loader has no doorstop_config.ini.' }
    $doorstopText = Get-Content -LiteralPath $doorstop -Raw
    if ($doorstopText -notmatch '(?im)^\s*enabled\s*=\s*true\s*$' -or $doorstopText -notmatch '(?im)^\s*target_assembly\s*=\s*BepInEx[\\/]core[\\/]BepInEx\.Preloader\.dll\s*$') { throw 'Existing Doorstop configuration does not enable the expected Mono loader.' }
    Write-Host "Reusing BepInEx $loaderVersion; the required separate runtime override is managed below."
} else {
    $loaderSource = [IO.Path]::GetFullPath($LoaderDir).TrimEnd('\','/')
    if (!(Test-Path -LiteralPath (Join-Path $loaderSource 'BepInEx\core\BepInEx.dll'))) { throw 'Compatible official loader missing. Run scripts/setup-dependencies.ps1.' }
    $loaderFiles = @(Get-ChildItem -LiteralPath $loaderSource -Recurse -File)
    # Validate every conflict before the first write; do not overwrite another loader.
    foreach ($file in $loaderFiles) {
        $relative = $file.FullName.Substring($loaderSource.Length+1)
        $destination = Resolve-ChildPath $GameDir $relative
        if (Test-Path -LiteralPath $destination) { throw "Potential existing loader file: $relative. Review before installation." }
    }
    foreach ($file in $loaderFiles) { Install-OwnedFile $file.FullName ($file.FullName.Substring($loaderSource.Length+1)) 'loader' }
}
foreach ($runtimeFile in @(Get-ChildItem -LiteralPath $RuntimeDir -File -Filter '*.dll')) {
    Install-OwnedFile $runtimeFile.FullName ('BepInEx\FamilyTimeVoice-runtime\' + $runtimeFile.Name) 'runtime'
}
# The shipped mscorlib is stripped. Doorstop supports a separate search override;
# keep the game Managed directory untouched and retain any existing search paths.
$doorstopPath = Resolve-ChildPath $GameDir 'doorstop_config.ini'
$doorstopText = Get-Content -LiteralPath $doorstopPath -Raw
$overrideEntry = 'BepInEx\FamilyTimeVoice-runtime'
$overrideMatch = [regex]::Match($doorstopText,'(?im)^[ \t]*dll_search_path_override[ \t]*=[ \t]*([^\r\n]*)')
if (!$overrideMatch.Success) { throw 'Doorstop config has no dll_search_path_override setting.' }
$searchPaths = @($overrideMatch.Groups[1].Value.Trim().Split(';') | Where-Object { $_ -and $_ -ne $overrideEntry -and !$_.Trim().StartsWith('#') })
$replacement = 'dll_search_path_override = ' + ((@($overrideEntry) + $searchPaths) -join ';')
$updatedDoorstop = $doorstopText.Substring(0,$overrideMatch.Index) + $replacement + $doorstopText.Substring($overrideMatch.Index+$overrideMatch.Length)
if ($updatedDoorstop -ne $doorstopText) {
    $preparedConfig = Join-Path $script:Workspace '.tools\doorstop-familytimevoice.ini'
    [IO.File]::WriteAllText($preparedConfig,$updatedDoorstop,[Text.UTF8Encoding]::new($false))
    Install-OwnedFile $preparedConfig 'doorstop_config.ini' 'loader'
}
Install-OwnedFile $PluginPath 'BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.dll'
if (!$LoaderSmokeTest) {
    Install-OwnedFile $jsonDependency 'BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.Json.dll'
    Install-OwnedFile $patcher 'BepInEx\patchers\FamilyTimeVoice.EngineRestoration.dll' 'preloader'
    foreach ($engineFile in @(Get-ChildItem -LiteralPath $engineDir -File -Filter '*.dll')) {
        Install-OwnedFile $engineFile.FullName ('BepInEx\FamilyTimeVoice-engine\' + $engineFile.Name) 'engine'
    }
    Install-OwnedFile $personaSource 'BepInEx\config\FamilyTimeVoice\wife.json' 'preference'
}
$manifest.lastInstallUtc = [DateTime]::UtcNow.ToString('o')
Write-Manifest $manifestPath $manifest
Write-Host "Installation recorded at $manifestPath"
if ($LoaderSmokeTest) { Write-Host 'Loader smoke-test installation only; run installation again after the voice build/profile is ready.' }
