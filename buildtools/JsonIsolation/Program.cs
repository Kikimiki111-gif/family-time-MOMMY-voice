using System;
using System.IO;
using Mono.Cecil;

if (args.Length != 2) throw new ArgumentException("Pass official Newtonsoft.Json.dll input and isolated output paths.");
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
if (assembly.Name.Name != "Newtonsoft.Json" || assembly.Name.Version != new Version(13, 0, 0, 0)) throw new InvalidDataException("Unexpected JSON dependency identity.");
assembly.Name.Name = "FamilyTimeVoice.Json";
assembly.Name.PublicKey = Array.Empty<byte>();
assembly.Name.HasPublicKey = false;
assembly.MainModule.Name = "FamilyTimeVoice.Json.dll";
assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
assembly.Write(args[1]);
Console.WriteLine("Created isolated FamilyTimeVoice.Json.dll from official Newtonsoft.Json 13.0.3.");
