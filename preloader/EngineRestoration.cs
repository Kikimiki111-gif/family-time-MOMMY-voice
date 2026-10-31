using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Preloader.Patching;
using Mono.Cecil;

namespace FamilyTimeVoice.Preloader
{
    // No Unity type references: loading this patcher must not preload an engine module.
    public static class EngineRestoration
    {
        private static readonly string[] Modules =
        {
            "UnityEngine.SharedInternalsModule.dll",
            "UnityEngine.CoreModule.dll",
            "UnityEngine.AudioModule.dll",
            "UnityEngine.IMGUIModule.dll",
            "UnityEngine.TextRenderingModule.dll",
            "UnityEngine.InputLegacyModule.dll",
            "UnityEngine.ImageConversionModule.dll"
        };
        private const string TestedPlayerHash = "8F66352D326B9251186C67FB171F0075D08E3523543B5E27579E4AD395C8E478";
        private static bool compatible;
        private static ManualLogSource log;
        private static DefaultAssemblyResolver resolver;
        private static readonly HashSet<string> removedContracts = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<MethodDefinition, MethodReference> removedDefinitions =
            new Dictionary<MethodDefinition, MethodReference>();

        public static IEnumerable<string> TargetDLLs => compatible ? Modules : new string[0];

        public static void Initialize()
        {
            log = Logger.CreateLogSource("FamilyTimeVoice.EngineRestoration");
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream native = File.OpenRead(Path.Combine(Paths.GameRootPath, "UnityPlayer.dll")))
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(native)).Replace("-", "");
                    if (!string.Equals(actual, TestedPlayerHash, StringComparison.Ordinal))
                    {
                        log.LogError("Engine restoration disabled: UnityPlayer differs from the tested Family Time build. Rebuild against this game's runtime before enabling voice.");
                        return;
                    }
                }
                string directory = Path.Combine(Paths.BepInExRootPath, "FamilyTimeVoice-engine");
                foreach (string name in Modules)
                    if (!File.Exists(Path.Combine(directory, name)))
                    {
                        log.LogError("Engine restoration disabled: missing matching Unity 6000.2.12 runtime module " + name);
                        return;
                    }
                // Cecil needs enum/type dependencies when it writes module metadata.
                // A fresh ReadAssembly default resolver does not inherit the loader's
                // search paths and cannot even resolve Core from the companion folder.
                resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(directory);
                resolver.AddSearchDirectory(Paths.ManagedPath);
                resolver.AddSearchDirectory(Path.Combine(Paths.BepInExRootPath, "FamilyTimeVoice-runtime"));
                resolver.AddSearchDirectory(Path.Combine(Paths.BepInExRootPath, "core"));

                // The built-in chainloader entrypoint patch must run AFTER full Core is
                // restored, otherwise replacing Core would erase that injected entrypoint.
                IList<PatcherPlugin> patchers = AssemblyPatcher.PatcherPlugins;
                for (int i = 0; i < patchers.Count; i++)
                {
                    if (patchers[i].TypeName == typeof(EngineRestoration).FullName)
                    {
                        PatcherPlugin self = patchers[i];
                        patchers.RemoveAt(i);
                        patchers.Insert(0, self);
                        break;
                    }
                }
                compatible = true;
                log.LogInfo("Restoring missing audio/UI managed wrappers from matching Unity 6000.2.12 in memory. Original game files are unchanged.");
            }
            catch (Exception ex)
            {
                log.LogError("Engine restoration disabled: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Patch(ref AssemblyDefinition assembly)
        {
            if (!compatible) return;
            string file = assembly.Name.Name + ".dll";
            string path = Path.Combine(Paths.BepInExRootPath, "FamilyTimeVoice-engine", file);
            AssemblyDefinition restored = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                InMemory = true,
                AssemblyResolver = resolver
            });
            if (restored.Name.Name != assembly.Name.Name || restored.Name.Version != assembly.Name.Version)
            {
                restored.Dispose();
                throw new InvalidOperationException("Restoration module identity does not match " + file);
            }
            PreserveOriginalContracts(assembly, restored);
            assembly = restored;
            log.LogInfo("Restored " + file + " from the matching engine runtime.");
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition type in types)
            {
                yield return type;
                foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
            }
        }

        private static void PreserveOriginalContracts(AssemblyDefinition original, AssemblyDefinition restored)
        {
            var originals = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
            foreach (TypeDefinition type in AllTypes(original.MainModule.Types)) originals[type.FullName] = type;
            int removed = 0;
            foreach (TypeDefinition type in AllTypes(restored.MainModule.Types))
            {
                TypeDefinition old;
                if (!originals.TryGetValue(type.FullName, out old)) continue;
                var oldMethods = new HashSet<string>(StringComparer.Ordinal);
                foreach (MethodDefinition method in old.Methods) oldMethods.Add(method.FullName);
                var deletedHere = new HashSet<MethodDefinition>();
                foreach (MethodDefinition method in new List<MethodDefinition>(type.Methods))
                {
                    // Other game assemblies were stripped against these original
                    // contracts. Expanding them would require absent implementations
                    // on existing derived/implementing classes and break their VTables.
                    if ((type.IsInterface || method.IsAbstract) && !oldMethods.Contains(method.FullName))
                    {
                        removedContracts.Add(method.FullName);
                        // Cecil detaches DeclaringType when removing a definition;
                        // retain its valid reference before the collection changes.
                        removedDefinitions[method] = AsMemberReference(method);
                        deletedHere.Add(method);
                        type.Methods.Remove(method);
                        removed++;
                    }
                }
                foreach (PropertyDefinition property in new List<PropertyDefinition>(type.Properties))
                {
                    if (property.GetMethod != null && deletedHere.Contains(property.GetMethod)) property.GetMethod = null;
                    if (property.SetMethod != null && deletedHere.Contains(property.SetMethod)) property.SetMethod = null;
                    if (property.GetMethod == null && property.SetMethod == null) type.Properties.Remove(property);
                }
                foreach (EventDefinition evt in new List<EventDefinition>(type.Events))
                {
                    if (evt.AddMethod != null && deletedHere.Contains(evt.AddMethod)) evt.AddMethod = null;
                    if (evt.RemoveMethod != null && deletedHere.Contains(evt.RemoveMethod)) evt.RemoveMethod = null;
                    if (evt.InvokeMethod != null && deletedHere.Contains(evt.InvokeMethod)) evt.InvokeMethod = null;
                    if (evt.AddMethod == null && evt.RemoveMethod == null && evt.InvokeMethod == null) type.Events.Remove(evt);
                }
                if (type.IsInterface)
                {
                    var originalParents = new HashSet<string>(StringComparer.Ordinal);
                    foreach (InterfaceImplementation entry in old.Interfaces) originalParents.Add(entry.InterfaceType.FullName);
                    foreach (InterfaceImplementation entry in new List<InterfaceImplementation>(type.Interfaces))
                        if (!originalParents.Contains(entry.InterfaceType.FullName)) type.Interfaces.Remove(entry);
                }
            }
            int overrides = 0;
            foreach (TypeDefinition type in AllTypes(restored.MainModule.Types))
            foreach (MethodDefinition method in type.Methods)
            {
                foreach (MethodReference contract in new List<MethodReference>(method.Overrides))
                {
                    if (IsRemovedContract(contract)) { method.Overrides.Remove(contract); overrides++; }
                }
                if (!method.HasBody) continue;
                foreach (Mono.Cecil.Cil.Instruction instruction in method.Body.Instructions)
                {
                    MethodReference reference = instruction.Operand as MethodReference;
                    if (reference != null && IsRemovedContract(reference))
                        instruction.Operand = AsMemberReference(reference);
                }
            }
            log.LogInfo("Preserved original interface/abstract contracts: removed " + removed + " added declarations and " + overrides + " dangling explicit overrides from " + restored.Name.Name + ".");
        }

        private static bool IsRemovedContract(MethodReference contract)
        {
            var definition = contract as MethodDefinition;
            if (definition != null && removedDefinitions.ContainsKey(definition)) return true;
            var instance = contract as GenericInstanceMethod;
            if (instance != null && IsRemovedContract(instance.ElementMethod)) return true;
            string key = contract.FullName;
            try { MethodDefinition resolved = contract.Resolve(); if (resolved != null) key = resolved.FullName; }
            catch (AssemblyResolutionException) { }
            return removedContracts.Contains(key);
        }

        private static MethodReference AsMemberReference(MethodReference source)
        {
            var definition = source as MethodDefinition;
            MethodReference saved;
            if (definition != null && removedDefinitions.TryGetValue(definition, out saved)) return saved;
            var instance = source as GenericInstanceMethod;
            if (instance != null)
            {
                var copiedInstance = new GenericInstanceMethod(AsMemberReference(instance.ElementMethod));
                foreach (TypeReference argument in instance.GenericArguments) copiedInstance.GenericArguments.Add(argument);
                return copiedInstance;
            }
            var copy = new MethodReference(source.Name, source.ReturnType, source.DeclaringType)
            {
                HasThis = source.HasThis,
                ExplicitThis = source.ExplicitThis,
                CallingConvention = source.CallingConvention
            };
            foreach (ParameterDefinition parameter in source.Parameters)
                copy.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
            foreach (GenericParameter parameter in source.GenericParameters)
                copy.GenericParameters.Add(new GenericParameter(parameter.Name, copy));
            return copy;
        }
    }
}
