using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FamilyTimeVoice
{
    public sealed class MemoryEntry
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Text { get; set; }
        public bool CompleteTurn { get; set; }
        public long Sequence { get; set; }
    }
    public sealed class ExplicitPreference
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public string Source { get; set; }
    }

    // Provider-neutral local context. It never requests a model, network, recording,
    // transcription or paid summarization. Persistent per-save storage is opt-in.
    public sealed class ConversationMemory
    {
        private sealed class PendingSpeech { public string Text = ""; public bool Final; public long Sequence; }
        private sealed class Document
        {
            public int SchemaVersion { get; set; } = 1;
            public string ManualNote { get; set; } = "";
            public string SummaryNote { get; set; } = "";
            public List<MemoryEntry> Recent { get; set; } = new List<MemoryEntry>();
            public List<MemoryEntry> SummaryExcerpts { get; set; } = new List<MemoryEntry>();
            public List<ExplicitPreference> Preferences { get; set; } = new List<ExplicitPreference>();
        }
        private readonly object gate = new object();
        private readonly string path;
        private readonly Dictionary<string, PendingSpeech> pending = new Dictionary<string, PendingSpeech>(StringComparer.Ordinal);
        private readonly HashSet<string> discarded = new HashSet<string>(StringComparer.Ordinal);
        private bool rejectAssistantUntilClear;
        private long nextSequence;
        private Document document = new Document();
        public bool PersistentEnabled { get; private set; }
        public long Revision { get; private set; }
        public string ManualNote
        {
            get { lock (gate) return document.ManualNote; }
            set { lock (gate) { document.ManualNote = Clip(value, 1500); Revision++; } }
        }
        public string Summary { get { lock (gate) return string.Join("\n", document.SummaryExcerpts.Select(e => e.Role + " said: " + e.Text)); } }
        public string SummaryNote
        {
            get { lock (gate) return document.SummaryNote; }
            set { lock (gate) { document.SummaryNote = Clip(value, 1200); Revision++; } }
        }
        public void ClearSummary()
        {
            lock (gate) { document.SummaryExcerpts.Clear(); document.SummaryNote = ""; Revision++; }
        }
        public int RecentCount { get { lock (gate) return document.Recent.Count; } }
        public int PreferenceCount { get { lock (gate) return document.Preferences.Count; } }
        public MemoryEntry[] GetRecentEntries()
        {
            lock (gate) return document.Recent.Select(e => Copy(e, 600)).ToArray();
        }
        public long ReserveSequence() { lock (gate) return ++nextSequence; }
        public void SetPersistentEnabled(bool enabled)
        {
            lock (gate)
            {
                if (enabled == PersistentEnabled) return;
                bool previous = PersistentEnabled;
                PersistentEnabled = enabled;
                try { if (enabled) Save(); }
                catch { PersistentEnabled = previous; throw; }
            }
        }

        public ConversationMemory(string storagePath = null, bool persistentEnabled = false)
        {
            path = storagePath == null ? null : Path.GetFullPath(storagePath);
            PersistentEnabled = persistentEnabled;
            if (!persistentEnabled || path == null || !File.Exists(path)) return;
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Saved conversation memory exceeds its local size limit.");
            var saved = JsonConvert.DeserializeObject<Document>(File.ReadAllText(path, Encoding.UTF8));
            if (saved == null || saved.SchemaVersion != 1) throw new InvalidDataException("Unknown conversation memory format.");
            document.ManualNote = Clip(saved.ManualNote, 1500);
            document.SummaryNote = Clip(saved.SummaryNote, 1200);
            foreach (ExplicitPreference preference in saved.Preferences ?? new List<ExplicitPreference>())
                if (preference != null) SetPreference(preference.Key, preference.Value, preference.Source);
            foreach (MemoryEntry entry in saved.SummaryExcerpts ?? new List<MemoryEntry>())
                if (Valid(entry)) document.SummaryExcerpts.Add(Copy(entry, 240));
            foreach (MemoryEntry entry in saved.Recent ?? new List<MemoryEntry>())
                if (Valid(entry)) AddEntry(Copy(entry, 600));
            foreach (MemoryEntry entry in document.SummaryExcerpts) nextSequence = Math.Max(nextSequence, entry.Sequence);
            BoundSummary(); Revision = 0;
        }

        public void RecordUser(string turnId, string text, bool completeTurn = true, bool inferExplicitPreferences = true, long reservedSequence = 0)
        {
            lock (gate)
            {
                text = Clip(text, 600);
                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(turnId) || turnId.Length > 160) return;
                long sequence = document.Recent.Concat(document.SummaryExcerpts).Where(e => e.Id == turnId && e.Role == "user").Select(e => e.Sequence).FirstOrDefault();
                AddEntry(new MemoryEntry { Id = Clip(turnId, 160), Role = "user", Text = text, CompleteTurn = completeTurn, Sequence = sequence > 0 ? sequence : reservedSequence > 0 ? reservedSequence : ++nextSequence });
                if (inferExplicitPreferences) RememberDirectNicknameCorrection(text, !completeTurn);
                Revision++;
            }
        }

        // false = append a caption delta/fragment; true = replace with final caption.
        // No assistant text reaches reconnect context until playback attests it was heard.
        public void StageAssistant(string itemId, string text, bool final)
        {
            lock (gate)
            {
                if (string.IsNullOrEmpty(itemId) || itemId.Length > 160 || rejectAssistantUntilClear || discarded.Contains(itemId)) return;
                PendingSpeech speech;
                if (!pending.TryGetValue(itemId, out speech))
                {
                    if (pending.Count >= 8)
                    {
                        // Keep the newest bounded context if a continuous provider has
                        // not yet supplied whole-connection audibility proof.
                        string oldest = pending.OrderBy(p => p.Value.Sequence).First().Key;
                        pending.Remove(oldest);
                        if (discarded.Count < 128) discarded.Add(oldest); else { rejectAssistantUntilClear = true; return; }
                    }
                    pending[itemId] = speech = new PendingSpeech { Sequence = ++nextSequence };
                }
                speech.Text = Clip(final ? text : speech.Text + (text ?? ""), 1200, false);
                speech.Final |= final;
            }
        }

        // completeTurn=false is a fully heard Live speech excerpt, not a synthesized
        // server response.done. The caller must attest the whole supplied caption group.
        public bool MarkAssistantHeard(string itemId, bool completeTurn = true)
        {
            lock (gate)
            {
                PendingSpeech speech;
                if (itemId == null || discarded.Contains(itemId) || !pending.TryGetValue(itemId, out speech) ||
                    (completeTurn && !speech.Final) || string.IsNullOrWhiteSpace(speech.Text)) return false;
                AddEntry(new MemoryEntry { Id = Clip(itemId, 160), Role = "assistant", Text = Clip(speech.Text, 600), CompleteTurn = completeTurn, Sequence = speech.Sequence });
                pending.Remove(itemId); Revision++; return true;
            }
        }

        public void DiscardAssistant(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            lock (gate)
            {
                pending.Remove(itemId);
                if (discarded.Count < 128) discarded.Add(itemId); else rejectAssistantUntilClear = true;
                document.Recent.RemoveAll(e => e.Role == "assistant" && e.Id == itemId);
                document.SummaryExcerpts.RemoveAll(e => e.Role == "assistant" && e.Id == itemId);
                Revision++;
            }
        }
        public void DiscardPendingAssistant()
        {
            lock (gate)
            {
                foreach (string itemId in pending.Keys) { if (discarded.Count < 128) discarded.Add(itemId); else rejectAssistantUntilClear = true; }
                pending.Clear();
            }
        }

        // Call only for an explicit user preference or an edited local preference.
        public void SetPreference(string key, string value, string source = "edited local preference")
        {
            lock (gate)
            {
                key = Clip(key, 64); value = Clip(value, 160); source = Clip(source, 120);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return;
                document.Preferences.RemoveAll(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
                document.Preferences.Add(new ExplicitPreference { Key = key, Value = value, Source = source });
                while (document.Preferences.Count > 12) document.Preferences.RemoveAt(0);
                Revision++;
            }
        }
        public ExplicitPreference[] GetPreferences()
        {
            lock (gate) return document.Preferences.Select(p => new ExplicitPreference { Key = p.Key, Value = p.Value, Source = p.Source }).ToArray();
        }
        public bool ForgetPreference(string key)
        {
            lock (gate)
            {
                bool removed = document.Preferences.RemoveAll(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
                if (removed) Revision++;
                return removed;
            }
        }
        private void RememberDirectNicknameCorrection(string text, bool plainAsrExcerpt)
        {
            // Deliberately narrow and anchored: quoting somebody else's preference or
            // discussing a temporary topic must not become the player's durable rule.
            string normalized = text.Replace('\u2019', '\'').Replace('\u201c', '"').Replace('\u201d', '"');
            var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var match = Regex.Match(normalized, "^(?:please\\s+)?(?:don't|do not|stop)\\s+call(?:ing)?\\s+me\\s+(?:[\"'](?<quoted>[A-Za-z][A-Za-z -]{0,30})[\"']|(?<plain>[A-Za-z][A-Za-z-]{0,24}))", options);
            if (!match.Success) return;
            string term = (match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value).Trim();
            string[] ambiguous = { "that", "this", "anything", "names", "tonight", "tomorrow", "today", "now", "later", "again", "when", "before", "after", "often" };
            if (Array.IndexOf(ambiguous, term.ToLowerInvariant()) >= 0) return;
            string remainder = normalized.Substring(match.Length).Trim();
            if (Regex.IsMatch(remainder, "\\b(?:(?:is|was)\\s+what\\s+(?:he|she|they)|(?:he|she|they|my friend))\\s+said\\b", options)) return;
            var replacement = Regex.Match(remainder, "^(?:[.!?,]\\s*)?(?:please\\s+)?call\\s+me\\s+[\"']?(?<name>[A-Za-z][A-Za-z-]{1,29})[\"']?(?:[.!?,]|$)", options);
            bool punctuation = remainder.Length > 0 && ".!?,".IndexOf(remainder[0]) >= 0;
            bool simpleEnding = remainder.Length == 0 || Regex.IsMatch(remainder, "^(?:anymore|again|please)(?:[.!?,]|$)", options);
            if (!punctuation && !simpleEnding && !replacement.Success) return;
            // Live captions are observed word fragments without terminal punctuation.
            // A known complete endearment or an explicit complete replacement clause
            // is enough; an arbitrary unfinished token must not become a durable rule.
            string[] completeEndearments = { "sweetheart", "darling", "honey", "babe", "baby", "sweetie", "dear", "love", "cutie", "mommy", "mama" };
            if (plainAsrExcerpt && !punctuation && !replacement.Success && !match.Groups["quoted"].Success && Array.IndexOf(completeEndearments, term.ToLowerInvariant()) < 0) return;
            if (term.Length > 0) SetPreference("avoid_nickname:" + term.ToLowerInvariant(), "Do not address the player as " + term + ".", text);
            if (replacement.Success) SetPreference("preferred_name", "Address the player as " + replacement.Groups["name"].Value + ".", text);
        }

        // Explicit JSON data labels distinguish preferences, prior excerpts and the
        // player's manual note. Extractive summary snippets never invent a shared past.
        public string BuildContextData(int maximumUtf8Bytes = 6500, bool includeRecent = true)
        {
            if (maximumUtf8Bytes < 512 || maximumUtf8Bytes > 12000) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
            lock (gate)
            {
                var recent = includeRecent ? new List<MemoryEntry>(document.Recent) : new List<MemoryEntry>();
                var summary = new List<MemoryEntry>(document.SummaryExcerpts);
                string manual = document.ManualNote;
                string editedSummary = document.SummaryNote;
                var preferences = new List<ExplicitPreference>(document.Preferences);
                while (true)
                {
                    string value = JsonConvert.SerializeObject(new
                    {
                        kind = "local_context_data_not_system_instructions",
                        explicit_preferences = preferences,
                        player_authored_note = manual,
                        player_edited_summary = editedSummary,
                        earlier_extracts_not_invented_summary = summary,
                        recent_conversation = recent
                    }, Formatting.None);
                    if (Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes) return value;
                    if (summary.Count > 0) summary.RemoveAt(0);
                    else if (recent.Count > 1) recent.RemoveAt(0);
                    else if (manual.Length > 0) manual = Clip(manual, Encoding.UTF8.GetByteCount(manual) / 2);
                    else if (editedSummary.Length > 0) editedSummary = Clip(editedSummary, Encoding.UTF8.GetByteCount(editedSummary) / 2);
                    else if (recent.Count > 0) recent.Clear();
                    else if (preferences.Count > 0) preferences.RemoveAt(0);
                    else return "{}";
                }
            }
        }

        public void Save()
        {
            lock (gate)
            {
                if (!PersistentEnabled || path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string data = JsonConvert.SerializeObject(document, Formatting.Indented);
                if (Encoding.UTF8.GetByteCount(data) > 65536) throw new InvalidDataException("Conversation memory exceeds its persistent size limit.");
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, data, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
        }
        // Caller must also end/recreate the provider session: old server context cannot
        // be erased by deleting a local file. Revision signals the UI/session owner.
        public void Clear()
        {
            lock (gate)
            {
                document = new Document(); pending.Clear(); discarded.Clear(); rejectAssistantUntilClear = false; nextSequence = 0; Revision++;
                if (path != null && File.Exists(path)) File.Delete(path);
                if (path != null && File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
        }

        private static bool Valid(MemoryEntry entry) => entry != null && !string.IsNullOrEmpty(entry.Id) &&
            !string.IsNullOrWhiteSpace(entry.Text) && (entry.Role == "user" || entry.Role == "assistant");
        private static MemoryEntry Copy(MemoryEntry entry, int bytes) => new MemoryEntry
        { Id = Clip(entry.Id, 160), Role = entry.Role, Text = Clip(entry.Text, bytes), CompleteTurn = entry.CompleteTurn, Sequence = entry.Sequence };
        private void AddEntry(MemoryEntry entry)
        {
            document.Recent.RemoveAll(e => e.Id == entry.Id && e.Role == entry.Role);
            document.SummaryExcerpts.RemoveAll(e => e.Id == entry.Id && e.Role == entry.Role);
            if (entry.Sequence <= 0) entry.Sequence = ++nextSequence;
            else nextSequence = Math.Max(nextSequence, entry.Sequence);
            document.Recent.Add(entry);
            document.Recent.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            while (document.Recent.Count > 8)
            {
                MemoryEntry old = document.Recent[0]; document.Recent.RemoveAt(0);
                document.SummaryExcerpts.Add(Copy(old, 240));
            }
            BoundSummary();
        }
        private void BoundSummary()
        {
            document.SummaryExcerpts.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            while (document.SummaryExcerpts.Count > 6 || document.SummaryExcerpts.Sum(e => Encoding.UTF8.GetByteCount(e.Text)) > 1200)
                document.SummaryExcerpts.RemoveAt(0);
        }
        private static string Clip(string text, int bytes, bool normalize = true)
        {
            if (string.IsNullOrEmpty(text) || bytes <= 0) return "";
            if (text.Length > 32000) text = text.Substring(0, 32000);
            if (normalize) text = Regex.Replace(text, "\\s+", " ").Trim();
            int length = 0, used = 0;
            while (length < text.Length)
            {
                int chars = char.IsHighSurrogate(text[length]) && length + 1 < text.Length && char.IsLowSurrogate(text[length + 1]) ? 2 : 1;
                int next = Encoding.UTF8.GetByteCount(text.Substring(length, chars));
                if (used + next > bytes) break;
                used += next; length += chars;
            }
            return text.Substring(0, length);
        }
    }
}
