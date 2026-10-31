using System;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;
namespace FamilyTimeVoice
{
    public sealed class MicrophoneCapture : IDisposable
    {
        private AudioClip clip;
        private string device;
        private int position;
        private StreamResampler resampler;
        private float[] allSamples;
        private float lastPoll;
        public bool Running => clip != null;
        public int SampleRate => clip == null ? 0 : clip.frequency;
        public void Start(string selectedDevice)
        {
            Stop();
            var devices = Microphone.devices;
            if (devices.Length == 0) throw new InvalidOperationException("No microphone is available.");
            device = string.IsNullOrEmpty(selectedDevice) ? devices[0] : selectedDevice;
            if (Array.IndexOf(devices, device) < 0) throw new InvalidOperationException("Selected microphone is disconnected.");
            clip = Microphone.Start(device, true, 2, 24000);
            if (!clip) throw new InvalidOperationException("Microphone could not start. Check Windows microphone permission.");
            position = 0; lastPoll = Time.realtimeSinceStartup;
            resampler = new StreamResampler(clip.frequency, 24000);
            allSamples = new float[clip.samples * clip.channels];
        }
        public byte[] Poll(bool transmit)
        {
            if (!clip) return null;
            if (Array.IndexOf(Microphone.devices, device) < 0 || !Microphone.IsRecording(device))
                throw new InvalidOperationException("Microphone was disconnected or recording stopped.");
            int cursor = Microphone.GetPosition(device);
            float now = Time.realtimeSinceStartup;
            if (now - lastPoll > 1.8f) { position = cursor; lastPoll = now; throw new InvalidOperationException("Microphone capture stalled; conversation stopped."); }
            lastPoll = now;
            int count = (cursor - position + clip.samples) % clip.samples;
            if (count == 0) return null;
            if (!transmit) { position = cursor; return null; }
            // Read bounded circular clip only on main thread. Typical capture chunk is 10-30 ms.
            clip.GetData(allSamples, 0);
            var chunk = new float[count * clip.channels];
            for (int i = 0; i < count; i++)
                Array.Copy(allSamples, ((position + i) % clip.samples) * clip.channels, chunk, i * clip.channels, clip.channels);
            position = cursor;
            return AudioCodec.Encode(resampler.Process(chunk, clip.channels));
        }
        public void Stop()
        {
            if (clip) { Microphone.End(device); UnityEngine.Object.Destroy(clip); }
            clip = null; allSamples = null; resampler = null;
        }
        public void Dispose() => Stop();
    }

    public sealed class PlaybackSnapshot
    {
        public string ResponseId;
        public string ItemId;
        public int ContentIndex;
        public int HeardMilliseconds;
        public bool Drained;
    }

    // Unity invokes this at the AudioSource DSP filter stage, after any streaming
    // clip prefetch. It contains no Unity API calls on the audio thread.
    public sealed class VoiceAudioFilter : MonoBehaviour
    {
        public Action<float[], int> Render;
        public void OnAudioFilterRead(float[] data, int channels)
        {
            var render = Render;
            if (render == null) Array.Clear(data, 0, data.Length);
            else render(data, channels);
        }
    }

    public sealed class StreamingPlayback : IDisposable
    {
        private const int Rate = 24000;
        private readonly object gate = new object();
        // A 512-token spoken reply can arrive faster than it is played and can
        // exceed the old twelve-second ring. Keep a hard 30-second/2.9 MB bound;
        // interruption still clears it immediately rather than waiting to drain.
        private readonly float[] ring = new float[Rate * 30];
        private int read, write, count;
        private long consumed;
        private int latencySamples;
        private int outputRate;
        private double phase;
        private long lastRenderedTicks;
        private sealed class DeliveryBlock { public long Ticks; public long NonSilent; }
        private readonly Queue<DeliveryBlock> delivery = new Queue<DeliveryBlock>();
        private long deliveredNonSilent;
        private float nextConfigurationCheck;
        private bool disposed;
        private string response, item;
        private int contentIndex;
        private readonly GameObject holder;
        private readonly AudioSource source;
        private readonly AudioClip clip;
        private readonly VoiceAudioFilter filter;
        public bool HasAudio { get { lock (gate) return count > 0 || OutstandingDeviceSamples() > 0; } }
        public string CurrentResponse { get { lock (gate) return response; } }
        public long TotalHeardNonSilentSamples { get { lock (gate) { DrainDeliveredBlocks(); return deliveredNonSilent; } } }
        public StreamingPlayback()
        {
            holder = new GameObject("FamilyTimeVoice.SpatialPlayback");
            UnityEngine.Object.DontDestroyOnLoad(holder);
            source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false; source.loop = true; source.spatialBlend = 1;
            source.pitch = 1f;
            source.minDistance = 2; source.maxDistance = 30; source.dopplerLevel = 0;
            source.rolloffMode = AudioRolloffMode.Linear;
            // The clip supplies silence only. Do not consume PCM here: Unity may
            // prefetch a streaming clip far ahead of what the listener hears.
            clip = AudioClip.Create("FamilyTimeVoice.Clock", Rate, 1, Rate, true,
                data => Array.Clear(data, 0, data.Length));
            source.clip = clip;
            filter = holder.AddComponent<VoiceAudioFilter>();
            filter.Render = RenderAudio;
            RefreshOutputConfiguration();
        }
        public void Follow(Transform target, float volume, bool mute)
        {
            if (target) holder.transform.position = target.position + Vector3.up * 1.5f;
            source.volume = Mathf.Clamp01(volume); source.mute = mute;
            if (Time.realtimeSinceStartup >= nextConfigurationCheck)
            {
                RefreshOutputConfiguration();
                nextConfigurationCheck = Time.realtimeSinceStartup + 1f;
            }
            if (HasAudio && !source.isPlaying) source.Play();
        }
        public bool Append(string responseId, string itemId, int index, byte[] bytes)
        {
            float[] data = AudioCodec.Decode(bytes);
            lock (gate)
            {
                if (disposed) return false;
                if (response != responseId || item != itemId)
                {
                    if (count > 0 || OutstandingDeviceSamples() > 0) return false;
                    response = responseId; item = itemId; contentIndex = index;
                    consumed = 0; phase = 0; lastRenderedTicks = 0;
                }
                else if (contentIndex != index) return false;
                if (data.Length > ring.Length - count) return false;
                foreach (float sample in data) { ring[write] = sample; write = (write + 1) % ring.Length; }
                count += data.Length; return true;
            }
        }
        private void RefreshOutputConfiguration()
        {
            int buffer, buffers;
            AudioSettings.GetDSPBufferSize(out buffer, out buffers);
            int rate = AudioSettings.outputSampleRate;
            if (rate <= 0) throw new InvalidOperationException("The audio output device is unavailable.");
            lock (gate)
            {
                outputRate = rate;
                // Count only DSP-rendered PCM, then subtract the complete device
                // pipeline. This is a conservative item-relative heard duration.
                latencySamples = (int)Math.Ceiling((double)buffer * buffers * Rate / rate);
            }
        }
        private void RenderAudio(float[] destination, int channels)
        {
            lock (gate)
            {
                Array.Clear(destination, 0, destination.Length);
                if (disposed || channels <= 0 || outputRate <= 0 || count == 0) return;
                int frames = destination.Length / channels;
                double step = (double)Rate / outputRate;
                bool rendered = false;
                long nonSilent = 0;
                for (int frame = 0; frame < frames && count > 0; frame++)
                {
                    float first = ring[read];
                    float second = count > 1 ? ring[(read + 1) % ring.Length] : first;
                    float sample = first + (second - first) * (float)phase;
                    for (int channel = 0; channel < channels; channel++)
                        destination[frame * channels + channel] = sample;
                    phase += step;
                    int advance = Math.Min(count, (int)phase);
                    for (int n = 0; n < advance; n++) if (ring[(read + n) % ring.Length] != 0f) nonSilent++;
                    read = (read + advance) % ring.Length;
                    count -= advance; consumed += advance; phase -= advance;
                    rendered = true;
                }
                if (count == 0) phase = 0;
                if (rendered)
                {
                    lastRenderedTicks = Stopwatch.GetTimestamp();
                    DrainDeliveredBlocks();
                    // An abnormal DSP queue cannot grow without bound or count dropped
                    // proof as heard. A missing block makes Live memory admission fail.
                    if (nonSilent > 0 && delivery.Count < 256) delivery.Enqueue(new DeliveryBlock { Ticks = lastRenderedTicks, NonSilent = nonSilent });
                }
            }
        }
        private void DrainDeliveredBlocks()
        {
            long now = Stopwatch.GetTimestamp();
            double pipelineSeconds = (double)latencySamples / Rate;
            while (delivery.Count > 0 && (double)(now - delivery.Peek().Ticks) / Stopwatch.Frequency >= pipelineSeconds)
                deliveredNonSilent += delivery.Dequeue().NonSilent;
        }
        public void ResetDeliveryCounters()
        {
            lock (gate) { delivery.Clear(); deliveredNonSilent = 0; }
        }
        public PlaybackSnapshot Snapshot()
        {
            lock (gate)
            {
                DrainDeliveredBlocks();
                return new PlaybackSnapshot { ResponseId = response, ItemId = item, ContentIndex = contentIndex,
                    HeardMilliseconds = (int)(Math.Max(0, consumed - OutstandingDeviceSamples()) * 1000 / Rate),
                    Drained = count == 0 && OutstandingDeviceSamples() == 0 };
            }
        }
        private long OutstandingDeviceSamples()
        {
            if (lastRenderedTicks == 0 || consumed == 0) return 0;
            double elapsed = (double)(Stopwatch.GetTimestamp() - lastRenderedTicks) / Stopwatch.Frequency;
            return Math.Min(consumed, Math.Max(0, latencySamples - (long)Math.Floor(elapsed * Rate)));
        }
        public PlaybackSnapshot StopAndClear()
        {
            source.Stop();
            lock (gate)
            {
                var result = Snapshot();
                delivery.Clear();
                read = write = count = 0; consumed = 0; phase = 0; lastRenderedTicks = 0; response = item = null;
                return result;
            }
        }
        public void Dispose()
        {
            StopAndClear();
            lock (gate) { disposed = true; filter.Render = null; }
            UnityEngine.Object.Destroy(clip); UnityEngine.Object.Destroy(holder);
        }
    }
}
