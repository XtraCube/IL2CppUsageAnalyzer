using HarmonyLib;
using Mono.Cecil;
using NameNormalizer;

namespace HarmonyPatchAnalyzer;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: HarmonyPatchAnalyzer <mod file path> <output file path>");
            return;
        }

        var modFilePath = args[0];
        var outputFilePath = args[1];

        if (!File.Exists(modFilePath))
        {
            Console.WriteLine("Invalid mod path provided.");
            return;
        }

        var methodNames = AnalyzeMod(modFilePath);
        using var writer = new StreamWriter(outputFilePath);
        foreach (var methodName in methodNames)
        {
            writer.WriteLine(methodName);
        }
    }

    private static string HarmonyPatchAttributeFullName { get; } = typeof(HarmonyPatch).FullName!;

    private static List<string> AnalyzeMod(string modFilePath)
    {
        var assembly = AssemblyDefinition.ReadAssembly(modFilePath);
        var methodNames = new List<string>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in MonoUtils.GetAllTypes(module))
            {
                // Collect stacked HarmonyPatch attributes on the type
                var typePatchAttrs = type.CustomAttributes
                    .Where(x => x.AttributeType.FullName == HarmonyPatchAttributeFullName)
                    .ToList();

                foreach (var method in type.Methods)
                {
                    // Collect stacked HarmonyPatch attributes on the method
                    var methodPatchAttrs = method.CustomAttributes
                        .Where(x => x.AttributeType.FullName == HarmonyPatchAttributeFullName)
                        .ToList();

                    // Combine type and method attributes for stacked usage
                    var allAttrs = new List<CustomAttribute>();
                    allAttrs.AddRange(typePatchAttrs);
                    allAttrs.AddRange(methodPatchAttrs);

                    if (allAttrs.Count <= 0) continue;
                    var patchInfo = ParseHarmonyPatchAttributes(allAttrs);
                    if (patchInfo.TargetType == null || patchInfo.MethodName == null) continue;
                    var normalized = Normalizer.NormalizeMonoName(
                        $"{patchInfo.TargetType.FullName}::{patchInfo.MethodName}"
                    );
                    methodNames.Add(normalized);
                }
            }
        }

        methodNames = methodNames.Distinct().OrderBy(x=>x).ToList();
        return methodNames;
    }

    // Helper to parse stacked HarmonyPatch attributes
    private static (TypeReference? TargetType, string? MethodName) ParseHarmonyPatchAttributes(List<CustomAttribute> attrs)
    {
        TypeReference? targetType = null;
        string? methodName = null;

        foreach (var arg in attrs.SelectMany(attr => attr.ConstructorArguments))
        {
            switch (arg.Value)
            {
                case TypeReference tr:
                    targetType = tr;
                    break;
                case string s:
                    methodName = s;
                    break;
            }
        }

        return (targetType, methodName);
    }
}
