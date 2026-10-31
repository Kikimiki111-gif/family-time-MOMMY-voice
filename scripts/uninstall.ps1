[CmdletBinding()]
param([string]$GameDir = '')
. (Join-Path $PSScriptRoot 'common.ps1')
$GameDir = Resolve-GameDirectory $GameDir
Assert-GameStopped $GameDir
$manifestPath = Resolve-ChildPath $GameDir 'BepInEx\FamilyTimeVoice-install.json'
if (!(Test-Path -LiteralPath $manifestPath)) { Write-Host 'No FamilyTimeVoice installation manifest found; nothing changed.'; return }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.pluginGuid -ne $script:PluginGuid) { throw 'Unrecognized installation manifest.' }
$ownDllPaths = @($manifest.files | Where-Object { $_.path -like '*.dll' -and $_.kind -in @('binary','preloader') } | ForEach-Object { Resolve-ChildPath $GameDir $_.path })
$otherMods = @()
foreach ($folder in @('BepInEx\plugins','BepInEx\patchers')) {
    $modFolder = Resolve-ChildPath $GameDir $folder
    if (Test-Path -LiteralPath $modFolder) {
        $otherMods += @(Get-ChildItem -LiteralPath $modFolder -Recurse -File -Filter '*.dll' | Where-Object { $_.FullName -notin $ownDllPaths })
    }
}
if ($otherMods.Count) { Write-Host 'Other mods are installed; the shared BepInEx loader will be retained.' }
$modifiedOwnMod = @($manifest.files | Where-Object { $_.kind -in @('binary','preloader') } | Where-Object {
    $ownedPath = Resolve-ChildPath $GameDir $_.path
    (Test-Path -LiteralPath $ownedPath) -and (Get-SHA256 $ownedPath) -ne $_.installedSha256
}).Count -gt 0
$remaining = @()
foreach ($record in @($manifest.files)) {
    $target = Resolve-ChildPath $GameDir $record.path
    if ($record.kind -eq 'preference') { Write-Host "Retained preference: $($record.path)"; continue }
    if ($record.kind -in @('loader','runtime') -and ($otherMods.Count -or $modifiedOwnMod)) { $remaining += $record; continue }
    if (!(Test-Path -LiteralPath $target)) { continue }
    if ((Get-SHA256 $target) -ne $record.installedSha256) { Write-Warning "Preserved changed file: $($record.path)"; $remaining += $record; continue }
    if ($record.originalExisted) {
        $backup = Resolve-ChildPath $GameDir $record.originalBackup
        if (!(Test-Path -LiteralPath $backup) -or (Get-SHA256 $backup) -ne $record.originalSha256) { Write-Warning "Missing or changed backup; preserved $($record.path)"; $remaining += $record; continue }
        Copy-Item -LiteralPath $backup -Destination $target -Force
        Write-Host "Restored original: $($record.path)"
    } else {
        Remove-Item -LiteralPath $target
        Write-Host "Removed owned file: $($record.path)"
    }
}
if ($remaining.Count) {
    $manifest.files = @($remaining)
    Write-Manifest $manifestPath $manifest
    Write-Warning 'Some files were retained; their ownership records remain in the manifest.'
} else {
    Move-Item -LiteralPath $manifestPath -Destination ($manifestPath + '.uninstalled-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.json')
}
Write-Host 'Preferences, protected key storage, usage ledger, saves, backups, and unrelated files are retained. No directories were recursively removed.'
