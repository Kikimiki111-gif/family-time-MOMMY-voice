// Test-only engine boundary. This does not claim native Unity audio validation.
using System;
namespace UnityEngine
{
    public class Object
    {
        public static bool operator !(Object value) => ReferenceEquals(value, null);
        public static implicit operator bool(Object value) => !ReferenceEquals(value, null);
        public static void Destroy(Object value) { }
        public static void DontDestroyOnLoad(Object value) { }
    }
    public class MonoBehaviour : Object { }
    public class Transform : Object { public Vector3 position; }
    public struct Vector3
    {
        public float x, y, z;
        public static Vector3 up => new Vector3 { y = 1 };
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3 { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };
        public static Vector3 operator *(Vector3 a, float f) => new Vector3 { x = a.x * f, y = a.y * f, z = a.z * f };
    }
    public class GameObject : Object
    {
        public Transform transform = new Transform();
        public GameObject(string name) { }
        public T AddComponent<T>() where T : new()
        {
            T value = new T();
            if (value is FamilyTimeVoice.VoiceAudioFilter filter) Boundary.Filter = filter;
            if (value is AudioSource source) Boundary.Source = source;
            return value;
        }
    }
    public static class Boundary
    {
        public static FamilyTimeVoice.VoiceAudioFilter Filter;
        public static AudioClip Clip;
        public static AudioSource Source;
    }
    public class AudioClip : Object
    {
        public int frequency = 24000, samples = 24000, channels = 1;
        public Action<float[]> Reader;
        public static AudioClip Create(string name, int length, int channels, int rate, bool stream, Action<float[]> reader)
        { Boundary.Clip = new AudioClip { frequency = rate, samples = length, channels = channels, Reader = reader }; return Boundary.Clip; }
        public void GetData(float[] destination, int offset) { }
    }
    public enum AudioRolloffMode { Linear }
    public class AudioSource : Object
    {
        public bool playOnAwake, loop, mute, isPlaying;
        public float spatialBlend, minDistance, maxDistance, dopplerLevel, volume, pitch;
        public AudioRolloffMode rolloffMode;
        public AudioClip clip;
        public void Play() { isPlaying = true; }
        public void Stop() { isPlaying = false; }
    }
    public static class AudioSettings
    {
        public static int outputSampleRate = 48000;
        public static int Buffer = 1024, Buffers = 4;
        public static void GetDSPBufferSize(out int buffer, out int buffers) { buffer = Buffer; buffers = Buffers; }
    }
    public static class Time { public static float realtimeSinceStartup; }
    public static class Mathf { public static float Clamp01(float x) => Math.Max(0, Math.Min(1, x)); }
    public static class Microphone
    {
        public static string[] devices = new[] { "test" };
        public static AudioClip Start(string device, bool loop, int length, int rate) => new AudioClip();
        public static void End(string device) { }
        public static bool IsRecording(string device) => true;
        public static int GetPosition(string device) => 0;
    }
}
