$ErrorActionPreference = 'Stop'
$script:Workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:PluginGuid = 'local.familytime.wifevoice'
$script:ReleaseVersion = '0.2.2'
$script:ExpectedExeHash = 'C859377F30EF31F9AAF8B558DE11E4804E10B50C0A6AB76C2060BA153F9019F8'
$script:ExpectedAssemblyHash = '7AAFF30819FC4D4174B22DF52995489D595AFBA0A0ED4B4BC89ED0BDD008542F'

function Get-SHA256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Resolve-ChildPath([string]$Root, [string]$Relative) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if ([IO.Path]::IsPathRooted($Relative)) { throw "Expected relative installation path: $Relative" }
    $target = [IO.Path]::GetFullPath((Join-Path $rootFull $Relative))
    if (!$target.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes intended directory: $Relative"
    }
    # Refuse junctions/symlinks, including a redirected game directory.
    $probe = $target
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            if ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing redirected installation path: $probe"
            }
        }
        if ($probe.Equals($rootFull,[StringComparison]::OrdinalIgnoreCase)) { break }
        $next = [IO.Path]::GetDirectoryName($probe)
        if ($next -eq $probe) { break }
        $probe = $next
    }
    return $target
}

function Get-GameDirectoryCandidates {
    $roots = @($script:Workspace, (Split-Path -Parent $script:Workspace), (Get-Location).Path)
    if ($env:USERPROFILE) { $roots += @('Games','Downloads','Desktop') | ForEach-Object { Join-Path $env:USERPROFILE $_ } }
    foreach ($programFiles in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($programFiles) { $roots += Join-Path $programFiles 'Steam\steamapps\common' }
    }
    $candidates = foreach ($root in @($roots | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $root -PathType Container)) { continue }
        $root
        # Inspect directory names in these common locations, then one extraction
        # level. Never recursively search a user's drives or private files.
        foreach ($folder in @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Family*Time*' })) {
            $folder.FullName
            Get-ChildItem -LiteralPath $folder.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
        }
    }
    return @($candidates | Where-Object { (Test-Path -LiteralPath (Join-Path $_ 'FamilyTime.exe') -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $_ 'FamilyTime_Data') -PathType Container) } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\','/') } | Select-Object -Unique)
}

function Resolve-GameDirectory([string]$GameDir = '', [switch]$NoPrompt) {
    if ([string]::IsNullOrWhiteSpace($GameDir) -and ![string]::IsNullOrWhiteSpace($env:FAMILY_TIME_GAME_DIR)) { $GameDir = $env:FAMILY_TIME_GAME_DIR }
    if ([string]::IsNullOrWhiteSpace($GameDir)) {
        $candidates = @(Get-GameDirectoryCandidates)
        if ($candidates.Count -eq 1) { $GameDir = $candidates[0]; Write-Host "Found Family Time: $GameDir" }
        else {
            if ($NoPrompt) { throw 'Game location is missing or ambiguous. Pass -GameDir with the folder containing FamilyTime.exe.' }
            if ($candidates.Count -gt 1) { Write-Host ('Multiple Family Time folders found: ' + ($candidates -join '; ')) }
            try { $GameDir = Read-Host 'Enter your Family Time folder (containing FamilyTime.exe), or its full executable path' }
            catch { throw 'Pass -GameDir with the folder containing your own FamilyTime.exe; interactive input is unavailable.' }
            if ([string]::IsNullOrWhiteSpace($GameDir)) { throw 'No game path supplied. Run again with -GameDir.' }
        }
    }
    $gameFull = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($GameDir.Trim().Trim('"'))).TrimEnd('\','/')
    if ((Test-Path -LiteralPath $gameFull -PathType Leaf) -and [IO.Path]::GetFileName($gameFull).Equals('FamilyTime.exe',[StringComparison]::OrdinalIgnoreCase)) { $gameFull = [IO.Path]::GetDirectoryName($gameFull) }
    if (!(Test-Path -LiteralPath (Resolve-ChildPath $gameFull 'FamilyTime.exe') -PathType Leaf)) { throw "FamilyTime.exe was not found in '$gameFull'. Pass the correct -GameDir." }
    return $gameFull
}

function Assert-Game([string]$GameDir = '') {
    $gameFull = Resolve-GameDirectory $GameDir
    $exe = Resolve-ChildPath $gameFull 'FamilyTime.exe'
    $assembly = Resolve-ChildPath $gameFull 'FamilyTime_Data\Managed\Assembly-CSharp.dll'
    foreach ($file in @($exe,$assembly)) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing game file: $file" } }
    if ((Get-SHA256 $exe) -ne $script:ExpectedExeHash -or (Get-SHA256 $assembly) -ne $script:ExpectedAssemblyHash) {
        throw 'This installer supports only the inspected Family Time build. Executable or Assembly-CSharp hash differs; inspect the update before installing.'
    }
    $identity = @(Get-Content -LiteralPath (Resolve-ChildPath $gameFull 'FamilyTime_Data\app.info'))
    if ($identity.Count -lt 2 -or $identity[0].Trim() -ne 'sgthale' -or $identity[1].Trim() -ne 'Family Time') { throw 'Game identity is not sgthale / Family Time.' }
    if (!(Test-Path -LiteralPath (Resolve-ChildPath $gameFull 'MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll'))) { throw 'Expected Mono runtime is missing.' }
    if ((Test-Path -LiteralPath (Resolve-ChildPath $gameFull 'GameAssembly.dll')) -or (Test-Path -LiteralPath (Resolve-ChildPath $gameFull 'FamilyTime_Data\il2cpp_data'))) { throw 'Ambiguous or IL2CPP backend; do not install Mono loader.' }
    $bytes = [IO.File]::ReadAllBytes($exe)
    $peOffset = [BitConverter]::ToInt32($bytes,60)
    if ([BitConverter]::ToUInt16($bytes,$peOffset+4) -ne 0x8664) { throw 'Expected Windows x64 executable.' }
    return $gameFull
}

function Assert-GameStopped([string]$GameDir) {
    $exe = Join-Path $GameDir 'FamilyTime.exe'
    foreach ($process in @(Get-Process -Name FamilyTime -ErrorAction SilentlyContinue)) {
        if (!$process.Path -or $process.Path.Equals($exe,[StringComparison]::OrdinalIgnoreCase)) { throw 'Close Family Time before changing its installed files.' }
    }
}

function Write-Manifest([string]$Path, $Manifest) {
    $parent = [IO.Path]::GetDirectoryName($Path)
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = $Path + '.tmp'
    $Manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Get-Dotnet {
    $portable = Join-Path $script:Workspace '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $portable) { return $portable }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw 'No .NET SDK found. Run scripts/setup-dependencies.ps1 first.'
}
