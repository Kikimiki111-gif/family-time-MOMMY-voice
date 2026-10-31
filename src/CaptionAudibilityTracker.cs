using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FamilyTimeVoice
{
    // Reconnect context admission only; this never controls playback or a provider.
    // Call after the production generation/connection guard has rejected stale events.
    public sealed class CaptionAudibilityTracker
    {
        private sealed class Group
        {
            public string ItemId, ResponseId, MemoryId, Outcome;
            public long ReceivedSamples;
            public bool CaptionFinal, AudioEof, Drained;
            public int HeardMs, ContentIndex = -1;
        }
        private readonly object gate = new object();
        private readonly ConversationMemory memory;
        private readonly Dictionary<string, Group> groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, string> outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> settled = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> fragments = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> userOrder = new Dictionary<string, long>(StringComparer.Ordinal);
        private string scope;
        private bool live, interrupted, assistantOverflow, hadAudio;
        private long connectionId;
        private int userSequence;
        private string userText = "";
        private long userEndMs = -1;
        private DateTime lastUserArrival;
        public string AssistantMemoryOmissionReason { get; private set; }
        public int PendingAssistantCount { get { lock (gate) return groups.Count; } }

        public CaptionAudibilityTracker(ConversationMemory memory, bool live = false)
        {
            this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
            BeginConnection(Guid.NewGuid().ToString("N"), live);
        }
        public void BeginConnection(string scope, bool live, long expectedConnectionId = 0)
        {
            lock (gate)
            {
                FlushUser();
                foreach (Group group in groups.Values) memory.DiscardAssistant(group.MemoryId);
                groups.Clear(); order.Clear(); outcomes.Clear(); settled.Clear(); fragments.Clear(); userOrder.Clear();
                this.scope = string.IsNullOrEmpty(scope) ? Guid.NewGuid().ToString("N") : scope;
                this.live = live; connectionId = expectedConnectionId;
                interrupted = assistantOverflow = hadAudio = false; userText = ""; userEndMs = -1;
                AssistantMemoryOmissionReason = null;
            }
        }

        public void ObserveAudio(AudioChunk chunk)
        {
            if (chunk?.Pcm == null || (chunk.Pcm.Length & 1) != 0 || chunk.Pcm.Length == 0) return;
            lock (gate)
            {
                if (!AcceptConnection(chunk.ConnectionId)) return;
                Group group = GetGroup(chunk.ItemId, chunk.ResponseId);
                if (group == null || !AcceptContent(group, chunk.ContentIndex)) return;
                group.ReceivedSamples += chunk.Pcm.Length / 2;
                group.Drained = false; hadAudio = true;
            }
        }

        public void ObserveTranscript(TranscriptChunk chunk, DateTime? observedUtc = null)
        {
            if (chunk == null || string.IsNullOrEmpty(chunk.Text)) return;
            lock (gate)
            {
                if (!AcceptConnection(chunk.ConnectionId)) return;
                if (chunk.Role == "user")
                {
                    if (!chunk.IsFragment)
                    {
                        if (chunk.Final)
                        {
                            long sequence;
                            userOrder.TryGetValue(chunk.ItemId ?? "", out sequence);
                            memory.RecordUser(MemoryId("user:" + chunk.ItemId), chunk.Text, reservedSequence: sequence);
                            if (chunk.ItemId != null) userOrder.Remove(chunk.ItemId);
                        }
                        return;
                    }
                    if (string.IsNullOrEmpty(chunk.FragmentId) || fragments.Contains("u:" + chunk.FragmentId)) return;
                    if (fragments.Count >= 2048) return;
                    fragments.Add("u:" + chunk.FragmentId);
                    if (userText.Length > 0 && chunk.StartMs >= 0 && userEndMs >= 0 && chunk.StartMs - userEndMs > 1500) FlushUser();
                    if (userText.Length + chunk.Text.Length > 3000) FlushUser();
                    userText += chunk.Text.Substring(0, Math.Min(chunk.Text.Length, 3000));
                    userEndMs = Math.Max(userEndMs, chunk.EndMs);
                    lastUserArrival = observedUtc ?? DateTime.UtcNow;
                    return;
                }
                if (chunk.Role != "assistant") return;
                if (userText.Length > 0) FlushUser();
                if (chunk.IsFragment)
                {
                    if (string.IsNullOrEmpty(chunk.FragmentId) || fragments.Contains("a:" + chunk.FragmentId)) return;
                    if (fragments.Count >= 2048) { OmitAssistant("Caption bookkeeping limit reached; assistant context omitted."); return; }
                    fragments.Add("a:" + chunk.FragmentId);
                }
                Group group = GetGroup(chunk.ItemId, chunk.ResponseId);
                if (group == null || !AcceptContent(group, chunk.ContentIndex)) return;
                memory.StageAssistant(group.MemoryId, chunk.Text, chunk.Final);
                group.CaptionFinal |= chunk.Final;
                TryCommit(group);
            }
        }

        // Actual Realtime input_audio_buffer.committed item identity reserves order
        // before an asynchronously completed transcription can arrive after the reply.
        public void UserTurnCommitted(string itemId)
        {
            lock (gate)
            {
                if (live || string.IsNullOrEmpty(itemId) || itemId.Length > 200 || userOrder.ContainsKey(itemId) || userOrder.Count >= 128) return;
                userOrder[itemId] = memory.ReserveSequence();
            }
        }

        // This must be the actual Realtime output_audio.done item ID, never local quiet.
        public void AudioGenerationEnded(string itemId)
        {
            lock (gate)
            {
                if (live) return;
                Group group = GetGroup(itemId, null);
                if (group == null) return;
                group.AudioEof = true; TryCommit(group);
            }
        }
        public void ResponseEnded(VoiceResponseOutcome outcome)
        {
            if (outcome == null) return;
            lock (gate)
            {
                if (live || !AcceptConnection(outcome.ConnectionId)) return;
                if (outcomes.Count < 128 && !string.IsNullOrEmpty(outcome.ResponseId)) outcomes[outcome.ResponseId] = outcome.Status;
                foreach (Group group in groups.Values.ToArray())
                {
                    if (group.ResponseId != outcome.ResponseId && group.ItemId != outcome.ItemId) continue;
                    group.Outcome = outcome.Status;
                    if (outcome.Status == "cancelled" || outcome.Status == "failed") Interrupt(group.ItemId);
                    else TryCommit(group);
                }
            }
        }
        public void ReportPlayback(VoicePlaybackReport report)
        {
            if (report == null || string.IsNullOrEmpty(report.ItemId)) return;
            lock (gate)
            {
                if (report.Interrupted) { Interrupt(report.ItemId); return; }
                Group group;
                if (!groups.TryGetValue(report.ItemId, out group)) return;
                group.HeardMs = Math.Max(group.HeardMs, Math.Max(0, report.HeardMilliseconds));
                group.Drained = report.Drained; TryCommit(group);
            }
        }
        public void Interrupt(string itemId)
        {
            lock (gate)
            {
                interrupted = true;
                if (string.IsNullOrEmpty(itemId))
                {
                    foreach (Group group in groups.Values.ToArray()) Discard(group);
                }
                else
                {
                    Group group;
                    if (groups.TryGetValue(itemId, out group)) Discard(group);
                    else Settle(itemId);
                }
            }
        }

        // The primary Live protocol does not provide an output-PCM timestamp anchor or
        // spoken-turn EOF. Admit excerpts only after proof that the ENTIRE connection's
        // produced speech was heard, including frames received during graceful close.
        // HasAudio==false, silence, caption arrival and a guessed clock offset are not proof.
        public bool FinalizeLiveConnection(bool gracefulServerClose, bool allProducedSpeechHeard, bool wasInterrupted = false)
        {
            lock (gate)
            {
                FlushUser();
                if (!live) return false;
                if (!gracefulServerClose || !allProducedSpeechHeard || wasInterrupted || interrupted || assistantOverflow || !hadAudio)
                {
                    OmitAssistant("Live assistant context omitted: complete heard-output coverage was not established.");
                    return false;
                }
                bool committed = false;
                foreach (string itemId in order.ToArray())
                {
                    Group group;
                    if (!groups.TryGetValue(itemId, out group)) continue;
                    committed |= memory.MarkAssistantHeard(group.MemoryId, false);
                    Settle(itemId); groups.Remove(itemId);
                }
                order.Clear(); return committed;
            }
        }
        public void Tick(DateTime? nowUtc = null)
        {
            lock (gate)
                if (userText.Length > 0 && ((nowUtc ?? DateTime.UtcNow) - lastUserArrival).TotalMilliseconds >= 1500) FlushUser();
        }
        public void FlushUser()
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(userText)) return;
                // A quiet grouping boundary is an excerpt boundary, never a fabricated
                // provider EOF. The memory recognizer conservatively checks a direct
                // complete correction clause; actual Live ASR often has no punctuation.
                memory.RecordUser(MemoryId("user-fragments:" + (++userSequence)), userText, false, true);
                userText = ""; userEndMs = -1;
            }
        }
        public void Clear()
        {
            lock (gate)
            {
                groups.Clear(); order.Clear(); outcomes.Clear(); settled.Clear(); fragments.Clear(); userOrder.Clear();
                userText = ""; userEndMs = -1; memory.Clear();
                scope = Guid.NewGuid().ToString("N"); interrupted = assistantOverflow = hadAudio = false;
                AssistantMemoryOmissionReason = null;
            }
        }

        private Group GetGroup(string itemId, string responseId)
        {
            if (string.IsNullOrEmpty(itemId) || itemId.Length > 200 || settled.Contains(itemId) || assistantOverflow) return null;
            Group group;
            if (!groups.TryGetValue(itemId, out group))
            {
                if (groups.Count >= 64) { OmitAssistant("Pending caption limit reached; assistant context omitted."); return null; }
                group = new Group { ItemId = itemId, ResponseId = responseId, MemoryId = MemoryId("assistant:" + itemId) };
                groups[itemId] = group; order.Add(itemId);
            }
            if (responseId != null)
            {
                group.ResponseId = responseId;
                string status;
                if (outcomes.TryGetValue(responseId, out status)) group.Outcome = status;
            }
            return group;
        }
        private bool AcceptContent(Group group, int contentIndex)
        {
            if (group.ContentIndex < 0) group.ContentIndex = contentIndex;
            if (group.ContentIndex == contentIndex) return true;
            Discard(group); return false;
        }
        private bool AcceptConnection(long id)
        {
            if (connectionId == 0) connectionId = id;
            return id == connectionId;
        }
        private void TryCommit(Group group)
        {
            if (live || !group.CaptionFinal || !group.AudioEof || !group.Drained || group.ReceivedSamples == 0) return;
            if (group.Outcome != "completed" && group.Outcome != "incomplete") return;
            // PCM24k, integer milliseconds rounded down by playback: tolerate only the
            // final sub-millisecond sample remainder, not any queued/unheard duration.
            if ((long)group.HeardMs * 24 + 23 < group.ReceivedSamples) return;
            if (!memory.MarkAssistantHeard(group.MemoryId, group.Outcome == "completed")) return;
            Settle(group.ItemId); groups.Remove(group.ItemId); order.Remove(group.ItemId);
        }
        private void Discard(Group group)
        {
            memory.DiscardAssistant(group.MemoryId); Settle(group.ItemId);
            groups.Remove(group.ItemId); order.Remove(group.ItemId);
        }
        private void Settle(string itemId)
        {
            if (settled.Count >= 1024) { OmitAssistant("Caption deduplication limit reached; assistant context omitted."); return; }
            settled.Add(itemId);
        }
        private void OmitAssistant(string reason)
        {
            AssistantMemoryOmissionReason = reason; assistantOverflow = true;
            foreach (Group group in groups.Values) memory.DiscardAssistant(group.MemoryId);
            groups.Clear(); order.Clear();
        }
        private string MemoryId(string id)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(scope + "|" + id))).Replace("-", "");
        }
    }
}
