[CmdletBinding()]
param([string]$GameDir = '', [string]$RuntimeDir = '')
. (Join-Path $PSScriptRoot 'common.ps1')
$GameDir = Assert-Game $GameDir
if (!$RuntimeDir) { $RuntimeDir = Join-Path $script:Workspace '.tools\unity-windows-corlibs-6000.2.12' }
$runtimeArguments = @{}
if (!(Test-Path -LiteralPath (Join-Path $RuntimeDir 'manifest.json'))) { $runtimeArguments.AllowDiagnosticRuntime = $true }
$testName = 'installer-test-' + [Guid]::NewGuid().ToString('N')
$fixture = Resolve-ChildPath (Join-Path $env:LOCALAPPDATA 'Temp\FamilyTimeVoiceTests') $testName
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
foreach ($relative in @('FamilyTime.exe','FamilyTime_Data\app.info','FamilyTime_Data\Managed\Assembly-CSharp.dll','MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll')) {
    $target = Resolve-ChildPath $fixture $relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $GameDir $relative) -Destination $target
}
function Assert-True([bool]$Condition,[string]$Message) { if (!$Condition) { throw "FAILED: $Message" }; Write-Host "PASS: $Message" }
$pluginRelative = 'BepInEx\plugins\FamilyTimeVoice\FamilyTimeVoice.dll'
$pluginTarget = Resolve-ChildPath $fixture $pluginRelative
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($pluginTarget)) | Out-Null
[IO.File]::WriteAllText($pluginTarget,'original pre-install bytes')
$originalHash = Get-SHA256 $pluginTarget
$configTarget = Resolve-ChildPath $fixture 'BepInEx\config\local.familytime.wifevoice.cfg'
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($configTarget)) | Out-Null
[IO.File]::WriteAllText($configTarget,'original non-secret preferences')
& (Join-Path $PSScriptRoot 'install.ps1') -GameDir $fixture -LoaderSmokeTest -RuntimeDir $RuntimeDir @runtimeArguments
$manifestPath = Join-Path $fixture 'BepInEx\FamilyTimeVoice-install.json'
$first = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$initialCount = @($first.files).Count
$originalRecord = @($first.files | Where-Object { $_.path -eq $pluginRelative })[0]
Assert-True ($originalRecord.originalSha256 -eq $originalHash) 'Installer records and backs up pre-existing plugin bytes.'
& (Join-Path $PSScriptRoot 'install.ps1') -GameDir $fixture -LoaderSmokeTest -RuntimeDir $RuntimeDir @runtimeArguments
$second = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-True (@($second.files).Count -eq $initialCount) 'Repeated installation creates no duplicate records.'
Assert-True ($second.backupId -eq $first.backupId) 'Repeated installation retains the first originals.'
$snapshot = $second.lastUpgradeSnapshot
Assert-True (![string]::IsNullOrWhiteSpace($snapshot)) 'Changed previous-mod binary receives a separate upgrade snapshot.'
[IO.File]::WriteAllText($configTarget,'edited non-secret preferences')
& (Join-Path $PSScriptRoot 'rollback.ps1') -GameDir $fixture
Assert-True ((Get-SHA256 $pluginTarget) -eq $originalHash) 'Rollback restores the exact previous mod binary.'
Assert-True ([IO.File]::ReadAllText($configTarget) -eq 'edited non-secret preferences') 'Default rollback preserves current preferences.'
& (Join-Path $PSScriptRoot 'rollback.ps1') -GameDir $fixture -RestorePreferences
Assert-True ([IO.File]::ReadAllText($configTarget) -eq 'original non-secret preferences') 'Explicit preference rollback restores its checked backup.'
& (Join-Path $PSScriptRoot 'install.ps1') -GameDir $fixture -LoaderSmokeTest -RuntimeDir $RuntimeDir @runtimeArguments
$afterRollbackInstall = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-True ($afterRollbackInstall.backupId -eq $first.backupId) 'Reinstall after rollback retains first-original uninstall history.'
$sourceHash = Get-SHA256 $pluginTarget
[IO.File]::AppendAllText($pluginTarget,'changed outside installer')
$refused = $false
try { & (Join-Path $PSScriptRoot 'install.ps1') -GameDir $fixture -LoaderSmokeTest -RuntimeDir $RuntimeDir @runtimeArguments } catch { $refused = $_.Exception.Message -like '*File changed outside*' }
Assert-True $refused 'Upgrade refuses to overwrite a plugin modified outside the installer.'
Copy-Item -LiteralPath (Join-Path $script:Workspace 'bin\Release\netstandard2.1\FamilyTimeVoice.dll') -Destination $pluginTarget -Force
Assert-True ((Get-SHA256 $pluginTarget) -eq $sourceHash) 'Test resets fixture to installer-owned bytes.'
$preference = Resolve-ChildPath $fixture 'BepInEx\config\FamilyTimeVoice\wife.json'
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($preference)) | Out-Null
[IO.File]::WriteAllText($preference,'{"sentinel":"user-edited persona"}')
$otherMod = Resolve-ChildPath $fixture 'BepInEx\plugins\UnrelatedMod.dll'
[IO.File]::WriteAllText($otherMod,'unrelated mod sentinel')
$otherHash = Get-SHA256 $otherMod
& (Join-Path $PSScriptRoot 'uninstall.ps1') -GameDir $fixture
Assert-True ((Get-SHA256 $pluginTarget) -eq $originalHash) 'Uninstall restores the original plugin bytes.'
Assert-True ((Get-SHA256 $otherMod) -eq $otherHash) 'Uninstall preserves unrelated mods.'
Assert-True (Test-Path -LiteralPath (Join-Path $fixture 'winhttp.dll')) 'Shared loader is retained while an unrelated mod exists.'
Assert-True (Test-Path -LiteralPath $preference) 'Uninstall preserves user preferences.'
Remove-Item -LiteralPath $otherMod
Assert-True ((Get-SHA256 $pluginTarget) -eq $originalHash) 'Original plugin bytes remain intact after the unrelated mod is removed.'
# The restored pre-existing plugin is itself a mod, so it must also be absent
# before this installation is allowed to remove its shared loader.
Remove-Item -LiteralPath $pluginTarget
& (Join-Path $PSScriptRoot 'uninstall.ps1') -GameDir $fixture
Assert-True (!(Test-Path -LiteralPath (Join-Path $fixture 'winhttp.dll'))) 'A later uninstall removes unchanged owned loader files after other mods are absent.'
$escaped = $false
try { $null = Resolve-ChildPath $fixture '..\escape.dll' } catch { $escaped = $true }
Assert-True $escaped 'Manifest paths cannot escape the intended game folder.'
Write-Host "All installer checks passed. Fixture retained at $fixture (no recursive deletion)."
