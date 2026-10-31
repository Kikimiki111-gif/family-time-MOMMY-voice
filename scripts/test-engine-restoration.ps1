param([string]$GameDir = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $taskRoot 'scripts\common.ps1')
$GameDir = Assert-Game $GameDir
$loaderCore = Join-Path $taskRoot '.tools\bepinex\BepInEx\core'
Add-Type -Path (Join-Path $loaderCore 'Mono.Cecil.dll')
Add-Type -Path (Join-Path $loaderCore 'BepInEx.dll')
Add-Type -Path (Join-Path $loaderCore 'BepInEx.Preloader.dll')
Add-Type -Path (Join-Path $taskRoot 'preloader\bin\Release\netstandard2.0\FamilyTimeVoice.EngineRestoration.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$engine = Join-Path $taskRoot '.tools\engine-runtime'
$managed = Join-Path $GameDir 'FamilyTime_Data\Managed'
@($engine, $managed, (Join-Path $GameDir 'BepInEx\FamilyTimeVoice-runtime'), $loaderCore) | ForEach-Object { $resolver.AddSearchDirectory($_) }
$patcher = [FamilyTimeVoice.Preloader.EngineRestoration]
$hidden = [Reflection.BindingFlags]'NonPublic,Static'
$patcher.GetField('resolver', $hidden).SetValue($null, $resolver)
$patcher.GetField('log', $hidden).SetValue($null, [BepInEx.Logging.ManualLogSource]::new('offline-engine-test'))
$preserve = $patcher.GetMethod('PreserveOriginalContracts', $hidden)
$modules = $patcher.GetField('Modules', $hidden).GetValue($null)
foreach ($name in $modules) {
    $parameters = [Mono.Cecil.ReaderParameters]::new()
    $parameters.AssemblyResolver = $resolver
    $original = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed $name), $parameters)
    $restored = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $engine $name), $parameters)
    try {
        $preserve.Invoke($null, @($original, $restored))
        foreach ($contract in $original.MainModule.Types | Where-Object IsInterface) {
            $replacement = $restored.MainModule.Types | Where-Object FullName -eq $contract.FullName
            $before = @($contract.Methods.FullName | Sort-Object)
            $after = @($replacement.Methods.FullName | Sort-Object)
            if (($before -join "`n") -ne ($after -join "`n")) { throw ('Interface contract changed: ' + $contract.FullName) }
        }
        $stream = [IO.MemoryStream]::new()
        try {
            $restored.Write($stream)
            if ($stream.Length -eq 0) { throw 'Empty restored assembly.' }
            Write-Output ('PASS original interface contracts and Cecil metadata write: ' + $name)
        } finally { $stream.Dispose() }
    } finally { $original.Dispose(); $restored.Dispose() }
}
Write-Output 'Seven restored modules passed offline contract/metadata checks; native runtime smoke is separate.'
