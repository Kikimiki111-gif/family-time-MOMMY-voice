using System;
using System.Linq;
using FamilyTimeVoice;

static class CaptionAudibilityTests
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
    static AudioChunk Audio(string item, int samples = 24000, long connection = 1) => new AudioChunk { ItemId = item, ResponseId = "r:" + item, ConnectionId = connection, Pcm = new byte[samples * 2] };
    static TranscriptChunk Caption(string item, string text, bool final = true, long connection = 1) => new TranscriptChunk { ItemId = item, ResponseId = "r:" + item, ConnectionId = connection, Role = "assistant", Text = text, Final = final };
    static TranscriptChunk Fragment(string id, string role, string item, string text, long start = 0, long end = 500) => new TranscriptChunk
    { ConnectionId = 1, FragmentId = id, Role = role, ItemId = item, ResponseId = "r:" + item, Text = text, StartMs = start, EndMs = end, IsFragment = true };
    static VoiceResponseOutcome Outcome(string item, string status = "completed") => new VoiceResponseOutcome { ConnectionId = 1, ResponseId = "r:" + item, ItemId = item, Status = status };
    static VoicePlaybackReport Drained(string item, int ms = 1000) => new VoicePlaybackReport { ItemId = item, HeardMilliseconds = ms, Drained = true };
    static int Assistants(ConversationMemory memory) => memory.GetRecentEntries().Count(e => e.Role == "assistant");

    static void Main()
    {
        var memory = new ConversationMemory(); var tracker = new CaptionAudibilityTracker(memory);
        tracker.ObserveAudio(Audio("a")); tracker.ObserveTranscript(Caption("a", "A final caption.")); tracker.AudioGenerationEnded("a");
        Check(Assistants(memory) == 0, "final caption plus real audio EOF does not imply it was heard");
        tracker.ReportPlayback(Drained("a", 100)); tracker.ResponseEnded(Outcome("a"));
        Check(Assistants(memory) == 0, "early drain or received duration cannot admit an unplayed response");
        tracker.ReportPlayback(Drained("a"));
        Check(Assistants(memory) == 1 && memory.GetRecentEntries().Last().CompleteTurn, "Realtime admits only final completed response with full actually-heard coverage");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory);
        tracker.UserTurnCommitted("late-user");
        tracker.ObserveAudio(Audio("answer")); tracker.ObserveTranscript(Caption("answer", "The reply arrives before ASR finishes."));
        tracker.ObserveTranscript(new TranscriptChunk { ConnectionId = 1, ItemId = "late-user", Role = "user", Final = true, Text = "My actual earlier question." });
        tracker.AudioGenerationEnded("answer"); tracker.ResponseEnded(Outcome("answer")); tracker.ReportPlayback(Drained("answer"));
        Check(memory.GetRecentEntries().Select(e => e.Role).SequenceEqual(new[] { "user", "assistant" }), "real input commit preserves conversation order when user ASR completes late");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory);
        tracker.ObserveAudio(Audio("gap", 12000)); tracker.ReportPlayback(Drained("gap", 500));
        tracker.ObserveAudio(Audio("gap", 12000)); tracker.ObserveTranscript(Caption("gap", "Both streamed pieces."));
        tracker.AudioGenerationEnded("gap"); tracker.ResponseEnded(Outcome("gap"));
        Check(Assistants(memory) == 0, "new audio invalidates an earlier underrun drain");
        tracker.ReportPlayback(Drained("gap")); Check(Assistants(memory) == 1, "final drain after both pieces permits context admission");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory);
        tracker.ObserveAudio(Audio("no-eof")); tracker.ObserveTranscript(Caption("no-eof", "No server audio EOF yet."));
        tracker.ReportPlayback(Drained("no-eof")); tracker.ResponseEnded(Outcome("no-eof"));
        Check(Assistants(memory) == 0, "response.done and local silence do not invent audio-generation EOF");
        tracker.AudioGenerationEnded("no-eof"); Check(Assistants(memory) == 1, "late real EOF safely completes the conjunction");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory);
        tracker.ObserveAudio(Audio("no-final")); tracker.ObserveTranscript(Caption("no-final", "Partial caption", false));
        tracker.AudioGenerationEnded("no-final"); tracker.ResponseEnded(Outcome("no-final")); tracker.ReportPlayback(Drained("no-final"));
        Check(Assistants(memory) == 0, "audio completion cannot turn a partial Realtime caption into a final transcript");
        tracker.ObserveTranscript(Caption("no-final", "Actual complete caption."));
        Check(Assistants(memory) == 1 && memory.GetRecentEntries().Last().Text == "Actual complete caption.", "late final caption replaces partial text and commits once");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory);
        tracker.Interrupt("cancelled"); tracker.ObserveAudio(Audio("cancelled")); tracker.ObserveTranscript(Caption("cancelled", "LATE_UNHEARD"));
        tracker.AudioGenerationEnded("cancelled"); tracker.ResponseEnded(Outcome("cancelled")); tracker.ReportPlayback(Drained("cancelled"));
        Check(Assistants(memory) == 0, "interruption before queued callbacks rejects the whole late item");
        tracker.ObserveAudio(Audio("dropped-tail", 2400)); tracker.ObserveTranscript(Caption("dropped-tail", "A short received prefix and an unreceived tail."));
        tracker.ReportPlayback(Drained("dropped-tail", 100));
        tracker.Interrupt("dropped-tail"); // Host dropped a produced PCM callback during close.
        tracker.AudioGenerationEnded("dropped-tail"); tracker.ResponseEnded(Outcome("dropped-tail"));
        Check(Assistants(memory) == 0, "host-dropped closing PCM invalidates an early final caption before later EOF");
        tracker.ObserveAudio(Audio("cancel-outcome")); tracker.ObserveTranscript(Caption("cancel-outcome", "CANCELLED_SERVER_TEXT"));
        tracker.ResponseEnded(Outcome("cancel-outcome", "cancelled")); tracker.AudioGenerationEnded("cancel-outcome"); tracker.ReportPlayback(Drained("cancel-outcome"));
        Check(Assistants(memory) == 0, "actual cancelled outcome cannot be restored as a completed exchange");
        tracker.ObserveAudio(Audio("cut", 6000)); tracker.ObserveTranscript(Caption("cut", "A heard but clipped answer."));
        tracker.AudioGenerationEnded("cut"); tracker.ResponseEnded(Outcome("cut", "incomplete")); tracker.ReportPlayback(Drained("cut", 250));
        Check(Assistants(memory) == 1 && !memory.GetRecentEntries().Last().CompleteTurn, "fully heard incomplete generation remains an explicit excerpt");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory);
        tracker.BeginConnection("active", false, 12);
        tracker.ObserveAudio(Audio("stale", connection: 11)); tracker.ObserveTranscript(Caption("stale", "Wrong connection.", connection: 11));
        Check(tracker.PendingAssistantCount == 0 && memory.RecentCount == 0, "old connection events are ignored before staging");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory, true);
        tracker.ObserveAudio(Audio("live")); tracker.ObserveTranscript(Fragment("a1", "assistant", "live", "A heard Live excerpt."));
        tracker.AudioGenerationEnded("live"); tracker.ResponseEnded(Outcome("live")); tracker.ReportPlayback(Drained("live"));
        Check(Assistants(memory) == 0, "Live drain/quiet never impersonates a spoken-turn done event");
        Check(!tracker.FinalizeLiveConnection(false, true) && Assistants(memory) == 0, "Live captions are omitted without graceful server-close proof");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory, true);
        tracker.ObserveAudio(Audio("live")); tracker.ObserveTranscript(Fragment("a1", "assistant", "live", "Coverage unknown."));
        Check(!tracker.FinalizeLiveConnection(true, false) && Assistants(memory) == 0, "graceful close alone does not prove every produced speech frame was heard");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory, true);
        DateTime start = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        tracker.ObserveTranscript(Fragment("u1", "user", "u", "Don't call me", 0, 300), start);
        tracker.ObserveTranscript(Fragment("u2", "user", "u", " sweetheart.", 300, 900), start.AddMilliseconds(600));
        tracker.ObserveTranscript(Fragment("u2", "user", "u", " sweetheart.", 300, 900), start.AddMilliseconds(700));
        tracker.Tick(start.AddSeconds(3));
        Check(memory.GetRecentEntries().Count(e => e.Role == "user") == 1 && memory.GetRecentEntries()[0].Text == "Don't call me sweetheart.", "actual-role user fragments group once without duplicate events");
        Check(!memory.GetRecentEntries()[0].CompleteTurn && memory.PreferenceCount == 1, "Live correction excerpt retains explicit preference without inventing provider EOF");
        tracker.ObserveAudio(Audio("l1")); tracker.ObserveTranscript(Fragment("a3", "assistant", "l1", "All right. I won't."));
        tracker.ObserveTranscript(Fragment("u3", "user", "u2", "Let's talk about the garden.", 2000, 2500), start.AddSeconds(4));
        tracker.ObserveAudio(Audio("l2")); tracker.ObserveTranscript(Fragment("a4", "assistant", "l2", "The garden sounds good."));
        Check(Assistants(memory) == 0, "Live assistant text remains staged during the active connection");
        Check(tracker.FinalizeLiveConnection(true, true), "whole-connection heard-output proof admits conservative Live excerpts");
        Check(memory.GetRecentEntries().Select(e => e.Role).SequenceEqual(new[] { "user", "assistant", "user", "assistant" }), "deferred Live captions retain original conversation order");
        Check(memory.GetRecentEntries().Where(e => e.Role == "assistant").All(e => !e.CompleteTurn), "Live excerpts never receive a synthetic completed-turn label");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory, true);
        // Verbatim synthetic-user fragments/times observed in candidate A/B captures.
        var observed = new[] {
            ("user", " I'm", 12600, 12800), ("user", " definitely", 13200, 13400),
            ("user", " winning", 13400, 13600), ("user", " this", 13600, 13800),
            ("user", " argument", 14200, 14400), ("assistant", " Mm-h", 14200, 14400),
            ("assistant", "m.", 14400, 14600), ("user", " Don't", 17600, 17800),
            ("user", " call me", 18000, 18200), ("user", " sweetheart", 18800, 19000),
            ("assistant", " Alright", 19000, 19200)
        };
        for(int i=0;i<observed.Length;i++) tracker.ObserveTranscript(Fragment("observed"+i,observed[i].Item1,"observed-group",observed[i].Item2,observed[i].Item3,observed[i].Item4),start.AddMilliseconds(observed[i].Item3));
        tracker.FlushUser();
        Check(memory.GetPreferences().Any(p=>p.Key=="avoid_nickname:sweetheart"), "actual unpunctuated Live correction fragments create the explicit nickname preference");
        Check(!memory.GetPreferences().Any(p=>p.Key=="preferred_name"), "close before the Alex caption does not invent the uncaptured preferred name");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory,true);
        tracker.ObserveTranscript(Fragment("plain-full","user","u","Don't call me sweetheart Call me Alex")); tracker.FlushUser();
        Check(memory.GetPreferences().Any(p=>p.Key=="avoid_nickname:sweetheart") && memory.GetPreferences().Any(p=>p.Key=="preferred_name" && p.Value.Contains("Alex")), "complete unpunctuated replacement clause preserves disliked nickname and explicit preferred name");
        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory,true);
        foreach(string unsafeInference in new[] { "Don't call me", "Don't call me sweet", "She said don't call me sweetheart", "Don't call me tonight", "Don't call me sweetheart when I'm upset", "Don't call me sweetheart is what she said", "Don't call me sweetheart Call me" })
        { tracker.ObserveTranscript(Fragment(Guid.NewGuid().ToString("N"),"user","u",unsafeInference));tracker.FlushUser(); }
        Check(memory.PreferenceCount==0, "plain ASR rejects unfinished, reported, phone-call and conditional preference inferences");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory, true);
        tracker.ObserveAudio(Audio("x")); tracker.ObserveTranscript(Fragment("x1", "assistant", "x", "Interrupted earlier.")); tracker.Interrupt("x");
        tracker.ObserveAudio(Audio("y")); tracker.ObserveTranscript(Fragment("y1", "assistant", "y", "Later speech."));
        Check(!tracker.FinalizeLiveConnection(true, true) && Assistants(memory) == 0, "any interrupted Live group prevents whole-connection assistant restoration");

        memory = new ConversationMemory(); tracker = new CaptionAudibilityTracker(memory, true);
        for (int i = 0; i < 20; i++) { tracker.ObserveAudio(Audio("bounded" + i)); tracker.ObserveTranscript(Fragment("f" + i, "assistant", "bounded" + i, "Recent excerpt " + i)); }
        tracker.FinalizeLiveConnection(true, true);
        Check(memory.RecentCount <= 8 && memory.GetRecentEntries().Last().Text == "Recent excerpt 19", "deferred caption storage remains bounded and keeps newest context");
        tracker.Clear();
        Check(memory.RecentCount == 0 && memory.PreferenceCount == 0 && memory.Summary == "" && tracker.PendingAssistantCount == 0, "clear removes tracker, recent context and derived memory together");
        Console.WriteLine(passed + " caption audibility checks passed offline; no timing anchor or physical hearing is claimed.");
    }
}
