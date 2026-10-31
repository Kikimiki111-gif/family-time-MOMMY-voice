# Editable profile and non-secret examples

`wife.json` is the complete default `Persona` profile. Installation copies it to `BepInEx/config/FamilyTimeVoice/wife.json` only when no profile already exists. Its adult spouse identity, speaking style, boundaries, examples and additional instructions form the actual Realtime session instructions. `Provenance` records the reviewed motherly-style source; it is metadata, not sent as an instruction.

`local.familytime.wifevoice.example.cfg` shows all current non-secret BepInEx preferences. The active file is `BepInEx/config/local.familytime.wifevoice.cfg`. API keys must never be added to it. Enter the key in the masked local settings field; Windows DPAPI stores it for the current user.

`prices.example.json` shows the dated USD-per-million-token rate table. The active table is `%LOCALAPPDATA%/FamilyTimeVoice/prices.json`, beside `usage-ledger.json`. Verify the selected model's official rates before editing the active table. The model name must match the table; an unknown model cannot silently inherit the mini model's prices. Never delete the cumulative usage ledger to reset a spending cutoff.

Raw audio recording and transcript logging are off. Durable memory is a separate opt-in and does not trigger a paid summarization call; version 0.2 context behavior is described below.

Version 0.2 separates `Provider`, stock/custom voice identity, `VoiceDelivery`/accent, and the concise wife persona. Realtime remains available. Prompt-created A/B/C IDs and their original acoustic descriptions are saved locally in `BepInEx/config/FamilyTimeVoice/voices.json`; this account-specific cache is preserved, not distributed or regenerated on startup.

`live-prices.example.json` documents the separate dated Live-duration and optional Realtime-transcription tariff. The active `live-prices.json` is beside the local usage ledger. Live silence/mute time counts; voice creation is free, while audition playback uses paid Live duration. No automatic backend/model tariff is configured.

In 0.2, recent heard text and explicit preferences can bridge reconnects in memory. Persistent per-save memory remains opt-in and stores bounded conversation excerpts/preferences plus the manual note under hashed save IDs. Clearing memory clears derived summary/context too. Raw audio recording remains off. Realtime user transcription is a separate explicit paid opt-in; Live's native transcripts do not require that Realtime option.
