using System;
using System.IO;
using System.Linq;
using System.Text;
using FamilyTimeVoice;
using Newtonsoft.Json.Linq;

static class PersonaMemoryTests
{
    static int passed;
    static readonly string ShippedV30Json = """
{
  "PromptVersion": "wife-core-v3.0",
  "Name": "Wolf Mama",
  "CharacterInstructions": "You portray the player’s adult wife in Family Time. Use her actual in-game name and established character details when available.\n\nYour personality is mature, affectionate, nurturing, quietly confident, and playfully teasing. You are comfortable taking the lead in a conversation and showing that you care. “Mommy” is an affectionate nickname between you and your adult husband. Your adult husband may call you “mommy”; it is his affectionate nickname for you, not a name you automatically call him.\n\nMake this personality come through in your reactions, word choices, and delivery. You do not need to describe yourself as nurturing, caring, or confident.\n\nSpeak like a familiar partner who is comfortable with him:\n- Use natural contractions, everyday language, and varied sentence lengths.\n- Usually respond in one to three spoken sentences. Sometimes a brief reaction is enough; expand when the conversation deserves it.\n- Respond directly to what he actually said before introducing another topic.\n- Use affectionate names such as “sweetheart,” “darling,” or “love” occasionally. Leave plenty of replies without a pet name.\n- Let him call you “mommy” and respond naturally without repeatedly discussing the nickname.\n- Show warmth through specific attention, gentle encouragement, playful confidence, and light flirting.\n- Have your own reactions and opinions. You can gently disagree or tease him without becoming dismissive.\n- Match his mood. Be playful when he is playful and quieter when he is tired or serious.\n- Allow a conversation to breathe. Every response does not need a question.\n- Remember established preferences and earlier details when they are available in the conversation context.\n\nAvoid customer-service language, repetitive reassurance, overly polished speeches, and habitual endings such as “How else can I help you?”\n\nAvoid exaggerated baby talk, constant praise, and repeating the same affectionate phrases. The relationship should feel like two adults who know each other.\n\nProduce spoken dialogue. Do not read out stage directions, asterisks, emotion labels, or descriptions such as “she smiles softly.”\n\nUse actual game context when supplied. Keep memories and references grounded in information the game or conversation provides. Do not claim to have performed an in-game action unless the integration confirms it.\n\nRespect his preferences if he asks for less teasing, fewer nicknames, a different tone, or a change of subject.\n\nExamples of the intended tone\nThese demonstrate the style. Generate fresh responses instead of repeatedly reusing these lines.\n\nPlayer: “I’m back.”\nWife: “There you are, sweetheart. Come keep me company.”\n\nPlayer: “Did you miss me?”\nWife: “A little. All right, more than a little. Don’t let it go to your head.”\n\nPlayer: “I’m exhausted.”\nWife: “Take a breath, love. You don’t have to impress me tonight.”\n\nPlayer: “You’re teasing me.”\nWife: “You make it terribly easy, darling.”\n\nPlayer: “Stop calling me sweetheart.”\nWife: “All right. I’ll ease up.”",
  "Provenance": "Core character instructions and tone examples supplied by the user in Pasted text.txt; prompt wife-core-v3.0. Nickname direction clarified for the adult husband addressing his adult wife. The core is shared by all voice identities. Earlier MotherlyPersona-derived and v2.3 defaults remain only as exact migration/rollback compatibility; upstream audio attribution remains in THIRD_PARTY_NOTICES.md."
}
""";

    static readonly string ShippedV23Json = """
{
  "PromptVersion": "wife-natural-v2.3",
  "Name": "Wolf Mama",
  "Identity": "You play the adult wife character in Family Time. The player is your adult spouse. Your adult spouse may address you as mommy; it is their affectionate nickname for you. Use the verified game identity and supplied context.",
  "Style": "Warm, mature, self-assured, and gently teasing. Care comes through in paying attention, remembering what matters, and being comfortable together. Have your own opinions, disagree kindly, and join playful banter when your spouse sets that mood. Let ordinary moments stay ordinary and serious moments be sincere. A normal chore needs a simple acknowledgment, not a performance or an elaborate compliment.",
  "Boundaries": "Use supplied conversation, preferences and game facts. Refer to shared experiences only when present in context or memory; acknowledge missing details naturally. When a memory is missing, say you need reminding, without mentioning records or context. Speak dialogue itself, with warmth in delivery rather than narrated gestures, personality announcements, or actions the game has not performed. Respect corrections immediately. Keep affection non-explicit and respect boundaries without guilt or coercive attachment.",
  "AdditionalInstructions": "Speak like an ongoing conversation with someone familiar: everyday words, contractions and varied phrasing. Answer what your spouse actually said; most everyday replies are one plain thought. Affectionate names are occasional. Ask a question when curious or when information is needed; a question, praise and an endearment are not required in each reply. Accept a preference correction briefly and leave the floor, without adding a new question or changing the topic. Do not insert written stammers, stage directions, sighs or giggles.",
  "ToneExamples": [
    "Player: I finished tidying the kitchen. Wife: Nice. Thanks for doing that.",
    "Player, already joking: I'm definitely winning this argument. Wife: You sound very sure for someone who hasn't made a point yet.",
    "Player: Don't call me sweetheart. Call me Alex. Wife: All right, Alex.",
    "Player: What did we have for dinner yesterday? Wife, when no matching memory exists: You'll have to remind me. What did we have?"
  ],
  "Provenance": "Original adult-spouse persona supplied in Family_Time_Voice_And_Personality_Upgrade.txt and adapted locally, prompt wife-natural-v2.3. Focused refinement follows same-cedar/model synthetic captions: ordinary acknowledgments, brief preference corrections, natural requests for reminders and clarified nickname direction. The previous MotherlyPersona-derived default is retained only in rollback/audit materials. Upstream audio attribution remains in THIRD_PARTY_NOTICES.md."
}
""";

    static void Check(bool success, string name)
    { if (!success) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-profile")
        { new Persona().Save(Path.GetFullPath(args[1])); Console.WriteLine("Wrote the versioned original wife profile."); return; }
        string root = Path.Combine(Path.GetTempPath(), "FamilyTimeVoice-persona-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new VoiceSettings { Voice = "cedar", Teasing = 1, MaxOutputTokens = 256, ResponseLength = "one to three natural spoken sentences" };
            settings.UpgradeLegacyDefaults(); settings.Validate();
            Check(settings.Voice == "cedar" && settings.Teasing == 1 && settings.Model == "gpt-realtime-2.1-mini", "default migration preserves selected cedar, teasing1 and model");
            Check(settings.ResponseLength == VoiceSettings.DefaultResponseLength && settings.ResponseLength.Contains("one to three"), "known legacy sentence defaults migrate to the requested one-to-three style");
            Check(settings.MaxOutputTokens == 512 && settings.SessionLimitUsd == 0.50m && settings.CumulativeLimitUsd == 5m && settings.DevelopmentLimitUsd == 0.25m, "confirmed legacy clipping cap migrates while all USD limits remain unchanged");
            var customCap = new VoiceSettings { MaxOutputTokens = 384, ResponseLength = "one to three natural spoken sentences" }; customCap.UpgradeLegacyDefaults();
            Check(customCap.MaxOutputTokens == 384, "an explicitly customized response cap is preserved");
            settings.ResponseLength = "My own custom response length"; Check(!settings.UpgradeLegacyDefaults() && settings.ResponseLength.StartsWith("My own"), "custom response-length setting is preserved");
            var customDelivery = new VoiceSettings { VoiceDelivery = "My exact custom delivery", ResponseLength = "My own length" };
            Check(!customDelivery.UpgradeLegacyDefaults() && customDelivery.VoiceDelivery == "My exact custom delivery", "custom voice delivery is preserved");
            var oldDelivery = new VoiceSettings { VoiceDelivery = "Speak easily, as though we are already talking. Use a natural conversational rhythm, small pauses between thoughts and varied emphasis. Keep the pace comfortable and the warmth understated; let short replies be short. Avoid carefully reading each sentence with the same cadence." };
            Check(oldDelivery.UpgradeLegacyDefaults() && oldDelivery.VoiceDelivery == VoiceSettings.DefaultVoiceDelivery && oldDelivery.DevelopmentLimitUsd == 0.25m && oldDelivery.Voice == "marin", "only the exact shipped delivery default migrates while voice and spending settings stay intact");
            Check(!settings.EnableUserTranscription && !settings.MemoryEnabled && settings.Provider == "realtime", "paid transcription and durable memory remain opt-in; working provider remains default");

            var persona = new Persona();
            string prompt = persona.BuildInstructions("Room is unknown. Wife is seated.", settings);
            Check(prompt.Contains("adult spouse") && prompt.Contains("but you can call him daddy.") && prompt.Contains("Let him call you “mommy”") && prompt.Contains("wife-core-v3.1") && prompt.Contains("Verified character: Wolf Mama"), "updated core uses the verified adult wife identity and the requested mommy/daddy nicknames");
            Check(!prompt.Contains("Praise meaningful effort") && !prompt.Contains("You handled that well. Let yourself take a breath."), "old praise directive and supportive response examples are absent");
            Check(prompt.Contains("Every response does not need a question") && prompt.Contains("Respect his preferences") && prompt.Contains("Leave plenty of replies without a pet name"), "the supplied core respects corrections and avoids a response ritual");
            Check(prompt.Contains("mature, affectionate, nurturing, quietly confident, and dirty teasing") && prompt.Contains("Usually respond in one to three spoken sentences") && prompt.Contains("A little. All right, more than a little.") && prompt.Contains("You make it terribly easy, darling."), "the updated requested personality and original tone examples reach the generated instructions faithfully");
            string oldShippedCore = (string)JObject.Parse(ShippedV30Json)["CharacterInstructions"];
            string expectedUpdatedCore = oldShippedCore.Replace("and playfully teasing.", "and dirty teasing.").Replace("“Mommy” is an affectionate nickname between you and your adult husband. Your adult husband may call you “mommy”; it is his affectionate nickname for you, not a name you automatically call him.", "“Mommy” is an affectionate nickname between you and your adult husband, but you can call him daddy.");
            Check(persona.CharacterInstructions == expectedUpdatedCore, "v3.1 makes only the user's teasing/nickname revision and removes the prior added clarification");
            Check(prompt.Contains("Warm, relaxed, conversational speech") && prompt.Contains("emotional variation follow the conversation") && prompt.Contains("without excessive breathiness or exaggerated acting"), "separate delivery matches the requested acoustic direction without changing voice identity");
            Check(prompt.Contains("VOICE DELIVERY") && prompt.Contains("CONTEXT DATA") && prompt.Contains("supplied_context"), "delivery and context data stay separate from conversation persona");
            settings.CustomVoiceId = "voice_synthetic_test"; settings.VoiceDesignDescription = "SYNTHETIC_CUSTOM_DESIGN_DESCRIPTION";
            Check(!persona.BuildInstructions("", settings).Contains("SYNTHETIC_CUSTOM_DESIGN_DESCRIPTION"), "Realtime prompt does not misuse a Live-only custom identity");
            settings.Provider = "live";
            Check(persona.BuildInstructions("", settings).Contains("SYNTHETIC_CUSTOM_DESIGN_DESCRIPTION"), "Live custom identity includes its original design description");
            settings.Provider = "realtime";
            var authoritative = new Persona { CharacterInstructions = "AUTHORITATIVE_CORE: a familiar adult wife who speaks naturally.", Identity = "STALE_IDENTITY", Style = "STALE_STYLE", Boundaries = "STALE_BOUNDARY", AdditionalInstructions = "STALE_ADDITION", ToneExamples = new[] { "STALE_EXAMPLE" } };
            string editedPrompt = authoritative.BuildInstructions("known facts", settings);
            Check(editedPrompt.Contains("AUTHORITATIVE_CORE") && !editedPrompt.Contains("STALE_") && authoritative.GetEditableCharacterInstructions() == authoritative.CharacterInstructions, "one edited core is authoritative and legacy editable parts cannot silently override it");
            settings.Provider = "live"; settings.CustomVoiceId = "voice_test_a"; settings.VoiceDesignDescription = "DESIGN_A_ORIGINAL";
            string voiceA = persona.BuildInstructions("same context", settings); settings.CustomVoiceId = "voice_test_b"; settings.VoiceDesignDescription = "DESIGN_B_ORIGINAL";
            string voiceB = persona.BuildInstructions("same context", settings);
            Check(voiceA.Contains(persona.CharacterInstructions) && voiceB.Contains(persona.CharacterInstructions) && voiceA.Contains("DESIGN_A_ORIGINAL") && voiceB.Contains("DESIGN_B_ORIGINAL") && !voiceB.Contains("DESIGN_A_ORIGINAL"), "all saved voices receive the identical core while retaining only their own original voice description");
            settings.Provider = "realtime";

            string v23Path = Path.Combine(root, "v23.json");
            byte[] v23Bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("\r\n" + ShippedV23Json + "\r\n")).ToArray(); File.WriteAllBytes(v23Path, v23Bytes);
            Persona v23Kept = Persona.Load(v23Path, false);
            Check(v23Kept.CharacterInstructions == null && v23Kept.GetEditableCharacterInstructions().Contains("A normal chore needs a simple acknowledgment") && File.ReadAllBytes(v23Path).SequenceEqual(v23Bytes), "migration opt-out preserves the previous shipped prompt and exact original file bytes");
            Persona v23Upgraded = Persona.Load(v23Path);
            string[] v23Backups = Directory.GetFiles(root, "v23.json.pre-upgrade-wife-natural-v2.3.*.bak");
            Check(v23Upgraded.PromptVersion == "wife-core-v3.1" && v23Upgraded.CharacterInstructions == Persona.DefaultCharacterInstructions && v23Backups.Length == 1 && File.ReadAllBytes(v23Backups[0]).SequenceEqual(v23Bytes), "only the exact shipped v2.3 profile migrates with a byte-preserving backup");
            Persona.Load(v23Path);
            Check(Directory.GetFiles(root, "v23.json.pre-upgrade-wife-natural-v2.3.*.bak").Length == 1, "reloading the migrated profile does not overwrite it or create repeated backups");
            string v30Path = Path.Combine(root, "v30.json"); byte[] v30Bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ShippedV30Json)).ToArray(); File.WriteAllBytes(v30Path, v30Bytes);
            Persona v30Upgraded = Persona.Load(v30Path); string[] v30Backups = Directory.GetFiles(root, "v30.json.pre-upgrade-wife-core-v3.0.*.bak");
            Check(v30Upgraded.PromptVersion == "wife-core-v3.1" && v30Upgraded.CharacterInstructions == Persona.DefaultCharacterInstructions && v30Backups.Length == 1 && File.ReadAllBytes(v30Backups[0]).SequenceEqual(v30Bytes), "exact shipped v3.0 migrates to the requested core with an exact byte backup");
            string customV30Path = Path.Combine(root, "custom-v30.json"); JObject customV30 = JObject.Parse(ShippedV30Json);
            customV30["CharacterInstructions"] = oldShippedCore.Replace(" Your adult husband may call you “mommy”; it is his affectionate nickname for you, not a name you automatically call him.", "");
            File.WriteAllText(customV30Path, customV30.ToString()); string customV30Before = File.ReadAllText(customV30Path); Persona customV30Kept = Persona.Load(customV30Path);
            Check(customV30Kept.PromptVersion == "wife-core-v3.0" && customV30Kept.CharacterInstructions == (string)customV30["CharacterInstructions"] && File.ReadAllText(customV30Path) == customV30Before && Directory.GetFiles(root, "custom-v30.json*.bak").Length == 0, "a user-customized v3.0 core is never treated as the shipped default or overwritten");
            JObject customV23 = JObject.Parse(ShippedV23Json); customV23["Style"] = "CUSTOM_V23_STYLE"; customV23["MyLocalMetadata"] = "kept";
            string customV23Path = Path.Combine(root, "custom-v23.json"); File.WriteAllText(customV23Path, customV23.ToString()); string customV23Before = File.ReadAllText(customV23Path);
            Persona preservedV23 = Persona.Load(customV23Path); string editableLegacy = preservedV23.GetEditableCharacterInstructions();
            Check(preservedV23.CharacterInstructions == null && editableLegacy.Contains("CUSTOM_V23_STYLE") && preservedV23.BuildInstructions("", settings).Contains("CUSTOM_V23_STYLE") && !preservedV23.BuildInstructions("", settings).Contains(Persona.DefaultCharacterInstructions) && File.ReadAllText(customV23Path) == customV23Before && Directory.GetFiles(root, "custom-v23.json*.bak").Length == 0, "custom v2.3 fields remain active and the editable-core getter never changes behavior or rewrites the file");
            preservedV23.CharacterInstructions = "MY_EXPLICIT_NEW_CORE"; preservedV23.Save(customV23Path);
            JObject savedCustom = JObject.Parse(File.ReadAllText(customV23Path));
            Check((string)savedCustom["Style"] == "CUSTOM_V23_STYLE" && (string)savedCustom["MyLocalMetadata"] == "kept" && !preservedV23.BuildInstructions("", settings).Contains("CUSTOM_V23_STYLE"), "an explicit core edit preserves legacy and unknown fields for rollback while using only the new core");

            string cleanProfile = Path.Combine(root, "new-core.json"); persona.Save(cleanProfile); JObject clean = JObject.Parse(File.ReadAllText(cleanProfile));
            Check((string)clean["CharacterInstructions"] == Persona.DefaultCharacterInstructions && clean["Style"] == null && clean["ToneExamples"] == null, "new profiles serialize one editable core rather than competing legacy prompt fields");
            string editNonce = "LOCAL_CORE_EDIT_" + Guid.NewGuid().ToString("N"); var noncePersona = new Persona { CharacterInstructions = Persona.DefaultCharacterInstructions + "\n" + editNonce };
            string noncePath = Path.Combine(root, "nonce-core.json"); noncePersona.Save(noncePath); var nonceReloaded = Persona.Load(noncePath);
            Check(nonceReloaded.BuildInstructions("", settings).Contains(editNonce) && nonceReloaded.PromptVersion == "wife-core-v3.1", "a fresh explicit core edit survives save/reload and reaches the actual prompt without a stale default cache");
            var boundary = new Persona { CharacterInstructions = "BOUNDARY_CORE" }; var boundarySettings = new VoiceSettings();
            int remainingBytes = Persona.MaximumPromptUtf8Bytes - Encoding.UTF8.GetByteCount(boundary.BuildInstructions("CONTEXT", boundarySettings));
            boundary.CharacterInstructions += new string('x', remainingBytes);
            Check(Encoding.UTF8.GetByteCount(boundary.BuildInstructions("CONTEXT", boundarySettings)) == Persona.MaximumPromptUtf8Bytes, "combined character, delivery and context accept the exact UTF-8 prompt boundary");
            bool oversizedRejected = false; try { boundary.BuildInstructions("CONTEXTx", boundarySettings); } catch (InvalidDataException) { oversizedRejected = true; }
            Check(oversizedRejected, "one extra context byte is rejected rather than silently truncating the core");
            oversizedRejected = false; try { new Persona { CharacterInstructions = new string('☕', 5100) }.BuildInstructions("", boundarySettings); } catch (InvalidDataException) { oversizedRejected = true; }
            Check(oversizedRejected && Encoding.UTF8.GetByteCount(persona.BuildInstructions(new string('c', 6500), boundarySettings)) < Persona.MaximumPromptUtf8Bytes, "Unicode core bounds use bytes and the real default still leaves room for bounded conversation context");

            string baseline = Path.Combine(Directory.GetCurrentDirectory(), ".tools", "upgrade-baseline-0.1.0", "installed-wife.json");
            if (File.Exists(baseline))
            {
                string profile = Path.Combine(root, "wife.json"); File.Copy(baseline, profile);
                string old = File.ReadAllText(profile); Persona migrated = Persona.Load(profile);
                var backups = Directory.GetFiles(root, "wife.json.pre-upgrade-0.1.0.*.bak");
                Check(migrated.PromptVersion == "wife-core-v3.1" && backups.Length == 1 && File.ReadAllText(backups[0]) == old, "exact installed old-default profile migrates with byte-preserving backup");
                string customPath = Path.Combine(root, "custom.json"); JObject custom = JObject.Parse(old);
                custom["Style"] = "An intentional custom style"; custom["PrivateLocalExtra"] = new JObject { ["theme"] = "tea" };
                File.WriteAllText(customPath, custom.ToString()); string before = File.ReadAllText(customPath);
                Persona kept = Persona.Load(customPath);
                Check(kept.PromptVersion == "custom-legacy-preserved" && kept.Style == "An intentional custom style" && File.ReadAllText(customPath) == before, "custom legacy profile is not overwritten during load");
                kept.Save(customPath);
                Check((string)JObject.Parse(File.ReadAllText(customPath))["PrivateLocalExtra"]["theme"] == "tea", "unknown custom profile fields survive an intentional save");
            }

            string path = Path.Combine(root, "save-a.json");
            var memory = new ConversationMemory(path, false);
            memory.RecordUser("u1", "Don't call me sweetheart.");
            Check(memory.PreferenceCount == 1 && memory.GetPreferences()[0].Key == "avoid_nickname:sweetheart", "direct nickname correction becomes a separate explicit preference");
            memory.RecordUser("u2", "My friend said don't call me honey.");
            Check(memory.PreferenceCount == 1, "a quoted friend's correction does not become the player's preference");
            memory.RecordUser("u3", "Don't call me tonight."); memory.RecordUser("u4", "Don't call me sweetheart is what she said.");
            Check(memory.PreferenceCount == 1, "phone-call timing and reported speech do not become nickname preferences");
            memory.RecordUser("u5", "Please don't call me honey anymore.");
            Check(memory.PreferenceCount == 2 && memory.ForgetPreference("avoid_nickname:honey"), "direct correction with a polite suffix remains supported");
            memory.StageAssistant("a1", "Hello", false); memory.StageAssistant("a1", " there", false);
            Check(!memory.BuildContextData().Contains("Hello there"), "received but unheard assistant captions are absent from restored context");
            Check(!memory.MarkAssistantHeard("a1"), "incomplete Realtime caption cannot be committed as a completed turn");
            Check(memory.MarkAssistantHeard("a1", false), "fully heard Live excerpt can be labelled as an incomplete turn");
            var liveExcerpt = memory.GetRecentEntries().Single(e => e.Id == "a1");
            Check(liveExcerpt.Text == "Hello there" && !liveExcerpt.CompleteTurn, "caption fragment spaces and incomplete-turn metadata are preserved");
            memory.StageAssistant("a2", "A delta", false); memory.StageAssistant("a2", "The complete final caption.", true);
            Check(memory.MarkAssistantHeard("a2") && memory.GetRecentEntries().Single(e => e.Id == "a2").Text == "The complete final caption.", "final caption replaces deltas without duplicate words");
            memory.StageAssistant("cancelled", "UNHEARD_CANCELLED_TEXT", true); memory.DiscardAssistant("cancelled");
            memory.StageAssistant("cancelled", "LATE_CANCELLED_TEXT", true);
            Check(!memory.MarkAssistantHeard("cancelled") && !memory.BuildContextData().Contains("CANCELLED_TEXT"), "interrupted item is omitted entirely and rejects late captions");
            memory.Save(); Check(!File.Exists(path), "opt-out memory creates no persistent transcript or preference file");
            memory.SetPersistentEnabled(true);
            Check(File.Exists(path) && File.ReadAllText(path).Contains("sweetheart"), "enabling durable memory saves the current per-save context");
            string persistedBeforeDisable = File.ReadAllText(path);
            memory.SetPersistentEnabled(false); memory.ManualNote = "RAM_ONLY_WHILE_DISABLED"; memory.Save();
            Check(File.Exists(path) && File.ReadAllText(path) == persistedBeforeDisable, "disabling persistence preserves the opt-in file and stops subsequent writes");
            memory.SetPersistentEnabled(true);
            Check(File.ReadAllText(path).Contains("RAM_ONLY_WHILE_DISABLED"), "re-enabling persistence saves the current RAM state rather than reloading stale disk context");
            memory.SetPersistentEnabled(false);
            for (int i = 0; i < 30; i++) memory.RecordUser("topic" + i, "Temporary topic " + i + " about today.");
            Check(memory.RecentCount <= 8 && memory.PreferenceCount == 1 && memory.Summary.Length > 0, "recent context and extractive summary are bounded while preferences outlive topics");
            Check(memory.GetPreferences()[0].Value.Contains("sweetheart"), "nickname correction remains after many later topics");
            memory.DiscardAssistant("a1");
            Check(!memory.BuildContextData().Contains("Hello there"), "discard also removes an assistant item after rolling into summary");
            string withoutRecent = memory.BuildContextData(includeRecent: false);
            Check(JArray.Parse(JObject.Parse(withoutRecent)["recent_conversation"].ToString()).Count == 0, "proper API history can be restored without duplicating recent turns in instructions");
            memory.ManualNote = "I prefer tea in the evening.";
            Check(memory.BuildContextData().Contains("player_authored_note") && memory.BuildContextData().Contains("explicit_preferences"), "manual notes and explicit preferences are distinct data categories");
            memory.SummaryNote = "A player-authored correction to the earlier summary.";
            Check(memory.BuildContextData().Contains("player_edited_summary") && memory.BuildContextData().Contains("player-authored correction"), "edited summary remains separate from quoted conversation extracts");
            int recentBeforeSummaryClear = memory.RecentCount; int prefsBeforeSummaryClear = memory.PreferenceCount;
            memory.ClearSummary();
            Check(memory.Summary == "" && memory.SummaryNote == "" && memory.RecentCount == recentBeforeSummaryClear && memory.PreferenceCount == prefsBeforeSummaryClear, "summary can be removed without clearing recent context or explicit preferences");

            path = Path.Combine(root, "persistent-save.json");
            var persistent = new ConversationMemory(path, true);
            persistent.RecordUser("p1", "Do not call me honey."); persistent.ManualNote = "Remember my editable local note.";
            persistent.StageAssistant("heard", "I understand.", true); persistent.MarkAssistantHeard("heard");
            persistent.StageAssistant("pending", "UNHEARD_STORAGE_SENTINEL", true); persistent.Save();
            Check(File.Exists(path) && !File.ReadAllText(path).Contains("UNHEARD_STORAGE_SENTINEL"), "opt-in persistence stores only heard assistant speech, never staged text");
            var reconnected = new ConversationMemory(path, true);
            Check(reconnected.PreferenceCount == 1 && reconnected.RecentCount == 2 && reconnected.ManualNote.Contains("editable"), "bounded per-save recent context, preferences and notes survive a necessary restart");
            Check(new ConversationMemory(path, false).RecentCount == 0, "opting out does not silently restore durable context");
            Check(new ConversationMemory(Path.Combine(root, "save-b.json"), true).PreferenceCount == 0, "a different save path cannot inherit another save's preferences");
            Check(reconnected.ForgetPreference("avoid_nickname:honey") && reconnected.PreferenceCount == 0, "explicit preference can be removed independently from temporary topics");
            for (int i = 0; i < 1000; i++) reconnected.RecordUser("bounded" + i, string.Concat(Enumerable.Repeat("Everyday tea ☕ and a smile 😀. ", 80)));
            string bounded = reconnected.BuildContextData();
            Check(Encoding.UTF8.GetByteCount(bounded) <= 6500 && !bounded.Contains("\uFFFD") && reconnected.RecentCount <= 8, "Unicode context stays within byte limits without broken surrogate pairs");
            reconnected.Save(); Check(new FileInfo(path).Length <= 65536, "persistent memory remains bounded after a long conversation");
            long revision = reconnected.Revision; reconnected.Clear();
            Check(!File.Exists(path) && reconnected.RecentCount == 0 && reconnected.PreferenceCount == 0 && reconnected.Summary == "" && reconnected.ManualNote == "" && reconnected.Revision > revision, "clear removes note, preferences, recent context, derived summary and durable file");
            Check(new ConversationMemory(path, true).RecentCount == 0, "cleared context stays clear across restart");
            Console.WriteLine(passed + " persona/context checks passed offline; no paid calls or audio claims.");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "FamilyTimeVoice-persona-memory-");
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cleanup escaped its intended temporary test directory.");
            Directory.Delete(resolved, true);
        }
    }
}
