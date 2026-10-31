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
    }

    public sealed class UsageLedger
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly LedgerData data;
        private string sessionId;
        private bool development;
        public PriceTable Prices { get; private set; }
        public decimal TotalUsd { get { lock (gate) return data.Entries.Sum(e => e.EstimatedUsd); } }
        public decimal SessionUsd { get { lock (gate) return data.Entries.Where(e => e.SessionId == sessionId).Sum(e => e.EstimatedUsd); } }
        public decimal DevelopmentUsd { get { lock (gate) return data.Entries.Where(e => e.Development).Sum(e => e.EstimatedUsd); } }
        public int UnresolvedCount { get { lock (gate) return data.Entries.Count(e => e.Status != "reported"); } }
        public string PricePath { get; private set; }

        public UsageLedger(string path, string pricePath = null)
        {
            this.path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(this.path));
            PricePath = pricePath ?? Path.Combine(Path.GetDirectoryName(this.path), "prices.json");
            Prices = File.Exists(PricePath) ? JsonConvert.DeserializeObject<PriceTable>(File.ReadAllText(PricePath)) : new PriceTable();
            if (Prices == null) throw new InvalidDataException("The price table is empty.");
            if (!File.Exists(PricePath)) AtomicWrite(PricePath, Prices);
            data = File.Exists(this.path) ? JsonConvert.DeserializeObject<LedgerData>(File.ReadAllText(this.path)) : new LedgerData();
            if (data == null || data.Version != 1 || data.Entries == null || data.Entries.Any(e => e.EstimatedUsd < 0)) throw new InvalidDataException("Usage ledger is invalid; paid use is stopped. Restore its backup before reconnecting.");
            foreach (var item in data.Entries.Where(e => e.Status == "pending")) item.Status = "unresolved";
            Save();
        }
        public void ReloadPrices()
        {
            lock (gate) { var loaded = JsonConvert.DeserializeObject<PriceTable>(File.ReadAllText(PricePath)); if (loaded == null) throw new InvalidDataException("Invalid price table."); Prices = loaded; }
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
        private UsageEntry Find(string id) { return data.Entries.FirstOrDefault(e => e.ReservationId == id) ?? throw new InvalidOperationException("Missing usage reservation."); }
        private static long Number(JObject item, string field) { long value = item == null || item[field] == null ? 0 : item[field].Value<long>(); if (value < 0) throw new InvalidDataException("Invalid negative token count."); return value; }
        private void Save() { AtomicWrite(path, data); }
        private static void AtomicWrite(string target, object value)
        {
            File.WriteAllText(target + ".tmp", JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(target + ".tmp", target, target + ".bak"); else File.Move(target + ".tmp", target);
        }
    }
}
