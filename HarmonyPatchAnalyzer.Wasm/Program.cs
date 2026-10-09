using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using Mono.Cecil;
using NameNormalizer;

namespace HarmonyPatchAnalyzer.Wasm;

public static partial class Program
{
    private const string HarmonyPatchAttributeFullName = "HarmonyLib.HarmonyPatch";

    public static void Main() { }

    [JSExport]
    public static string[] AnalyzeMod(byte[] assemblyBytes)
    {
        using var stream = new MemoryStream(assemblyBytes);

        var readerParameters = new ReaderParameters
        {
            AssemblyResolver = new NullAssemblyResolver(),
            InMemory = true
        };

        var assembly = AssemblyDefinition.ReadAssembly(stream, readerParameters);
        var methodNames = new List<string>();

        foreach (var module in assembly.Modules)
        {
            foreach (var type in MonoUtils.GetAllTypes(module))
            {
                string? typeRef = null;
                foreach (var attr in type.CustomAttributes.Where(x => x.AttributeType.FullName == HarmonyPatchAttributeFullName))
                {
                    ScanAttribute(attr, methodNames);
                    typeRef = attr.ConstructorArguments.Select(x => x.Value).OfType<TypeReference>().FirstOrDefault()?.FullName;
                }

                foreach (var method in type.Methods)
                {
                    foreach (var attr in method.CustomAttributes.Where(x => x.AttributeType.FullName == HarmonyPatchAttributeFullName))
                    {
                        if (typeRef != null)
                        {
                            var methodRef = attr.ConstructorArguments.Select(x => x.Value).OfType<string>().FirstOrDefault();
                            if (methodRef != null)
                            {
                                methodNames.Add(Normalizer.NormalizeMonoName($"{typeRef}::{methodRef}"));
                            }
                        }
                        ScanAttribute(attr, methodNames);
                    }
                }
            }
        }

        return methodNames.Distinct().OrderBy(x => x).ToArray();
    }

    private static void ScanAttribute(CustomAttribute attribute, List<string> methodNames)
    {
        var typeRef = attribute.ConstructorArguments.Select(x => x.Value).OfType<TypeReference>().FirstOrDefault();
        if (typeRef != null)
        {
            var methodRef = attribute.ConstructorArguments.Select(x => x.Value).OfType<string>().FirstOrDefault();
            if (methodRef != null)
            {
                methodNames.Add(Normalizer.NormalizeMonoName($"{typeRef.FullName}::{methodRef}"));
            }
        }
    }
}

/// <summary>
/// Dummy assembly resolver that returns empty assembly stubs instead of throwing when dependencies (like 0Harmony) are missing.
/// </summary>
public class NullAssemblyResolver : IAssemblyResolver
{
    public AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        return AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name.Name, name.Version),
            name.Name,
            ModuleKind.Dll);
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        return Resolve(name);
    }

    public void Dispose() { }
}