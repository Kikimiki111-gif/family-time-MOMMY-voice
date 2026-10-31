using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FamilyTimeVoice
{
    // One receive loop and one bounded, serialized send loop per connection.
    // All callbacks are background-thread callbacks; the Unity entry point marshals them.
    public sealed class RealtimeSession : IVoiceSession
    {
        private sealed class Connection
        {
            public readonly ClientWebSocket Socket = new ClientWebSocket();
            public readonly CancellationTokenSource Stop = new CancellationTokenSource();
            public readonly SemaphoreSlim SendSignal = new SemaphoreSlim(0);
            public readonly ConcurrentQueue<byte[]> Outgoing = new ConcurrentQueue<byte[]>();
            public readonly TaskCompletionSource<bool> Created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Updated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly Dictionary<string, string> Reservations = new Dictionary<string, string>();
            public readonly Dictionary<string, string> ItemResponses = new Dictionary<string, string>();
            public readonly HashSet<string> RejectedResponses = new HashSet<string>();
            public readonly HashSet<string> ZeroTruncateResponses = new HashSet<string>();
            public readonly HashSet<string> TruncatedItems = new HashSet<string>();
            public VoiceSettings Settings;
            public int InstructionBytes;
            public DateTime Started = DateTime.UtcNow;
            public DateTime Activity = DateTime.UtcNow;
            public string InputReservation;
            public string PendingReservation;
            public string AwaitingReservation;
            public long Id;
            public long InputBytes;
            public bool InputSpeech;
            public bool InputCommitted;
            public bool RequestPending;
            public bool AwaitingResponse;
            public bool CancelNextResponse;
            public string ActiveResponse;
            public int QueuedCount;
            public int QueuedBytes;
            public bool Ready;
            public bool Closed;
            public string Instructions;
            public string Context;
            public readonly Dictionary<string, int> InputDurations = new Dictionary<string, int>();
            public readonly Queue<string> CommittedCaptures = new Queue<string>();
            public readonly HashSet<string> TranscriptionReservations = new HashSet<string>();
            public readonly HashSet<string> CommittedTranscriptions = new HashSet<string>();
            public readonly Dictionary<string, string> TranscriptionItems = new Dictionary<string, string>();
            public readonly HashSet<string> CompletedTranscriptions = new HashSet<string>();
            public Task SendTask = Task.CompletedTask;
            public Task ReceiveTask = Task.CompletedTask;
            public Task WatchTask = Task.CompletedTask;
        }
        private readonly object gate = new object();
        private readonly UsageLedger ledger;
        private Connection current;
        private long nextConnectionId;
        private readonly List<VoiceHistoryMessage> history = new List<VoiceHistoryMessage>();
        public event Action<AudioChunk> Audio;
        public event Action<TranscriptChunk> Transcript;
        public event Action<string> StateChanged;
        public event Action<string> Fault;
        public event Action InputSpeechStarted;
        public event Action<string> UserTurnCommitted;
        public event Action<string> AudioGenerationEnded;
        public event Action<VoiceResponseOutcome> ResponseEnded;
        public event Action ReconnectRequested { add { } remove { } }
        public bool RequiresContinuousInput => false;
        public bool Connected { get { lock (gate) return current != null && current.Ready && !current.Closed; } }
        public long ConnectionId { get { lock (gate) return current == null ? 0 : current.Id; } }
        public bool AcceptsAudio(long connectionId, string responseId) { lock (gate) return current != null && current.Id == connectionId && !current.RejectedResponses.Contains(responseId); }
        public bool Responding { get { lock (gate) return current != null && (current.ActiveResponse != null || current.AwaitingResponse); } }
        public DateTime LastActivityUtc { get { lock (gate) return current == null ? DateTime.UtcNow : current.Activity; } }
        public RealtimeSession(UsageLedger ledger) { this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger)); }

        public Task StartAsync(string key, VoiceSettings settings, Persona persona, string context, bool developmentTest = false)
        { return StartCoreAsync(key, settings, persona.BuildInstructions(context, settings), context, developmentTest); }
        public Task StartWithInstructionsAsync(string key, VoiceSettings settings, string exactInstructions, bool developmentTest = false)
        { return StartCoreAsync(key, settings, exactInstructions, null, developmentTest); }
        private async Task StartCoreAsync(string key, VoiceSettings settings, string instructions, string context, bool developmentTest)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Enter an API key in the masked local settings field.");
            settings.Validate();
            if (string.IsNullOrWhiteSpace(instructions) || Encoding.UTF8.GetByteCount(instructions) > 16384) throw new InvalidDataException("Supply a bounded nonempty character prompt.");
            // Freeze session settings: editing UI values takes effect on the next connection.
            var frozen = JsonConvert.DeserializeObject<VoiceSettings>(JsonConvert.SerializeObject(settings));
            var connection = new Connection { Settings = frozen, InstructionBytes = Encoding.UTF8.GetByteCount(instructions), Instructions = instructions, Context = context };
            lock (gate)
            {
                if (current != null) throw new InvalidOperationException("End the current conversation before reconnecting.");
                ledger.BeginSession(frozen, developmentTest);
                // Fail before connecting if a complete bounded turn cannot be reserved.
                string testReserve = ledger.Reserve(frozen, connection.InstructionBytes); ledger.ReleaseUncommitted(testReserve);
                connection.Id = ++nextConnectionId; current = connection;
            }
            Notify(StateChanged, "Connecting");
            try
            {
                connection.Socket.Options.SetRequestHeader("Authorization", "Bearer " + key.Trim());
                connection.Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    await connection.Socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(frozen.Model)), timeout.Token).ConfigureAwait(false);
                }
                connection.SendTask = Task.Run(() => SendLoop(connection));
                connection.ReceiveTask = Task.Run(() => ReceiveLoop(connection));
                await WaitHandshake(connection.Created.Task, connection.Stop.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (!IsCurrent(connection)) throw new OperationCanceledException();
                    var input = new JObject { ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = 24000 }, ["turn_detection"] = frozen.HandsFree ? (JToken)new JObject { ["type"] = "server_vad", ["threshold"] = frozen.VadThreshold, ["prefix_padding_ms"] = 300, ["silence_duration_ms"] = frozen.VadSilenceMs, ["create_response"] = true, ["interrupt_response"] = true } : JValue.CreateNull() };
                    if (frozen.EnableUserTranscription) input["transcription"] = new JObject { ["model"] = "gpt-transcribe" };
                    Enqueue(connection, new JObject { ["type"] = "session.update", ["session"] = new JObject { ["type"] = "realtime", ["model"] = frozen.Model, ["output_modalities"] = new JArray("audio"), ["instructions"] = instructions, ["max_output_tokens"] = frozen.MaxOutputTokens, ["audio"] = new JObject { ["input"] = input, ["output"] = new JObject { ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = 24000 }, ["voice"] = frozen.Voice } }, ["truncation"] = new JObject { ["type"] = "retention_ratio", ["retention_ratio"] = 0.8, ["token_limits"] = new JObject { ["post_instructions"] = frozen.ContextTokens } } } });
                }
                await WaitHandshake(connection.Updated.Task, connection.Stop.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (!IsCurrent(connection)) throw new OperationCanceledException();
                    foreach (var prior in history) Enqueue(connection, new JObject { ["type"] = "conversation.item.create", ["item"] = new JObject { ["type"] = "message", ["role"] = prior.Role, ["content"] = new JArray(new JObject { ["type"] = prior.Role == "assistant" ? "output_text" : "input_text", ["text"] = prior.Text }) } });
                    connection.Ready = true; connection.Activity = DateTime.UtcNow;
                }
                connection.WatchTask = Task.Run(() => WatchLimits(connection));
                Notify(StateChanged, "Listening");
            }
            catch (Exception)
            {
                bool report;
                lock (gate) { report = IsCurrent(connection); if (report) CloseConnection(connection); }
                if (report) { Notify(Fault, "Could not connect to OpenAI. Check the private key, selected model access, network and TLS support; no automatic retry."); Notify(StateChanged, "Error"); }
                throw new InvalidOperationException("OpenAI connection failed. Check key/model access and network; details are kept secret.");
            }
        }

        public void AppendPcm(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready || c.Closed) return;
                try
                {
                    if ((pcm.Length & 1) != 0 || pcm.Length > 96000) throw new InvalidDataException("Invalid PCM16 microphone frame.");
                    if (!c.Settings.HandsFree && c.PendingReservation != null) throw new InvalidOperationException("A committed microphone turn is still waiting for the previous response.");
                    EnsureInputReserve(c);
                    c.InputBytes += pcm.Length;
                    if ((!c.Settings.HandsFree || c.InputSpeech) && c.InputBytes > c.Settings.MaxTurnSeconds * 48000L) throw new InvalidOperationException("Maximum microphone turn duration reached; conversation ended to protect usage.");
                    if (!c.Settings.HandsFree) c.Activity = DateTime.UtcNow;
                    Enqueue(c, new JObject { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(pcm) });
                }
                catch (Exception ex) { Fail(c, SafeLocalMessage(ex)); }
            }
        }

        public void FinishTurn()
        {
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready || c.Settings.HandsFree) return;
                try
                {
                    if (c.InputBytes < 4800) { Enqueue(c, new JObject { ["type"] = "input_audio_buffer.clear" }); ReleaseInput(c); return; }
                    c.Activity = DateTime.UtcNow;
                    if (c.Settings.EnableUserTranscription) { c.CommittedCaptures.Enqueue(c.InputReservation); c.CommittedTranscriptions.Add(c.InputReservation); }
                    Enqueue(c, new JObject { ["type"] = "input_audio_buffer.commit" });
                    c.InputCommitted = true; c.RequestPending = true;
                    c.PendingReservation = c.InputReservation; c.InputReservation = null; c.InputBytes = 0;
                    TryCreateResponse(c);
                    Notify(StateChanged, "Responding");
                }
                catch (Exception ex) { Fail(c, SafeLocalMessage(ex)); }
            }
        }

        public void Interrupt(string itemId, int contentIndex, int heardMs, bool cancelResponse = true)
        {
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready) return;
                try
                {
                    string response = cancelResponse ? c.ActiveResponse : null;
                    string itemResponse;
                    bool resolvedItem = !string.IsNullOrEmpty(itemId) && c.ItemResponses.TryGetValue(itemId, out itemResponse);
                    if (resolvedItem) response = c.ItemResponses[itemId];
                    if (response != null) c.RejectedResponses.Add(response);
                    if (response != null && string.IsNullOrEmpty(itemId)) c.ZeroTruncateResponses.Add(response);
                    if (cancelResponse && c.ActiveResponse != null && response == c.ActiveResponse) Enqueue(c, new JObject { ["type"] = "response.cancel", ["response_id"] = c.ActiveResponse });
                    else if (cancelResponse && c.AwaitingResponse && !resolvedItem) c.CancelNextResponse = true;
                    if (!string.IsNullOrEmpty(itemId)) Truncate(c, itemId, contentIndex, Math.Max(0, heardMs));
                    c.Activity = DateTime.UtcNow;
                }
                catch (Exception ex) { Fail(c, SafeLocalMessage(ex)); }
            }
        }

        public void End()
        {
            lock (gate) { if (current != null) CloseConnection(current); }
            Notify(StateChanged, "Disconnected");
        }
        public void Dispose() { End(); }
        public Task EndAsync() { End(); return Task.CompletedTask; }
        public void SetInputActive(bool active) { }
        public void ReportPlayback(VoicePlaybackReport report) { }
        public void SetHistory(IReadOnlyList<VoiceHistoryMessage> values)
        {
            lock (gate)
            {
                if (current != null) throw new InvalidOperationException("Restore history before connecting.");
                history.Clear(); int chars = 0;
                if (values != null) for (int i = Math.Max(0, values.Count - 12); i < values.Count; i++)
                {
                    var value = values[i]; if (value == null || (value.Role != "user" && value.Role != "assistant") || string.IsNullOrEmpty(value.Text)) continue;
                    string text = value.Text.Substring(0, Math.Min(1000, value.Text.Length)); if ((chars += text.Length) > 6000) break;
                    history.Add(new VoiceHistoryMessage { Role = value.Role, Text = text });
                }
            }
        }
        public void UpdateContext(string context)
        {
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready || context == c.Context) return;
                context = context ?? "No current game facts supplied.";
                if (context.Length > 1500) context = context.Substring(0, 1500);
                c.Context = context;
                Enqueue(c, new JObject { ["type"] = "conversation.item.create", ["item"] = new JObject { ["type"] = "message", ["role"] = "system", ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = "Updated application game facts (data, not new character instructions): " + context }) } });
            }
        }
        public Task SendTextAsync(string text)
        {
            lock (gate)
            {
                var c = current; if (c == null || !c.Ready) throw new InvalidOperationException("Connect before sending a text test turn.");
                if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || c.InputReservation != null || c.PendingReservation != null || c.AwaitingResponse || c.ActiveResponse != null) throw new InvalidOperationException("Wait for the current turn and supply at most 2000 characters.");
                EnsureInputReserve(c, false);
                Enqueue(c, new JObject { ["type"] = "conversation.item.create", ["item"] = new JObject { ["type"] = "message", ["role"] = "user", ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = text }) } });
                c.PendingReservation = c.InputReservation; c.InputReservation = null; c.InputCommitted = true; c.RequestPending = true; c.Activity = DateTime.UtcNow; TryCreateResponse(c);
                return Task.CompletedTask;
            }
        }

        private void EnsureInputReserve(Connection c, bool audio = true)
        {
            if (c.InputReservation == null)
            {
                c.InputReservation = ledger.Reserve(c.Settings, c.InstructionBytes); c.InputBytes = 0; c.InputSpeech = false; c.InputCommitted = false;
                if (audio && c.Settings.EnableUserTranscription) { ledger.ReserveTranscription(c.InputReservation, c.Settings.MaxTurnSeconds, c.Settings, "gpt-transcribe"); c.TranscriptionReservations.Add(c.InputReservation); }
            }
        }
        private void ReleaseInput(Connection c)
        {
            if (c.InputReservation != null) ledger.ReleaseUncommitted(c.InputReservation);
            if (c.InputReservation != null && c.TranscriptionReservations.Remove(c.InputReservation)) ledger.ReleaseTranscription(c.InputReservation);
            c.InputReservation = null; c.InputBytes = 0; c.InputCommitted = false; c.InputSpeech = false;
        }
        private void TryCreateResponse(Connection c)
        {
            if (c.RequestPending && c.ActiveResponse == null && !c.AwaitingResponse)
            {
                if (c.PendingReservation == null) throw new InvalidOperationException("Missing accounting reservation for the turn.");
                c.AwaitingReservation = c.PendingReservation; c.PendingReservation = null;
                c.RequestPending = false; c.AwaitingResponse = true;
                Enqueue(c, new JObject { ["type"] = "response.create" });
            }
        }
        private void Truncate(Connection c, string itemId, int index, int heardMs)
        {
            if (!c.TruncatedItems.Add(itemId)) return;
            Enqueue(c, new JObject { ["type"] = "conversation.item.truncate", ["item_id"] = itemId, ["content_index"] = index, ["audio_end_ms"] = heardMs });
        }
        private bool IsCurrent(Connection c) { return ReferenceEquals(current, c) && !c.Closed; }
        private void Enqueue(Connection c, JObject value)
        {
            if (!IsCurrent(c)) return;
            value["event_id"] = "ftv_" + Guid.NewGuid().ToString("N");
            var bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
            if (Interlocked.Increment(ref c.QueuedCount) > 128 || Interlocked.Add(ref c.QueuedBytes, bytes.Length) > 2097152) throw new InvalidOperationException("Network cannot keep up with microphone audio; conversation stopped.");
            c.Outgoing.Enqueue(bytes); c.SendSignal.Release();
        }
        private async Task SendLoop(Connection c)
        {
            try
            {
                while (!c.Stop.IsCancellationRequested)
                {
                    await c.SendSignal.WaitAsync(c.Stop.Token).ConfigureAwait(false);
                    byte[] frame;
                    if (!c.Outgoing.TryDequeue(out frame)) continue;
                    Interlocked.Decrement(ref c.QueuedCount); Interlocked.Add(ref c.QueuedBytes, -frame.Length);
                    await c.Socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text, true, c.Stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { lock (gate) { if (IsCurrent(c)) Fail(c, "OpenAI network send failed. Conversation stopped; reconnect deliberately."); } }
        }
        private async Task ReceiveLoop(Connection c)
        {
            var buffer = new byte[16384];
            try
            {
                while (!c.Stop.IsCancellationRequested)
                {
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await c.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), c.Stop.Token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Connection closed.");
                            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 1048576) throw new InvalidDataException("Invalid server frame.");
                            message.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        JObject value = JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
                        lock (gate) { if (IsCurrent(c)) Handle(c, value); }
                    }
                }
            }
            catch (Exception ex) { lock (gate) { if (IsCurrent(c)) Fail(c, SafeLocalMessage(ex)); } }
        }

        private void Handle(Connection c, JObject message)
        {
            if (!IsCurrent(c)) return;
            string type = (string)message["type"];
            if (type == "session.created") { c.Created.TrySetResult(true); return; }
            if (type == "session.updated") { c.Updated.TrySetResult(true); return; }
            if (type == "input_audio_buffer.speech_started")
            {
                EnsureInputReserve(c); c.InputSpeech = true; c.InputBytes = 0; c.Activity = DateTime.UtcNow;
                if (c.ActiveResponse != null) c.RejectedResponses.Add(c.ActiveResponse);
                Notify(InputSpeechStarted); Notify(StateChanged, "Listening"); return;
            }
            if (type == "input_audio_buffer.committed")
            {
                if (c.Settings.HandsFree) { c.InputCommitted = true; c.InputBytes = 0; }
                string committedItem = (string)message["item_id"];
                if (!string.IsNullOrEmpty(committedItem)) Notify(UserTurnCommitted, committedItem);
                if (c.Settings.EnableUserTranscription)
                {
                    string capture = c.Settings.HandsFree ? c.InputReservation : c.CommittedCaptures.Count > 0 ? c.CommittedCaptures.Dequeue() : null;
                    string item = (string)message["item_id"];
                    if (capture == null || item == null) throw new InvalidDataException("Missing transcription reservation identity.");
                    ledger.BindTranscription(capture, item); c.TranscriptionItems[item] = capture; c.CommittedTranscriptions.Add(capture);
                }
                return;
            }
            if (type == "conversation.item.input_audio_transcription.completed")
            {
                string item = (string)message["item_id"], capture;
                if (item != null && c.CompletedTranscriptions.Contains(item)) return;
                if (item == null || !c.TranscriptionItems.TryGetValue(item, out capture)) throw new InvalidDataException("Unexpected transcription usage.");
                ledger.ReportTranscription(item, message["usage"] as JObject);
                c.TranscriptionItems.Remove(item); c.TranscriptionReservations.Remove(capture); c.CommittedTranscriptions.Remove(capture);
                c.CompletedTranscriptions.Add(item);
                Notify(Transcript, new TranscriptChunk { ConnectionId = c.Id, ItemId = item, FragmentId = (string)message["event_id"], ResponseId = "user-" + item, Role = "user", Text = (string)message["transcript"] ?? "", Final = true }); return;
            }
            if (type == "conversation.item.input_audio_transcription.failed") throw new InvalidDataException("User transcription failed; usage cannot be confirmed.");
            if (type == "response.created")
            {
                string id = (string)message["response"]?["id"];
                if (string.IsNullOrEmpty(id)) throw new InvalidDataException("Invalid response ID.");
                string reservation;
                if (!c.Settings.HandsFree)
                {
                    reservation = c.AwaitingReservation;
                    if (reservation == null) throw new InvalidOperationException("Unexpected unreserved response.");
                    c.AwaitingReservation = null;
                }
                else
                {
                    if (c.InputReservation == null) throw new InvalidOperationException("Unexpected unreserved VAD response.");
                    reservation = c.InputReservation;
                    c.InputReservation = null; c.InputBytes = 0; c.InputSpeech = false; c.InputCommitted = false;
                }
                ledger.BindResponse(reservation, id); c.Reservations[id] = reservation;
                c.ActiveResponse = id; c.AwaitingResponse = false; c.Activity = DateTime.UtcNow;
                if (c.CancelNextResponse) { c.CancelNextResponse = false; c.RejectedResponses.Add(id); c.ZeroTruncateResponses.Add(id); Enqueue(c, new JObject { ["type"] = "response.cancel", ["response_id"] = id }); }
                else Notify(StateChanged, "Responding");
                return;
            }
            string responseId = (string)message["response_id"];
            string itemId = (string)message["item_id"];
            if (type == "response.output_item.added") { itemId = (string)message["item"]?["id"]; }
            if (responseId != null && itemId != null) c.ItemResponses[itemId] = responseId;
            if (responseId != null && c.RejectedResponses.Contains(responseId))
            {
                if (itemId != null && c.ZeroTruncateResponses.Contains(responseId) && (type == "response.output_audio.delta" || (type == "response.content_part.added" && (string)message["part"]?["type"] == "audio"))) Truncate(c, itemId, (int?)message["content_index"] ?? 0, 0);
                if (type != "response.done") return;
            }
            if (type == "response.output_audio.delta")
            {
                if (responseId == null || itemId == null || responseId != c.ActiveResponse) return;
                byte[] pcm = Convert.FromBase64String((string)message["delta"] ?? "");
                if ((pcm.Length & 1) != 0 || pcm.Length > 524288) throw new InvalidDataException("Invalid server audio.");
                c.Activity = DateTime.UtcNow;
                Notify(Audio, new AudioChunk { ConnectionId = c.Id, ResponseId = responseId, ItemId = itemId, ContentIndex = (int?)message["content_index"] ?? 0, Pcm = pcm });
                return;
            }
            if (type == "response.output_audio.done")
            { if (itemId != null && responseId != null && !c.RejectedResponses.Contains(responseId)) Notify(AudioGenerationEnded, itemId); return; }
            if (type == "response.output_audio_transcript.delta" || type == "response.output_audio_transcript.done")
            {
                if (responseId == null || itemId == null || responseId != c.ActiveResponse) return;
                Notify(Transcript, new TranscriptChunk { ConnectionId = c.Id, ResponseId = responseId, ItemId = itemId, ContentIndex = (int?)message["content_index"] ?? 0, Text = (string)message[type.EndsWith(".done", StringComparison.Ordinal) ? "transcript" : "delta"] ?? "", Final = type.EndsWith(".done", StringComparison.Ordinal) }); return;
            }
            if (type == "response.done")
            {
                var response = message["response"] as JObject;
                string id = (string)response?["id"]; if (id == null) throw new InvalidDataException("Invalid final response ID.");
                ledger.Report(id, response["usage"] as JObject); c.Reservations.Remove(id);
                if (c.ActiveResponse == id) c.ActiveResponse = null;
                c.Activity = DateTime.UtcNow;
                string status = (string)response["status"];
                var endedItems = response["output"] as JArray;
                string endedItem = endedItems != null && endedItems.Count > 0 ? (string)endedItems[0]?["id"] : null;
                Notify(ResponseEnded, new VoiceResponseOutcome { ConnectionId = c.Id, ResponseId = id, ItemId = endedItem, Status = status, Reason = (string)(response["status_details"] as JObject)?["reason"] });
                if (status == "failed") throw new InvalidOperationException("OpenAI could not complete the reply. Check model access, quota and rate limits.");
                TryCreateResponse(c);
                if (c.ActiveResponse == null && !c.AwaitingResponse) Notify(StateChanged, "Listening");
                return;
            }
            if (type == "error")
            {
                string code = (string)message["error"]?["code"];
                // The only expected protocol race: cancel arriving after normal completion.
                if (code == "response_cancel_not_active") return;
                string userMessage = code == "invalid_api_key" ? "OpenAI rejected the private API key." : code == "insufficient_quota" ? "OpenAI quota is exhausted. Conversation stopped." : code == "rate_limit_exceeded" ? "OpenAI rate limit reached. Conversation stopped; no automatic retry." : "OpenAI rejected a voice event. Check model/voice access, quota and settings; conversation stopped.";
                Fail(c, userMessage);
            }
        }

        private async Task WatchLimits(Connection c)
        {
            try
            {
                while (!c.Stop.IsCancellationRequested)
                {
                    await Task.Delay(500, c.Stop.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!IsCurrent(c)) return;
                        if ((DateTime.UtcNow - c.Started).TotalSeconds >= c.Settings.MaxSessionSeconds) { Fail(c, "Session time limit reached; microphone and network released."); return; }
                        if ((DateTime.UtcNow - c.Activity).TotalSeconds >= c.Settings.IdleSeconds) { CloseConnection(c); Notify(StateChanged, "Disconnected — idle limit"); return; }
                        if (ledger.TotalUsd >= c.Settings.CumulativeLimitUsd || ledger.SessionUsd >= c.Settings.SessionLimitUsd) { Fail(c, "Estimated mod usage cutoff reached (USD); conversation ended."); return; }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        private void Fail(Connection c, string message)
        {
            if (!IsCurrent(c)) return;
            CloseConnection(c); Notify(Fault, message); Notify(StateChanged, "Error");
        }
        private void CloseConnection(Connection c)
        {
            c.Closed = true; c.Ready = false; if (ReferenceEquals(current, c)) current = null;
            c.Created.TrySetCanceled(); c.Updated.TrySetCanceled();
            try { if (c.InputReservation != null) { if (!c.InputCommitted && !c.InputSpeech && !c.Settings.HandsFree) ledger.ReleaseUncommitted(c.InputReservation); else ledger.MarkUnresolved(c.InputReservation); } } catch { }
            try { if (c.PendingReservation != null) ledger.MarkUnresolved(c.PendingReservation); } catch { }
            try { if (c.AwaitingReservation != null) ledger.MarkUnresolved(c.AwaitingReservation); } catch { }
            foreach (string reservation in c.Reservations.Values) { try { ledger.MarkUnresolved(reservation); } catch { } }
            foreach (string reservation in c.TranscriptionReservations)
            { try { if (c.CommittedTranscriptions.Contains(reservation)) ledger.MarkAuxiliaryUnresolved(reservation); else ledger.ReleaseTranscription(reservation); } catch { } }
            c.Stop.Cancel(); c.Socket.Abort(); c.Socket.Dispose();
            byte[] ignored; while (c.Outgoing.TryDequeue(out ignored)) { }
            Task cleanup = Task.WhenAll(c.SendTask, c.ReceiveTask, c.WatchTask).ContinueWith(_ => { c.SendSignal.Dispose(); c.Stop.Dispose(); }, TaskScheduler.Default);
        }
        private static async Task WaitHandshake(Task ready, CancellationToken stop)
        {
            Task timeout = Task.Delay(12000, stop);
            if (await Task.WhenAny(ready, timeout).ConfigureAwait(false) != ready) throw new TimeoutException("Voice session handshake timed out.");
            await ready.ConfigureAwait(false);
        }
        private static string SafeLocalMessage(Exception error)
        {
            // Never pass server payloads or framework/socket exception text to diagnostics.
            if (error is InvalidOperationException && error.Message.StartsWith("Estimated mod usage", StringComparison.Ordinal)) return error.Message;
            if (error is InvalidOperationException && error.Message.StartsWith("Maximum microphone", StringComparison.Ordinal)) return error.Message;
            return "Voice connection failed or usage could not be verified. Conversation stopped; reservations retained when final usage was unavailable.";
        }
        private static void Notify<T>(Action<T> action, T value) { if (action != null) { try { action(value); } catch { } } }
        private static void Notify(Action action) { if (action != null) { try { action(); } catch { } } }
    }
}
