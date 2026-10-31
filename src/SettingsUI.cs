using System;
using System.Globalization;
using UnityEngine;

namespace FamilyTimeVoice
{
    public sealed partial class Plugin
    {
        private Rect window = new Rect(35, 80, 650, 610);
        private Vector2 scroll;
        private bool positioned;
        private int tab;
        private readonly string[] tabs = { "Connection", "Voice", "Audio", "Character / memory", "Usage / interface" };
        private string preferenceKey = "", preferenceValue = "";
        private bool editProfile, showVoiceDescriptions, showStockVoices, showVoiceDesignTools;
        private bool UiIdle => session == null && !starting && !closing && !creatingVoice;
        private bool UsesLive => string.Equals(settings.Provider, "live", StringComparison.OrdinalIgnoreCase);
        private GUIStyle Wrapped => new GUIStyle(GUI.skin.label) { wordWrap = true };
        private void OnGUI()
        {
            if (game == null) { GUI.Label(new Rect(30, 30, 700, 80), message); return; }
            if (!panel && GUI.Button(new Rect(Screen.width - 205, 16, 190, 34), "Voice settings [" + panelKey.Value + "]")) SetPanel(true);
            if (statusDisplay.Value)
            {
                string target = game.Ready ? "Wolf Mama " + game.Distance.ToString("0.0") + " m" : "Waiting for Wolf Mama";
                GUI.Box(new Rect(15, 15, 560, 52), "Wife voice: " + (playback != null && playback.HasAudio ? "Speaking" : state) + " | " + target + "\n" + panelKey.Value + " settings · " + talkKey.Value + " start/end · hold " + pttKey.Value + " speak");
            }
            if (subtitles.Value && !string.IsNullOrEmpty(subtitle) && (playback.HasAudio || Time.realtimeSinceStartup < subtitleUntil))
            {
                var style = new GUIStyle(GUI.skin.box) { wordWrap = true, fontSize = 18, alignment = TextAnchor.MiddleCenter };
                GUI.Box(new Rect(Screen.width * 0.17f, Screen.height - 125, Screen.width * 0.66f, 100), "Wolf Mama: " + subtitle, style);
            }
            if (panel)
            {
                window.width = Mathf.Min(680, Screen.width - 30); window.height = Mathf.Min(690, Screen.height - 85);
                if (!positioned) { window.x = Mathf.Max(15, (Screen.width - window.width) / 2); positioned = true; }
                Color previous = GUI.color;
                GUI.color = new Color(0.055f, 0.065f, 0.085f, 1f);
                GUI.DrawTexture(window, Texture2D.whiteTexture);
                GUI.color = Color.white;
                window = GUILayout.Window(821405, window, DrawPanel, "Family Time · Wolf Mama voice (AI-generated)");
                GUI.color = previous;
            }
        }
        private void DrawPanel(int id)
        {
            GUILayout.Label(state + (microphone.Running ? " · microphone active" : " · microphone off"));
            if (!string.IsNullOrEmpty(message)) GUILayout.Label(message, new GUIStyle(GUI.skin.label) { wordWrap = true });
            GUILayout.BeginHorizontal();
            GUI.enabled = !closing && !creatingVoice;
            if (session == null && !starting) { if (GUILayout.Button("Start conversation")) _ = StartConversation(); }
            else if (GUILayout.Button("End conversation")) EndConversation();
            GUI.enabled = session != null && !closing;
            if (GUILayout.Button("Interrupt reply")) Interrupt();
            GUI.enabled = true;
            if (GUILayout.Button("Close [" + panelKey.Value + "]")) SetPanel(false);
            GUILayout.EndHorizontal();
            int nextTab = GUILayout.Toolbar(tab, tabs); if (nextTab != tab) { tab = nextTab; scroll = Vector2.zero; }
            scroll = GUILayout.BeginScrollView(scroll);
            GUI.enabled = UiIdle;
            switch (tab)
            {
                case 0:
                    GUILayout.Label(secrets.HasKey ? "API key: protected locally or set by OPENAI_API_KEY." : "API key: missing");
                    GUILayout.Label("Enter locally; never paste a key into chat.");
                    keyInput = GUILayout.PasswordField(keyInput, '*', 512);
                    if (GUILayout.Button("Save protected key"))
                    { try { secrets.Save(keyInput); keyInput = ""; message = "Key protected with Windows DPAPI."; } catch (Exception ex) { message = ex.Message; } }
                    GUILayout.Label("Conversation provider");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button((!UsesLive ? "Selected: " : "") + "Realtime")) { settings.Provider = "realtime"; SaveUiSettings(); }
                    if (GUILayout.Button((UsesLive ? "Selected: " : "") + "GPT-Live")) { settings.Provider = "live"; SaveUiSettings(); }
                    GUILayout.EndHorizontal();
                    if (UsesLive)
                    {
                        settings.LiveModel = Field("GPT-Live model", settings.LiveModel);
                        GUILayout.Label("Continuous conversation. Active idle/muted time counts: end unused sessions. Choose a synthetic candidate on the Voice tab.", Wrapped);
                    }
                    else
                    {
                        settings.Model = Field("Realtime model", settings.Model);
                        settings.EnableUserTranscription = GUILayout.Toggle(settings.EnableUserTranscription, "Enable paid user transcription for reconnect memory");
                        GUILayout.Label("Optional transcription has its own price and shares the USD limits. Active server context works without it; user-text reconnect memory needs actual captions.", Wrapped);
                    }
                    GUI.enabled = UiIdle && !UsesLive;
                    if (GUILayout.Button("Test connection (no microphone or generated reply)")) _ = TestConnection();
                    GUI.enabled = UiIdle;
                    if (GUILayout.Button("Start development voice check (USD " + settings.DevelopmentLimitUsd.ToString("0.00") + " total allowance)")) _ = StartConversation(true);
                    GUILayout.Label("Only api.openai.com is used. No connection starts on launch. Raw audio is not saved; durable conversation memory is a separate opt-in.", Wrapped);
                    break;
                case 1:
                    DrawVoiceTab();
                    break;
                case 2:
                    GUILayout.Label("Microphone: " + (string.IsNullOrEmpty(settings.Microphone) ? "system default" : settings.Microphone));
                    if (GUILayout.Button("Use system default")) settings.Microphone = "";
                    foreach (var device in Microphone.devices) if (GUILayout.Button(device)) settings.Microphone = device;
                    settings.HandsFree = GUILayout.Toggle(settings.HandsFree, UsesLive ? "Hands-free microphone" : "Hands-free (server VAD)");
                    settings.Headphones = GUILayout.Toggle(settings.Headphones, "I use headphones (allow hands-free interruption)");
                    GUILayout.Label(UsesLive ? "GPT-Live receives continuous audio; PTT gates your microphone. Use headphones for dependable full-duplex conversation." : "Speaker hands-free pauses capture during replies to prevent echo loops. Push-to-talk is the default.", Wrapped);
                    pttKey.Value = Field("PTT key (InputSystem Key name)", pttKey.Value);
                    GUI.enabled = true;
                    settings.Volume = Slider("Volume", settings.Volume);
                    bool wasMuted = settings.Mute;
                    settings.Mute = GUILayout.Toggle(settings.Mute, "Mute wife voice (ends conversation)");
                    if (session != null && ((!wasMuted && settings.Mute) || settings.Volume <= 0)) EndConversation();
                    GUI.enabled = UiIdle && !UsesLive;
                    settings.VadThreshold = Slider("VAD threshold", (float)settings.VadThreshold);
                    settings.VadSilenceMs = IntField("VAD silence (ms)", settings.VadSilenceMs);
                    GUI.enabled = UiIdle;
                    if (GUILayout.Button("Offline microphone / wife-audio check (2 seconds, free)")) OfflineAudioCheck();
                    break;
                case 3:
                    GUILayout.Label("Persona: " + persona.Name + " · " + persona.PromptVersion);
                    settings.Language = Field("Language", settings.Language);
                    settings.Affection = Slider("Affection", settings.Affection);
                    settings.Teasing = Slider("Teasing when the exchange is playful", settings.Teasing);
                    settings.Endearments = GUILayout.Toggle(settings.Endearments, "Occasional endearments");
                    settings.ResponseLength = Field("Response length", settings.ResponseLength);
                    editProfile = GUILayout.Toggle(editProfile, "Edit conversation persona");
                    if (editProfile)
                    {
                        GUILayout.Label("Character instructions · shared by every voice");
                        GUILayout.Label("Saved in FamilyTimeVoice/wife.json as CharacterInstructions. Applies when the next conversation starts or reconnects.", Wrapped);
                        string core = persona.GetEditableCharacterInstructions();
                        string edited = GUILayout.TextArea(core, 9000, GUILayout.MinHeight(240));
                        if (edited != core) persona.CharacterInstructions = edited;
                    }
                    GUILayout.Label("Recent conversation stays in RAM across necessary reconnects. Preferences and compact conversation excerpts are saved only with the opt-in below. Your manual note is saved separately, as before, and is included in conversation only with memory enabled. Unheard/cancelled wife speech is omitted.", Wrapped);
                    bool persist = GUILayout.Toggle(settings.MemoryEnabled, "Remember conversation/preferences for this save on disk (opt-in)");
                    if (persist != settings.MemoryEnabled)
                    {
                        try { conversationMemory?.SetPersistentEnabled(persist); settings.MemoryEnabled = persist; SavePreferences(false); }
                        catch (Exception ex) { message = "Memory setting could not be saved: " + ex.GetType().Name; }
                    }
                    GUILayout.Label("Your editable local note");
                    string note = GUILayout.TextArea(memory ?? "", 1500, GUILayout.MinHeight(55));
                    if (note != memory) { memory = note; if (conversationMemory != null) conversationMemory.ManualNote = note; }
                    if (conversationMemory != null)
                    {
                        GUILayout.Label("Earlier context (quoted extracts)");
                        GUI.enabled = false; GUILayout.TextArea(string.IsNullOrEmpty(conversationMemory.Summary) ? "No earlier extracts." : conversationMemory.Summary, 2000, GUILayout.MinHeight(40));
                        GUI.enabled = UiIdle;
                        GUILayout.Label("Your editable summary / correction (separate from quoted extracts)");
                        string summaryNote = GUILayout.TextArea(conversationMemory.SummaryNote ?? "", 1200, GUILayout.MinHeight(45));
                        if (summaryNote != conversationMemory.SummaryNote) conversationMemory.SummaryNote = summaryNote;
                        if (GUILayout.Button("Clear earlier summary (keep recent conversation and preferences)")) conversationMemory.ClearSummary();
                        GUILayout.Label("Explicit preferences");
                        foreach (ExplicitPreference pref in conversationMemory.GetPreferences())
                        {
                            GUILayout.BeginHorizontal(); GUILayout.Label(pref.Key);
                            if (GUILayout.Button("Remove")) conversationMemory.ForgetPreference(pref.Key);
                            GUILayout.EndHorizontal();
                            string changed = GUILayout.TextField(pref.Value ?? "", 160);
                            if (changed != pref.Value) conversationMemory.SetPreference(pref.Key, changed, "edited local preference");
                        }
                        preferenceKey = Field("New preference name", preferenceKey);
                        preferenceValue = Field("Preference value", preferenceValue);
                        if (GUILayout.Button("Add explicit preference") && !string.IsNullOrWhiteSpace(preferenceKey) && !string.IsNullOrWhiteSpace(preferenceValue))
                        { conversationMemory.SetPreference(preferenceKey, preferenceValue); preferenceKey = preferenceValue = ""; }
                    }
                    GUI.enabled = !closing && !creatingVoice;
                    if (GUILayout.Button("Clear memory and start with a clean conversation")) ClearMemory();
                    GUI.enabled = UiIdle;
                    if (GUILayout.Button("Reload wife.json")) { try { persona = Persona.Load(profilePath); } catch { message = "Could not read wife.json."; } }
                    break;
                case 4:
                    GUI.enabled = true;
                    GUILayout.Label("Estimated mod usage (USD): session $" + ledger.SessionUsd.ToString("0.0000") + " · cumulative $" + ledger.TotalUsd.ToString("0.0000"));
                    GUILayout.Label("Development allowance used: $" + ledger.DevelopmentUsd.ToString("0.0000") + " / $" + settings.DevelopmentLimitUsd.ToString("0.00"));
                    GUILayout.Label("Session timer: " + (session == null ? "off" : (DateTime.UtcNow - connectedAt).ToString(@"mm\:ss")));
                    GUILayout.Label("Local estimates are not an account-wide billing cap and cannot observe other projects. Unknown usage retains a reservation. Edit dated prices.json beside the local ledger for other models.");
                    GUILayout.Label("GPT-Live duration updates are cumulative snapshots, not values to add together. Active idle/muted time is metered; any backend/tool work is additional.", Wrapped);
                    if (ledger.LivePrices != null) GUILayout.Label("Dated " + ledger.LivePrices.VerifiedDate + ": GPT-Live voice USD " + ledger.LivePrices.VoiceUsdPerMinute.ToString("0.0000") + "/active minute; optional user transcription USD " + ledger.LivePrices.TranscriptionUsdPerMinute.ToString("0.0000") + "/minute.", Wrapped);
                    GUI.enabled = UiIdle;
                    settings.SessionLimitUsd = MoneyField("Session cutoff USD", settings.SessionLimitUsd);
                    settings.CumulativeLimitUsd = MoneyField("Cumulative cutoff USD", settings.CumulativeLimitUsd);
                    settings.DevelopmentLimitUsd = MoneyField("Development allowance USD", settings.DevelopmentLimitUsd);
                    settings.IdleSeconds = IntField("Idle disconnect seconds", settings.IdleSeconds);
                    settings.MaxSessionSeconds = IntField("Maximum session seconds", settings.MaxSessionSeconds);
                    settings.MaxTurnSeconds = IntField("Maximum PTT seconds", settings.MaxTurnSeconds);
                    settings.MaxOutputTokens = IntField("Realtime response token ceiling", settings.MaxOutputTokens);
                    settings.ContextTokens = IntField("Realtime conversation context tokens", settings.ContextTokens);
                    panelKey.Value = Field("Settings panel key", panelKey.Value);
                    talkKey.Value = Field("Conversation key", talkKey.Value);
                    distance.Value = FloatField("Wife interaction distance (m)", distance.Value);
                    subtitles.Value = GUILayout.Toggle(subtitles.Value, "Show response subtitles");
                    statusDisplay.Value = GUILayout.Toggle(statusDisplay.Value, "Show connection status");
                    break;
            }
            GUI.enabled = true;
            GUILayout.EndScrollView();
            GUI.enabled = UiIdle;
            if (GUILayout.Button("Save settings and wife profile (next conversation)"))
            { try { settings.Validate(); persona.BuildInstructions("", settings); SavePreferences(); message = "Saved. End and Start to apply session changes."; } catch (Exception ex) { message = ex.Message; } }
            GUI.enabled = true;
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
        private void DrawVoiceTab()
        {
            GUILayout.Label("Voice identity");
            string selectedName = settings.Voice + " · Realtime";
            if (UsesLive)
            {
                selectedName = "Saved synthetic voice · GPT-Live";
                if (voiceCatalog != null)
                    foreach (SavedVoiceDesign saved in voiceCatalog.Saved)
                        if (saved.VoiceId == settings.CustomVoiceId && !string.IsNullOrEmpty(saved.VoiceId))
                            selectedName = saved.Name + " (" + saved.Code + ") · GPT-Live";
            }
            GUILayout.Label("Selected: " + selectedName, Wrapped);
            GUILayout.Label("Selection saves immediately and applies to the next conversation. An active conversation closes first, then reconnects with bounded remembered context. Every voice uses the same character instructions.", Wrapped);
            GUI.enabled = !starting && !closing && !creatingVoice;
            GUILayout.Space(8); GUILayout.Label("Your saved voices · AI-generated original adult voices");
            showVoiceDescriptions = GUILayout.Toggle(showVoiceDescriptions, "Show original voice descriptions");
            int savedCount = 0;
            if (voiceCatalog != null)
            foreach (SavedVoiceDesign saved in voiceCatalog.Saved)
            {
                if (string.IsNullOrEmpty(saved.VoiceId)) continue;
                savedCount++;
                bool selected = UsesLive && settings.CustomVoiceId == saved.VoiceId;
                if (GUILayout.Button((selected ? "Selected: " : "Use ") + saved.Name + " (" + saved.Code + ") · GPT-Live")) SelectDesignedVoice(saved);
                if (showVoiceDescriptions) GUILayout.Label(saved.Description, Wrapped);
            }
            if (savedCount == 0) GUILayout.Label("No saved voice identities were found in FamilyTimeVoice/voices.json.", Wrapped);
            showStockVoices = GUILayout.Toggle(showStockVoices, "Show stock Realtime voices");
            if (showStockVoices)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Use cedar · Realtime")) SelectStockVoice("cedar");
                if (GUILayout.Button("Use marin · Realtime")) SelectStockVoice("marin");
                GUILayout.EndHorizontal(); GUILayout.BeginHorizontal();
                foreach (string voice in new[] { "alloy", "ash", "ballad", "coral" }) if (GUILayout.Button(voice)) SelectStockVoice(voice);
                GUILayout.EndHorizontal(); GUILayout.BeginHorizontal();
                foreach (string voice in new[] { "echo", "sage", "shimmer", "verse" }) if (GUILayout.Button(voice)) SelectStockVoice(voice);
                GUILayout.EndHorizontal();
            }
            showVoiceDesignTools = GUILayout.Toggle(showVoiceDesignTools, "Show voice design tools");
            if (showVoiceDesignTools && voiceCatalog != null)
            {
                GUILayout.Label("Existing saved identities are reused. Creating a missing design is explicit; listening uses the normal spending limits.", Wrapped);
                foreach (VoiceDesignCandidate candidate in voiceCatalog.Candidates)
                {
                    SavedVoiceDesign saved = voiceCatalog.GetSaved(candidate.Code);
                    if (saved != null && !string.IsNullOrEmpty(saved.VoiceId)) continue;
                    bool uncertain = saved != null && (saved.CreationStatus == "creating" || saved.CreationStatus == "uncertain");
                    bool enabled = GUI.enabled; GUI.enabled = enabled && UiIdle && !uncertain;
                    if (GUILayout.Button("Create " + candidate.Name + " (" + candidate.Code + ") once · free voice design")) _ = CreateVoiceAsync(candidate.Code);
                    GUI.enabled = enabled;
                    if (saved != null) GUILayout.Label("Creation status: " + saved.CreationStatus + (uncertain ? ". Reconcile saved metadata before retrying." : ""), Wrapped);
                }
            }
            GUI.enabled = UiIdle;
            GUILayout.Label("Voice delivery (does not create a voice identity)");
            settings.VoiceDelivery = GUILayout.TextArea(settings.VoiceDelivery ?? "", 2000, GUILayout.MinHeight(75));
            settings.Accent = Field("Accent / pronunciation", settings.Accent);
            GUI.enabled = true;
        }
        private void SaveUiSettings() { try { settings.Validate(); SavePreferences(false); } catch (Exception ex) { message = ex.Message; } }
        private string Field(string label, string value) { GUILayout.Label(label); return GUILayout.TextField(value ?? "", 300); }
        private float Slider(string label, float value) { GUILayout.Label(label + ": " + value.ToString("0.00")); return GUILayout.HorizontalSlider(value, 0, 1); }
        private int IntField(string label, int value) { int result; return int.TryParse(Field(label, value.ToString(CultureInfo.InvariantCulture)), out result) ? result : value; }
        private float FloatField(string label, float value) { float result; return float.TryParse(Field(label, value.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : value; }
        private decimal MoneyField(string label, decimal value) { decimal result; return decimal.TryParse(Field(label, value.ToString(CultureInfo.InvariantCulture)), NumberStyles.Number, CultureInfo.InvariantCulture, out result) ? result : value; }
    }
}
