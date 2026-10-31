# Third-party notices

The project's original code is MIT-licensed under LICENSE. Adapted code, runtime dependencies and build tools retain their respective notices below. Complete licenses are preserved under third_party. The game, Unity libraries and API services are not covered by the project's MIT license.

## Adapted source

- **OpenAI Realtime API for Unity**, Martin Pluisch, MIT: https://github.com/mapluisch/OpenAI-Realtime-API-for-Unity at `0540daaae2fa4463d437432244a1e1a0d9f49600`. PCM conversion in `src/AudioMath.cs` adapts `AudioProcessingUtils.cs`, adding explicit little-endian packing, clipping, NaN and partial-sample validation. License: `third_party/OpenAI-Realtime-API-for-Unity/LICENSE`. The prototype wrapper, recorder/player, FFT and VAD are not compiled or distributed as mod source.
- **MCP Emotional Support**, MIT: https://github.com/bnookala/mcp-emotional-support at `9a2ad19d7e4d05070aaecf442ed665343abd776c`. The historical 0.1 default adapted MotherlyPersona. Its exact migration-comparison strings remain in `src/Settings.cs`; the current `wife-core-v3.1` character instructions are user-supplied original text. No MCP server or canned-response generator is included. License: `third_party/mcp-emotional-support/LICENSE`.

## Runtime and build dependencies

| Component | Version | License file |
|---|---|---|
| BepInEx | 5.4.23.6 (`4865251c5251293dbd7040c1d8796d277857e47f`) | MIT: `third_party/BepInEx-5.4.23.6/LICENSE` |
| HarmonyX | 2.9.0 | MIT: `third_party/HarmonyX-2.9.0/LICENSE` and original Harmony `LICENSE.Harmony` |
| BepInEx.Harmony | `d4cdcb4cdeac14a0b77012165f5f5a9f5032a9fa` | MIT: `third_party/BepInEx.Harmony/LICENSE` |
| Mono.Cecil runtime | 0.10.4 | MIT: `third_party/Mono.Cecil-0.10.4/LICENSE.txt` |
| MonoMod.RuntimeDetour / Utils | 22.1.29.1 (`v22.01.29.01`) | MIT: `third_party/MonoMod-22.1.29.1/LICENSE` |
| Newtonsoft.Json | 13.0.3 (`0a2e291c0d9c0c7675d445703e51750363a549ef`) | MIT: `third_party/Newtonsoft.Json-13.0.3/LICENSE.md` |
| Mono.Cecil build tool | 0.11.6 | MIT: `third_party/Mono.Cecil-0.11.6/LICENSE.txt` |
| UnityDoorstop | 4.6.0 | LGPL 2.1: `third_party/UnityDoorstop-4.6.0/LICENSE.LGPL-2.1.txt` |

The release bundles the unmodified official BepInEx Windows x64 loader archive. Source locations: https://github.com/BepInEx/BepInEx, https://github.com/BepInEx/HarmonyX/tree/v2.9.0, https://github.com/BepInEx/BepInEx.Harmony, https://github.com/jbevain/cecil, https://github.com/MonoMod/MonoMod/tree/v22.01.29.01, and https://github.com/JamesNK/Newtonsoft.Json.

`buildtools/JsonIsolation` renames the official Newtonsoft.Json assembly to `FamilyTimeVoice.Json.dll` to avoid binding to the game's stripped JSON assembly. JSON classes and implementation remain unchanged. Original DLL SHA256: `8C1DD5C184B4E2E7EAD06971FF3EBCB46783BE972292D1DEB1061744369B4D80`.

## UnityDoorstop corresponding source

`winhttp.dll` from the unmodified loader is an independently replaceable shared library, licensed separately under LGPL 2.1. The release and source repository include its complete corresponding v4.6.0 source/build files and license in `third_party/UnityDoorstop-4.6.0/source-v4.6.0.zip`. Source: https://github.com/NeighTools/UnityDoorstop/tree/v4.6.0. Source archive SHA256: `95A3D62F5CB2FC792F1DFC61E6B09F78E685E0966401E6F9E6FA58540249E0EA`. That revision has no Git submodules. The mod does not restrict replacement of this library or debugging modifications to it. Keep this notice and corresponding source with redistributed loader copies.

## Downloaded proprietary dependencies

Unity engine modules and the full Windows managed runtime are downloaded locally and are not included in git or release ZIPs. Unity Technologies' licenses apply to those dependencies. Setup verifies the matching Unity 6000.2.12 wrapper archive from https://unity.bepinex.dev/libraries/6000.2.12.zip and the signed official Windows Editor installer at https://download.unity3d.com/download_unity/e89d5df0e333/Windows64EditorInstaller/UnitySetup64-6000.2.12f1.exe. Installation uses a separate runtime search override and an in-memory wrapper restoration preloader, leaving the original game's assemblies untouched. No Unity C# reference implementation or proprietary game assets are copied into project source.

Additional historical reference licenses may be retained under third_party; those do not imply an active production dependency.
