using System;
using System.Collections.Generic;
using FamilyTimeVoice;

class AudioTests
{
    static int count;
    static void Check(bool success, string name) { if (!success) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
    static void Main()
    {
        byte[] encoded = AudioCodec.Encode(new float[] { -2, -1, 0, 1, 2, float.NaN, float.PositiveInfinity });
        Check(encoded[0] == 0 && encoded[1] == 128 && encoded[6] == 255 && encoded[7] == 127, "PCM16 little endian and saturation");
        Check(AudioCodec.Decode(encoded)[5] == 0, "NaN becomes silence");
        bool rejected = false; try { AudioCodec.Decode(new byte[3]); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Incomplete PCM16 rejected");
        foreach (int rate in new[] { 16000, 24000, 44100, 48000 })
        {
            var input = new float[rate];
            for (int i = 0; i < rate; i++) input[i] = (float)Math.Sin(2 * Math.PI * 440 * i / rate);
            float[] whole = new StreamResampler(rate, 24000).Process(input);
            var incremental = new StreamResampler(rate, 24000); var chunks = new List<float>();
            for (int p = 0; p < rate; p += 137) { var piece = new float[Math.Min(137, rate - p)]; Array.Copy(input, p, piece, 0, piece.Length); chunks.AddRange(incremental.Process(piece)); }
            double maxDifference = 0; for (int i = 0; i < Math.Min(whole.Length, chunks.Count); i++) maxDifference = Math.Max(maxDifference, Math.Abs(whole[i] - chunks[i]));
            Check(whole.Length == chunks.Count && maxDifference < 0.000001, rate + " Hz chunk boundaries preserve phase");
            Check(Math.Abs(whole.Length - 24000) <= 2, rate + " Hz preserves duration");
            double err = 0; for (int i = 0; i < whole.Length; i++) err += Math.Pow(whole[i] - Math.Sin(2 * Math.PI * 440 * i / 24000), 2);
            Check(Math.Sqrt(err / whole.Length) < 0.005, rate + " Hz preserves test tone");
        }
        var stereo = new StreamResampler(24000, 24000).Process(new float[] { 1, -1, 0.5f, 0.5f }, 2);
        Check(stereo.Length == 2 && stereo[0] == 0 && stereo[1] == 0.5f, "Stereo downmix produces mono");
        var queue = new MainThreadQueue(); int calls = 0;
        for (int i = 0; i < 300; i++) queue.Enqueue(() => calls++);
        Check(queue.TakeOverflow() && !queue.TakeOverflow(), "Main thread overflow is signalled once");
        Action action; while (queue.TryDequeue(out action)) action();
        Check(calls == 256, "Main thread dispatch is bounded");
        Console.WriteLine(count + " audio tests passed");
    }
}
