using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FamilyTimeVoice
{
    public sealed class VoiceDesignCandidate
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ScriptHint { get; set; }
    }
    public sealed class SavedVoiceDesign
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string VoiceId { get; set; }
        public long CreatedAtUnix { get; set; }
        public string AttemptedAtUtc { get; set; }
        public string CreationStatus { get; set; }
        public int? LastHttpStatus { get; set; }
        public string LastErrorCode { get; set; }
        public string LastErrorParam { get; set; }
        public string SyntheticLabel { get; set; } = "AI-generated original adult voice";
    }
    public sealed class VoiceDesignCatalogData
    {
        public int Version { get; set; } = 1;
        public List<SavedVoiceDesign> Voices { get; set; } = new List<SavedVoiceDesign>();
    }

    // Prompt voices are GPT-Live identities, never Realtime stock-voice aliases.
    // This class does nothing remotely until CreateAsync is explicitly called.
    public sealed class VoiceDesignCatalog : IDisposable
    {
        public const string ComparisonPassage = "There you are. I was about to take a break. Come sit with me and tell me what happened. And if this is another terrible idea, at least let me hear it first.";
        private static readonly Uri CreateEndpoint = new Uri("https://api.openai.com/v1/audio/voices");
        private readonly string path;
        private readonly HttpClient client;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly VoiceDesignCatalogData data;
        private bool disposed;
        private static readonly ReadOnlyCollection<VoiceDesignCandidate> defaults = Array.AsReadOnly(new[] {
            new VoiceDesignCandidate {
                Code = "A", Name = "Family Time Wife Warm",
                Description = "An original adult feminine voice with the maturity of a woman in her thirties or forties. A lower-middle feminine register, rounded resonance, a smooth tone with a little natural huskiness, and relaxed conversational pacing. Clear English pronunciation. The delivery is calm and self-assured, with gentle changes in emphasis and a subtle smile during playful lines. Comfortable speaking volume, not a whisper; avoid an announcer cadence and exaggerated breathiness.",
                ScriptHint = ComparisonPassage },
            new VoiceDesignCandidate {
                Code = "B", Name = "Family Time Wife Cozy",
                Description = "An original adult feminine voice with the maturity of a woman in her thirties or forties. A lower-middle feminine register and rounded resonance, with a softer, smoother texture and very little huskiness. Keep relaxed natural conversational pacing and clear English pronunciation. The delivery is calm and self-assured, with gentle changes in emphasis and a subtle smile during playful lines. Use comfortable speaking volume, not a whisper. Avoid sleepy or sing-song delivery, an announcer cadence and exaggerated breathiness.",
                ScriptHint = ComparisonPassage },
            new VoiceDesignCandidate {
                Code = "C", Name = "Family Time Wife Playful",
                Description = "An original adult feminine voice with the maturity of a woman in her thirties or forties. A slightly lower feminine register, rounded resonance, a smooth tone with a little more dry texture and natural huskiness. Keep the voice clearly feminine, relaxed and intelligible, with clear English pronunciation and a natural conversational pace. Calm, self-assured delivery with flexible intonation for understated teasing and a subtle smile during playful lines. Use comfortable speaking volume, not a whisper. Avoid a theatrical performance, an announcer cadence and exaggerated breathiness.",
                ScriptHint = ComparisonPassage }
        });

        public IReadOnlyList<VoiceDesignCandidate> Candidates => defaults.Select(Copy).ToArray();
        public IReadOnlyList<SavedVoiceDesign> Saved { get { lock (data) return data.Voices.Select(Copy).ToArray(); } }
        public VoiceDesignCatalog(string path, HttpMessageHandler handler = null)
        {
            this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
            Directory.CreateDirectory(Path.GetDirectoryName(this.path));
            data = File.Exists(this.path) ? JsonConvert.DeserializeObject<VoiceDesignCatalogData>(File.ReadAllText(this.path)) : new VoiceDesignCatalogData();
            ValidateData(data);
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, true) { Timeout = TimeSpan.FromSeconds(90) };
        }

        public VoiceDesignCandidate Get(string code) { return Copy(defaults.FirstOrDefault(v => v.Code == code) ?? throw new ArgumentException("Choose voice candidate A, B or C.")); }
        public SavedVoiceDesign GetSaved(string code) { Get(code); lock (data) return Copy(data.Voices.FirstOrDefault(v => v.Code == code)); }

        public async Task<SavedVoiceDesign> CreateAsync(string code, string apiKey, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceDesignCatalog));
            VoiceDesignCandidate candidate = Get(code);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var catalogLock = await AcquireFileLock(cancellationToken).ConfigureAwait(false))
                {
                ReloadCore();
                SavedVoiceDesign record;
                lock (data) record = data.Voices.FirstOrDefault(v => v.Code == code);
                if (record != null && !string.IsNullOrWhiteSpace(record.VoiceId)) return Copy(record);
                if (record != null && (record.CreationStatus == "creating" || record.CreationStatus == "uncertain"))
                    throw new InvalidOperationException("Voice creation may already have succeeded. Reconcile its saved metadata with OpenAI before retrying; no duplicate voice was created.");
                if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("A configured private API key is required to create a voice.");
                if (apiKey.Length > 512 || apiKey.Trim().Any(char.IsWhiteSpace)) throw new InvalidOperationException("The private API credential format is invalid; re-enter it locally.");
                cancellationToken.ThrowIfCancellationRequested();
                record = record ?? new SavedVoiceDesign { Code = code };
                record.Name = candidate.Name; record.Description = candidate.Description;
                record.CreationStatus = "creating"; record.AttemptedAtUtc = DateTime.UtcNow.ToString("O");
                record.LastHttpStatus = null; record.LastErrorCode = null; record.LastErrorParam = null;
                lock (data) { if (!data.Voices.Contains(record)) data.Voices.Add(record); Save(); }

                using (var request = new HttpRequestMessage(HttpMethod.Post, CreateEndpoint))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                    // The shared short comparison passage is for auditions. Omit
                    // optional script_hint: this account rejects it as too short.
                    var body = new JObject { ["type"] = "prompt", ["name"] = candidate.Name, ["model"] = "auto", ["prompt"] = candidate.Description };
                    request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    try
                    {
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                int status = (int)response.StatusCode;
                                record.LastHttpStatus = status;
                                try
                                {
                                    var error = JObject.Parse(await ReadMetadata(response.Content, cancellationToken).ConfigureAwait(false))["error"] as JObject;
                                    record.LastErrorCode = SafeIdentifier((string)error?["code"], false);
                                    record.LastErrorParam = SafeIdentifier((string)error?["param"], true);
                                }
                                catch { /* Error messages and raw payloads are never retained or exposed. */ }
                                record.CreationStatus = status >= 400 && status < 500 ? "rejected" : "uncertain";
                                lock (data) Save();
                                throw new VoiceDesignRequestException("Voice creation failed (HTTP " + status + "; code " + (record.LastErrorCode ?? "unavailable") + "; parameter " + (record.LastErrorParam ?? "unavailable") + "). Check voice-create permission or account availability. No automatic retry was attempted.");
                            }
                            string json = await ReadMetadata(response.Content, cancellationToken).ConfigureAwait(false);
                            var result = JObject.Parse(json);
                            string id = (string)result["id"];
                            if ((string)result["object"] != "audio.voice" || (string)result["type"] != "prompt" || string.IsNullOrWhiteSpace(id) || !id.StartsWith("voice_", StringComparison.Ordinal) || id.Length > 200 || result["created_at"] == null)
                                throw new InvalidDataException("Voice response metadata was incomplete; reconcile the created voice before retrying.");
                            long createdAt = result["created_at"].Value<long>();
                            if (createdAt < 0) throw new InvalidDataException("Invalid voice creation timestamp.");
                            record.VoiceId = id; record.CreatedAtUnix = createdAt; record.CreationStatus = "created"; record.LastHttpStatus = (int)response.StatusCode;
                            lock (data) Save(); return Copy(record);
                        }
                    }
                    catch (VoiceDesignRequestException) { throw; }
                    catch
                    {
                        record.CreationStatus = "uncertain"; lock (data) Save();
                        throw new InvalidOperationException("Voice creation did not finish with confirmed metadata. No automatic retry was attempted; reconcile the saved catalog before creating another voice.");
                    }
                }
                }
            }
            finally { gate.Release(); }
        }

        public void Reload()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceDesignCatalog));
            if (!gate.Wait(0)) throw new InvalidOperationException("Voice creation is in progress; reload after it finishes.");
            try { ReloadCore(); } finally { gate.Release(); }
        }
        private void ReloadCore()
        {
            if (!File.Exists(path)) return;
            var loaded = JsonConvert.DeserializeObject<VoiceDesignCatalogData>(File.ReadAllText(path)); ValidateData(loaded);
            lock (data) { data.Voices.Clear(); data.Voices.AddRange(loaded.Voices); }
        }
        private async Task<FileStream> AcquireFileLock(CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(100);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Another local process is creating a voice; no duplicate request was sent."); await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
            }
        }
        private static void ValidateData(VoiceDesignCatalogData value)
        {
            if (value == null || value.Version != 1 || value.Voices == null || value.Voices.Count > 3 || value.Voices.GroupBy(v => v.Code).Any(g => g.Count() != 1) || value.Voices.Any(v => !defaults.Any(d => d.Code == v.Code) || (!string.IsNullOrEmpty(v.VoiceId) && (!v.VoiceId.StartsWith("voice_", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(v.Description)))))
                throw new InvalidDataException("Invalid saved voice catalog. Preserve it and repair its metadata before creating voices.");
        }

        private void Save()
        {
            File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(data, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak"); else File.Move(path + ".tmp", path);
        }
        private static VoiceDesignCandidate Copy(VoiceDesignCandidate value) { return new VoiceDesignCandidate { Code = value.Code, Name = value.Name, Description = value.Description, ScriptHint = value.ScriptHint }; }
        private static SavedVoiceDesign Copy(SavedVoiceDesign value) { return value == null ? null : new SavedVoiceDesign { Code = value.Code, Name = value.Name, Description = value.Description, VoiceId = value.VoiceId, CreatedAtUnix = value.CreatedAtUnix, AttemptedAtUtc = value.AttemptedAtUtc, CreationStatus = value.CreationStatus, SyntheticLabel = value.SyntheticLabel, LastHttpStatus = value.LastHttpStatus, LastErrorCode = value.LastErrorCode, LastErrorParam = value.LastErrorParam }; }
        private static string SafeIdentifier(string value, bool parameter)
        {
            if (value == null) return null;
            if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_.\[\]-]{1,120}\z", RegexOptions.CultureInvariant)) return "unrecognized";
            string[] allowed = parameter
                ? new[] { "type", "name", "model", "prompt", "script_hint" }
                : new[] { "invalid_request_error", "invalid_request", "invalid_value", "unsupported_value", "unknown_parameter", "unsupported_parameter", "missing_required_parameter", "invalid_type", "permission_denied", "insufficient_permissions", "insufficient_quota", "rate_limit_exceeded", "model_not_found", "not_found", "organization_restricted", "authentication_error", "invalid_api_key", "access_denied", "voice_limit_reached", "maximum_voices_exceeded", "unsupported_voice_type", "voice_creation_not_available", "invalid_prompt", "invalid_script_hint", "unsupported_model", "bad_request" };
            return Array.IndexOf(allowed, value) >= 0 ? value : "unrecognized";
        }
        private static async Task<string> ReadMetadata(HttpContent content, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                using (var input = await content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[4096]; int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                    { if (output.Length + count > 65536) throw new InvalidDataException("Oversized voice metadata response."); output.Write(buffer, 0, count); }
                    return Encoding.UTF8.GetString(output.ToArray());
                }
            }
        }
        public void Dispose() { if (disposed) return; disposed = true; client.Dispose(); }
        private sealed class VoiceDesignRequestException : InvalidOperationException { public VoiceDesignRequestException(string message) : base(message) { } }
    }
}
