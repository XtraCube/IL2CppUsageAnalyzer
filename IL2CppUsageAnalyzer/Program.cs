using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NameNormalizer;

namespace IL2CppUsageAnalyzer;

public static class Program
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
    [SuppressMessage("ReSharper", "CollectionNeverQueried.Global")]
    public class AnalyzedMethodInfo
    {
        public string ReturnType { get; set; } = string.Empty;
        public int MonoCount { get; set; }
        public int XrefCount { get; set; }
        public int ThisCount { get; set; }
        public string Type { get; set; } = "Method";

        public HashSet<string> Tags { get; set; } = [];
        public List<string> MonoUsages { get; set; } = [];
        public List<string> XrefUsages { get; set; } = [];
    }

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
    public class XrefInfo
    {
        public int CallCount { get; set; }
        public string[] Usages { get; set; } = [];
    }

    public static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: IL2CppUsageAnalyzer <path to mono assembly csharp> <path to xref dump>");
            return;
        }
        var monoAssemblyPath = args[0];
        var xrefDump = args[1];

        if (!File.Exists(monoAssemblyPath) || !File.Exists(xrefDump))
        {
            Console.WriteLine("Invalid file paths provided.");
            return;
        }

        var methodInfos = AnalyzeMethods(monoAssemblyPath);
        var xrefMethodRefs = ParseXrefs(xrefDump);
        var xrefGenericCollapsed = xrefMethodRefs
            .Where(kvp => kvp.Key.Contains('<') && kvp.Key.Contains('>'))
            .GroupBy(kvp => Normalizer.CollapseGenerics(kvp.Key))
            .ToDictionary(
                g => g.Key,
                g => new XrefInfo {
                    CallCount = g.Sum(kvp => kvp.Value.CallCount),
                    Usages = g.SelectMany(kvp => kvp.Value.Usages).Distinct().ToArray()
                }
            );

        var strippedCount = 0;
        var inlinedCount = 0;
        var usedByInlineCount = 0;

        foreach (var (key, methodInfo) in methodInfos)
        {
            methodInfo.MonoUsages = methodInfo.MonoUsages.Select(Normalizer.NormalizeXrefName).ToList();

            // check if the method is stripped
            var xrefName = Normalizer.NormalizeXrefName(key);
            if (!xrefMethodRefs.TryGetValue(xrefName, out var xrefInfo) && !xrefGenericCollapsed.TryGetValue(xrefName, out xrefInfo))
            {
                // method is stripped
                methodInfo.Tags.Add("stripped");
                strippedCount++;
                continue;
            }

            // compare call counts to catch inlined methods
            methodInfo.XrefCount = xrefInfo.CallCount;
            methodInfo.XrefUsages.AddRange(xrefInfo.Usages);
            if (methodInfo.XrefCount < methodInfo.MonoCount)
            {
                methodInfo.Tags.Add("inlined");
                inlinedCount++;
            }
            else if (methodInfo.XrefCount > methodInfo.MonoCount)
            {
                methodInfo.Tags.Add("used-by-inline");
                usedByInlineCount++;
            }
            else
            {
                // compare mono and xref usages to catch more inlined methods
                var monoUsagesSet = new HashSet<string>(methodInfo.MonoUsages);
                var xrefUsagesSet = new HashSet<string>(methodInfo.XrefUsages);
                var missingInXref = monoUsagesSet.Except(xrefUsagesSet).ToList();
                var missingInMono = xrefUsagesSet.Except(monoUsagesSet).ToList();

                if (missingInXref.Count > 0 || missingInMono.Count > 0)
                {
                    methodInfo.Tags.Add("inlined");
                    inlinedCount++;
                }
                else
                {
                    methodInfo.Tags.Add("matched");
                }
            }
        }

        var normalized = methodInfos.ToDictionary(k => Normalizer.NormalizeXrefName(k.Key), e => e.Value);

        Console.WriteLine($"Total methods analyzed: {methodInfos.Count}");
        Console.WriteLine($"Methods stripped: {strippedCount}");
        Console.WriteLine($"Methods inlined: {inlinedCount}");
        Console.WriteLine($"Methods used by inline: {usedByInlineCount}");
        Console.WriteLine();
        const string resultsFile = "method_analysis_results.json";
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(resultsFile, json);
        Console.WriteLine($"Analysis results saved to {resultsFile}");
    }

    private static Dictionary<string, XrefInfo> ParseXrefs(string xrefDumpPath)
    {
        var text = File.ReadAllText(xrefDumpPath);
        var xrefs = JsonSerializer.Deserialize<Dictionary<string, XrefInfo>>(text);
        if (xrefs == null)
        {
            throw new InvalidOperationException("Failed to parse xref dump.");
        }

        return xrefs;
    }

    private static Dictionary<string, AnalyzedMethodInfo> AnalyzeMethods(string assemblyPath)
    {
        var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var methodRefs = new Dictionary<string, AnalyzedMethodInfo>();
        var stateMachineToMethod = new Dictionary<string, string>();

        // First pass: index all methods
        foreach (var module in assembly.Modules)
        {
            foreach (var type in MonoUtils.GetAllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    var methodName = Normalizer.NormalizeMonoMethod(method);

                    if (method.CustomAttributes.Any(attr => attr.AttributeType.Name.Contains("IteratorStateMachineAttribute")))
                    {
                        var attr = method.CustomAttributes.First(a => a.AttributeType.Name.Contains("IteratorStateMachineAttribute"));
                        if (attr.ConstructorArguments.Count == 1)
                        {
                            if (attr.ConstructorArguments[0].Value is TypeReference stateMachineType)
                            {
                                stateMachineToMethod[stateMachineType.FullName] = methodName;
                            }
                        }
                    }

                    if (method.IsConstructor)
                    {
                        // Skip constructors
                        continue;
                    }

                    // look for attributes that indicate compiler-generated methods
                    var compilerGeneratedAttr = method.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name.Contains("CompilerGeneratedAttribute"));

                    // look for attributes that indicate compiler-generated type
                    var compilerGeneratedTypeAttr = type.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name.Contains("CompilerGeneratedAttribute"));

                    // Check if the method is generic
                    var isGeneric = method.HasGenericParameters;
                    var isProperty = method.IsSpecialName && (method.Name.StartsWith("get_") || method.Name.StartsWith("set_"));
                    var isCompilerGenerated = compilerGeneratedAttr != null || compilerGeneratedTypeAttr != null;

                    var methodInfo = new AnalyzedMethodInfo
                    {
                        ReturnType = Normalizer.NormalizeMonoName(method.ReturnType.FullName),
                        Type = isProperty ? "Property" : "Method",
                    };

                    if (isGeneric)
                    {
                        methodInfo.Tags.Add("generic");
                    }

                    if (isCompilerGenerated)
                    {
                        methodInfo.Tags.Add("compiler-generated");
                    }

                    methodRefs.TryAdd(methodName, methodInfo);
                }
            }
        }

        // Second pass: find usages
        foreach (var module in assembly.Modules) {
            foreach (var type in MonoUtils.GetAllTypes(module)) {
                foreach (var method in type.Methods) {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    var methodName = Normalizer.NormalizeMonoMethod(method);
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode == OpCodes.Ldarg_0)
                        {
                            if (!methodRefs.TryGetValue(methodName, out var methodInfo))
                            {
                                continue;
                            }
                            methodInfo.ThisCount += 1;
                        }

                        if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                        {
                            if (instr.Operand is MethodReference called)
                            {
                                var calledName = Normalizer.NormalizeMonoMethod(called);
                                if (!methodRefs.TryGetValue(calledName, out var calledMethodInfo))
                                {
                                    continue;
                                }
                                calledMethodInfo.MonoCount += 1;
                                calledMethodInfo.MonoUsages.Add(Normalizer.NormalizeMonoMethod(method));
                            }
                        }

                        // Detect instantiations of state machine classes
                        if (instr.OpCode == OpCodes.Newobj && instr.Operand is MethodReference constructor)
                        {
                            var typeName = constructor.DeclaringType.FullName;
                            if (stateMachineToMethod.TryGetValue(typeName, out var coroutineMethod))
                            {
                                if (!methodRefs.TryGetValue(coroutineMethod, out var coroutineMethodInfo))
                                {
                                    continue;
                                }

                                if (coroutineMethod == methodName)
                                {
                                    continue;
                                }

                                coroutineMethodInfo.MonoCount += 1;
                                coroutineMethodInfo.MonoUsages.Add(methodName);
                            }
                        }
                    }
                }
            }
        }
        return methodRefs;
    }
}