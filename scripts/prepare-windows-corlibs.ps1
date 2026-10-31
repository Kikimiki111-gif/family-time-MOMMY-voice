[CmdletBinding()]
param([string]$EditorDir = '')
. (Join-Path $PSScriptRoot 'common.ps1')
$toolsDir = Join-Path $script:Workspace '.tools'
$runtimeDir = Join-Path $toolsDir 'unity-windows-corlibs-6000.2.12'
$expectedCorlibHash = '44C8BA3B568C49A6A635680E72770CD9E3230369B60A357A37799D7921AC1539'
if (!$EditorDir -and (Test-Path -LiteralPath (Join-Path $runtimeDir 'manifest.json'))) {
    try {
        $cached = Get-Content -LiteralPath (Join-Path $runtimeDir 'manifest.json') -Raw | ConvertFrom-Json
        $healthy = $cached.unityVersion -eq '6000.2.12f1' -and $cached.platform -eq 'Windows' -and (Get-SHA256 (Join-Path $runtimeDir 'mscorlib.dll')) -eq $expectedCorlibHash
        foreach ($record in @($cached.files)) {
            $cachedFile = Resolve-ChildPath $runtimeDir $record.file
            if (!(Test-Path -LiteralPath $cachedFile) -or (Get-SHA256 $cachedFile) -ne $record.sha256) { $healthy = $false; break }
        }
        if ($healthy -and @($cached.files).Count -eq 15) { Write-Host 'Verified matching Windows core libraries are already prepared.'; return }
    } catch { Write-Host 'Cached Windows core libraries need to be prepared again.' }
}
if (!$EditorDir) {
    $EditorDir = Join-Path $toolsDir 'UnityEditor6000.2.12'
    if (!(Test-Path -LiteralPath (Join-Path $EditorDir 'Editor\Data\MonoBleedingEdge'))) {
        foreach ($programFiles in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
            if (!$programFiles) { continue }
            $hubEditor = Join-Path $programFiles 'Unity\Hub\Editor\6000.2.12f1'
            if (Test-Path -LiteralPath (Join-Path $hubEditor 'Editor\Data\MonoBleedingEdge')) { $EditorDir = $hubEditor; break }
        }
    }
}
$EditorDir = [IO.Path]::GetFullPath($EditorDir)
New-Item -ItemType Directory -Force -Path $toolsDir,$runtimeDir | Out-Null
$monoRoot = Join-Path $EditorDir 'Editor\Data\MonoBleedingEdge\lib\mono'
$candidateDirs = @('unityjit-win32','net_4_x-win32') | ForEach-Object { Join-Path $monoRoot $_ }
$corlibSource = $candidateDirs | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'mscorlib.dll') } | Select-Object -First 1
if (!$corlibSource) {
    $archive = Join-Path $toolsDir 'UnitySetup64-6000.2.12f1.exe'
    $url = 'https://download.unity3d.com/download_unity/e89d5df0e333/Windows64EditorInstaller/UnitySetup64-6000.2.12f1.exe'
    if (!(Test-Path -LiteralPath $archive)) {
        Write-Host 'Downloading the exact official Windows Editor (approximately 4.12 GB) to obtain matching full Windows core libraries.'
        Invoke-WebRequest -Uri $url -OutFile $archive
    }
    # MD5 is the published Unity release integrity value; Authenticode additionally
    # verifies the signed publisher. No downloaded installer is run before both.
    if ((Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash -ne 'A06E21AFFA4DBB766D9CF3CAB1122AE4') { throw 'Official Unity Editor release checksum mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $archive
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Unity Technologies') { throw 'Official Unity Editor signature did not validate.' }
    Write-Host 'Installing only the standalone official Editor dependency to the local tools folder; Windows may require UAC approval. No Hub, IDE, license activation or Editor launch is needed.'
    $process = Start-Process -FilePath $archive -ArgumentList @('/S',('/D='+$EditorDir)) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -notin @(0,3010)) { throw "Unity Editor dependency installation failed: $($process.ExitCode)" }
    $corlibSource = $candidateDirs | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'mscorlib.dll') } | Select-Object -First 1
    if (!$corlibSource) { throw 'The installed exact Windows Editor contains no expected full core library directory.' }
}
if ((Get-SHA256 (Join-Path $corlibSource 'mscorlib.dll')) -ne $expectedCorlibHash) { throw 'The selected Editor does not contain the verified Unity 6000.2.12f1 Windows core library. Supply the exact version with -EditorDir.' }
$names = @('Microsoft.CSharp.dll','Mono.Posix.dll','Mono.Security.dll','mscorlib.dll','netstandard.dll','System.Configuration.dll','System.Core.dll','System.Data.dll','System.dll','System.Net.Http.dll','System.Numerics.dll','System.Runtime.Serialization.dll','System.Security.dll','System.Xml.dll','System.Xml.Linq.dll')
$files = @()
foreach ($name in $names) {
    $source = Join-Path $corlibSource $name
    if (!(Test-Path -LiteralPath $source)) { $source = Join-Path $corlibSource ('Facades\' + $name) }
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $runtimeDir $name) -Force
        $files += [pscustomobject]@{file=$name;sha256=Get-SHA256 $source}
    }
}
if (!(Test-Path -LiteralPath (Join-Path $runtimeDir 'mscorlib.dll')) -or !(Test-Path -LiteralPath (Join-Path $runtimeDir 'netstandard.dll'))) { throw 'Full Windows core libraries are incomplete.' }
[pscustomobject]@{unityVersion='6000.2.12f1';platform='Windows';sourceDirectory=$corlibSource;sourceInstaller='https://download.unity3d.com/download_unity/e89d5df0e333/Windows64EditorInstaller/UnitySetup64-6000.2.12f1.exe';publishedInstallerMD5='A06E21AFFA4DBB766D9CF3CAB1122AE4';files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtimeDir 'manifest.json') -Encoding UTF8
Write-Host "Matching full Windows core libraries prepared: $runtimeDir"
Write-Host 'Game Managed assemblies remain untouched. Installation uses a separate Doorstop runtime search override.'
