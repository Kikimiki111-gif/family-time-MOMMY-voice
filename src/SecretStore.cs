using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FamilyTimeVoice
{
    // DPAPI user scope; no plaintext secret is written to configuration or logs.
    public sealed class SecretStore
    {
        private readonly string path;
        public SecretStore(string path = null)
        {
            this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FamilyTimeVoice", "api-key.dpapi");
        }
        public bool HasKey { get { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) || File.Exists(path); } }
        public string Load()
        {
            string environmentKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(environmentKey)) return environmentKey.Trim();
            if (!File.Exists(path)) return null;
            byte[] encrypted = File.ReadAllBytes(path);
            byte[] plain = Transform(encrypted, false);
            try { return Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain, 0, plain.Length); }
        }
        public void Save(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Enter an API key in the masked local field.");
            byte[] plain = Encoding.UTF8.GetBytes(key.Trim());
            try
            {
                byte[] encrypted = Transform(plain, true);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path + ".tmp", encrypted);
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public void Delete() { if (File.Exists(path)) File.Delete(path); }
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        private static byte[] Transform(byte[] input, bool protect)
        {
            var source = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
            Blob output = default(Blob);
            try
            {
                Marshal.Copy(input, 0, source.Data, input.Length);
                bool ok = protect ? CryptProtectData(ref source, "Family Time Voice API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new InvalidOperationException("Windows could not access the user-protected API key. Re-enter it locally under this Windows account.");
                var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                for (int i = 0; i < source.Length; i++) Marshal.WriteByte(source.Data, i, 0);
                Marshal.FreeHGlobal(source.Data);
                if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
            }
        }
    }
}
