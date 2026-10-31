using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FamilyTimeVoice
{
    // GPT-Live is a continuous stream. Application segments below are playback
    // bookkeeping, never server response IDs or claims of completed spoken turns.
    public sealed class LiveSession : IVoiceSession
    {
        private const string LiveInteractionPolicy = "\n\nLive interaction policy\n"
            + "Backchannel policy: Use occasional brief listening cues such as mm-hm when natural. Leave room for unfinished thoughts and comfortable pauses; do not compete with the speaker or narrate listening gestures.\n"
            + "Interruption policy: Yield the floor to an explicit wait, stop, or correction. A brief yeah or mm-hm can be an acknowledgment rather than an interruption; use the conversational context instead of always stopping.\n"
            + "Delegation policy: Answer ordinary small talk and relationship conversation directly from the supplied context. No game actions, external lookups, or backend tools are available. Do not invent actions or results. If a request requires an unavailable capability, explain that limit naturally instead of requesting backend work.";
        private sealed class Connection
        {
            public readonly ClientWebSocket Socket = new ClientWebSocket();
            public readonly CancellationTokenSource Stop = new CancellationTokenSource();
            public readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
            public readonly ConcurrentQueue<byte[]> Outgoing = new ConcurrentQueue<byte[]>();
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Final = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly HashSet<string> Delegations = new HashSet<string>();
            public readonly HashSet<string> RejectedSegments = new HashSet<string>();
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public VoiceSettings Settings;
            public string UsageId;
            public long Id;
            public bool Ready, Closing, Closed, Finalized, InputActive, InputMuted, HasPlayback;
            public int Count, Bytes, Segment;
            public string ItemId;
            public long OutputSamples;
            public long QuietSamples, QueuedSamples, IgnoredSamples, IgnoredNonSilentSamples, LastTranscriptEndMs;
            public long NonSilentReceivedSamples;
            public long GroupQueuedSamples;
            public bool ServerCloseReceived;
            public double FirstAudioArrivalMs = -1, LastAudioArrivalMs = -1;
            public DateTime Activity = DateTime.UtcNow;
            public DateTime LastOutput = DateTime.MinValue;
            public string Context;
            public Task Sender = Task.CompletedTask, Receiver = Task.CompletedTask, Watcher = Task.CompletedTask, ClosingTask;
        }
        private readonly object gate = new object();
        private readonly UsageLedger ledger;
        private readonly List<VoiceHistoryMessage> history = new List<VoiceHistoryMessage>();
        private Connection current;
        private long sequence;
        private Task lastClose = Task.CompletedTask;
        private VoiceMediaStatistics lastMedia = new VoiceMediaStatistics();
        public event Action<AudioChunk> Audio;
        public event Action<TranscriptChunk> Transcript;
        public event Action<string> StateChanged;
        public event Action<string> Fault;
        public event Action InputSpeechStarted { add { } remove { } }
        public event Action<string> UserTurnCommitted { add { } remove { } } // Live has transcript fragments, not committed turns.
        public event Action<string> AudioGenerationEnded { add { } remove { } } // No such Live server event.
        public event Action<VoiceResponseOutcome> ResponseEnded { add { } remove { } } // No Live spoken response lifecycle.
        public event Action ReconnectRequested;
        public event Action<VoiceMediaFrame> MediaFrameReceived;
        public VoiceMediaStatistics MediaStatistics { get { lock (gate) return current == null ? lastMedia : Statistics(current); } }
        public bool RequiresContinuousInput => true;
        public bool Connected { get { lock (gate) return current != null && current.Ready && !current.Closing && !current.Closed; } }
        public bool Responding => false; // No Live response lifecycle; use actual audio playback.
        public long ConnectionId { get { lock (gate) return current == null ? 0 : current.Id; } }
        public DateTime LastActivityUtc { get { lock (gate) return current == null ? DateTime.UtcNow : current.Activity; } }
        public bool AcceptsAudio(long id, string segment) { lock (gate) return current != null && current.Id == id && current.Ready && !current.Closing && !current.RejectedSegments.Contains(segment); }
        public LiveSession(UsageLedger ledger) { this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger)); }
        public Task StartAsync(string key, VoiceSettings settings, Persona persona, string context, bool developmentTest = false)
        { return StartCoreAsync(key, settings, persona.BuildInstructions(context, settings), context, developmentTest); }
        public Task StartWithInstructionsAsync(string key, VoiceSettings settings, string exactInstructions, bool developmentTest = false)
        { return StartCoreAsync(key, settings, exactInstructions, null, developmentTest); }
        private async Task StartCoreAsync(string key, VoiceSettings settings, string instructions, string context, bool development)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Enter an API key in the masked local field.");
            settings.Validate();
            instructions = WithInteractionPolicy(instructions);
            var frozen = JsonConvert.DeserializeObject<VoiceSettings>(JsonConvert.SerializeObject(settings));
            var c = new Connection { Settings = frozen, Context = context, InputActive = frozen.HandsFree };
            lock (gate)
            {
                if (current != null || !lastClose.IsCompleted) throw new InvalidOperationException("Wait for the previous voice session to finish closing.");
                c.UsageId = ledger.BeginLiveSession(frozen, development); c.Id = ++sequence; current = c;
            }
            Notify(StateChanged, "Connecting");
            try
            {
                c.Socket.Options.SetRequestHeader("Authorization", "Bearer " + key.Trim());
                c.Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.Stop.Token))
                { timeout.CancelAfter(12000); await c.Socket.ConnectAsync(new Uri("wss://api.openai.com/v1/live/sessions"), timeout.Token).ConfigureAwait(false); }
                c.Sender = Task.Run(() => SendLoop(c)); c.Receiver = Task.Run(() => ReceiveLoop(c));
                lock (gate)
                {
                    if (!Current(c)) throw new OperationCanceledException();
                    Enqueue(c, BuildStartEvent(frozen, instructions, history));
                }
                await Handshake(c.Started.Task, 12000, c.Stop.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (!Current(c) || c.Closing) throw new OperationCanceledException();
                    c.Ready = true; c.Activity = DateTime.UtcNow;
                    if (!c.InputActive) { Enqueue(c, new JObject { ["type"] = "session.input_audio.mute" }); c.InputMuted = true; }
                }
                c.Watcher = Task.Run(() => Watch(c)); Notify(StateChanged, "Listening");
            }
            catch
            {
                lock (gate) { if (Current(c)) Abort(c); }
                Notify(Fault, "GPT-Live startup failed. Check private key, model/voice access and network. No provider fallback or retry.");
                Notify(StateChanged, "Error");
                throw new InvalidOperationException("GPT-Live could not start; credentials and server payloads are hidden.");
            }
        }
        internal static JObject BuildStartEvent(VoiceSettings settings, string instructions, IReadOnlyList<VoiceHistoryMessage> priorHistory)
        {
            instructions = WithInteractionPolicy(instructions);
            JToken voice = string.IsNullOrWhiteSpace(settings.CustomVoiceId) ? (JToken)settings.Voice : new JObject { ["id"] = settings.CustomVoiceId };
            var input = new JArray();
            if (priorHistory != null) foreach (var prior in priorHistory) input.Add(new JObject { ["type"] = "message", ["role"] = prior.Role, ["content"] = new JArray(new JObject { ["type"] = prior.Role == "assistant" ? "output_text" : "input_text", ["text"] = prior.Text }) });
            return new JObject { ["type"] = "session.start", ["session"] = new JObject { ["model"] = settings.LiveModel, ["instructions"] = instructions, ["input"] = input, ["store"] = false, ["audio"] = new JObject { ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = 24000 }, ["output"] = new JObject { ["voice"] = voice } }, ["delegation"] = new JObject { ["type"] = "client" } } };
        }
        private static string WithInteractionPolicy(string instructions)
        {
            if (string.IsNullOrWhiteSpace(instructions)) throw new InvalidDataException("Use a nonempty bounded character prompt.");
            string result = instructions.EndsWith(LiveInteractionPolicy, StringComparison.Ordinal) ? instructions : instructions + LiveInteractionPolicy;
            if (Encoding.UTF8.GetByteCount(result) > 16384) throw new InvalidDataException("The character prompt and Live interaction policy exceed the bounded instruction limit.");
            return result;
        }
        public void SetHistory(IReadOnlyList<VoiceHistoryMessage> values)
        {
            lock (gate)
            {
                if (current != null) throw new InvalidOperationException("Restore history before connecting.");
                history.Clear(); int count = 0;
                if (values != null) for (int i = Math.Max(0, values.Count - 12); i < values.Count; i++)
                { var item = values[i]; if (item == null || (item.Role != "user" && item.Role != "assistant") || string.IsNullOrEmpty(item.Text)) continue; string text = item.Text.Substring(0, Math.Min(1000, item.Text.Length)); if ((count += text.Length) > 6000) break; history.Add(new VoiceHistoryMessage { Role = item.Role, Text = text }); }
            }
        }
        public void SetInputActive(bool active)
        {
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready || c.Closing || c.InputActive == active) return;
                c.InputActive = active; c.Activity = DateTime.UtcNow;
                TryEnqueue(c, new JObject { ["type"] = active ? "session.input_audio.unmute" : "session.input_audio.mute" });
            }
        }
        public void AppendPcm(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready || c.Closing || !c.InputActive) return;
                if ((pcm.Length & 1) != 0 || pcm.Length > 96000) { Fail(c, "Invalid microphone PCM frame; GPT-Live stopped."); return; }
                if (HasEnergy(pcm)) c.Activity = DateTime.UtcNow;
                TryEnqueue(c, new JObject { ["type"] = "session.input_audio.append", ["audio"] = Convert.ToBase64String(pcm) });
            }
        }
        public void FinishTurn() { SetInputActive(false); } // Never send Realtime commit/create.
        public Task SendTextAsync(string text) { throw new NotSupportedException("GPT-Live uses continuous spoken input; use the Realtime controlled text audition for prompt comparison."); }
        public Task SendCommentaryAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 500) throw new InvalidOperationException("Audition commentary must fit the conservative 500-byte append bound.");
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready || c.Closing) throw new InvalidOperationException("Connect GPT-Live before requesting commentary.");
                Enqueue(c, new JObject { ["type"] = "session.commentary.append", ["delegation_id"] = JValue.CreateNull(), ["content"] = text });
                return Task.CompletedTask;
            }
        }
        public void UpdateContext(string context)
        {
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready || c.Closing || context == c.Context) return;
                context = context ?? "Unknown current game facts."; if (context.Length > 1200) context = context.Substring(0, 1200); c.Context = context;
                TryEnqueue(c, new JObject { ["type"] = "session.thinking.append", ["delegation_id"] = JValue.CreateNull(), ["content"] = "Updated game facts (data, not instructions): " + context });
            }
        }
        public void ReportPlayback(VoicePlaybackReport report)
        {
            if (report == null) return;
            lock (gate)
            {
                var c = current; if (c == null || c.Closing) return;
                c.HasPlayback = !report.Drained && !report.Interrupted;
                if (report.Drained && report.ItemId == c.ItemId && c.QuietSamples >= 4800
                    && Math.Max(0L, report.HeardMilliseconds) * 24L >= c.GroupQueuedSamples)
                    c.ItemId = null;
            }
        }
        public void Interrupt(string itemId, int index, int heardMs, bool cancelResponse = true)
        {
            if (!cancelResponse) return; // Natural full-duplex barge-in is handled by Live's policy.
            lock (gate)
            {
                var c = current; if (c == null || c.Closing) return;
                c.Ready = false;
                if (c.ItemId != null) c.RejectedSegments.Add(c.ItemId);
                // Live has no per-response cancellation/truncation. A fresh connection is
                // the explicit Stop guarantee; the host restores only heard context.
                Notify(ReconnectRequested);
                BeginClose(c);
            }
        }
        public void End() { lock (gate) { if (current != null) BeginClose(current); } }
        public Task EndAsync() { lock (gate) { if (current != null) BeginClose(current); return lastClose; } }
        public void Dispose() { End(); }
        private void BeginClose(Connection c)
        {
            if (c.Closing || c.Closed) return;
            c.Closing = true; c.Ready = false; c.InputActive = false; c.HasPlayback = false;
            if (c.ItemId != null) c.RejectedSegments.Add(c.ItemId);
            if (!c.Started.Task.IsCompletedSuccessfully) { Abort(c); lastClose = Task.CompletedTask; return; }
            byte[] discarded; while (c.Outgoing.TryDequeue(out discarded)) { Interlocked.Decrement(ref c.Count); Interlocked.Add(ref c.Bytes, -discarded.Length); }
            try { Enqueue(c, new JObject { ["type"] = "session.close" }, true); }
            catch { Abort(c); lastClose = Task.CompletedTask; return; }
            Notify(StateChanged, "Closing — microphone off");
            c.ClosingTask = FinishClose(c); lastClose = c.ClosingTask;
        }
        private async Task FinishClose(Connection c)
        {
            try { await Handshake(c.Final.Task, 8000, c.Stop.Token).ConfigureAwait(false); if (!c.Finalized) throw new InvalidDataException(); }
            catch { Notify(Fault, "GPT-Live final usage unconfirmed; conservative local estimate retained."); }
            finally { lock (gate) { if (!c.Closed) Abort(c); } Notify(StateChanged, "Disconnected"); }
        }
        private async Task Watch(Connection c)
        {
            try
            {
                while (!c.Stop.IsCancellationRequested)
                {
                    await Task.Delay(500, c.Stop.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!Current(c) || c.Closing) return;
                        try { ledger.UpdateLiveClock(c.UsageId, c.Clock.Elapsed.TotalSeconds, c.Settings); }
                        catch { Fail(c, "Estimated mod usage limit reached (USD); closing GPT-Live."); return; }
                        if (c.Clock.Elapsed.TotalSeconds >= c.Settings.MaxSessionSeconds) { Fail(c, "Session duration limit reached; closing GPT-Live."); return; }
                        if ((DateTime.UtcNow - c.Activity).TotalSeconds >= c.Settings.IdleSeconds) { BeginClose(c); return; }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        private void Handle(Connection c, JObject value)
        {
            if (!Current(c)) return;
            string type = (string)value["type"];
            if (type == "session.started")
            {
                string id = (string)value["session"]?["id"]; if (string.IsNullOrEmpty(id)) throw new InvalidDataException();
                ledger.BindLiveSession(c.UsageId, id); c.Started.TrySetResult(true); return;
            }
            if (type == "session.usage.updated") { ledger.UpdateLiveDuration(c.UsageId, (double?)value["usage"]?["seconds"] ?? throw new InvalidDataException()); return; }
            if (type == "session.closed")
            {
                c.ServerCloseReceived = true;
                double? seconds = (double?)value["usage"]?["seconds"];
                ledger.CompleteLiveSession(c.UsageId, seconds); c.Finalized = seconds.HasValue; c.Final.TrySetResult(c.Finalized);
                if (!c.Closing) { c.Closing = true; c.Ready = false; c.ClosingTask = FinishClose(c); lastClose = c.ClosingTask; }
                return;
            }
            if (type == "session.input_audio.muted") { c.InputMuted = true; return; }
            if (type == "session.input_audio.unmuted") { c.InputMuted = false; return; }
            if (type == "session.output_audio.delta")
            {
                byte[] pcm = Convert.FromBase64String((string)value["delta"] ?? ""); if ((pcm.Length & 1) != 0 || pcm.Length > 524288) throw new InvalidDataException();
                long offset = c.OutputSamples; c.OutputSamples += pcm.Length / 2;
                if (c.FirstAudioArrivalMs < 0) c.FirstAudioArrivalMs = c.Clock.Elapsed.TotalMilliseconds;
                c.LastAudioArrivalMs = c.Clock.Elapsed.TotalMilliseconds;
                bool voice = HasNonZero(pcm); int enqueueBytes = 0;
                c.NonSilentReceivedSamples += CountNonZeroSamples(pcm, 0);
                if (!c.Closing && c.Ready)
                {
                    if (voice) { EnsureSegment(c); c.QuietSamples = 0; enqueueBytes = pcm.Length; if (HasEnergy(pcm)) c.Activity = DateTime.UtcNow; }
                    else if (c.ItemId != null && c.QuietSamples < 4800) { enqueueBytes = (int)Math.Min(pcm.Length, (4800 - c.QuietSamples) * 2); c.QuietSamples += pcm.Length / 2; }
                }
                c.QueuedSamples += enqueueBytes / 2; c.IgnoredSamples += (pcm.Length - enqueueBytes) / 2;
                c.GroupQueuedSamples += enqueueBytes / 2;
                c.IgnoredNonSilentSamples += CountNonZeroSamples(pcm, enqueueBytes);
                Notify(MediaFrameReceived, new VoiceMediaFrame { Pcm = pcm, SampleOffset = offset, Enqueued = enqueueBytes > 0, QueuedSampleCount = enqueueBytes / 2, NonSilent = voice, DuringClose = c.Closing });
                if (enqueueBytes == 0) return;
                c.LastOutput = DateTime.UtcNow;
                byte[] queued = pcm;
                if (enqueueBytes < pcm.Length) { queued = new byte[enqueueBytes]; Buffer.BlockCopy(pcm, 0, queued, 0, enqueueBytes); }
                Notify(Audio, new AudioChunk { ConnectionId = c.Id, ResponseId = c.ItemId, ItemId = c.ItemId, ContentIndex = 0, Pcm = queued, SampleOffset = offset, ApplicationSegment = true }); return;
            }
            if (c.Closing && type != "session.input_transcript.delta" && type != "session.output_transcript.delta") return;
            if (type == "session.input_transcript.delta" || type == "session.output_transcript.delta")
            {
                bool user = type == "session.input_transcript.delta"; string text = (string)value["delta"] ?? "";
                long start = (long?)value["start_ms"] ?? throw new InvalidDataException(); long end = (long?)value["end_ms"] ?? throw new InvalidDataException(); if (start < 0 || end < start) throw new InvalidDataException();
                c.LastTranscriptEndMs = Math.Max(c.LastTranscriptEndMs, end);
                if (!user) EnsureSegment(c);
                string fragmentId = (string)value["event_id"] ?? Guid.NewGuid().ToString("N");
                Notify(Transcript, new TranscriptChunk { ConnectionId = c.Id, ResponseId = user ? "live-user" : c.ItemId, ItemId = user ? fragmentId : c.ItemId, FragmentId = fragmentId, Role = user ? "user" : "assistant", Text = text, StartMs = start, EndMs = end, IsFragment = true, Final = false });
                c.Activity = DateTime.UtcNow;
                if (user && IsExplicitStop(text)) Interrupt(c.ItemId, 0, 0, true);
                return;
            }
            if (type == "session.delegation.created")
            {
                string id = (string)value["delegation"]?["id"], target = (string)value["delegation"]?["target"];
                if (target != "client" || string.IsNullOrEmpty(id)) { Fail(c, "Unexpected unconfigured backend delegation; stopped to protect spending."); return; }
                if (!c.Delegations.Add(id)) return;
                if (c.Delegations.Count > 16) { Fail(c, "Repeated unsupported delegation; ending conversation."); return; }
                Enqueue(c, new JObject { ["type"] = "session.commentary.append", ["delegation_id"] = id, ["content"] = "No external tools or backend work are available in this conversation. No action was performed. Answer ordinary conversation directly; for an unavailable factual lookup, explain that you cannot check it." }); return;
            }
            if (type == "response.event" || type == "decision.event") { Fail(c, "Unconfigured backend work detected; closing to prevent unpriced spending."); return; }
            if (type == "error") { Fail(c, SafeError(value["error"] as JObject, value)); }
        }
        internal static string SafeError(JObject error, JObject envelope = null)
        {
            string code = SafeDiagnosticIdentifier(error?["code"], false, "unrecognized_error");
            string parameter = SafeDiagnosticIdentifier(error?["param"], true, "hidden");
            string category = SafeDiagnosticIdentifier(error?["type"], false, "hidden");
            string scopes = "";
            if (code == "missing_scope" && error?["message"]?.Type == JTokenType.String)
            {
                var required = new List<string>();
                foreach (Match match in Regex.Matches((string)error["message"], @"\bapi\.[a-z_]+(?:\.[a-z_]+){0,8}\b", RegexOptions.CultureInvariant))
                {
                    if (!required.Contains(match.Value)) required.Add(match.Value);
                    if (required.Count == 4) break;
                }
                scopes = "; required_scopes=" + (required.Count == 0 ? "unreported" : string.Join(",", required));
            }
            return "GPT-Live error: code=" + code + "; type=" + category + "; param=" + parameter
                + scopes
                + "; error_schema=" + DiagnosticSchema(error) + "; event_schema=" + DiagnosticSchema(envelope)
                + ". Conversation closed; no automatic retry. Message and private error values hidden.";
        }
        private static string SafeDiagnosticIdentifier(JToken token, bool fieldPath, string hidden)
        {
            if (token == null) return "absent";
            if (token.Type != JTokenType.String) return hidden;
            string value = (string)token;
            if (value == null || value.Length > (fieldPath ? 128 : 80)) return hidden;
            string pattern = fieldPath ? @"^[a-z][a-z_]*(?:\[[0-9]{1,6}\])?(?:\.[a-z][a-z_]*(?:\[[0-9]{1,6}\])?)*$" : @"^[a-z][a-z_]{0,79}$";
            return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant) ? value : hidden;
        }
        private static string DiagnosticSchema(JObject value)
        {
            if (value == null) return "absent";
            var fields = new List<string>();
            foreach (var property in value.Properties())
            {
                if (fields.Count >= 12) break;
                if (Regex.IsMatch(property.Name, @"^[a-z][a-z_]{0,79}$", RegexOptions.CultureInvariant)) fields.Add(property.Name + ":" + property.Value.Type.ToString().ToLowerInvariant());
            }
            return string.Join(",", fields);
        }
        private void EnsureSegment(Connection c) { if (c.ItemId == null) { c.ItemId = "live-segment-" + c.Id + "-" + (++c.Segment); c.QuietSamples = 0; c.GroupQueuedSamples = 0; } }
        private static VoiceMediaStatistics Statistics(Connection c)
        { return new VoiceMediaStatistics { ReceivedSamples = c.OutputSamples, QueuedSamples = c.QueuedSamples, IgnoredSamples = c.IgnoredSamples, IgnoredNonSilentSamples = c.IgnoredNonSilentSamples, NonSilentReceivedSamples = c.NonSilentReceivedSamples, FinalUsageConfirmed = c.Finalized, GracefulServerClose = c.ServerCloseReceived, FirstAudioArrivalMs = c.FirstAudioArrivalMs, LastAudioArrivalMs = c.LastAudioArrivalMs, LastTranscriptEndMs = c.LastTranscriptEndMs, ServerTimelineOriginVerified = false }; }
        private static bool IsExplicitStop(string text)
        {
            string value = text.TrimStart().ToLowerInvariant();
            return value == "stop" || value == "stop." || value == "wait" || value == "wait." || value.StartsWith("stop,", StringComparison.Ordinal) || value.StartsWith("wait,", StringComparison.Ordinal) || value.StartsWith("hold on", StringComparison.Ordinal);
        }
        private bool Current(Connection c) { return ReferenceEquals(current, c) && !c.Closed; }
        private void TryEnqueue(Connection c, JObject value) { try { Enqueue(c, value); } catch { Fail(c, "Voice network cannot keep up; GPT-Live stopped."); } }
        private void Enqueue(Connection c, JObject value, bool closing = false)
        {
            if (!Current(c) || (c.Closing && !closing)) return;
            value["event_id"] = "ftvl_" + Guid.NewGuid().ToString("N"); byte[] bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
            if (Interlocked.Increment(ref c.Count) > 128 || Interlocked.Add(ref c.Bytes, bytes.Length) > 2097152) throw new IOException("Bounded send queue full.");
            c.Outgoing.Enqueue(bytes); c.Signal.Release();
        }
        private async Task SendLoop(Connection c)
        {
            try { while (!c.Stop.IsCancellationRequested) { await c.Signal.WaitAsync(c.Stop.Token).ConfigureAwait(false); byte[] data; if (!c.Outgoing.TryDequeue(out data)) continue; Interlocked.Decrement(ref c.Count); Interlocked.Add(ref c.Bytes, -data.Length); await c.Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, c.Stop.Token).ConfigureAwait(false); } }
            catch { lock (gate) { if (Current(c)) AbortWithError(c, "GPT-Live network send failed; final usage may be estimated."); } }
        }
        private async Task ReceiveLoop(Connection c)
        {
            byte[] buffer = new byte[16384];
            try
            {
                while (!c.Stop.IsCancellationRequested)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do { result = await c.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), c.Stop.Token).ConfigureAwait(false); if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 1048576) throw new IOException(); stream.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
                        var value = JObject.Parse(Encoding.UTF8.GetString(stream.ToArray())); lock (gate) if (Current(c)) Handle(c, value);
                    }
                }
            }
            catch { lock (gate) { if (Current(c) && !c.Finalized) AbortWithError(c, "GPT-Live connection lost before confirmed finalization; conservative usage retained."); else if (Current(c)) Abort(c); } }
        }
        private void Fail(Connection c, string message) { if (!Current(c) || c.Closing) return; Notify(Fault, message); BeginClose(c); }
        private void AbortWithError(Connection c, string message) { Abort(c); Notify(Fault, message); Notify(StateChanged, "Error"); }
        private void Abort(Connection c)
        {
            if (c.Closed) return; lastMedia = Statistics(c); c.Closed = true; c.Ready = false; c.Closing = true; if (ReferenceEquals(current, c)) current = null;
            if (!c.Finalized) { try { ledger.CompleteLiveSession(c.UsageId, null); } catch { } }
            c.Started.TrySetCanceled(); c.Final.TrySetCanceled(); c.Stop.Cancel(); c.Socket.Abort(); c.Socket.Dispose(); byte[] discarded; while (c.Outgoing.TryDequeue(out discarded)) { }
            Task cleanup = Task.WhenAll(c.Sender, c.Receiver, c.Watcher).ContinueWith(_ => { c.Signal.Dispose(); c.Stop.Dispose(); }, TaskScheduler.Default);
        }
        private static bool HasEnergy(byte[] pcm) { for (int i = 0; i + 1 < pcm.Length; i += 32) { int sample = (short)(pcm[i] | pcm[i + 1] << 8); if (Math.Abs(sample) > 600) return true; } return false; }
        private static bool HasNonZero(byte[] pcm) { for (int i = 0; i < pcm.Length; i++) if (pcm[i] != 0) return true; return false; }
        private static long CountNonZeroSamples(byte[] pcm, int offset) { long count = 0; for (int i = offset; i + 1 < pcm.Length; i += 2) if (pcm[i] != 0 || pcm[i + 1] != 0) count++; return count; }
        private static async Task Handshake(Task value, int milliseconds, CancellationToken stop) { if (await Task.WhenAny(value, Task.Delay(milliseconds, stop)).ConfigureAwait(false) != value) throw new TimeoutException(); await value.ConfigureAwait(false); }
        private static void Notify<T>(Action<T> handler, T value) { if (handler != null) try { handler(value); } catch { } }
        private static void Notify(Action handler) { if (handler != null) try { handler(); } catch { } }
    }
}
