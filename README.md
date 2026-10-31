# Family Time MOMMY Voice 

An open-source BepInEx mod that adds voice conversation with Wolf Mama in **Family Time**. Talk through your microphone, choose a voice, and edit the character's personality in the game.

**Windows x64 · Unity 6000.2.12f1 · Mono · BepInEx 5.4.23.6**

[Download the latest release](https://github.com/Kikimiki111-gif/family-time-voice/releases/latest) · [Installation guide](docs/installation.md) · [Testing](docs/testing.md)

## What it does

- Push-to-talk conversation with the game's confirmed adult wife character.
- OpenAI Realtime stock voices and separate GPT-Live prompt-designed voices.
- Saved voice selection with a shared, editable character prompt.
- Original adult-wife personality with affectionate, suggestive teasing and optional pet names. Adjust it to your preference.
- Separate voice identity, vocal delivery, and conversational personality controls.
- Bounded recent context and explicit preferences across controlled reconnects; persistent memory is optional.
- Interrupt, end, volume/mute, microphone selection, and estimated spending cutoffs.
- No network connection or microphone capture starts simply by launching the game.

This is an unofficial mod. You need your own compatible game copy and your own OpenAI API key with access to the chosen model. API usage costs money. Game assets, Unity runtime libraries, API keys, account-specific voice IDs, saved conversations, and recordings are not included.

## Install the compiled release

1. Download `FamilyTimeVoice-0.2.2.zip` from Releases and extract it outside the game folder.
2. Close Family Time. Open PowerShell in the extracted folder.
3. Prepare the pinned dependencies, then install:

```powershell
.\scripts\setup-dependencies.ps1 -InstallOnly
.\scripts\install.ps1 -GameDir 'D:\Games\Family Time'
```

Replace the example path with the folder containing `FamilyTime.exe`. If omitted, the scripts try common local locations and ask when a valid location cannot be determined.

The game strips managed libraries needed by mods. Setup obtains matching engine wrappers and the official Unity 6000.2.12f1 Windows Editor dependency, about **4.12 GB to download** and roughly **8 GB installed**. Windows may ask for administrator approval. You do not need to launch or activate the Editor. If that exact Editor is already installed, use `-EditorDir 'D:\Unity\6000.2.12f1'`. No proprietary runtime binaries are redistributed in this release.

See the [installation guide](docs/installation.md) for compatibility, setup, removal, and rollback.

## Play

- **F8:** settings.
- **F9:** start/end conversation near Wolf Mama (default range: 8 metres).
- **Hold V:** speak; release to finish your input.

Open **F8 → Connection**, enter your own API key in the masked field, and choose **Save protected key**. The key is protected for your Windows user using DPAPI. An `OPENAI_API_KEY` environment variable takes precedence if you deliberately set one.

In **F8 → Voice**, use a stock Realtime voice or create a prompt-designed GPT-Live voice once using the explicit design tools. The supplied Warm (A), Cozy (B), and Playful (C) descriptions are original synthetic adult voices. Each player creates identities in their own API project; another person's private voice IDs are not shipped. Existing identities are reused. Voice creation is free; listening sessions are billed by the provider. Model availability and permissions depend on your account.

Voice selection saves immediately. Changing voice during a conversation closes the old session before one replacement starts with relevant bounded context. Selecting the same voice again does not reconnect.

During a **GPT-Live** reply, pressing V or choosing **Interrupt reply** stops current and queued speech through a supported session close/restart. **Release V, wait for Ready, then press V again to speak.** Speech during reconnect is not recorded. Realtime keeps its cancel/truncate behavior. F9/End cancels pending restarts.

## Edit the personality

Use **F8 → Character / memory → Edit conversation persona**, edit the single **Character instructions** box, then **Save settings and wife profile**. Every selected voice uses the same instructions on the next conversation start or reconnect.

The equivalent file is `BepInEx/config/FamilyTimeVoice/wife.json`, field `CharacterInstructions`. Voice delivery is edited separately on the Voice tab. The shipped `wife-core-v3.1` profile treats the player and wife as adults, grounds memories in supplied context, and respects corrections. It does not alter game actions or invent integration capabilities.

Recent context stays bounded in memory. Disk persistence is opt-in. Raw audio recording and transcript logging are off. Do not commit your installation's config, voice catalog, memories, key storage, or usage ledger to a public repository.

## Spending controls

The Usage tab provides estimated session, cumulative, and development cutoffs. Defaults are USD 0.50/session, USD 5.00 cumulative, and USD 0.25 development allowance. GPT-Live active time includes silence/muting; end unused sessions. Dated prices are stored locally and must match the model. Unknown final usage retains a conservative reservation. These controls track this mod, not your whole OpenAI account; use your account's billing controls as well.

## Build from source

Install the exact compatible game, clone this repository, then run:

```powershell
.\scripts\setup-dependencies.ps1
.\scripts\build.ps1 -GameDir 'D:\Games\Family Time'
.\tests\run.ps1 -GameDir 'D:\Games\Family Time'
.\scripts\package.ps1
```

The setup script supplies a pinned portable .NET SDK and dependencies. Builds reference your local game libraries; the source repository does not contain them. `setup-dependencies.ps1 -BuildOnly` prepares build dependencies without installing the Windows Editor, but a playable installation still needs the matching Windows runtime.

The full test suite uses local fixtures and does not spend API credit. Native audio and subjective voice quality require an in-game check. See [testing and known limits](docs/testing.md).

## License and credits

Project source is licensed under [MIT](LICENSE). Adapted code and bundled dependencies retain their own notices in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). UnityDoorstop remains separately licensed under LGPL 2.1; its corresponding source and license are included. The game, Unity libraries, and API services are not licensed by this project's MIT license.
