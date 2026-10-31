# Installation and removal

## Supported game

Use a legally obtained Windows x64 Mono build of Family Time, Unity **6000.2.12f1**. The installer checks the executable and game assembly before changing mod-owned files:

- `FamilyTime.exe`: SHA256 `C859377F30EF31F9AAF8B558DE11E4804E10B50C0A6AB76C2060BA153F9019F8`
- `Assembly-CSharp.dll`: SHA256 `7AAFF30819FC4D4174B22DF52995489D595AFBA0A0ED4B4BC89ED0BDD008542F`

Other game builds, IL2CPP, Linux/macOS, and BepInEx 6 are unverified. Do not disable compatibility checks for a different build; review the adapter first.

## Compiled release

Extract the release outside the game directory. Close the game. Run PowerShell from the extracted folder:

```powershell
.\scripts\setup-dependencies.ps1 -InstallOnly
.\scripts\install.ps1 -GameDir 'D:\Games\Family Time'
```

The example directory is a placeholder. Supply the actual game folder or executable; the installer also supports the `FAMILY_TIME_GAME_DIR` environment variable and shallow detection of common local folders. It prompts only if a valid location cannot be determined.

Dependency preparation verifies pinned checksums. Matching proprietary engine wrappers are downloaded locally. Full Windows runtime libraries come from the signed official Unity 6000.2.12f1 Windows Editor installer (approximately 4.12 GB download / 8 GB disk). Windows may display a UAC prompt. Keep the tools cache for later reinstalls. An existing matching Editor can be reused:

```powershell
.\scripts\setup-dependencies.ps1 -InstallOnly -EditorDir 'D:\Unity\6000.2.12f1'
```

Setup does not launch or activate the Editor. Installation uses a separate Doorstop runtime directory and an in-memory wrapper restoration preloader; original game assemblies remain untouched. Compiled releases do not need a .NET SDK. Building from source uses normal dependency setup without `-InstallOnly`.

## First conversation

1. Start the game and load a save. Approach Wolf Mama within 8 metres.
2. Open F8 → Connection and save your own OpenAI API key in the masked field.
3. Choose a model/provider your project can access. Stock Realtime voices are available on the Voice tab. To use prompt-designed Live voices, explicitly create the desired saved identity once in your own project.
4. Review spending cutoffs. Press F9, wait for Ready, hold V while speaking, then release.
5. End with F9. For Live interruption, release V and wait for the replacement session to become Ready before speaking again.

Key storage: `%LOCALAPPDATA%/FamilyTimeVoice/api-key.dpapi`, protected for the current Windows account. Non-secret settings: `BepInEx/config/local.familytime.wifevoice.cfg`. Personality: `BepInEx/config/FamilyTimeVoice/wife.json`. The local `voices.json` catalog contains account/project-specific saved IDs and original descriptions; retain it when upgrading but do not share it as a public default.

Microphone access must already be allowed in Windows. Select the right device on the Audio tab. The free two-second offline microphone/audio check tests capture and the native playback path without network requests or saving a recording.

## Updates and rollback

Close the game and run the new release's installer. It preserves preferences, protected key, voice catalog, memory and spending history. Exact known shipped persona defaults may migrate with a timestamped backup; customized profiles remain intact.

The installer maintains `BepInEx/FamilyTimeVoice-install.json` and snapshots changed previous mod binaries under `BepInEx/FamilyTimeVoice-upgrades`. To restore the latest checked snapshot:

```powershell
.\scripts\rollback.ps1 -GameDir 'D:\Games\Family Time'
```

`-RestorePreferences` additionally restores snapshotted non-secret preferences, backing up the current copies. Keys, voice catalog, memory and cumulative usage are not rewound.

## Remove

```powershell
.\scripts\uninstall.ps1 -GameDir 'D:\Games\Family Time'
```

Removal checks recorded hashes and restores/removes only unchanged mod-owned files. Modified files and unrelated mods are preserved. Settings, key storage, saves, voice catalog and usage history remain. Do not delete the usage ledger to bypass a cutoff.

## Troubleshooting

Read `BepInEx/LogOutput.log` and the game's Unity `Player.log`. Share only relevant sanitized error lines; remove paths, keys, account-specific IDs and conversation text. Unsupported models, missing permissions, quota/network failures or unconfirmed usage stop the session without automatic paid retries. The game must be closed before installing or rolling back.
