# Tests and known limits

The mod targets one inspected Windows x64 Unity 6000.2.12f1 Mono build. Automated checks and compilation do not establish subjective naturalness, microphone audibility, or support for other game versions.

## Offline verification

After building Release:

```powershell
.\tests\run.ps1 -GameDir 'D:\Games\Family Time'
```

The suite covers PCM conversion and resampling, bounded playback and truncation, provider-specific wire events, stale callback rejection, cancellation/reconnect serialization, exact saved voice selection and BepInEx persistence, the shared editable persona, bounded heard context/preferences, audibility accounting, USD reservations, secret-safe diagnostics, restored-engine metadata, and installation/rollback fixtures. Fixtures do not load a credential or make paid API requests.

The saved-voice host tests use synthetic IDs. They invoke the actual plugin selection methods and actual Live start-event builder, ensuring that each selected ID/description and shared character core reach the next session payload. Real project-specific IDs are intentionally excluded from source and fixtures.

## In-game verification

The existing mod has been exercised in the supported game: wife targeting, save/load, microphone conversation, controls and conversation context were checked during development. The 0.2.1 startup/profile migration and updated settings UI were checked in the actual game. Physical listening/interruption for every later prompt revision is not implied by that history. The 0.2.2 public-release result is recorded in the release notes.

For a local check:

1. Load a save, approach Wolf Mama, and run the free offline Audio check.
2. Save your own key and choose a voice. Check that the saved choice survives a game restart.
3. Start a short conversation with F9 and V. Try a follow-up using an established preference.
4. Interrupt a reply and confirm current and queued speech stop. With Live, release V, wait for Ready, and press again to speak.
5. Change voice during a conversation and check continuity. End with F9 and confirm normal movement/input is restored.

Live comparisons spend API credit and must fit your configured limits. No automated tests claim to hear audio or judge which voice sounds best.

## Limits

- Windows x64 / Mono / exact game fingerprint only; other builds and platforms are unverified.
- Prompt-generated voices work only with GPT-Live and require access in your own OpenAI project.
- GPT-Live hard interruption uses a supported session close/start, which has reconnect latency. It cannot use Realtime's per-response cancellation messages.
- Persistent memory is optional and bounded; this mod is not a full transcript archive. Cancelled/unheard assistant speech is conservatively omitted.
- User captions for Realtime reconnect memory require the separate opt-in paid transcription option. Live supplies native captions.
- Hands-free echo/interruption and subjective voice quality depend on the microphone, speakers/headphones and selected provider.
- Spending controls are conservative local estimates, not an account-wide billing cap. Unknown usage retains a reservation.
- No proprietary game or Unity runtime libraries, private keys, actual voice IDs, conversations or recordings are distributed.
