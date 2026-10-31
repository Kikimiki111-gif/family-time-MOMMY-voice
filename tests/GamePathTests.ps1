$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\common.ps1')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('FamilyTimeVoice-paths-' + [Guid]::NewGuid().ToString('N'))
$first = Join-Path $fixtureRoot 'Family Time'
$second = Join-Path $fixtureRoot 'Another Family Time'
foreach ($folder in @($first,$second)) {
    New-Item -ItemType Directory -Path (Join-Path $folder 'FamilyTime_Data') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder 'FamilyTime.exe'),'path-only sentinel, never executable')
}
$previousEnvironment = $env:FAMILY_TIME_GAME_DIR
$script:pathCandidates = @()
$script:pathPromptCount = 0
function Get-GameDirectoryCandidates { return $script:pathCandidates }
function Read-Host { param([string]$Prompt); $script:pathPromptCount++; return $second }
function Assert-PathCheck([bool]$Condition,[string]$Message) { if (!$Condition) { throw "FAILED: $Message" }; Write-Host "PASS: $Message" }
try {
    $env:FAMILY_TIME_GAME_DIR = ''
    Assert-PathCheck ((Resolve-GameDirectory $first -NoPrompt) -eq $first) 'Explicit game folders support paths with spaces.'
    Assert-PathCheck ((Resolve-GameDirectory ('"' + (Join-Path $first 'FamilyTime.exe') + '"') -NoPrompt) -eq $first) 'A quoted executable path resolves to its own game folder.'
    $env:FAMILY_TIME_GAME_DIR = $second
    Assert-PathCheck ((Resolve-GameDirectory -NoPrompt) -eq $second) 'The optional environment override supplies a game location.'
    Assert-PathCheck ((Resolve-GameDirectory $first -NoPrompt) -eq $first) 'An explicit CLI path takes precedence over the environment override.'
    $env:FAMILY_TIME_GAME_DIR = ''
    $script:pathCandidates = @($first)
    Assert-PathCheck ((Resolve-GameDirectory -NoPrompt) -eq $first -and $script:pathPromptCount -eq 0) 'A unique discovered copy needs no prompt.'
    $refused = $false
    try { $null = Resolve-GameDirectory (Join-Path $fixtureRoot 'missing') -NoPrompt } catch { $refused = $_.Exception.Message -like '*FamilyTime.exe was not found*' }
    Assert-PathCheck ($refused -and $script:pathPromptCount -eq 0) 'An invalid explicit path is refused instead of silently choosing another copy.'
    $script:pathCandidates = @()
    $refused = $false
    try { $null = Resolve-GameDirectory -NoPrompt } catch { $refused = $_.Exception.Message -like '*missing or ambiguous*' }
    Assert-PathCheck $refused 'Noninteractive use reports a missing location without requesting input.'
    $script:pathCandidates = @($first,$second)
    $refused = $false
    try { $null = Resolve-GameDirectory -NoPrompt } catch { $refused = $_.Exception.Message -like '*missing or ambiguous*' }
    Assert-PathCheck ($refused -and $script:pathPromptCount -eq 0) 'Multiple copies are never chosen arbitrarily.'
    Assert-PathCheck ((Resolve-GameDirectory) -eq $second -and $script:pathPromptCount -eq 1) 'An ambiguous location asks once and uses the supplied folder.'
    Write-Host "Game-path checks passed. Sentinel fixture retained at $fixtureRoot. No actual game files were read or changed."
} finally { $env:FAMILY_TIME_GAME_DIR = $previousEnvironment }
