[CmdletBinding()]
param([string]$GameDir = '', [string]$RuntimeDir = '')
# The sandbox harness deliberately calls the real installer/uninstaller. It copies
# only the inspected fingerprint files into a fresh local temporary fixture and
# checks upgrade/refusal/rollback and unrelated-mod preservation. No paid API use.
& (Join-Path $PSScriptRoot '..\scripts\test-installation.ps1') -GameDir $GameDir -RuntimeDir $RuntimeDir
