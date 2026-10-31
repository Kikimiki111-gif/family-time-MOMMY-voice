# Version 0.1.0 accounting regression fixture

These are exact copies of this mod's original `UsageLedger.cs` and `Settings.cs`, preserved before the voice/personality upgrade. They are our mod source, not proprietary game code. The small project builds only these original files to exercise old-version JSON read/write behavior on temporary fixtures. It performs no networking, key access or game actions.

- UsageLedger SHA256: `7748A5309C835D086560EDE7C0CEBF0F419BDE58093D76676426D313072F86CB`.
- Settings SHA256: `BAF5B1F7BD4C759A702DC5D4E41BD0FEEC1073FA6173499D590ABB0EB09FC274`.
- Settings source retains its original profile attribution; the release's `THIRD_PARTY_NOTICES.md` applies.

`UpgradeUsageTests` prefers the actual preserved installed 0.1.0 plugin when available locally, and otherwise uses this source-built original accounting assembly. The regression verifies that rollback cannot erase Live/backend/transcription costs or bypass the cumulative limit, even when the old serializer drops new metadata fields.
