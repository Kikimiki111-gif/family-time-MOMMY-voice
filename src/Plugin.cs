using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FamilyTimeVoice
{
    [BepInPlugin("local.familytime.wifevoice", "Family Time Wife Voice", "0.2.2")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        private readonly MainThreadQueue dispatch = new MainThreadQueue();
        private readonly Dictionary<PropertyInfo, ConfigEntry<string>> preferences = new Dictionary<PropertyInfo, ConfigEntry<string>>();
        private VoiceSettings settings;
        private Persona persona;
        private GameAdapter game;
        private StreamingPlayback playback;
        private MicrophoneCapture microphone;
        private SecretStore secrets;
        private UsageLedger ledger;
        private IVoiceSession session, closingSession;
        private ConversationMemory conversationMemory;
        private CaptionAudibilityTracker audibility;
        private VoiceDesignCatalog voiceCatalog;
        private Task pendingClose = Task.CompletedTask;
        // Only one provider can be closing. Its terminal cleanup must survive a
        // full ordinary event queue or the close gate could remain stuck forever.
        private Action pendingTerminal;
        private ConfigEntry<string> panelKey, talkKey, pttKey;
        private ConfigEntry<float> distance;
        private ConfigEntry<bool> subtitles, statusDisplay;
        private string profilePath, memoryPath, memory = "", keyInput = "";
        private string state = "Disconnected", message = "", subtitle = "", subtitleResponse = "";
        private bool panel, starting, closing, creatingVoice, talking, shuttingDown, testing, developmentSession;
        private bool releaseRequired, connectionInterrupted, wasPlaying, interruptedPushToTalk;
        private string saveIdentity;
        private int generation, lastTargetId;
        private long contextRevision;
        private float turnStart, echoUntil, subtitleUntil, nextMemorySave;
        private DateTime connectedAt;
        private float offlineUntil;
        private long offlineBytes;
        private int offlineRate;
        private void Awake()
        {
            try
            {
                settings = new VoiceSettings();
                foreach (var property in typeof(VoiceSettings).GetProperties())
                {
                    var converter = TypeDescriptor.GetConverter(property.PropertyType);
                    var entry = Config.Bind("Voice", property.Name, converter.ConvertToInvariantString(property.GetValue(settings, null)), "Applied next conversation. Amounts in USD.");
                    preferences[property] = entry;
                    try { property.SetValue(settings, converter.ConvertFromInvariantString(entry.Value), null); }
                    catch { Logger.LogWarning("Invalid preference " + property.Name + "; using default."); }
                }
                settings.UpgradeLegacyDefaults();
                panelKey = Config.Bind("Interface", "PanelKey", "F8"); talkKey = Config.Bind("Interface", "ConversationKey", "F9"); pttKey = Config.Bind("Interface", "PushToTalkKey", "V");
                distance = Config.Bind("Interface", "InteractionDistance", 8f); subtitles = Config.Bind("Interface", "Subtitles", true); statusDisplay = Config.Bind("Interface", "StatusDisplay", true);
                profilePath = Path.Combine(Paths.ConfigPath, "FamilyTimeVoice", "wife.json"); memoryPath = Path.Combine(Paths.ConfigPath, "FamilyTimeVoice", "memory-note.txt");
                persona = Persona.Load(profilePath); if (File.Exists(memoryPath)) memory = File.ReadAllText(memoryPath);
                secrets = new SecretStore(); ledger = new UsageLedger(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FamilyTimeVoice", "usage-ledger.json"));
                voiceCatalog = new VoiceDesignCatalog(Path.Combine(Paths.ConfigPath, "FamilyTimeVoice", "voices.json"));
                game = new GameAdapter(); microphone = new MicrophoneCapture(); playback = new StreamingPlayback();
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
                Logger.LogInfo("Family Time Wife Voice 0.2.2 loaded. No microphone or network started. F8 settings, F9 conversation, V push-to-talk.");
            }
            catch (Exception ex) { state = "Error"; message = "Initialization failed: " + ex.GetType().Name; game?.Dispose(); game = null; Logger.LogError(message); }
        }
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        { EndConversation(); game?.Refresh(); }
        private bool Pressed(string binding) { Key key; return Keyboard.current != null && Enum.TryParse(binding, true, out key) && key != Key.None && Keyboard.current[key].wasPressedThisFrame; }
        private bool Held(string binding) { Key key; return Keyboard.current != null && Enum.TryParse(binding, true, out key) && key != Key.None && Keyboard.current[key].isPressed; }
        private void Update()
        {
            if (dispatch.TakeOverflow()) Fail("Voice event buffer filled while the game was busy; conversation stopped.");
            int n = 0; Action action;
            while (n++ < 256 && dispatch.TryDequeue(out action)) try { action(); } catch (Exception ex) { Fail("Voice processing failed: " + ex.GetType().Name); }
            // Drain final captions/ASR before committing close-time memory. If the
            // ordinary per-frame work budget was exhausted, defer this one terminal
            // action rather than allowing it to overtake queued transcript callbacks.
            if (n <= 256)
            {
                var terminal = Interlocked.Exchange(ref pendingTerminal, null);
                if (terminal != null) try { terminal(); } catch (Exception ex) { Fail("Voice finalization failed: " + ex.GetType().Name); }
            }
            if (game == null) return;
            game.Refresh(); RefreshSaveNote();
            if (!game.Ready) lastTargetId = 0;
            else if (game.Target.GetInstanceID() != lastTargetId)
            { lastTargetId = game.Target.GetInstanceID(); Logger.LogInfo("Confirmed wife singleton GAT.WolfMama reacquired; instance " + lastTargetId + "."); }
            if (session != null && !testing && !game.Ready) { EndConversation(); message = "Conversation ended for a save/load or unavailable wife."; }
            if (Pressed(panelKey.Value)) SetPanel(!panel);
            if (!panel && Pressed(talkKey.Value)) { if (session != null || starting || closing) EndConversation(); else _ = StartConversation(); }
            playback.Follow(game.Target, settings.Volume, settings.Mute);
            bool playing = playback.HasAudio;
            if (wasPlaying && !playing) echoUntil = Time.realtimeSinceStartup + 0.7f;
            wasPlaying = playing;
            ReportPlayback(); audibility?.Tick();
            if (conversationMemory != null && Time.realtimeSinceStartup >= nextMemorySave)
            { nextMemorySave = Time.realtimeSinceStartup + 2f; conversationMemory.Save(); }
            if (offlineUntil > 0)
            {
                try
                {
                    var captured = microphone.Poll(true); if (captured != null) offlineBytes += captured.Length;
                    if (Time.realtimeSinceStartup >= offlineUntil)
                    {
                        microphone.Stop(); offlineUntil = 0; var snapshot = playback.StopAndClear();
                        message = "Offline audio check: " + offlineBytes + " PCM bytes captured at " + offlineRate + " Hz; " + snapshot.HeardMilliseconds + " ms passed the playback DSP pipeline. No network or recording saved.";
                        Logger.LogInfo(message); state = "Disconnected";
                    }
                }
                catch (Exception ex) { Fail(ex.Message); }
                return;
            }
            if (session == null || !session.Connected || !microphone.Running) return;
            try
            {
                if (game.ContextRevision != contextRevision) { contextRevision = game.ContextRevision; session.UpdateContext(game.Context); }
                bool held = Held(pttKey.Value); if (!held) releaseRequired = false;
                bool wants = !panel && Application.isFocused && !releaseRequired && (settings.HandsFree || held);
                bool continuous = session.RequiresContinuousInput;
                if (wants && !talking && !settings.HandsFree)
                {
                    if (TryInterruptPushToTalk(continuous, playing)) return;
                    if (!continuous) Interrupt();
                    microphone.Poll(false); talking = true; turnStart = Time.realtimeSinceStartup; state = "Listening";
                }
                bool transmit = wants;
                if (settings.HandsFree && !settings.Headphones && (session.Responding || playback.HasAudio || Time.realtimeSinceStartup < echoUntil)) transmit = false;
                if (continuous) session.SetInputActive(transmit);
                var bytes = microphone.Poll(transmit); if (bytes != null && bytes.Length > 0) session.AppendPcm(bytes);
                if (!settings.HandsFree && talking && (!wants || Time.realtimeSinceStartup - turnStart >= settings.MaxTurnSeconds))
                { if (wants) releaseRequired = true; FinishMicrophoneTurn(); }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void FinishMicrophoneTurn()
        {
            if (!talking) return; talking = false; if (session?.Connected != true) return;
            session.FinishTurn(); state = session.RequiresContinuousInput ? "Listening" : "Responding";
        }
        private bool TryInterruptPushToTalk(bool continuous, bool playbackActive)
        {
            if (!continuous || !playbackActive) return false;
            // Live has no per-response cancel. This press stops the old
            // stream; recording resumes only after a fresh, explicit press
            // in the replacement session. Do not silently lose an utterance
            // while the supported close/start handshake is in progress.
            interruptedPushToTalk = true; releaseRequired = true; talking = false;
            message = "Reply interrupted. Wait for Ready, then release and press " + pttKey.Value + " again to speak. Speech during reconnect is not recorded.";
            Interrupt();
            return true;
        }
        private void MicrophoneReady(bool held)
        {
            // Recheck at actual readiness, rather than the start of the network
            // handshake: a key pressed during connection must not send speech.
            releaseRequired = held;
            if (interruptedPushToTalk)
            {
                state = held ? "Ready — release " + pttKey.Value + ", then hold to repeat" : "Ready — hold " + pttKey.Value + " to repeat";
                message = "Her reply and queued speech stopped. Hold " + pttKey.Value + " for a new turn after releasing it. Speech during reconnect was not recorded.";
                interruptedPushToTalk = false;
            }
            else state = "Ready — hold " + pttKey.Value;
        }
        private string BuildContext()
        {
            var value = new JObject { ["game_facts"] = JToken.Parse(game.Context), ["local_memory"] = JToken.Parse(conversationMemory.BuildContextData(4800, false)) };
            value["heard_excerpts"] = JArray.FromObject(conversationMemory.GetRecentEntries().Where(e => !e.CompleteTurn));
            return value.ToString(Formatting.None);
        }
        private async Task StartConversation(bool development = false)
        {
            if (session != null || starting || closing || creatingVoice || shuttingDown) return;
            if (!game.Ready || game.Distance > distance.Value) { message = "Approach Wolf Mama (within " + distance.Value.ToString("0.0") + " m)."; SetPanel(true); return; }
            if (settings.Mute || settings.Volume <= 0) { message = "Unmute wife voice and raise its volume before starting."; SetPanel(true); return; }
            if (!secrets.HasKey) { message = "Enter your key in the masked local field and save it."; SetPanel(true); return; }
            int current = ++generation; starting = true; developmentSession = development; state = "Connecting";
            if (!interruptedPushToTalk) message = "";
            IVoiceSession next = null;
            try
            {
                ValidateInterface(); settings.Validate(); SavePreferences(false); ledger.ReloadPrices(); persona = Persona.Load(profilePath);
                RefreshSaveNote(); conversationMemory.ManualNote = settings.MemoryEnabled ? memory : "";
                next = settings.Provider.Equals("live", StringComparison.OrdinalIgnoreCase) ? (IVoiceSession)new LiveSession(ledger) : new RealtimeSession(ledger);
                var tracker = new CaptionAudibilityTracker(conversationMemory, next.RequiresContinuousInput);
                tracker.BeginConnection(Guid.NewGuid().ToString("N"), next.RequiresContinuousInput);
                audibility = tracker; session = next; connectionInterrupted = false; releaseRequired = Held(pttKey.Value);
                playback.StopAndClear(); playback.ResetDeliveryCounters();
                next.SetHistory(conversationMemory.GetRecentEntries().Where(e => e.CompleteTurn).Select(e => new VoiceHistoryMessage { Role = e.Role, Text = e.Text }).ToArray());
                bool Current() => current == generation && (session == next || closingSession == next);
                next.StateChanged += value => dispatch.Enqueue(() =>
                {
                    if (!Current()) return;
                    if (value.StartsWith("Disconnected", StringComparison.Ordinal) && session == next) EndConversation();
                    else if (!closing) state = value;
                });
                next.Fault += value => dispatch.Enqueue(() => { if (!Current()) return; if (closing) { message = value; Logger.LogWarning(value); } else Fail(value); });
                next.InputSpeechStarted += () => dispatch.Enqueue(() => { if (Current() && session == next && settings.HandsFree && settings.Headphones) Interrupt(false); });
                var rt = next as RealtimeSession;
                if (rt != null) rt.UserTurnCommitted += item => dispatch.Enqueue(() => { if (Current()) tracker.UserTurnCommitted(item); });
                next.AudioGenerationEnded += item => dispatch.Enqueue(() => { if (Current()) tracker.AudioGenerationEnded(item); });
                next.ResponseEnded += outcome => dispatch.Enqueue(() =>
                {
                    if (!Current()) return; tracker.ResponseEnded(outcome);
                    if (outcome.Status == "incomplete") message = "Reply reached its output limit; increase Maximum response tokens if needed.";
                });
                next.ReconnectRequested += () => dispatch.Enqueue(() =>
                {
                    if (!Current() || session != next || closing) return;
                    connectionInterrupted = true; tracker.Interrupt(null); bool dev = developmentSession;
                    EndConversation(); _ = RestartAfterClose(generation, dev);
                });
                next.Audio += chunk => dispatch.Enqueue(() =>
                {
                    if (!Current()) return;
                    if (session != next || closing || !next.AcceptsAudio(chunk.ConnectionId, chunk.ResponseId))
                    {
                        // A caption can finish before the final PCM callbacks reach
                        // this thread. Any dropped chunk makes that item's complete
                        // heard-text claim unsafe, even if its earlier audio drained.
                        if (chunk.Pcm != null && chunk.Pcm.Length > 0) tracker.Interrupt(chunk.ItemId);
                        return;
                    }
                    if (next.RequiresContinuousInput && talking && !settings.Headphones) { connectionInterrupted = true; tracker.Interrupt(null); return; }
                    ReportPlayback();
                    if (!playback.Append(chunk.ResponseId, chunk.ItemId, chunk.ContentIndex, chunk.Pcm)) { Fail("Playback buffer overflow or overlapping audio."); return; }
                    tracker.ObserveAudio(chunk); state = "Speaking"; subtitleUntil = Time.realtimeSinceStartup + 8f;
                });
                next.Transcript += chunk => dispatch.Enqueue(() =>
                {
                    if (!Current()) return;
                    if (chunk.Role == "user") { tracker.ObserveTranscript(chunk); return; }
                    if (session != next || closing || !next.AcceptsAudio(chunk.ConnectionId, chunk.ResponseId)) return;
                    tracker.ObserveTranscript(chunk);
                    if (subtitleResponse != chunk.ResponseId) { subtitle = ""; subtitleResponse = chunk.ResponseId; }
                    subtitle = chunk.Final ? chunk.Text : subtitle + chunk.Text;
                    if (subtitle.Length > 1500) subtitle = subtitle.Substring(subtitle.Length - 1500);
                    subtitleUntil = Time.realtimeSinceStartup + 8f;
                });
                contextRevision = game.ContextRevision;
                await next.StartAsync(secrets.Load(), settings, persona, BuildContext(), development);
                dispatch.Enqueue(() =>
                {
                    if (!Current() || session != next || closing) return;
                    try { microphone.Start(settings.Microphone); connectedAt = DateTime.UtcNow; MicrophoneReady(Held(pttKey.Value)); SetPanel(false); }
                    catch (Exception ex) { Fail(ex.Message); }
                });
            }
            catch (Exception ex) { dispatch.Enqueue(() => { if (current == generation && !closing && (next == null || session == next)) Fail(ex.Message); }); }
            finally { dispatch.Enqueue(() => { if (current == generation) starting = false; }); }
        }
        private void ReportPlayback()
        {
            if (playback == null || session == null) return;
            var heard = playback.Snapshot();
            var report = new VoicePlaybackReport { ItemId = heard.ItemId, HeardMilliseconds = heard.HeardMilliseconds, Drained = heard.Drained };
            audibility?.ReportPlayback(report); session.ReportPlayback(report);
        }
        private PlaybackSnapshot ClearLocalPlayback(bool interrupted)
        {
            var heard = playback?.StopAndClear(); subtitle = ""; if (heard == null) return null;
            var report = new VoicePlaybackReport { ItemId = heard.ItemId, HeardMilliseconds = heard.HeardMilliseconds, Drained = heard.Drained, Interrupted = interrupted };
            audibility?.ReportPlayback(report); session?.ReportPlayback(report);
            if (interrupted && session?.RequiresContinuousInput == true) connectionInterrupted = true;
            return heard;
        }
        private void Interrupt(bool cancelResponse = true)
        {
            var heard = ClearLocalPlayback(true);
            if (session?.RequiresContinuousInput == true && cancelResponse) { connectionInterrupted = true; audibility?.Interrupt(null); }
            session?.Interrupt(heard?.ItemId, heard?.ContentIndex ?? 0, heard?.HeardMilliseconds ?? 0, cancelResponse);
        }
        private void EndConversation()
        {
            starting = talking = testing = false; offlineUntil = 0; microphone?.Stop();
            if (session == null) { if (closing) generation++; playback?.StopAndClear(); if (!closing) state = "Disconnected"; return; }
            ReportPlayback(); var heard = playback.StopAndClear();
            var old = session; var tracker = audibility; var localMemory = conversationMemory; int current = generation;
            long heardSamples = playback.TotalHeardNonSilentSamples;
            bool interrupted = connectionInterrupted || !heard.Drained;
            tracker?.ReportPlayback(new VoicePlaybackReport { ItemId = heard.ItemId, HeardMilliseconds = heard.HeardMilliseconds, Drained = heard.Drained, Interrupted = !heard.Drained });
            tracker?.FlushUser(); session = null; closingSession = old; closing = true; subtitle = ""; state = "Closing — microphone off";
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); pendingClose = completion.Task;
            _ = FinishClose(old, tracker, localMemory, current, heardSamples, interrupted, completion);
        }
        private async Task FinishClose(IVoiceSession old, CaptionAudibilityTracker tracker, ConversationMemory localMemory, int current, long heardSamples, bool interrupted, TaskCompletionSource<bool> completion)
        {
            try { await old.EndAsync(); } catch { }
            finally
            {
                Interlocked.Exchange(ref pendingTerminal, () =>
                {
                    try
                    {
                        if (current == generation)
                        {
                            var live = old as LiveSession;
                            if (live != null)
                            {
                                var media = live.MediaStatistics;
                                tracker?.FinalizeLiveConnection(media.GracefulServerClose && media.FinalUsageConfirmed,
                                    media.IgnoredNonSilentSamples == 0 && media.NonSilentReceivedSamples == heardSamples, interrupted);
                            }
                            tracker?.FlushUser(); localMemory?.DiscardPendingAssistant(); localMemory?.Save();
                        }
                        old.Dispose(); if (closingSession == old) { closingSession = null; closing = false; if (state.StartsWith("Closing", StringComparison.Ordinal)) state = "Disconnected"; }
                    }
                    finally { completion.TrySetResult(true); }
                });
            }
        }
        private async Task RestartAfterClose(int intent, bool development)
        {
            await pendingClose;
            dispatch.Enqueue(() => { if (intent == generation && !shuttingDown && game.Ready && !closing && session == null) _ = StartConversation(development); });
        }
        private void ClearMemory()
        {
            EndConversation(); generation++; audibility?.Clear(); conversationMemory?.Clear(); memory = "";
            if (memoryPath != null) File.WriteAllText(memoryPath, "");
            message = "Per-save conversation, preferences, summary and note cleared. Start a clean conversation when ready.";
        }
        private void SelectStockVoice(string voice)
        {
            if (starting || closing || creatingVoice || shuttingDown) return;
            if (string.Equals(settings.Provider, "realtime", StringComparison.OrdinalIgnoreCase) && settings.Voice == voice && string.IsNullOrEmpty(settings.CustomVoiceId) && string.IsNullOrEmpty(settings.VoiceDesignDescription)) return;
            bool restart = session != null || closing; bool dev = developmentSession; EndConversation();
            settings.Provider = "realtime"; settings.Voice = voice; settings.CustomVoiceId = ""; settings.VoiceDesignDescription = ""; SavePreferences(false);
            if (restart) _ = RestartAfterClose(generation, dev);
        }
        private void SelectDesignedVoice(SavedVoiceDesign design)
        {
            if (design == null || string.IsNullOrEmpty(design.VoiceId)) return;
            if (starting || closing || creatingVoice || shuttingDown) return;
            if (string.Equals(settings.Provider, "live", StringComparison.OrdinalIgnoreCase) && settings.CustomVoiceId == design.VoiceId && settings.VoiceDesignDescription == design.Description) return;
            bool restart = session != null || closing; bool dev = developmentSession; EndConversation();
            settings.Provider = "live"; settings.CustomVoiceId = design.VoiceId; settings.VoiceDesignDescription = design.Description; SavePreferences(false);
            if (restart) _ = RestartAfterClose(generation, dev);
        }
        private async Task CreateVoiceAsync(string code)
        {
            if (creatingVoice || session != null || starting || closing) return;
            creatingVoice = true; message = "Creating synthetic voice " + code + " once (metadata only)...";
            try { await voiceCatalog.CreateAsync(code, secrets.Load()); dispatch.Enqueue(() => { message = "Synthetic candidate " + code + " saved. Listening sessions use the normal spending limits."; }); }
            catch (Exception ex) { dispatch.Enqueue(() => { message = ex.Message; }); }
            finally { dispatch.Enqueue(() => creatingVoice = false); }
        }
        private void OfflineAudioCheck()
        {
            if (session != null || starting || closing || offlineUntil > 0) return;
            if (!game.Ready) { message = "Enter gameplay before the offline wife-audio check."; return; }
            try
            {
                microphone.Start(settings.Microphone); offlineRate = microphone.SampleRate; offlineBytes = 0;
                var tone = new float[6000]; for (int i = 0; i < tone.Length; i++) tone[i] = 0.08f * (float)Math.Sin(2 * Math.PI * 440 * i / 24000);
                playback.StopAndClear(); playback.Append("offline", "offline-tone", 0, AudioCodec.Encode(tone));
                offlineUntil = Time.realtimeSinceStartup + 2f; state = "Offline microphone/audio check";
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        private void Fail(string reason) { EndConversation(); state = "Error"; message = reason; Logger.LogWarning("Voice stopped: " + reason); }
        private void SetPanel(bool value) { if (panel == value) return; if (value && talking) FinishMicrophoneTurn(); panel = value; game?.HoldInput(value); }
        private void ValidateInterface()
        {
            if (float.IsNaN(distance.Value) || float.IsInfinity(distance.Value) || distance.Value <= 0 || distance.Value > 100) throw new InvalidOperationException("Interaction distance must be between 0 and 100 metres.");
            foreach (string binding in new[] { panelKey.Value, talkKey.Value, pttKey.Value })
            { Key key; if (!Enum.TryParse(binding, true, out key) || key == Key.None || !Enum.IsDefined(typeof(Key), key)) throw new InvalidOperationException("Use valid InputSystem key names such as F8, F9 and V."); }
            if (panelKey.Value.Equals(talkKey.Value, StringComparison.OrdinalIgnoreCase) || panelKey.Value.Equals(pttKey.Value, StringComparison.OrdinalIgnoreCase) || talkKey.Value.Equals(pttKey.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose three different panel, conversation and push-to-talk keys.");
        }
        private void SavePreferences(bool saveProfile = true)
        {
            ValidateInterface();
            foreach (var pair in preferences) pair.Value.Value = TypeDescriptor.GetConverter(pair.Key.PropertyType).ConvertToInvariantString(pair.Key.GetValue(settings, null));
            Config.Save(); if (saveProfile) persona.Save(profilePath);
            if (memory.Length > 1500) memory = memory.Substring(0, 1500); File.WriteAllText(memoryPath, memory);
            if (conversationMemory != null) { conversationMemory.SetPersistentEnabled(settings.MemoryEnabled); conversationMemory.ManualNote = settings.MemoryEnabled ? memory : ""; conversationMemory.Save(); }
        }
        private void RefreshSaveNote()
        {
            string next = game.SaveId; if (saveIdentity == next) return;
            if (saveIdentity != null) { EndConversation(); generation++; conversationMemory?.Save(); File.WriteAllText(memoryPath, memory.Substring(0, Math.Min(memory.Length, 1500))); }
            saveIdentity = next; string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(next))).Replace("-", "").Substring(0, 24);
            string folder = Path.Combine(Paths.ConfigPath, "FamilyTimeVoice", "memory"); Directory.CreateDirectory(folder);
            memoryPath = Path.Combine(folder, key + ".txt"); memory = File.Exists(memoryPath) ? File.ReadAllText(memoryPath) : "";
            conversationMemory = new ConversationMemory(Path.Combine(folder, key + ".json"), settings.MemoryEnabled);
            conversationMemory.ManualNote = settings.MemoryEnabled ? memory : ""; audibility = null;
        }
        private async Task TestConnection()
        {
            if (starting || session != null || closing || creatingVoice) return;
            if (settings.Provider.Equals("live", StringComparison.OrdinalIgnoreCase)) { message = "GPT-Live bills active time even without speech. Use the bounded development voice check."; return; }
            int current = ++generation; starting = testing = true; message = "Testing Realtime without microphone capture...";
            var probe = new RealtimeSession(ledger); session = probe;
            try
            {
                settings.Validate(); ledger.ReloadPrices(); persona = Persona.Load(profilePath);
                await probe.StartAsync(secrets.Load(), settings, persona, "Connection test; do not speak.", true);
                dispatch.Enqueue(() => { if (current != generation) return; EndConversation(); message = "Realtime connection accepted. No microphone or response requested."; });
            }
            catch (Exception ex) { dispatch.Enqueue(() => { if (current != generation) return; EndConversation(); message = ex.Message; }); }
        }
        private void OnApplicationQuit() => Shutdown();
        private void OnDestroy() => Shutdown();
        private void Shutdown()
        {
            if (shuttingDown) return; shuttingDown = true; UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            EndConversation(); conversationMemory?.Save(); SetPanel(false); playback?.Dispose(); microphone?.Dispose(); game?.Dispose(); voiceCatalog?.Dispose(); keyInput = "";
        }
    }
}
