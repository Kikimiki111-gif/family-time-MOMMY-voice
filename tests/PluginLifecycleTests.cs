using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using FamilyTimeVoice;
using Newtonsoft.Json.Linq;

static class Program
{
    static int passed;
    static readonly BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    static async Task<int> Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        string game = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Family Time");
        bool preparedOnly = Array.IndexOf(args, "--prepared-only") >= 0;
        var paths = new List<string>
        {
            Path.Combine(root, ".tools", "engine-runtime"),
            Path.Combine(root, ".tools", "unity-windows-corlibs-6000.2.12"),
            Path.Combine(root, ".tools", "bepinex", "BepInEx", "core"),
            Path.Combine(game, "FamilyTime_Data", "Managed"),
            Path.Combine(root, "bin", "Release", "netstandard2.1")
        };
        // Source tests must work before any mod is installed. These are only
        // legacy fallbacks; --prepared-only proves no installed BepInEx is used.
        if (!preparedOnly) paths.AddRange(new[] { Path.Combine(game, "BepInEx", "FamilyTimeVoice-engine"), Path.Combine(game, "BepInEx", "FamilyTimeVoice-runtime"), Path.Combine(game, "BepInEx", "core") });
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        { foreach (string path in paths) { string file = Path.Combine(path, name.Name + ".dll"); if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file); } return null; };
        try { await Run(); Console.WriteLine("Passed " + passed + " actual-plugin managed lifecycle checks" + (preparedOnly ? " using prepared dependencies without installed BepInEx paths" : "") + "; no Unity native calls, network or credentials."); return 0; }
        catch (Exception error) { if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException; Console.WriteLine("Plugin lifecycle check failed: " + error.GetType().Name + ": " + error.Message); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task Run()
    {
        Type type = typeof(Plugin);
        object stopped = RuntimeHelpers.GetUninitializedObject(type);
        Set(stopped, "closing", true); Set(stopped, "generation", 7);
        type.GetMethod("EndConversation", Fields).Invoke(stopped, null);
        Check(Get<int>(stopped, "generation") == 8, "End during closing invalidates the pending restart generation");
        Set(stopped, "closing", false);
        type.GetMethod("EndConversation", Fields).Invoke(stopped, null);
        Check(Get<int>(stopped, "generation") == 8 && Get<string>(stopped, "state") == "Disconnected", "End while already disconnected preserves the clean lifecycle state");

        object instance = RuntimeHelpers.GetUninitializedObject(type); var queue = new MainThreadQueue(); Set(instance, "dispatch", queue);
        for (int i = 0; i < 256; i++) queue.Enqueue(() => { });
        var fake = new FakeSession(); Set(instance, "closing", true); Set(instance, "closingSession", fake); Set(instance, "generation", 12); Set(instance, "state", "Closing — microphone off");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task closing = (Task)type.GetMethod("FinishClose", Fields).Invoke(instance, new object[] { fake, null, null, 12, 0L, false, completion });
        Check(!closing.IsCompleted && !completion.Task.IsCompleted && Get<Action>(instance, "pendingTerminal") == null, "close waits for provider finalization before posting terminal cleanup");
        fake.Final.TrySetResult(true); await closing.WaitAsync(TimeSpan.FromSeconds(2));
        Action terminal = Get<Action>(instance, "pendingTerminal");
        Check(terminal != null && !completion.Task.IsCompleted && !queue.TakeOverflow(), "terminal cleanup survives an already-full bounded ordinary callback queue");
        int drained = 0; Action callback; while (queue.TryDequeue(out callback)) { callback(); drained++; }
        Check(drained == 256, "terminal posting preserves every earlier ordinary caption callback");
        Set(instance, "pendingTerminal", null); terminal();
        Check(completion.Task.IsCompletedSuccessfully && !Get<bool>(instance, "closing") && Get<IVoiceSession>(instance, "closingSession") == null, "actual terminal action releases closing gate and pending-close task");
        Check(fake.EndCalls == 1 && fake.DisposeCalls == 1, "provider close and disposal occur once");

        var stale = new FakeSession(); stale.Final.TrySetResult(true); Set(instance, "closing", true); Set(instance, "closingSession", stale); Set(instance, "generation", 14);
        var staleCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ((Task)type.GetMethod("FinishClose", Fields).Invoke(instance, new object[] { stale, null, null, 13, 0L, true, staleCompletion }));
        terminal = Get<Action>(instance, "pendingTerminal"); Set(instance, "pendingTerminal", null); terminal();
        Check(staleCompletion.Task.IsCompletedSuccessfully && !Get<bool>(instance, "closing") && stale.DisposeCalls == 1, "invalidated generation still releases resources without reviving old memory");

        string fixture = Path.Combine(Path.GetTempPath(), "FamilyTimeVoiceSelectorTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        object selector = RuntimeHelpers.GetUninitializedObject(type);
        var config = new ConfigFile(Path.Combine(fixture, "voice.cfg"), false);
        typeof(BaseUnityPlugin).GetField("<Config>k__BackingField", Fields).SetValue(selector, config);
        var settings = new VoiceSettings(); Set(selector, "settings", settings);
        var preferences = new Dictionary<PropertyInfo, ConfigEntry<string>>();
        foreach (var property in typeof(VoiceSettings).GetProperties())
            preferences[property] = config.Bind("Voice", property.Name, TypeDescriptor.GetConverter(property.PropertyType).ConvertToInvariantString(property.GetValue(settings)), "Offline selection fixture");
        Set(selector, "preferences", preferences);
        Set(selector, "panelKey", config.Bind("Interface", "PanelKey", "F8"));
        Set(selector, "talkKey", config.Bind("Interface", "ConversationKey", "F9"));
        Set(selector, "pttKey", config.Bind("Interface", "PushToTalkKey", "V"));
        Set(selector, "distance", config.Bind("Interface", "InteractionDistance", 8f));
        Set(selector, "memory", ""); Set(selector, "memoryPath", Path.Combine(fixture, "manual-note.txt"));
        var persona = new Persona(); Set(selector, "persona", persona);
        foreach (string code in new[] { "A", "B", "C" })
        {
            var design = new SavedVoiceDesign { Code = code, Name = "Saved test voice " + code, VoiceId = "voice_offline_" + code, Description = "Original synthetic voice description " + code };
            type.GetMethod("SelectDesignedVoice", Fields).Invoke(selector, new object[] { design });
            var reopened = new ConfigFile(config.ConfigFilePath, false);
            Check(reopened.Bind("Voice", "Provider", "").Value == "live" && reopened.Bind("Voice", "CustomVoiceId", "").Value == design.VoiceId && reopened.Bind("Voice", "VoiceDesignDescription", "").Value == design.Description,
                "actual saved " + code + " selector persists its exact provider, identity and original description");
            string prompt = persona.BuildInstructions("No current game activity supplied.", settings);
            var startEvent = (JObject)typeof(LiveSession).GetMethod("BuildStartEvent", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { settings, prompt, null });
            Check((string)startEvent["session"]["audio"]["output"]["voice"]["id"] == design.VoiceId && ((string)startEvent["session"]["instructions"]).Contains(persona.GetEditableCharacterInstructions()) && ((string)startEvent["session"]["instructions"]).Contains(design.Description),
                "next Live start uses saved " + code + " identity and the shared editable wife persona");
        }

        var unchanged = new FakeSession { Continuous = true, Active = true }; Set(selector, "session", unchanged);
        type.GetMethod("SelectDesignedVoice", Fields).Invoke(selector, new object[] { new SavedVoiceDesign { Code = "C", VoiceId = settings.CustomVoiceId, Description = settings.VoiceDesignDescription } });
        Check(unchanged.EndCalls == 0 && Get<IVoiceSession>(selector, "session") == unchanged,
            "clicking the already-selected saved identity does not end or restart a paid session");
        settings.Provider = "realtime"; settings.Voice = "cedar"; settings.CustomVoiceId = settings.VoiceDesignDescription = "";
        type.GetMethod("SelectStockVoice", Fields).Invoke(selector, new object[] { "cedar" });
        Check(unchanged.EndCalls == 0 && Get<IVoiceSession>(selector, "session") == unchanged,
            "clicking the already-selected stock identity does not reconnect");
        Set(selector, "starting", true);
        type.GetMethod("SelectDesignedVoice", Fields).Invoke(selector, new object[] { new SavedVoiceDesign { Code = "B", VoiceId = "voice_offline_B", Description = "Different saved voice" } });
        Check(settings.Provider == "realtime" && unchanged.EndCalls == 0,
            "direct voice selection during startup cannot mutate the connecting identity or start a second session");
        Set(selector, "starting", false);

        var interrupted = new FakeSession { Continuous = true, Active = true, QueuedAudio = 3 };
        Set(selector, "session", interrupted); Set(selector, "talking", false);
        bool stoppedInput = (bool)type.GetMethod("TryInterruptPushToTalk", Fields).Invoke(selector, new object[] { true, true });
        Check(stoppedInput && interrupted.InterruptCalls == 1 && !interrupted.Active && interrupted.QueuedAudio == 0,
            "Live PTT interruption invokes hard provider stop rather than opening a second input turn");
        Check(interrupted.AppendCalls == 0 && Get<bool>(selector, "releaseRequired") && Get<bool>(selector, "interruptedPushToTalk") && Get<string>(selector, "message").Contains("not recorded"),
            "restart press is gated and explicitly says speech during reconnect is not recorded");
        type.GetMethod("MicrophoneReady", Fields).Invoke(selector, new object[] { true });
        Check(Get<bool>(selector, "releaseRequired") && Get<string>(selector, "state").Contains("release V") && Get<string>(selector, "message").Contains("not recorded"),
            "actual readiness preserves the release requirement and visible repeat notice while V is held");
        Set(selector, "interruptedPushToTalk", true);
        type.GetMethod("MicrophoneReady", Fields).Invoke(selector, new object[] { false });
        Check(!Get<bool>(selector, "releaseRequired") && Get<string>(selector, "state").Contains("hold V to repeat"),
            "release before readiness permits only a new explicit press after reconnect");

        // Exercise the host's new PTT branch against the real Live state
        // machine. Its socket is never connected and its ledger is test-owned.
        var liveLedger = new UsageLedger(Path.Combine(fixture, "offline-live-ledger.json"));
        var liveSettings = new VoiceSettings { Provider = "live" };
        var live = new LiveSession(liveLedger);
        object liveConnection = Activator.CreateInstance(typeof(LiveSession).GetNestedType("Connection", BindingFlags.NonPublic), true);
        Set(liveConnection, "Settings", liveSettings); Set(liveConnection, "UsageId", liveLedger.BeginLiveSession(liveSettings, true));
        Set(liveConnection, "Id", 1L); Set(liveConnection, "Ready", true); Get<TaskCompletionSource<bool>>(liveConnection, "Started").TrySetResult(true);
        typeof(LiveSession).GetField("current", Fields).SetValue(live, liveConnection);
        int liveFrames = 0, reconnects = 0; live.Audio += _ => liveFrames++; live.ReconnectRequested += () => reconnects++;
        var packet = JObject.Parse("{\"type\":\"session.output_audio.delta\",\"delta\":\"AQABAAEA\"}");
        var handle = typeof(LiveSession).GetMethod("Handle", Fields);
        handle.Invoke(live, new object[] { liveConnection, packet });
        string oldSegment = Get<string>(liveConnection, "ItemId"); Set(selector, "session", live);
        stoppedInput = (bool)type.GetMethod("TryInterruptPushToTalk", Fields).Invoke(selector, new object[] { true, true });
        handle.Invoke(live, new object[] { liveConnection, packet });
        Check(stoppedInput && reconnects == 1 && !live.Connected && !live.AcceptsAudio(1, oldSegment) && liveFrames == 1,
            "PTT invokes real Live close and rejects stale provider PCM before dispatching a replacement stream");
        handle.Invoke(live, new object[] { liveConnection, JObject.Parse("{\"type\":\"session.closed\",\"usage\":{\"seconds\":0}}") });
        await live.EndAsync().WaitAsync(TimeSpan.FromSeconds(2)); live.Dispose();

        object guarded = RuntimeHelpers.GetUninitializedObject(type); Set(guarded, "closing", true);
        await (Task)type.GetMethod("StartConversation", Fields).Invoke(guarded, new object[] { false });
        Check(Get<IVoiceSession>(guarded, "session") == null, "conversation start during close cannot create a concurrent provider or access credentials");
        var restartQueue = new MainThreadQueue(); Set(guarded, "dispatch", restartQueue); Set(guarded, "generation", 20);
        var closeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); Set(guarded, "pendingClose", closeGate.Task);
        Task restart = (Task)type.GetMethod("RestartAfterClose", Fields).Invoke(guarded, new object[] { 20, true });
        Check(!restart.IsCompleted, "replacement session waits for the existing provider close gate");
        type.GetMethod("EndConversation", Fields).Invoke(guarded, null); closeGate.TrySetResult(true); await restart.WaitAsync(TimeSpan.FromSeconds(2));
        while (restartQueue.TryDequeue(out callback)) callback();
        Check(Get<int>(guarded, "generation") == 21 && Get<IVoiceSession>(guarded, "session") == null,
            "End during interrupted-session close cancels the queued restart without starting paid work");
    }
    static void Set(object value, string field, object data) { value.GetType().GetField(field, Fields).SetValue(value, data); }
    static T Get<T>(object value, string field) { return (T)value.GetType().GetField(field, Fields).GetValue(value); }
    static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS: " + label); }
    sealed class FakeSession : IVoiceSession
    {
        public readonly TaskCompletionSource<bool> Final = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EndCalls, DisposeCalls, InterruptCalls, AppendCalls, QueuedAudio;
        public bool Continuous, Active;
        public event Action<AudioChunk> Audio { add { } remove { } }
        public event Action<TranscriptChunk> Transcript { add { } remove { } }
        public event Action<string> StateChanged { add { } remove { } }
        public event Action<string> Fault { add { } remove { } }
        public event Action InputSpeechStarted { add { } remove { } }
        public event Action<string> UserTurnCommitted { add { } remove { } }
        public event Action<string> AudioGenerationEnded { add { } remove { } }
        public event Action<VoiceResponseOutcome> ResponseEnded { add { } remove { } }
        public event Action ReconnectRequested { add { } remove { } }
        public bool Connected => Active; public bool Responding => false; public bool RequiresContinuousInput => Continuous; public long ConnectionId => 1; public DateTime LastActivityUtc => DateTime.UtcNow;
        public bool AcceptsAudio(long connectionId, string responseId) => false;
        public Task StartAsync(string key, VoiceSettings settings, Persona persona, string context, bool developmentTest = false) => Task.CompletedTask;
        public Task StartWithInstructionsAsync(string key, VoiceSettings settings, string instructions, bool developmentTest = false) => Task.CompletedTask;
        public void AppendPcm(byte[] pcm) { AppendCalls++; } public void SetInputActive(bool active) { } public void FinishTurn() { }
        public void Interrupt(string itemId, int contentIndex, int heardMs, bool cancelResponse = true) { InterruptCalls++; if (cancelResponse) { Active = false; QueuedAudio = 0; } }
        public void SetHistory(IReadOnlyList<VoiceHistoryMessage> history) { } public void UpdateContext(string context) { } public void ReportPlayback(VoicePlaybackReport report) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task EndAsync() { EndCalls++; return Final.Task; } public void End() { EndCalls++; } public void Dispose() { DisposeCalls++; }
    }
}
