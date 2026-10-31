using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FamilyTimeVoice
{
    public sealed class AudioChunk
    {
        public string ResponseId { get; internal set; }
        public string ItemId { get; internal set; }
        public int ContentIndex { get; internal set; }
        public byte[] Pcm { get; internal set; }
        public long ConnectionId { get; internal set; }
        public long SampleOffset { get; internal set; }
        public bool ApplicationSegment { get; internal set; }
    }
    public sealed class TranscriptChunk
    {
        public string ResponseId { get; internal set; }
        public string ItemId { get; internal set; }
        public int ContentIndex { get; internal set; }
        public string Text { get; internal set; }
        public bool Final { get; internal set; }
        public long ConnectionId { get; internal set; }
        public string Role { get; internal set; } = "assistant";
        public string FragmentId { get; internal set; }
        public long StartMs { get; internal set; } = -1;
        public long EndMs { get; internal set; } = -1;
        public bool IsFragment { get; internal set; }
    }
    public sealed class VoiceHistoryMessage
    {
        public string Role { get; set; }
        public string Text { get; set; }
    }
    public sealed class VoicePlaybackReport
    {
        public string ItemId { get; set; }
        public int HeardMilliseconds { get; set; }
        public bool Drained { get; set; }
        public bool Interrupted { get; set; }
    }
    public sealed class VoiceResponseOutcome
    {
        public long ConnectionId { get; internal set; }
        public string ResponseId { get; internal set; }
        public string ItemId { get; internal set; }
        public string Status { get; internal set; }
        public string Reason { get; internal set; }
    }
    public sealed class VoiceMediaFrame
    {
        public byte[] Pcm { get; internal set; }
        public long SampleOffset { get; internal set; }
        public bool Enqueued { get; internal set; }
        public int QueuedSampleCount { get; internal set; }
        public bool NonSilent { get; internal set; }
        public bool DuringClose { get; internal set; }
    }
    public sealed class VoiceMediaStatistics
    {
        public long ReceivedSamples { get; internal set; }
        public long QueuedSamples { get; internal set; }
        public long IgnoredSamples { get; internal set; }
        public long IgnoredNonSilentSamples { get; internal set; }
        public long NonSilentReceivedSamples { get; internal set; }
        public bool FinalUsageConfirmed { get; internal set; }
        public bool GracefulServerClose { get; internal set; }
        public double FirstAudioArrivalMs { get; internal set; } = -1;
        public double LastAudioArrivalMs { get; internal set; } = -1;
        public long LastTranscriptEndMs { get; internal set; }
        public bool ServerTimelineOriginVerified { get; internal set; } = false;
    }
    public interface IVoiceSession : IDisposable
    {
        event Action<AudioChunk> Audio;
        event Action<TranscriptChunk> Transcript;
        event Action<string> StateChanged;
        event Action<string> Fault;
        event Action InputSpeechStarted;
        event Action<string> UserTurnCommitted;
        event Action<string> AudioGenerationEnded;
        event Action<VoiceResponseOutcome> ResponseEnded;
        event Action ReconnectRequested;
        bool Connected { get; }
        bool Responding { get; }
        bool RequiresContinuousInput { get; }
        long ConnectionId { get; }
        DateTime LastActivityUtc { get; }
        bool AcceptsAudio(long connectionId, string responseId);
        Task StartAsync(string key, VoiceSettings settings, Persona persona, string context, bool developmentTest = false);
        Task StartWithInstructionsAsync(string key, VoiceSettings settings, string exactInstructions, bool developmentTest = false);
        void AppendPcm(byte[] pcm);
        void SetInputActive(bool active);
        void FinishTurn();
        void Interrupt(string itemId, int contentIndex, int heardMs, bool cancelResponse = true);
        void SetHistory(IReadOnlyList<VoiceHistoryMessage> history);
        void UpdateContext(string context);
        void ReportPlayback(VoicePlaybackReport report);
        Task SendTextAsync(string text);
        Task EndAsync();
        void End();
    }
}
