using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FamilyTimeVoice;

class Program
{
    static int inspected;
    static byte[] privateKey;
    static bool publicRelease;
    static byte[] privatePathUtf8, privatePathUtf16;
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new Exception();
            publicRelease = args.Contains("--public");
            string privatePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (publicRelease && !string.IsNullOrWhiteSpace(privatePath))
            { privatePathUtf8 = Encoding.UTF8.GetBytes(privatePath); privatePathUtf16 = Encoding.Unicode.GetBytes(privatePath); }
            if (args.Contains("--check-local-key"))
            {
                string key = new SecretStore().Load();
                if (string.IsNullOrWhiteSpace(key)) throw new Exception();
                privateKey = Encoding.UTF8.GetBytes(key); key = null;
            }
            using (var file = File.OpenRead(args[0])) Inspect(file, 0);
            Console.WriteLine("PASS release contents: " + inspected + " entries inspected; no prohibited game libraries or private storage.");
            if (privateKey != null) Console.WriteLine("PASS actual local API key absent from the release, including nested archives. Key value was never output.");
            return 0;
        }
        catch { Console.WriteLine("FAIL release verification; artifact or private-storage check failed. No secret details emitted."); return 1; }
        finally { if (privateKey != null) CryptographicOperations.ZeroMemory(privateKey); }
    }
    static void Inspect(Stream stream, int depth)
    {
        if (depth > 3) throw new Exception();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            string leaf = name.Split('/').Last();
            if (publicRelease && (name.Split('/').Any(part => new[] { "GameSmokePlugin", "LiveAudition", "UpgradeAudition", "BaselinePromptExtractor", "upgrade" }.Contains(part)) ||
                new[] { "test-report.md", "game-inspection.md", "SOURCE_AUDIT.md", "voices.json", "memory-note.txt", "artifacts-build.txt" }.Contains(leaf) ||
                leaf.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".log", StringComparison.OrdinalIgnoreCase))) throw new Exception();
            if (name.Split('/').Contains(".tools") || leaf.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("usage-ledger.json", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase) || leaf.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) || (leaf.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) throw new Exception();
            if (entry.Length > 20000000) throw new Exception();
            using var bytes = new MemoryStream(); using (var input = entry.Open()) input.CopyTo(bytes);
            byte[] data = bytes.ToArray(); inspected++;
            if (privateKey != null && Contains(data, privateKey)) throw new Exception();
            if (privatePathUtf8 != null && (Contains(data, privatePathUtf8) || Contains(data, privatePathUtf16))) throw new Exception();
            if (leaf.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) Inspect(new MemoryStream(data, false), depth + 1);
        }
    }
    static bool Contains(byte[] data, byte[] needle)
    {
        for (int i = 0; i <= data.Length - needle.Length; i++)
        {
            int j = 0; while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
