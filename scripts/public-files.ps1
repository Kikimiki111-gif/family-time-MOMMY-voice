# One allowlist shared by public source staging and release packaging.
function Get-PublicSourcePaths([string]$Root) {
    $result = New-Object 'System.Collections.Generic.List[string]'
    foreach ($relative in @('.gitignore','LICENSE','README.md','THIRD_PARTY_NOTICES.md','FamilyTimeVoice.csproj','docs\installation.md','docs\testing.md','docs\dependencies.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf)) { throw "Public source file missing: $relative" }
        $result.Add($relative)
    }
    foreach ($directory in @('src','profiles','scripts','preloader','tests','buildtools\JsonIsolation','buildtools\ReleaseVerifier','tests\LegacyLedgerBaseline')) {
        foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root $directory) -File | Where-Object { $_.Extension -in @('.cs','.csproj','.ps1','.json','.props','.md') -or $_.Name -like '*.example' })) {
            $result.Add($file.FullName.Substring($Root.TrimEnd('\','/').Length + 1))
        }
    }
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root 'third_party') -File -Recurse | Where-Object { $_.Name -match '(?i)^LICENSE(?:\..*)?$' })) {
        $result.Add($file.FullName.Substring($Root.TrimEnd('\','/').Length + 1))
    }
    $result.Add('third_party\UnityDoorstop-4.6.0\source-v4.6.0.zip')
    return @($result | Sort-Object -Unique)
}
