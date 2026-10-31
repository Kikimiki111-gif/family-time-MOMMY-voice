using System;


namespace FamilyTimeVoice
{
    // PCM conversion adapted from mapluisch/OpenAI-Realtime-API-for-Unity,
    // commit 0540daaae2fa4463d437432244a1e1a0d9f49600. MIT; see notices.
    public static class AudioCodec
    {
        public static byte[] Encode(float[] samples)
        {
            var data = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                float v = float.IsNaN(samples[i]) ? 0 : Math.Max(-1, Math.Min(1, samples[i]));
                short value = (short)Math.Round(v < 0 ? v * 32768 : v * 32767);
                data[i * 2] = (byte)value; data[i * 2 + 1] = (byte)(value >> 8);
            }
            return data;
        }
        public static float[] Decode(byte[] data)
        {
            if (data == null || data.Length % 2 != 0) throw new ArgumentException("PCM16 needs complete samples.");
            var samples = new float[data.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(data[i * 2] | data[i * 2 + 1] << 8) / 32768f;
            return samples;
        }
    }

    // Carries interpolation phase across microphone chunks, including wrap boundaries.
    public sealed class StreamResampler
    {
        private readonly double step;
        private double next;
        private long position;
        private float previous;
        private bool hasPrevious;
        public StreamResampler(int inputRate, int outputRate)
        {
            if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException();
            step = (double)inputRate / outputRate;
        }
        public float[] Process(float[] input, int channels = 1)
        {
            if (channels <= 0 || input.Length % channels != 0) throw new ArgumentException("Incomplete audio frame.");
            var result = new System.Collections.Generic.List<float>();
            for (int frame = 0; frame < input.Length / channels; frame++)
            {
                float sample = 0;
                for (int c = 0; c < channels; c++) sample += input[frame * channels + c] / channels;
                if (!hasPrevious) { previous = sample; hasPrevious = true; }
                while (next <= position)
                {
                    var t = Math.Max(0, Math.Min(1, next - (position - 1)));
                    result.Add(previous + (sample - previous) * (float)t);
                    next += step;
                }
                previous = sample; position++;
            }
            return result.ToArray();
        }
    }

}
