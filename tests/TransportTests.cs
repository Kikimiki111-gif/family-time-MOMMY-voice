using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using System.Net.WebSockets;
using FamilyTimeVoice;
using Newtonsoft.Json.Linq;

static class Program
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
    static void Throws(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("FAIL: " + name); }
    static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static object MakeConnection(RealtimeSession session, VoiceSettings settings)
    {
        var type = typeof(RealtimeSession).GetNestedType("Connection", BindingFlags.NonPublic);
        var c = Activator.CreateInstance(type, true);
        type.GetField("Settings").SetValue(c, settings); type.GetField("Ready").SetValue(c, true); type.GetField("Id").SetValue(c, 1L);
        typeof(RealtimeSession).GetField("current", Hidden).SetValue(session, c); return c;
    }
    static void Handle(RealtimeSession session, object c, string json) { typeof(RealtimeSession).GetMethod("Handle", Hidden).Invoke(session, new[] { c, JObject.Parse(json) }); }
    static string[] Queued(object c)
    {
        var queue = (ConcurrentQueue<byte[]>)c.GetType().GetField("Outgoing").GetValue(c);
        return queue.ToArray().Select(bytes => System.Text.Encoding.UTF8.GetString(bytes)).ToArray();
    }
    static void Set(object c, string field, object value) { c.GetType().GetField(field).SetValue(c, value); }
    static T Get<T>(object c, string field) { return (T)c.GetType().GetField(field).GetValue(c); }
    static void RunWatcher(RealtimeSession session, object c)
    {
        var watch = (Task)typeof(RealtimeSession).GetMethod("WatchLimits", Hidden).Invoke(session, new[] { c });
        if (Task.WhenAny(watch, Task.Delay(1900)).GetAwaiter().GetResult() != watch) throw new Exception("Limit watcher did not stop within its bounded test deadline.");
        watch.GetAwaiter().GetResult();
    }
    static JObject BillableUsage(long audioInput)
    {
        return JObject.Parse("{\"input_tokens\":" + audioInput + ",\"output_tokens\":0,\"input_token_details\":{\"text_tokens\":0,\"audio_tokens\":" + audioInput + "},\"output_token_details\":{\"text_tokens\":0,\"audio_tokens\":0}}");
    }
    static void TimerAndResourceTests(string root, VoiceSettings settings)
    {
        var timerLedger = new UsageLedger(Path.Combine(root, "timer.json")); timerLedger.BeginSession(settings);
        var timerSession = new RealtimeSession(timerLedger); object tc = MakeConnection(timerSession, settings); string fault = "";
        timerSession.Fault += value => fault = value;
        Set(tc, "Started", DateTime.UtcNow.AddSeconds(-settings.MaxSessionSeconds - 5));
        RunWatcher(timerSession, tc);
        Check(!timerSession.Connected && fault.Contains("Session time limit") && Get<bool>(tc, "Closed"), "maximum session timer closes connection resources");

        var idleLedger = new UsageLedger(Path.Combine(root, "idle.json")); idleLedger.BeginSession(settings);
        var idleSession = new RealtimeSession(idleLedger); object ic = MakeConnection(idleSession, settings); string state = "";
        idleSession.StateChanged += value => state = value;
        Set(ic, "Activity", DateTime.UtcNow.AddSeconds(-settings.IdleSeconds - 5));
        RunWatcher(idleSession, ic);
        Check(!idleSession.Connected && state.StartsWith("Disconnected") && state.Contains("idle"), "idle timer disconnects with a resource-release state");

        var budgetLedger = new UsageLedger(Path.Combine(root, "budget.json")); budgetLedger.BeginSession(settings);
        string budgetReserve = budgetLedger.Reserve(settings, 0); budgetLedger.BindResponse(budgetReserve, "large-reported-usage");
        budgetLedger.Report("large-reported-usage", BillableUsage(50001));
        var budgetSession = new RealtimeSession(budgetLedger); object bc = MakeConnection(budgetSession, settings); fault = "";
        budgetSession.Fault += value => fault = value; RunWatcher(budgetSession, bc);
        Check(!budgetSession.Connected && fault.Contains("usage cutoff"), "reported session spending cutoff stops an active session");

        var cumulativeSettings = new VoiceSettings { SessionLimitUsd = 10m }; cumulativeSettings.Validate();
        var totalLedger = new UsageLedger(Path.Combine(root, "total-limit.json")); totalLedger.BeginSession(cumulativeSettings);
        string totalReserve = totalLedger.Reserve(cumulativeSettings, 0); totalLedger.BindResponse(totalReserve, "total-reported-usage");
        totalLedger.Report("total-reported-usage", BillableUsage(500001));
        // Report usage after BeginSession. The session ceiling is raised only in this
        // fixture, so only the unchanged cumulative ceiling can trigger this watcher.
        var totalSession = new RealtimeSession(totalLedger); object cc = MakeConnection(totalSession, cumulativeSettings); fault = "";
        totalSession.Fault += value => fault = value; RunWatcher(totalSession, cc);
        Check(!totalSession.Connected && totalLedger.TotalUsd > cumulativeSettings.CumulativeLimitUsd && totalLedger.SessionUsd < cumulativeSettings.SessionLimitUsd && fault.Contains("usage cutoff"), "reported cumulative spending cutoff stops active resources");

        var pendingLedger = new UsageLedger(Path.Combine(root, "pending-stop.json")); pendingLedger.BeginSession(settings);
        var pendingSession = new RealtimeSession(pendingLedger); object pc = MakeConnection(pendingSession, settings);
        Set(pc, "Ready", false);
        var created = Get<TaskCompletionSource<bool>>(pc, "Created"); var updated = Get<TaskCompletionSource<bool>>(pc, "Updated");
        pendingSession.End();
        Check(created.Task.IsCanceled && updated.Task.IsCanceled && pendingSession.ConnectionId == 0 && Get<bool>(pc, "Closed"), "End cancels pending session handshakes and invalidates epoch");
        Check(Get<ClientWebSocket>(pc, "Socket").State != WebSocketState.Open && Get<ClientWebSocket>(pc, "Socket").State != WebSocketState.Connecting, "End disposes pending socket transport");
        pendingLedger.BeginSession(settings); object rc = MakeConnection(pendingSession, settings);
        pendingSession.AppendPcm(new byte[4800]); pendingSession.FinishTurn(); decimal pendingCost = pendingLedger.TotalUsd;
        Check(Get<bool>(rc, "AwaitingResponse"), "resource test reaches pending response state");
        pendingSession.End();
        Check(!pendingSession.Responding && pendingSession.ConnectionId == 0 && Queued(rc).Length == 0 && pendingLedger.UnresolvedCount == 1 && pendingLedger.TotalUsd == pendingCost, "End drains pending response and retains unknown-usage reserve");

        var overflowLedger = new UsageLedger(Path.Combine(root, "overflow.json")); overflowLedger.BeginSession(settings);
        var overflowSession = new RealtimeSession(overflowLedger); object oc = MakeConnection(overflowSession, settings); fault = "";
        overflowSession.Fault += value => fault = value;
        for (int i = 0; i < 129; i++) overflowSession.AppendPcm(new byte[4800]);
        Check(!overflowSession.Connected && Get<bool>(oc, "Closed") && Queued(oc).Length == 0 && fault.Length > 0, "outgoing queue overflow stops and drains without throwing");
    }
    static void LiveProtocolTests(string root)
    {
        string safe = LiveSession.SafeError(JObject.Parse("{\"code\":\"invalid_value\",\"param\":\"session.audio.format.rate\",\"message\":\"SYNTHETIC_PRIVATE_VALUE\"}"));
        Check(safe.Contains("invalid_value") && safe.Contains("session.audio.format.rate") && !safe.Contains("SYNTHETIC_PRIVATE_VALUE"), "Live schema diagnostics include only allowlisted code and field path");
        safe = LiveSession.SafeError(JObject.Parse("{\"code\":\"SYNTHETIC_PRIVATE_CODE\",\"param\":\"SYNTHETIC_PRIVATE_PARAMETER\"}"));
        Check(!safe.Contains("SYNTHETIC_PRIVATE") && safe.Contains("unrecognized_error") && safe.Contains("param=hidden"), "Live diagnostics hide arbitrary error code and parameter values");
        safe = LiveSession.SafeError(JObject.Parse("{\"code\":\"invalid_client_event\",\"type\":\"invalid_request_error\",\"param\":\"session.input[0].content[1].type\",\"message\":\"SYNTHETIC_PRIVATE_VALUE\"}"), JObject.Parse("{\"type\":\"error\",\"error\":{\"message\":\"SYNTHETIC_PRIVATE_VALUE\"}}"));
        Check(safe.Contains("code=invalid_client_event") && safe.Contains("type=invalid_request_error") && safe.Contains("param=session.input[0].content[1].type") && safe.Contains("message:string") && safe.Contains("event_schema=type:string,error:object") && !safe.Contains("SYNTHETIC_PRIVATE_VALUE"), "Live safe diagnostics admit bounded lowercase protocol identifiers and schema kinds without message values");
        safe = LiveSession.SafeError(JObject.Parse("{\"type\":\"invalid_request_error\",\"code\":{},\"message\":\"SYNTHETIC_PRIVATE_VALUE\"}"));
        Check(safe.Contains("code=unrecognized_error") && safe.Contains("param=absent") && safe.Contains("code:object") && !safe.Contains("SYNTHETIC_PRIVATE_VALUE"), "Live safe diagnostics identify absent or nonstring fields without indexing or printing private payloads");
        safe = LiveSession.SafeError(JObject.Parse("{\"code\":\"missing_scope\",\"type\":\"invalid_request_error\",\"message\":\"Required api.model.live.request and api.live.write, api.model.live.request again. Credential sk-proj-SYNTHETIC_PRIVATE_KEY must stay hidden.\"}"));
        Check(safe.Contains("required_scopes=api.model.live.request,api.live.write") && !safe.Contains("sk-proj-") && !safe.Contains("SYNTHETIC_PRIVATE_KEY") && !safe.Contains("Credential"), "missing-scope diagnostics extract only distinct explicit permission tokens and exclude credential text");
        safe = LiveSession.SafeError(JObject.Parse("{\"code\":\"invalid_value\",\"message\":\"api.model.live.request sk-proj-SYNTHETIC_PRIVATE_KEY\"}"));
        Check(!safe.Contains("required_scopes") && !safe.Contains("api.model.live.request") && !safe.Contains("sk-proj-"), "scope extraction is disabled for every non-missing-scope error");
        var settings = new VoiceSettings { Provider = "live", CustomVoiceId = "voice_test_synthetic", VoiceDesignDescription = "Original mature feminine voice, warm lower-middle register, clear English and relaxed natural delivery.", LiveModel = "gpt-live-1" }; settings.Validate();
        var start = LiveSession.BuildStartEvent(settings, "Exact original wife prompt", new[] { new VoiceHistoryMessage { Role = "user", Text = "No pet names." }, new VoiceHistoryMessage { Role = "assistant", Text = "Understood." } });
        Check((string)start["type"] == "session.start" && (string)start["session"]["model"] == "gpt-live-1" && (int)start["session"]["audio"]["format"]["rate"] == 24000 && (string)start["session"]["audio"]["output"]["voice"]["id"] == settings.CustomVoiceId, "Live startup uses its own model/customvoice/PCM schema");
        Check((string)start["session"]["delegation"]["type"] == "client" && (bool)start["session"]["store"] == false && ((JArray)start["session"]["input"]).Count == 2 && start["session"]["output_modalities"] == null, "Live startup restores bounded history without Realtime fields or recording");
        string actualInstructions = (string)start["session"]["instructions"];
        string recomposed = (string)LiveSession.BuildStartEvent(settings, actualInstructions, null)["session"]["instructions"];
        Check(actualInstructions.Contains("Backchannel policy:") && actualInstructions.Contains("Interruption policy:") && actualInstructions.Contains("Delegation policy:") && actualInstructions.Contains("occasional brief listening cues") && actualInstructions.Contains("No game actions, external lookups, or backend tools are available") && recomposed == actualInstructions, "actual Live startup includes the focused interaction policy once without invented capabilities");
        Throws(() => LiveSession.BuildStartEvent(settings, new string('x', 16384), null), "Live validates the combined persona and interaction-policy instruction bound");
        var ledger = new UsageLedger(Path.Combine(root, "live-protocol.json"));
        var live = new LiveSession(ledger); Type nested = typeof(LiveSession).GetNestedType("Connection", BindingFlags.NonPublic); object c = Activator.CreateInstance(nested, true);
        Set(c, "Settings", settings); Set(c, "UsageId", ledger.BeginLiveSession(settings, true)); Set(c, "Id", 1L); Set(c, "Ready", true); Get<TaskCompletionSource<bool>>(c, "Started").TrySetResult(true);
        typeof(LiveSession).GetField("current", Hidden).SetValue(live, c);
        Action<string> handle = json => typeof(LiveSession).GetMethod("Handle", Hidden).Invoke(live, new[] { c, JObject.Parse(json) });
        Func<string[]> frames = () => Get<ConcurrentQueue<byte[]>>(c, "Outgoing").ToArray().Select(bytes => System.Text.Encoding.UTF8.GetString(bytes)).ToArray();
        live.SetInputActive(true); live.AppendPcm(new byte[] { 0, 16, 0, 16 }); live.FinishTurn();
        Check(frames().Any(s => s.Contains("session.input_audio.unmute")) && frames().Any(s => s.Contains("session.input_audio.append")) && frames().Any(s => s.Contains("session.input_audio.mute")) && !frames().Any(s => s.Contains("input_audio_buffer") || s.Contains("response.create")), "Live PTT uses mute/unmute and continuous audio without Realtime commit loop");
        int audio = 0; AudioChunk output = null; TranscriptChunk transcript = null; int reconnect = 0; int fakeLiveCommits = 0;
        live.UserTurnCommitted += _ => fakeLiveCommits++;
        live.Audio += chunk => { audio++; output = chunk; }; live.Transcript += chunk => transcript = chunk; live.ReconnectRequested += () => reconnect++;
        handle("{\"type\":\"session.output_audio.delta\",\"delta\":\"AAAAAA==\"}"); Check(audio == 0, "Live silent frames cannot keep speaking state alive");
        handle("{\"type\":\"session.output_audio.delta\",\"delta\":\"ABAAEA==\"}");
        Check(audio == 1 && output.ApplicationSegment && live.AcceptsAudio(1, output.ResponseId) && !live.Responding, "Live audio has local playback group without fake response lifecycle");
        handle("{\"type\":\"session.output_audio.delta\",\"delta\":\"AAAAAA==\"}");
        Check(audio == 2 && output.Pcm.All(b => b == 0) && output.SampleOffset == 4 && live.MediaStatistics.ReceivedSamples == 6 && live.MediaStatistics.NonSilentReceivedSamples == 2 && !live.MediaStatistics.ServerTimelineOriginVerified, "Live preserves intra-segment silence and counts every sample without inventing transcript clock alignment");
        handle("{\"type\":\"session.output_transcript.delta\",\"event_id\":\"fragment-a\",\"delta\":\" Hello\",\"start_ms\":100,\"end_ms\":400}");
        Check(transcript.Role == "assistant" && transcript.IsFragment && !transcript.Final && transcript.StartMs == 100 && transcript.EndMs == 400 && transcript.Text == " Hello", "Live captions preserve role, whitespace and approximate server timing without fake done");
        handle("{\"type\":\"session.input_transcript.delta\",\"event_id\":\"fragment-u\",\"delta\":\"mm-hm\",\"start_ms\":200,\"end_ms\":500}");
        Check(transcript.Role == "user" && reconnect == 0, "brief Live listening acknowledgement is not a hard interruption");
        Check(fakeLiveCommits == 0, "Live fragments never invent a committed-user-turn event");
        handle("{\"type\":\"session.delegation.created\",\"delegation\":{\"id\":\"client-work\",\"target\":\"client\"},\"offset_ms\":600}");
        Check(frames().Any(s => s.Contains("session.commentary.append") && s.Contains("client-work")) && !frames().Any(s => s.Contains("response.create")), "unsupported client delegation receives bounded unavailability result without second model");
        live.SendCommentaryAsync("Read this fictional audition sample: There you are.").GetAwaiter().GetResult();
        Check(frames().Any(s => s.Contains("session.commentary.append") && s.Contains("fictional audition") && s.Contains("\"delegation_id\":null")), "Live audition commentary uses documented bounded append without backend work");
        handle("{\"type\":\"session.usage.updated\",\"usage\":{\"seconds\":12}}"); handle("{\"type\":\"session.usage.updated\",\"usage\":{\"seconds\":15}}");
        live.Interrupt(output.ItemId, 0, 20);
        Check(reconnect == 1 && !live.AcceptsAudio(1, output.ResponseId) && !live.Connected && frames().Any(s => s.Contains("session.close")) && !frames().Any(s => s.Contains("response.cancel") || s.Contains("conversation.item.truncate")), "explicit Live stop invalidates playback and requests fresh session with protocol-correct close");
        Check(!live.EndAsync().IsCompleted, "Live keeps transport alive while awaiting final session.closed usage");
        handle("{\"type\":\"session.output_audio.delta\",\"delta\":\"ABAAEA==\"}");
        Check(audio == 2, "hard-stopped Live connection counts but never dispatches late audio");
        handle("{\"type\":\"session.closed\",\"reason\":\"close_requested\",\"usage\":{\"seconds\":16}}"); live.EndAsync().GetAwaiter().GetResult();
        Check(live.ConnectionId == 0 && ledger.SessionUsd == 16m * 0.05m / 60m, "Live final cumulative duration is persisted once before resource disposal");
        Check(live.MediaStatistics.GracefulServerClose && live.MediaStatistics.FinalUsageConfirmed && live.MediaStatistics.NonSilentReceivedSamples == 4 && live.MediaStatistics.IgnoredNonSilentSamples == 2, "Live final statistics retain every nonzero sample including unheard closing output");
        handle("{\"type\":\"session.output_audio.delta\",\"delta\":\"ABAAEA==\"}");
        Check(audio == 2 && live.MediaStatistics.NonSilentReceivedSamples == 4, "closed Live epoch rejects a late callback before dispatch or counter mutation");

        var rtSettings = new VoiceSettings { EnableUserTranscription = true }; var rtLedger = new UsageLedger(Path.Combine(root, "optin-transcription.json")); rtLedger.BeginSession(rtSettings);
        var rt = new RealtimeSession(rtLedger); object rtc = MakeConnection(rt, rtSettings); TranscriptChunk user = null; rt.Transcript += value => user = value; string committed = null; rt.UserTurnCommitted += value => committed = value;
        rt.AppendPcm(new byte[4800]); rt.FinishTurn(); Handle(rt, rtc, "{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"user-turn\"}");
        Check(committed == "user-turn" && user == null, "Realtime reserves factual user-turn order before delayed paid transcription completes");
        Handle(rt, rtc, "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"user-turn\",\"transcript\":\"Please do not call me sweetheart.\",\"usage\":{\"type\":\"duration\",\"seconds\":0.1}}");
        Check(user != null && user.Role == "user" && user.Final && user.Text.Contains("sweetheart"), "opt-in Realtime user transcription reaches memory contract with separate usage");
        VoiceResponseOutcome outcome = null; rt.ResponseEnded += value => outcome = value;
        Handle(rt, rtc, "{\"type\":\"response.created\",\"response\":{\"id\":\"clipped\"}}");
        Handle(rt, rtc, "{\"type\":\"response.done\",\"response\":{\"id\":\"clipped\",\"status\":\"incomplete\",\"status_details\":{\"type\":\"incomplete\",\"reason\":\"max_output_tokens\"},\"output\":[{\"type\":\"message\",\"id\":\"clipped-item\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"input_token_details\":{\"text_tokens\":1,\"audio_tokens\":0},\"output_token_details\":{\"text_tokens\":0,\"audio_tokens\":1}}}}");
        Check(outcome != null && outcome.Status == "incomplete" && outcome.Reason == "max_output_tokens" && outcome.ItemId == "clipped-item", "Realtime actual response outcome reports token clipping separately from playback EOF");
        string endedState = ""; rt.StateChanged += value => endedState = value;
        rt.SendTextAsync("Reply briefly.").GetAwaiter().GetResult();
        Handle(rt, rtc, "{\"type\":\"response.created\",\"response\":{\"id\":\"complete-null\"}}");
        Handle(rt, rtc, "{\"type\":\"response.done\",\"response\":{\"id\":\"complete-null\",\"status\":\"completed\",\"status_details\":null,\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"input_token_details\":{\"text_tokens\":1,\"audio_tokens\":0},\"output_token_details\":{\"text_tokens\":0,\"audio_tokens\":1}}}}");
        Check(outcome.Status == "completed" && outcome.Reason == null && outcome.ItemId == null && !rt.Responding && endedState == "Listening", "actual completed response with JSON-null status details and empty output reaches Listening without throwing"); rt.End();
    }
    static void LiveGroupBoundaryTests(string root)
    {
        var settings = new VoiceSettings { Provider = "live" };
        var ledger = new UsageLedger(Path.Combine(root, "live-groups.json"));
        var live = new LiveSession(ledger); Type nested = typeof(LiveSession).GetNestedType("Connection", BindingFlags.NonPublic); object c = Activator.CreateInstance(nested, true);
        Set(c, "Settings", settings); Set(c, "UsageId", ledger.BeginLiveSession(settings)); Set(c, "Id", 1L); Set(c, "Ready", true); Get<TaskCompletionSource<bool>>(c, "Started").TrySetResult(true);
        typeof(LiveSession).GetField("current", Hidden).SetValue(live, c);
        Action<string> handle = json => typeof(LiveSession).GetMethod("Handle", Hidden).Invoke(live, new[] { c, JObject.Parse(json) });
        AudioChunk latest = null; int fakeEof = 0; live.Audio += chunk => latest = chunk; live.AudioGenerationEnded += _ => fakeEof++;
        byte[] voice = new byte[960]; voice[1] = 16;
        Action<byte[]> packet = pcm => handle("{\"type\":\"session.output_audio.delta\",\"delta\":\"" + Convert.ToBase64String(pcm) + "\"}");
        packet(voice); string first = latest.ItemId;
        live.ReportPlayback(new VoicePlaybackReport { ItemId = first, HeardMilliseconds = 20, Drained = true });
        packet(voice);
        Check(latest.ItemId == first, "Live momentary playback underrun preserves local group without observed quiet");
        packet(new byte[9600]);
        live.ReportPlayback(new VoicePlaybackReport { ItemId = first, HeardMilliseconds = 40, Drained = true });
        Check(Get<string>(c, "ItemId") == first, "Live quiet-group retirement waits for all already-queued PCM to be heard");
        packet(voice);
        live.ReportPlayback(new VoicePlaybackReport { ItemId = first, HeardMilliseconds = 240, Drained = true });
        Check(Get<string>(c, "ItemId") == first && latest.ItemId == first, "new nonzero Live packet prevents a stale drained snapshot retiring in-flight audio");
        packet(new byte[9600]);
        live.ReportPlayback(new VoicePlaybackReport { ItemId = first, HeardMilliseconds = 460, Drained = true });
        packet(voice);
        Check(latest.ItemId != first && fakeEof == 0, "observed 200ms quiet plus actual drain permits a new local group without fake response EOF");
        handle("{\"type\":\"session.closed\",\"reason\":\"close_requested\",\"usage\":{\"seconds\":1}}"); live.EndAsync().GetAwaiter().GetResult();
    }
    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "FamilyTimeVoice-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var settings = new VoiceSettings(); settings.Validate();
            var persona = new Persona(); string profile = Path.Combine(root, "wife.json"); persona.Save(profile);
            Check(Persona.Load(profile).BuildInstructions("Room: test", settings).Contains("adult spouse"), "persona roundtrip applies adult partner identity");
            string ledgerPath = Path.Combine(root, "usage.json"); var ledger = new UsageLedger(ledgerPath); ledger.BeginSession(settings, true);
            string reserve = ledger.Reserve(settings, 1000); decimal reservation = ledger.TotalUsd; Check(reservation > 0, "reserve written before paid turn");
            ledger.BindResponse(reserve, "r1");
            var usage = JObject.Parse("{\"input_tokens\":300,\"output_tokens\":30,\"input_token_details\":{\"text_tokens\":100,\"audio_tokens\":200,\"cached_tokens\":150,\"cached_tokens_details\":{\"text_tokens\":50,\"audio_tokens\":100}},\"output_token_details\":{\"text_tokens\":10,\"audio_tokens\":20}}");
            Check(ledger.Report("r1", usage) && ledger.TotalUsd == 0.001487m, "cached/uncached modality cost exact");
            Check(!ledger.Report("r1", usage) && ledger.TotalUsd == 0.001487m, "duplicate response usage not charged twice");
            string unresolved = ledger.Reserve(settings, 1000); decimal before = ledger.TotalUsd;
            var restarted = new UsageLedger(ledgerPath); Check(restarted.TotalUsd == before && restarted.UnresolvedCount == 1, "unresolved reserve survives process restart");
            Check(restarted.DevelopmentUsd == before, "development allowance includes unresolved and known costs");
            Throws(() => { var cheap = new VoiceSettings { CumulativeLimitUsd = 0.0001m }; restarted.BeginSession(cheap); }, "cumulative cutoff cannot be bypassed by reconnecting");
            Throws(() => { var bad = new VoiceSettings { Model = "unpriced-model" }; restarted.BeginSession(bad); }, "unknown model prices fail closed");
            ledger.ReleaseUncommitted(unresolved);
            string missing = ledger.Reserve(settings, 0); ledger.BindResponse(missing, "missing"); before = ledger.TotalUsd;
            Throws(() => ledger.Report("missing", null), "missing usage stops paid operation"); Check(ledger.TotalUsd == before, "missing usage retains conservative estimate");

            var cancellationLedger = new UsageLedger(Path.Combine(root, "cancel-usage.json")); cancellationLedger.BeginSession(settings);
            var session = new RealtimeSession(cancellationLedger); object c = MakeConnection(session, settings); int audio = 0; session.Audio += _ => audio++;
            session.AppendPcm(new byte[4800]); session.FinishTurn(); session.Interrupt(null, 0, 0);
            Handle(session, c, "{\"type\":\"response.created\",\"response\":{\"id\":\"race\"}}");
            Handle(session, c, "{\"type\":\"response.output_audio.delta\",\"response_id\":\"race\",\"item_id\":\"unheard\",\"content_index\":0,\"delta\":\"AAAAAA==\"}");
            Check(audio == 0 && Queued(c).Any(s => s.Contains("response.cancel")), "cancel before response.created suppresses late audio");
            Check(Queued(c).Any(s => s.Contains("conversation.item.truncate") && s.Contains("\"audio_end_ms\":0")), "unheard cancelled item truncated at zero");
            session.AppendPcm(new byte[4800]); session.FinishTurn();
            Check(Queued(c).Count(s => s.Contains("response.create\"")) == 1, "new response waits for prior cancellation completion");
            Handle(session, c, "{\"type\":\"response.done\",\"response\":{\"id\":\"race\",\"status\":\"cancelled\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"input_token_details\":{\"text_tokens\":1,\"audio_tokens\":0},\"output_token_details\":{\"text_tokens\":0,\"audio_tokens\":1}}}}");
            Check(Queued(c).Count(s => s.Contains("response.create\"")) == 2, "pending turn starts exactly once after cancelled response");
            Handle(session, c, "{\"type\":\"response.created\",\"response\":{\"id\":\"next\"}}");
            Handle(session, c, "{\"type\":\"response.output_audio.delta\",\"response_id\":\"race\",\"item_id\":\"unheard\",\"content_index\":0,\"delta\":\"AAAAAA==\"}");
            Handle(session, c, "{\"type\":\"response.output_audio.delta\",\"response_id\":\"next\",\"item_id\":\"heard\",\"content_index\":0,\"delta\":\"AAAAAA==\"}");
            Check(audio == 1, "late cancelled chunks cannot leak into a newer response");
            session.Interrupt("heard", 0, 125); Check(Queued(c).Any(s => s.Contains("\"item_id\":\"heard\"") && s.Contains("\"audio_end_ms\":125")), "interruption uses item-relative heard duration");
            Check(!session.AcceptsAudio(1, "next"), "already dispatched chunks are rejected immediately after interruption");
            session.End(); Check(!session.Connected && !session.AcceptsAudio(1, "next"), "closed connection rejects already queued callbacks");

            var finalLedger = new UsageLedger(Path.Combine(root, "final-usage.json")); finalLedger.BeginSession(settings);
            var finalSession = new RealtimeSession(finalLedger); object fc = MakeConnection(finalSession, settings);
            finalSession.AppendPcm(new byte[4800]); finalSession.FinishTurn();
            Handle(finalSession, fc, "{\"type\":\"response.created\",\"response\":{\"id\":\"finished\"}}");
            Handle(finalSession, fc, "{\"type\":\"response.output_audio.delta\",\"response_id\":\"finished\",\"item_id\":\"finished-item\",\"content_index\":0,\"delta\":\"AAAAAA==\"}");
            Handle(finalSession, fc, "{\"type\":\"response.done\",\"response\":{\"id\":\"finished\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"input_token_details\":{\"text_tokens\":1,\"audio_tokens\":0},\"output_token_details\":{\"text_tokens\":0,\"audio_tokens\":1}}}}");
            finalSession.AppendPcm(new byte[4800]); finalSession.FinishTurn();
            Handle(finalSession, fc, "{\"type\":\"response.created\",\"response\":{\"id\":\"newer\"}}");
            finalSession.Interrupt("finished-item", 0, 125, false);
            Check(Queued(fc).Any(s => s.Contains("\"item_id\":\"finished-item\"") && s.Contains("\"audio_end_ms\":125")), "completed server response still truncates unplayed local audio");
            Check(!Queued(fc).Any(s => s.Contains("response.cancel") && s.Contains("newer")) && finalSession.AcceptsAudio(1, "newer"), "delayed VAD interruption never cancels a newer response");
            finalSession.Interrupt(null, 0, 0, false);
            Check(finalSession.AcceptsAudio(1, "newer"), "delayed empty-item VAD callback preserves newer generation");
            finalSession.End();

            var errorLedger = new UsageLedger(Path.Combine(root, "errors.json")); errorLedger.BeginSession(settings);
            var errorSession = new RealtimeSession(errorLedger); object ec = MakeConnection(errorSession, settings); string fault = "";
            errorSession.Fault += value => fault = value;
            Handle(errorSession, ec, "{\"type\":\"error\",\"error\":{\"code\":\"insufficient_quota\",\"message\":\"SYNTHETIC_PRIVATE_VALUE\"}}");
            Check(fault.Contains("quota") && !fault.Contains("SYNTHETIC_PRIVATE_VALUE") && !errorSession.Connected, "quota failure is useful, redacted and disconnects");
            Throws(() => errorSession.StartAsync("", settings, persona, "test").GetAwaiter().GetResult(), "missing key fails before network connection");
            errorLedger.BeginSession(settings); object nc = MakeConnection(errorSession, settings); fault = "";
            ((System.Threading.Tasks.Task)typeof(RealtimeSession).GetMethod("ReceiveLoop", Hidden).Invoke(errorSession, new[] { nc })).GetAwaiter().GetResult();
            Check(fault.Contains("stopped") && !errorSession.Connected, "network receive failure stops without retry or raw exception leakage");

            var secret = new SecretStore(Path.Combine(root, "test-key.dpapi")); secret.Save("test-synthetic-key");
            // No real credential is used in this test; keep synthetic value out of output too.
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "test-key.dpapi"))).Contains("test-synthetic-key"), "DPAPI file excludes plaintext key");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))) Check(secret.Load() == "test-synthetic-key", "DPAPI roundtrip under Windows user");
            secret.Delete(); Check(!File.Exists(Path.Combine(root, "test-key.dpapi")), "private key deletion works");
            TimerAndResourceTests(root, settings);
            LiveProtocolTests(root);
            LiveGroupBoundaryTests(root);
            Console.WriteLine("Passed " + passed + " transport/persona/accounting/secret checks; no network or paid API calls.");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string intended = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "FamilyTimeVoice-tests-");
            if (!resolved.StartsWith(intended, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup target escaped its intended temporary directory.");
            Directory.Delete(resolved, true);
        }
    }
}
