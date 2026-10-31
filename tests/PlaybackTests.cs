using System;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using FamilyTimeVoice;
using UnityEngine;

static class PlaybackTests
{
    static int passed;
    static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
    static byte[] Pcm(int samples, float value = 0.5f) => AudioCodec.Encode(Enumerable.Repeat(value, samples).ToArray());
    static void SetLastRender(StreamingPlayback player, long ticks) => typeof(StreamingPlayback).GetField("lastRenderedTicks", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(player, ticks);
    static void AgeDelivery(StreamingPlayback player)
    {
        var queue = (System.Collections.IEnumerable)typeof(StreamingPlayback).GetField("delivery", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(player);
        foreach (object block in queue) block.GetType().GetField("Ticks").SetValue(block, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2);
    }
    static void Main()
    {
        using (var player = new StreamingPlayback())
        {
            Check(Boundary.Source.pitch == 1f && Boundary.Source.dopplerLevel == 0, "voice identity keeps neutral pitch and no movement Doppler");
            player.Append("proof", "proof", 0, Pcm(240));
            Boundary.Filter.OnAudioFilterRead(new float[960], 2);
            Check(player.TotalHeardNonSilentSamples == 0, "device pipeline samples are excluded from whole-connection audibility proof");
            player.StopAndClear(); AgeDelivery(player);
            Check(player.TotalHeardNonSilentSamples == 0, "clearing an unheard device tail never counts it later");
            player.Append("proof2", "proof2", 0, Pcm(240));
            Boundary.Filter.OnAudioFilterRead(new float[960], 2); AgeDelivery(player);
            Check(player.TotalHeardNonSilentSamples == 240, "delivered nonzero input samples count exactly once after the full DSP delay");
            player.StopAndClear();
            Check(player.TotalHeardNonSilentSamples == 240, "closing playback preserves already delivered connection totals");
            player.ResetDeliveryCounters();
            Check(player.TotalHeardNonSilentSamples == 0, "new connection resets its audibility scope");
            Check(player.Append("response1", "item1", 2, Pcm(48000)), "two seconds can be queued in bounded stream");
            var prefetched = Enumerable.Repeat(1f, 24000).ToArray(); Boundary.Clip.Reader(prefetched);
            Check(prefetched.All(v => v == 0), "Unity PCM prefetch receives silence, never conversation samples");
            var beforeOutput = player.StopAndClear();
            Check(beforeOutput.ItemId == "item1" && beforeOutput.ContentIndex == 2 && beforeOutput.HeardMilliseconds == 0,
                "queued or prefetched audio cannot count as heard");
            Check(!player.HasAudio && player.CurrentResponse == null, "interruption clears queued audio and response identity");

            Check(player.Append("response2", "item2", 0, Pcm(48000)), "new item can start after interruption");
            var rendered = new float[48000 * 2]; Boundary.Filter.OnAudioFilterRead(rendered, 2);
            Check(rendered.All(v => Math.Abs(v - 0.5f) < 0.0001), "24 kHz mono renders one second at 48 kHz with correct stereo channels");
            SetLastRender(player, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2);
            var oneSecond = player.StopAndClear();
            Check(oneSecond.HeardMilliseconds == 1000, "heard duration counts rendered item samples, excludes second queued second");
            var after = new float[4096]; Boundary.Filter.OnAudioFilterRead(after, 2);
            Check(after.All(v => v == 0), "audio callback after interruption contains no cancelled samples");

            Check(player.Append("response3", "item3", 1, Pcm(40)), "short response appends");
            Boundary.Filter.OnAudioFilterRead(new float[160], 2);
            SetLastRender(player, Stopwatch.GetTimestamp());
            Check(player.HasAudio, "rendered device tail remains active even with an empty ring");
            Check(!player.Append("overlap", "other-item", 0, Pcm(10)), "new item cannot replace identity while previous device tail remains");
            Check(player.StopAndClear().HeardMilliseconds == 0, "short audio still in full DSP pipeline is not yet heard");

            Check(player.Append("response4", "item4", 0, Pcm(24000 * 30)), "bounded ring accommodates a burst of the upgraded response ceiling");
            Check(!player.Append("response4", "item4", 0, Pcm(1)), "ring rejects overflow without unbounded allocation");
            Check(!player.Append("response4", "item4", 1, Pcm(1)), "different audio content index cannot reuse current counters");
            player.StopAndClear();
        }
        var disposed = new StreamingPlayback(); disposed.Dispose();
        Check(!disposed.Append("late", "late-item", 0, Pcm(10)), "disposed playback rejects late network data");
        Console.WriteLine(passed + " playback checks passed with a simulated Unity boundary; native DSP/spatial listening remains an in-game check.");
    }
}
