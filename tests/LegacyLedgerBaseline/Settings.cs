using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace FamilyTimeVoice
{
    public sealed class VoiceSettings
    {
        public string Model { get; set; } = "gpt-realtime-2.1-mini";
        public string Voice { get; set; } = "marin";
        public string Microphone { get; set; } = "";
        public bool HandsFree { get; set; } = false;
        public bool Headphones { get; set; } = false;
        public float Volume { get; set; } = 0.85f;
        public bool Mute { get; set; } = false;
        public double VadThreshold { get; set; } = 0.5;
        public int VadSilenceMs { get; set; } = 650;
        public int MaxOutputTokens { get; set; } = 256;
        public int ContextTokens { get; set; } = 8000;
        public int MaxTurnSeconds { get; set; } = 30;
        public int MaxSessionSeconds { get; set; } = 600;
        public int IdleSeconds { get; set; } = 60;
        public decimal SessionLimitUsd { get; set; } = 0.50m;
        public decimal CumulativeLimitUsd { get; set; } = 5.00m;
        public decimal DevelopmentLimitUsd { get; set; } = 0.25m;
        public string Language { get; set; } = "English";
        public float Affection { get; set; } = 0.75f;
        public float Teasing { get; set; } = 0.4f;
        public bool Endearments { get; set; } = true;
        public string ResponseLength { get; set; } = "one to three natural spoken sentences";
        public bool MemoryEnabled { get; set; } = false;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Model) || Model.Length > 100) throw new InvalidOperationException("Enter a model name with a verified matching price table.");
            string[] voices = { "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar" };
            if (Array.IndexOf(voices, Voice) < 0) throw new InvalidOperationException("Select a supported stock voice.");
            if (MaxOutputTokens < 32 || MaxOutputTokens > 1024 || ContextTokens < 1000 || ContextTokens > 16000) throw new InvalidOperationException("Output/context token limits are outside the supported cost-control range.");
            if (MaxTurnSeconds < 1 || MaxTurnSeconds > 60 || IdleSeconds < 10 || MaxSessionSeconds < 10 || MaxSessionSeconds > 3600) throw new InvalidOperationException("Invalid audio/session duration limit.");
            if (SessionLimitUsd <= 0 || CumulativeLimitUsd <= 0 || DevelopmentLimitUsd <= 0) throw new InvalidOperationException("Spending limits must be positive USD amounts.");
            if (VadThreshold < 0 || VadThreshold > 1 || VadSilenceMs < 200 || VadSilenceMs > 3000) throw new InvalidOperationException("Invalid VAD settings.");
        }
    }

    public sealed class Persona
    {
        public string Name { get; set; } = "Wolf Mama";
        public string Identity { get; set; } = "The adult wife and partner in Family Time. The player is your adult spouse.";
        public string Style { get; set; } = "Warm, nurturing, reassuring, protective, playfully teasing, gently confident, affectionate and non-explicit. Have your own opinions and lead naturally when appropriate.";
        public string Boundaries { get; set; } = "Respect preferences and boundaries. No baby talk, coercive attachment, guilt, exclusive dependence, or replacement of real relationships. Never invent memories, vision, game observations or actions.";
        public string AdditionalInstructions { get; set; } = "Cozy domestic conversation and mild romantic flirting are welcome. Praise meaningful effort. Do not repeat catchphrases or use customer-service introductions. Use occasional endearments naturally.";
        public string[] ToneExamples { get; set; } = { "There you are, darling. How has your day been?", "Come sit with me for a moment. Tell me what happened.", "You handled that well. Let yourself take a breath." };
        public string Provenance { get; set; } = "Caring style adapted from MotherlyPersona, bnookala/mcp-emotional-support @9a2ad19d7e4d05070aaecf442ed665343abd776c (MIT). Adult spouse framing and game-specific profile rewritten locally. See THIRD_PARTY_NOTICES.md.";

        public static Persona Load(string path)
        {
            if (!File.Exists(path)) { var value = new Persona(); value.Save(path); return value; }
            return JsonConvert.DeserializeObject<Persona>(File.ReadAllText(path, Encoding.UTF8)) ?? throw new InvalidDataException("The wife profile is empty.");
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }

        public string BuildInstructions(string context, VoiceSettings settings)
        {
            var text = new StringBuilder();
            text.AppendLine("You are roleplaying " + Name + ", a fictional adult game character. " + Identity);
            text.AppendLine(Style); text.AppendLine(Boundaries); text.AppendLine(AdditionalInstructions);
            text.AppendLine("Speak in " + settings.Language + ". Prefer " + settings.ResponseLength + ", unless asked for more.");
            text.AppendLine("Affection intensity (0-1): " + settings.Affection.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "; playful teasing (0-1): " + settings.Teasing.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ".");
            text.AppendLine(settings.Endearments ? "Occasional darling/sweetheart is appropriate; vary wording." : "Do not use endearments.");
            if (ToneExamples != null) text.AppendLine("Tone examples; do not repeat mechanically: " + string.Join(" | ", ToneExamples));
            text.AppendLine("The following is the only verified game context. Unknown state must stay unknown: " + (context ?? "No current observations available."));
            string result = text.ToString();
            if (Encoding.UTF8.GetByteCount(result) > 12000) throw new InvalidDataException("Profile/context is too large; shorten it to protect the budget.");
            return result;
        }
    }
}
