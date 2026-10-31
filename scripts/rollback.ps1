[CmdletBinding()]
param(
    [string]$GameDir = '',
    [string]$SnapshotManifest = '',
    [switch]$RestorePreferences
)
. (Join-Path $PSScriptRoot 'common.ps1')
$GameDir = Assert-Game $GameDir
Assert-GameStopped $GameDir
$installManifestPath = Resolve-ChildPath $GameDir 'BepInEx\FamilyTimeVoice-install.json'
if (!(Test-Path -LiteralPath $installManifestPath)) { throw 'No owned installation manifest found.' }
$installation = Get-Content -LiteralPath $installManifestPath -Raw | ConvertFrom-Json
if ($installation.schemaVersion -ne 1 -or $installation.pluginGuid -ne $script:PluginGuid) { throw 'Unrecognized installation manifest.' }
if (!$SnapshotManifest) { $SnapshotManifest = $installation.lastUpgradeSnapshot }
if (!$SnapshotManifest) { throw 'No previous-mod snapshot recorded. Use the preserved 0.1.0 release/source baseline.' }
$snapshotPath = Resolve-ChildPath $GameDir $SnapshotManifest
$upgradeRoot = Resolve-ChildPath $GameDir 'BepInEx\FamilyTimeVoice-upgrades'
if (!$snapshotPath.StartsWith($upgradeRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Rollback snapshots must be inside this mod upgrade-backup directory.' }
$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
if ($snapshot.schemaVersion -ne 1 -or $snapshot.pluginGuid -ne $script:PluginGuid) { throw 'Unrecognized previous-mod snapshot.' }
$allowed = @('BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.dll','BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.Json.dll','BepInEx\patchers\FamilyTimeVoice.EngineRestoration.dll','BepInEx\config\local.familytime.wifevoice.cfg','BepInEx\config\FamilyTimeVoice\wife.json')
$operations = @()
foreach ($file in @($snapshot.files)) {
    if ($file.path -notin $allowed) { throw 'Snapshot contains a file outside the allowed mod/setting paths.' }
    if ($file.preference -and !$RestorePreferences) { continue }
    $source = Resolve-ChildPath $GameDir $file.backup
    $target = Resolve-ChildPath $GameDir $file.path
    $expectedSource = Resolve-ChildPath ([IO.Path]::GetDirectoryName($snapshotPath)) ('files\' + $file.path)
    if (!$source.Equals($expectedSource,[StringComparison]::OrdinalIgnoreCase)) { throw 'Snapshot backup path does not match its recorded mod file.' }
    if (!(Test-Path -LiteralPath $source) -or (Get-SHA256 $source) -ne $file.sha256) { throw "Previous-mod backup changed: $($file.path)" }
    $record = @($installation.files | Where-Object { $_.path -eq $file.path })
    if (!$file.preference -and (Test-Path -LiteralPath $target) -and (Get-SHA256 $target) -ne $file.sha256 -and ($record.Count -ne 1 -or (Get-SHA256 $target) -ne $record[0].installedSha256)) { throw "Preserved outside-modified installed file: $($file.path)" }
    $operations += [pscustomobject]@{source=$source;target=$target;record=$record;file=$file}
}
$beforeRollback = 'BepInEx\FamilyTimeVoice-upgrades\before-rollback-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff')
foreach ($operation in $operations) {
    if (Test-Path -LiteralPath $operation.target) {
        $backup = Resolve-ChildPath $GameDir ($beforeRollback + '\' + $operation.file.path)
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($backup)) | Out-Null
        Copy-Item -LiteralPath $operation.target -Destination $backup
    }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($operation.target)) | Out-Null
    Copy-Item -LiteralPath $operation.source -Destination $operation.target -Force
    if ($operation.record.Count -eq 1) { $operation.record[0].installedSha256 = $operation.file.sha256 }
    Write-Host "Restored previous mod file: $($operation.file.path)"
}
$installation | Add-Member -NotePropertyName pluginVersion -NotePropertyValue $snapshot.fromVersion -Force
$installation | Add-Member -NotePropertyName lastRollbackUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
Write-Manifest $installManifestPath $installation
Write-Host 'Previous binaries restored. Key, cumulative ledger, voice IDs and memory were not restored or reset.'
if (!$RestorePreferences) { Write-Host 'Current preferences/persona remain. Use -RestorePreferences only to restore their previous versions too; current copies are backed up first.' }
