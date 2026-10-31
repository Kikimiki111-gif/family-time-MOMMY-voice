using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FamilyTimeVoice
{
    public sealed class PriceTable
    {
        public string Model { get; set; } = "gpt-realtime-2.1-mini";
        public string VerifiedDate { get; set; } = "2026-10-31";
        public string Currency { get; set; } = "USD";
        public decimal TextInput { get; set; } = 0.60m;
        public decimal TextCached { get; set; } = 0.06m;
        public decimal TextOutput { get; set; } = 2.40m;
        public decimal AudioInput { get; set; } = 10.00m;
        public decimal AudioCached { get; set; } = 0.30m;
        public decimal AudioOutput { get; set; } = 20.00m;
        public decimal ImageInput { get; set; } = 0.80m;
        public decimal ImageCached { get; set; } = 0.08m;
        public string Source { get; set; } = "https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini";
        public void Validate(string model)
        {
            DateTime date;
            if (Model != model || Currency != "USD" || !DateTime.TryParse(VerifiedDate, out date) || TextInput <= 0 || TextOutput <= 0 || AudioInput <= 0 || AudioOutput <= 0 || ImageInput <= 0 || TextCached < 0 || AudioCached < 0 || ImageCached < 0 || TextCached > TextInput || AudioCached > AudioInput || ImageCached > ImageInput)
                throw new InvalidOperationException("No valid USD price table for the selected model. Verify and edit prices.json before connecting.");
        }
    }

    public sealed class UsageEntry
    {
        public string ReservationId { get; set; }
        public string ResponseId { get; set; }
        public string SessionId { get; set; }
        public string CreatedUtc { get; set; }
        public bool Development { get; set; }
        public string Status { get; set; }
        public decimal EstimatedUsd { get; set; }
        public long InputText { get; set; }
        public long InputAudio { get; set; }
        public long CachedText { get; set; }
        public long CachedAudio { get; set; }
        public long OutputText { get; set; }
        public long OutputAudio { get; set; }
    }
    public sealed class LedgerData
    {
        public int Version { get; set; } = 1;
        public List<UsageEntry> Entries { get; set; } = new List<UsageEntry>();
        // Additive Version 1 fields: existing Realtime entries and totals are never reset.
        public List<LiveUsageEntry> LiveSessions { get; set; } = new List<LiveUsageEntry>();
        public List<AuxiliaryUsageEntry> Auxiliary { get; set; } = new List<AuxiliaryUsageEntry>();
    }

    public sealed class LiveUsageEntry
    {
        public string SessionId { get; set; }
        public string ApiSessionId { get; set; }
        public string CreatedUtc { get; set; }
        public bool Development { get; set; }
        public string Status { get; set; } = "pending";
        public decimal ReportedSeconds { get; set; }
        public decimal WallSeconds { get; set; }
        public decimal EstimatedUsd { get; set; }
        public bool FinalDurationConfirmed { get; set; }
        public decimal VoiceRateUsdPerMinute { get; set; } = 0.05m;
        public int CloseReserveSeconds { get; set; } = 10;
    }

    public sealed class AuxiliaryUsageEntry
    {
        public string ReservationId { get; set; }
        public string SessionId { get; set; }
        public string ExternalId { get; set; }
        public string Kind { get; set; }
        public string Model { get; set; }
        public bool Development { get; set; }
        public string Status { get; set; } = "pending";
        public decimal EstimatedUsd { get; set; }
        public decimal Seconds { get; set; }
        public decimal UnitRateUsdPerMinute { get; set; }
    }

    public sealed class BackendTokenPrice
    {
        public decimal Input { get; set; }
        public decimal CachedInput { get; set; }
        public decimal Output { get; set; }
        public Dictionary<string, decimal> ToolUsdPerCall { get; set; } = new Dictionary<string, decimal>();
    }

    public sealed class LivePriceTable
    {
        public string Model { get; set; } = "gpt-live-1";
        public string Currency { get; set; } = "USD";
        public string VerifiedDate { get; set; } = "2026-10-31";
        public decimal VoiceUsdPerMinute { get; set; } = 0.05m;
        public int CloseReserveSeconds { get; set; } = 10;
        public string TranscriptionModel { get; set; } = "gpt-transcribe";
        public decimal TranscriptionUsdPerMinute { get; set; } = 0.0045m;
        public string Source { get; set; } = "https://developers.openai.com/api/docs/pricing";
        // No implicit model/tool tariff. The initial client-delegation provider runs no backend.
        public Dictionary<string, BackendTokenPrice> BackendModels { get; set; } = new Dictionary<string, BackendTokenPrice>();

        public void Validate()
        {
            DateTime date;
            if (Model != "gpt-live-1" || Currency != "USD" || !DateTime.TryParse(VerifiedDate, out date) || VoiceUsdPerMinute <= 0 || CloseReserveSeconds < 5 || CloseReserveSeconds > 60 || TranscriptionUsdPerMinute <= 0 || string.IsNullOrWhiteSpace(TranscriptionModel) || BackendModels == null)
                throw new InvalidDataException("No valid dated Live/transcription USD price table; paid use stopped.");
        }
    }

    public sealed class UsageLedger
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly LedgerData data;
        private string sessionId;
        private bool development;
        public PriceTable Prices { get; private set; }
        public LivePriceTable LivePrices { get; private set; }
        // Entries is the cost authority, including legacy-shaped Live/Aux mirrors.
        // The old plugin preserves these rows even when it drops new metadata fields.
        public decimal TotalUsd { get { lock (gate) return data.Entries.Sum(e => e.EstimatedUsd); } }
        public decimal SessionUsd { get { lock (gate) return SessionCost(sessionId); } }
        public decimal DevelopmentUsd { get { lock (gate) return data.Entries.Where(e => e.Development).Sum(e => e.EstimatedUsd); } }
        public int UnresolvedCount { get { lock (gate) return data.Entries.Count(e => e.Status != "reported"); } }
        public string PricePath { get; private set; }
        public string LivePricePath { get; private set; }

        public UsageLedger(string path, string pricePath = null)
        {
            this.path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(this.path));
            PricePath = pricePath ?? Path.Combine(Path.GetDirectoryName(this.path), "prices.json");
            Prices = File.Exists(PricePath) ? JsonConvert.DeserializeObject<PriceTable>(File.ReadAllText(PricePath)) : new PriceTable();
            if (Prices == null) throw new InvalidDataException("The price table is empty.");
            if (!File.Exists(PricePath)) AtomicWrite(PricePath, Prices);
            LivePricePath = Path.Combine(Path.GetDirectoryName(this.path), "live-prices.json");
            LivePrices = File.Exists(LivePricePath) ? JsonConvert.DeserializeObject<LivePriceTable>(File.ReadAllText(LivePricePath)) : new LivePriceTable();
            if (LivePrices == null) throw new InvalidDataException("The Live price table is empty.");
            if (!File.Exists(LivePricePath)) AtomicWrite(LivePricePath, LivePrices);
            data = File.Exists(this.path) ? JsonConvert.DeserializeObject<LedgerData>(File.ReadAllText(this.path)) : new LedgerData();
            if (data == null || data.Version != 1 || data.Entries == null || data.LiveSessions == null || data.Auxiliary == null || data.Entries.Any(e => e.EstimatedUsd < 0) || data.LiveSessions.Any(e => e.EstimatedUsd < 0 || e.ReportedSeconds < 0 || e.WallSeconds < 0 || e.VoiceRateUsdPerMinute <= 0 || e.CloseReserveSeconds < 5) || data.Auxiliary.Any(e => e.EstimatedUsd < 0)) throw new InvalidDataException("Usage ledger is invalid; paid use is stopped. Restore its backup before reconnecting.");
            foreach (var item in data.Entries.Where(e => e.Status == "pending")) item.Status = "unresolved";
            foreach (var item in data.LiveSessions.Where(e => e.Status == "pending")) item.Status = "unresolved";
            foreach (var item in data.Auxiliary.Where(e => e.Status == "pending")) item.Status = "unresolved";
            Save();
        }
        public void ReloadPrices()
        {
            lock (gate) { var loaded = JsonConvert.DeserializeObject<PriceTable>(File.ReadAllText(PricePath)); var live = JsonConvert.DeserializeObject<LivePriceTable>(File.ReadAllText(LivePricePath)); if (loaded == null || live == null) throw new InvalidDataException("Invalid price table."); live.Validate(); Prices = loaded; LivePrices = live; }
        }
        public void BeginSession(VoiceSettings settings, bool developmentTest = false)
        {
            lock (gate)
            {
                settings.Validate(); Prices.Validate(settings.Model);
                if (TotalUsd >= settings.CumulativeLimitUsd) throw new InvalidOperationException("Estimated cumulative mod usage limit reached (USD).");
                if (developmentTest && DevelopmentUsd >= settings.DevelopmentLimitUsd) throw new InvalidOperationException("Estimated development-test allowance reached (USD).");
                sessionId = Guid.NewGuid().ToString("N"); development = developmentTest;
            }
        }
        public decimal EstimateReserve(VoiceSettings settings, int instructionUtf8Bytes)
        {
            lock (gate)
            {
                Prices.Validate(settings.Model);
                decimal maxInput = Math.Max(Prices.TextInput, Math.Max(Prices.AudioInput, Prices.ImageInput));
                decimal maxOutput = Math.Max(Prices.TextOutput, Prices.AudioOutput);
                // UTF-8 bytes conservatively bound instruction tokens. Include new audio,
                // context headroom and special tokens; retain this reserve if usage is lost.
                return ((settings.ContextTokens + instructionUtf8Bytes + settings.MaxTurnSeconds * 12 + 1024) * maxInput + settings.MaxOutputTokens * maxOutput) / 1000000m + 0.002m;
            }
        }
        public string Reserve(VoiceSettings settings, int instructionUtf8Bytes)
        {
            lock (gate)
            {
                if (sessionId == null) throw new InvalidOperationException("No accounting session started.");
                decimal amount = EstimateReserve(settings, instructionUtf8Bytes);
                if (SessionUsd + amount > settings.SessionLimitUsd || TotalUsd + amount > settings.CumulativeLimitUsd || (development && DevelopmentUsd + amount > settings.DevelopmentLimitUsd)) throw new InvalidOperationException("Estimated mod usage limit: insufficient reserve for another bounded turn (USD).");
                string id = Guid.NewGuid().ToString("N");
                data.Entries.Add(new UsageEntry { ReservationId = id, SessionId = sessionId, CreatedUtc = DateTime.UtcNow.ToString("O"), Development = development, Status = "pending", EstimatedUsd = amount }); Save(); return id;
            }
        }
        public void BindResponse(string reservationId, string responseId)
        {
            lock (gate) { var entry = Find(reservationId); if (entry.ResponseId != null && entry.ResponseId != responseId) throw new InvalidOperationException("Response accounting mismatch."); entry.ResponseId = responseId; Save(); }
        }
        public void ReleaseUncommitted(string reservationId)
        {
            lock (gate) { var entry = Find(reservationId); if (entry.ResponseId == null && entry.Status == "pending") { data.Entries.Remove(entry); Save(); } }
        }
        public void MarkUnresolved(string reservationId)
        {
            lock (gate) { var entry = Find(reservationId); if (entry.Status != "reported") { entry.Status = "unresolved"; Save(); } }
        }
        public bool Report(string responseId, JObject usage)
        {
            lock (gate)
            {
                var entry = data.Entries.FirstOrDefault(e => e.ResponseId == responseId);
                if (entry == null) throw new InvalidOperationException("Response arrived without a spending reservation; paid use stopped.");
                if (entry.Status == "reported") return false;
                if (usage == null || usage["input_tokens"] == null || usage["output_tokens"] == null || usage["input_token_details"]?["text_tokens"] == null || usage["input_token_details"]?["audio_tokens"] == null || usage["output_token_details"]?["text_tokens"] == null || usage["output_token_details"]?["audio_tokens"] == null) { entry.Status = "unresolved"; Save(); throw new InvalidDataException("OpenAI did not report detailed usage. Reserved estimate retained; paid session stopped."); }
                var input = (JObject)usage["input_token_details"]; var output = (JObject)usage["output_token_details"];
                entry.InputText = Number(input, "text_tokens"); entry.InputAudio = Number(input, "audio_tokens");
                entry.OutputText = Number(output, "text_tokens"); entry.OutputAudio = Number(output, "audio_tokens");
                long image = Number(input, "image_tokens");
                if (Number(usage, "input_tokens") < entry.InputText + entry.InputAudio + image || Number(usage, "output_tokens") < entry.OutputText + entry.OutputAudio) throw new InvalidDataException("Usage totals do not match modality totals; paid session stopped.");
                var cached = input["cached_tokens_details"] as JObject;
                entry.CachedText = Math.Min(entry.InputText, Number(cached, "text_tokens")); entry.CachedAudio = Math.Min(entry.InputAudio, Number(cached, "audio_tokens"));
                long cachedImage = Math.Min(image, Number(cached, "image_tokens"));
                long inputUnknown = Math.Max(0, Number(usage, "input_tokens") - entry.InputText - entry.InputAudio - image);
                long outputUnknown = Math.Max(0, Number(usage, "output_tokens") - entry.OutputText - entry.OutputAudio);
                decimal cost = ((entry.InputText - entry.CachedText) * Prices.TextInput + entry.CachedText * Prices.TextCached + (entry.InputAudio - entry.CachedAudio) * Prices.AudioInput + entry.CachedAudio * Prices.AudioCached + (image - cachedImage) * Prices.ImageInput + cachedImage * Prices.ImageCached + entry.OutputText * Prices.TextOutput + entry.OutputAudio * Prices.AudioOutput + inputUnknown * Math.Max(Prices.AudioInput, Math.Max(Prices.TextInput, Prices.ImageInput)) + outputUnknown * Math.Max(Prices.AudioOutput, Prices.TextOutput)) / 1000000m;
                entry.EstimatedUsd = cost; entry.Status = "reported"; Save(); return true;
            }
        }
        public string BeginLiveSession(VoiceSettings settings, bool developmentTest = false)
        {
            lock (gate)
            {
                settings.Validate(); LivePrices.Validate();
                if (settings.LiveModel != LivePrices.Model) throw new InvalidOperationException("Unpriced Live model; verify a matching dated price table before connecting.");
                decimal reserve = LivePrices.CloseReserveSeconds * LivePrices.VoiceUsdPerMinute / 60m;
                EnsureBudget(settings, 0m, reserve, developmentTest);
                sessionId = Guid.NewGuid().ToString("N"); development = developmentTest;
                data.LiveSessions.Add(new LiveUsageEntry { SessionId = sessionId, CreatedUtc = DateTime.UtcNow.ToString("O"), Development = development, EstimatedUsd = reserve, VoiceRateUsdPerMinute = LivePrices.VoiceUsdPerMinute, CloseReserveSeconds = LivePrices.CloseReserveSeconds });
                Save(); return sessionId;
            }
        }

        public void BindLiveSession(string localId, string apiSessionId)
        {
            if (string.IsNullOrWhiteSpace(apiSessionId)) throw new InvalidDataException("Live session ID missing.");
            lock (gate) { var entry = Live(localId); if (entry.ApiSessionId != null && entry.ApiSessionId != apiSessionId) throw new InvalidOperationException("Live session accounting mismatch."); entry.ApiSessionId = apiSessionId; Save(); }
        }

        public void UpdateLiveDuration(string localId, double cumulativeSeconds)
        {
            decimal seconds = ValidSeconds(cumulativeSeconds);
            lock (gate)
            {
                var entry = Live(localId); if (entry.FinalDurationConfirmed) return;
                // API updates are snapshots: 12 then 15 means 15 seconds, never 27.
                entry.ReportedSeconds = Math.Max(entry.ReportedSeconds, seconds);
                entry.EstimatedUsd = Math.Max(entry.EstimatedUsd, Math.Max(entry.ReportedSeconds, entry.WallSeconds + entry.CloseReserveSeconds) * entry.VoiceRateUsdPerMinute / 60m);
                Save();
            }
        }

        public void UpdateLiveClock(string localId, double elapsedSeconds, VoiceSettings settings)
        {
            decimal seconds = ValidSeconds(elapsedSeconds);
            lock (gate)
            {
                var entry = Live(localId); if (entry.FinalDurationConfirmed) return;
                entry.WallSeconds = Math.Max(entry.WallSeconds, seconds);
                entry.EstimatedUsd = Math.Max(entry.ReportedSeconds, entry.WallSeconds + entry.CloseReserveSeconds) * entry.VoiceRateUsdPerMinute / 60m;
                // Save the conservative reserve even when this check tells the transport to close.
                Save(); EnsureBudget(settings, SessionCost(localId), 0m, entry.Development);
            }
        }

        public void CompleteLiveSession(string localId, double? finalDurationSeconds)
        {
            lock (gate)
            {
                var entry = Live(localId);
                if (entry.FinalDurationConfirmed) return;
                if (!finalDurationSeconds.HasValue) { entry.Status = "unresolved"; Save(); return; }
                decimal seconds = ValidSeconds(finalDurationSeconds.Value);
                if (seconds < entry.ReportedSeconds) { entry.Status = "unresolved"; Save(); throw new InvalidDataException("Live final duration is below reported usage; conservative estimate retained."); }
                entry.ReportedSeconds = seconds;
                entry.EstimatedUsd = seconds * entry.VoiceRateUsdPerMinute / 60m;
                entry.FinalDurationConfirmed = true; entry.Status = "reported"; Save();
            }
        }

        // Reserved before enabling a paid Realtime transcription turn. The caller
        // binds its item ID at commit and reports the separate transcription usage.
        public string ReserveTranscription(VoiceSettings settings, int maximumSeconds, string model = "gpt-transcribe")
        {
            return ReserveTranscriptionInternal(Guid.NewGuid().ToString("N"), settings, maximumSeconds, model);
        }
        public void ReserveTranscription(string localCaptureId, int maximumSeconds, VoiceSettings settings, string model = "gpt-transcribe")
        {
            if (string.IsNullOrWhiteSpace(localCaptureId)) throw new InvalidOperationException("Missing local transcription capture ID.");
            ReserveTranscriptionInternal(localCaptureId, settings, maximumSeconds, model);
        }
        private string ReserveTranscriptionInternal(string localCaptureId, VoiceSettings settings, int maximumSeconds, string model)
        {
            lock (gate)
            {
                if (sessionId == null) throw new InvalidOperationException("No accounting session started.");
                if (data.Auxiliary.Any(e => e.ReservationId == localCaptureId)) throw new InvalidOperationException("Duplicate transcription reservation ID.");
                LivePrices.Validate();
                if (model != LivePrices.TranscriptionModel || maximumSeconds < 1 || maximumSeconds > 120) throw new InvalidOperationException("Unpriced transcription model or duration; paid use stopped.");
                decimal amount = (maximumSeconds + 5m) * LivePrices.TranscriptionUsdPerMinute / 60m;
                EnsureBudget(settings, SessionCost(sessionId), amount, development);
                var entry = NewAuxiliary("transcription", model, sessionId, development, amount);
                entry.ReservationId = localCaptureId;
                entry.UnitRateUsdPerMinute = LivePrices.TranscriptionUsdPerMinute;
                data.Auxiliary.Add(entry); Save(); return entry.ReservationId;
            }
        }

        public void BindTranscription(string reservationId, string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) throw new InvalidDataException("Missing transcription item ID.");
            lock (gate) { var entry = Auxiliary(reservationId); if (entry.Kind != "transcription" || (entry.ExternalId != null && entry.ExternalId != itemId) || data.Auxiliary.Any(e => e != entry && e.Kind == "transcription" && e.ExternalId == itemId)) throw new InvalidOperationException("Transcription accounting mismatch."); entry.ExternalId = itemId; Save(); }
        }

        public bool ReportTranscription(string itemId, JObject usage)
        {
            lock (gate)
            {
                var entry = data.Auxiliary.FirstOrDefault(e => e.Kind == "transcription" && e.ExternalId == itemId);
                if (entry == null) throw new InvalidOperationException("Transcription arrived without a spending reservation; paid use stopped.");
                if (entry.Status == "reported") return false;
                if (usage == null || (string)usage["type"] != "duration" || usage["seconds"] == null) { entry.Status = "unresolved"; Save(); throw new InvalidDataException("Transcription duration usage missing; reserve retained."); }
                entry.Seconds = ValidSeconds(usage["seconds"].Value<double>());
                if (entry.UnitRateUsdPerMinute <= 0) { entry.Status = "unresolved"; Save(); throw new InvalidDataException("Transcription tariff missing from reservation; reserve retained."); }
                entry.EstimatedUsd = entry.Seconds * entry.UnitRateUsdPerMinute / 60m;
                entry.Status = "reported"; Save(); return true;
            }
        }

        public void ReleaseAuxiliaryUncommitted(string reservationId)
        {
            lock (gate) { var entry = Auxiliary(reservationId); if (entry.ExternalId == null && entry.Status == "pending") { data.Auxiliary.Remove(entry); data.Entries.RemoveAll(e => e.ReservationId == "aux:" + reservationId); Save(); } }
        }
        public void ReleaseTranscription(string localCaptureId) { ReleaseAuxiliaryUncommitted(localCaptureId); }
        public void MarkAuxiliaryUnresolved(string reservationId)
        {
            lock (gate) { var entry = Auxiliary(reservationId); if (entry.Status != "reported") { entry.Status = "unresolved"; Save(); } }
        }

        public string ReserveLiveBackend(string liveId, string model, int maximumInputTokens, int maximumOutputTokens, VoiceSettings settings, IDictionary<string, int> maximumToolCalls = null)
        {
            lock (gate)
            {
                var live = Live(liveId); BackendTokenPrice price = Backend(model);
                if (maximumInputTokens < 0 || maximumOutputTokens < 0) throw new InvalidDataException("Negative backend token limit.");
                decimal amount = (maximumInputTokens * price.Input + maximumOutputTokens * price.Output) / 1000000m;
                if (maximumToolCalls != null) foreach (var tool in maximumToolCalls)
                {
                    decimal rate;
                    if (tool.Value < 0 || price.ToolUsdPerCall == null || !price.ToolUsdPerCall.TryGetValue(tool.Key, out rate) || rate < 0) throw new InvalidDataException("Unpriced delegated tool; paid use stopped before reservation.");
                    amount += rate * tool.Value;
                }
                EnsureBudget(settings, SessionCost(liveId), amount, live.Development);
                var entry = NewAuxiliary("backend", model, liveId, live.Development, amount);
                data.Auxiliary.Add(entry); Save(); return entry.ReservationId;
            }
        }

        public void BindLiveBackend(string reservationId, string responseId)
        {
            if (string.IsNullOrWhiteSpace(responseId)) throw new InvalidDataException("Backend response ID missing.");
            lock (gate) { var entry = Auxiliary(reservationId); if (entry.Kind != "backend" || (entry.ExternalId != null && entry.ExternalId != responseId) || data.Auxiliary.Any(e => e != entry && e.Kind == "backend" && e.ExternalId == responseId)) throw new InvalidOperationException("Backend accounting mismatch."); entry.ExternalId = responseId; Save(); }
        }

        public bool ReportLiveBackend(string liveId, string responseId, string model, JObject usage, IDictionary<string, int> toolCalls = null)
        {
            lock (gate)
            {
                Live(liveId); BackendTokenPrice price = Backend(model);
                var entry = data.Auxiliary.FirstOrDefault(e => e.Kind == "backend" && e.SessionId == liveId && e.ExternalId == responseId);
                if (entry == null || entry.Model != model) throw new InvalidOperationException("Delegated response has no matching spending reservation; paid use stopped.");
                if (entry.Status == "reported") return false;
                try
                {
                    if (usage == null || usage["input_tokens"] == null || usage["output_tokens"] == null) throw new InvalidDataException("Delegated token usage missing.");
                    long input = Number(usage, "input_tokens"), output = Number(usage, "output_tokens");
                    long cached = Number(usage["input_tokens_details"] as JObject, "cached_tokens");
                    if (cached > input) throw new InvalidDataException("Invalid delegated cache usage.");
                    decimal cost = ((input - cached) * price.Input + cached * price.CachedInput + output * price.Output) / 1000000m;
                    if (toolCalls != null) foreach (var tool in toolCalls)
                    {
                        decimal rate;
                        if (tool.Value < 0 || price.ToolUsdPerCall == null || !price.ToolUsdPerCall.TryGetValue(tool.Key, out rate) || rate < 0) throw new InvalidDataException("Unpriced delegated tool; paid use stopped.");
                        cost += tool.Value * rate;
                    }
                    entry.EstimatedUsd = cost; entry.Status = "reported"; Save(); return true;
                }
                catch { entry.Status = "unresolved"; Save(); throw; }
            }
        }

        private BackendTokenPrice Backend(string model)
        {
            LivePrices.Validate(); BackendTokenPrice price;
            if (!LivePrices.BackendModels.TryGetValue(model, out price) || price == null || price.Input <= 0 || price.Output <= 0 || price.CachedInput < 0 || price.CachedInput > price.Input)
                throw new InvalidOperationException("Unpriced delegated model; paid use stopped.");
            return price;
        }
        private void EnsureBudget(VoiceSettings settings, decimal currentSessionCost, decimal additional, bool isDevelopment)
        {
            if (currentSessionCost + additional > settings.SessionLimitUsd || TotalUsd + additional > settings.CumulativeLimitUsd || (isDevelopment && DevelopmentUsd + additional > settings.DevelopmentLimitUsd))
                throw new InvalidOperationException("Estimated mod usage limit: closing before the spending reserve is exhausted (USD).");
        }
        private decimal SessionCost(string id) { return data.Entries.Where(e => e.SessionId == id).Sum(e => e.EstimatedUsd); }
        private LiveUsageEntry Live(string id) { return data.LiveSessions.FirstOrDefault(e => e.SessionId == id) ?? throw new InvalidOperationException("Missing Live accounting session."); }
        private AuxiliaryUsageEntry Auxiliary(string id) { return data.Auxiliary.FirstOrDefault(e => e.ReservationId == id) ?? throw new InvalidOperationException("Missing auxiliary usage reservation."); }
        private static AuxiliaryUsageEntry NewAuxiliary(string kind, string model, string id, bool dev, decimal amount) { return new AuxiliaryUsageEntry { ReservationId = Guid.NewGuid().ToString("N"), Kind = kind, Model = model, SessionId = id, Development = dev, EstimatedUsd = amount }; }
        private static decimal ValidSeconds(double seconds) { if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 86400) throw new InvalidDataException("Invalid usage duration."); return (decimal)seconds; }
        private UsageEntry Find(string id) { return data.Entries.FirstOrDefault(e => e.ReservationId == id) ?? throw new InvalidOperationException("Missing usage reservation."); }
        private static long Number(JObject item, string field) { long value = item == null || item[field] == null ? 0 : item[field].Value<long>(); if (value < 0) throw new InvalidDataException("Invalid negative token count."); return value; }
        private void Save()
        {
            foreach (var live in data.LiveSessions) SyncMirror("live:" + live.SessionId, live.SessionId, live.Development, live.Status, live.EstimatedUsd, live.CreatedUtc);
            foreach (var auxiliary in data.Auxiliary) SyncMirror("aux:" + auxiliary.ReservationId, auxiliary.SessionId, auxiliary.Development, auxiliary.Status, auxiliary.EstimatedUsd, null);
            AtomicWrite(path, data);
        }
        private void SyncMirror(string id, string ownerSessionId, bool isDevelopment, string status, decimal amount, string createdUtc)
        {
            var row = data.Entries.FirstOrDefault(e => e.ReservationId == id);
            if (row == null) { row = new UsageEntry { ReservationId = id, SessionId = ownerSessionId, CreatedUtc = createdUtc ?? DateTime.UtcNow.ToString("O") }; data.Entries.Add(row); }
            row.EstimatedUsd = amount; row.Development = isDevelopment; row.Status = status;
        }
        private static void AtomicWrite(string target, object value)
        {
            File.WriteAllText(target + ".tmp", JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(target + ".tmp", target, target + ".bak"); else File.Move(target + ".tmp", target);
        }
    }
}
