using System.IO;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace StringDeobfuscator;

public record DeobfuscationResult(int PatchCount, int ResourcesRemoved, int FexTypeCount);

public static class Deobfuscator
{
    public static DeobfuscationResult Deobfuscate(string dllPath, string outputPath)
    {
        // Step 1: Discover the string accessor method
        string? accessorTypeName = null;
        string? accessorMethodName = null;
        var fexTypeNames = new HashSet<string>();
        var candidateAccessors = new List<(string typeName, string methodName, uint mdToken)>();

        using (var scanModule = ModuleDefMD.Load(dllPath))
        {
            foreach (var type in scanModule.Types.Where(t => t.Name.String.StartsWith("fex#")))
            {
                fexTypeNames.Add(type.Name.String);
                foreach (var m in type.Methods.Where(m => m.IsStatic && m.HasBody))
                {
                    if (m.ReturnType.FullName == "System.String" &&
                        m.Parameters.Count(p => !p.IsHiddenThisParameter) == 1 &&
                        m.Parameters.Where(p => !p.IsHiddenThisParameter).First().Type.FullName == "System.Int32")
                    {
                        candidateAccessors.Add((type.Name.String, m.Name.String, m.MDToken.Raw));
                    }
                }
            }

            if (candidateAccessors.Count == 0)
                throw new InvalidOperationException("No string accessor method found (no static string(int) on a fex# type).");

            if (candidateAccessors.Count == 1)
            {
                accessorTypeName = candidateAccessors[0].typeName;
                accessorMethodName = candidateAccessors[0].methodName;
            }
            else
            {
                var callCounts = candidateAccessors.ToDictionary(c => c.mdToken, c => 0);
                foreach (var type in scanModule.GetTypes().Where(t => !t.Name.String.StartsWith("fex#")))
                {
                    foreach (var method in type.Methods.Where(m => m.HasBody))
                    {
                        foreach (var instr in method.Body.Instructions)
                        {
                            if (instr.OpCode == OpCodes.Call && instr.Operand is IMethod called &&
                                callCounts.ContainsKey(called.MDToken.Raw))
                            {
                                callCounts[called.MDToken.Raw]++;
                            }
                        }
                    }
                }

                var best = callCounts.OrderByDescending(kv => kv.Value).First();
                var bestCandidate = candidateAccessors.First(c => c.mdToken == best.Key);
                accessorTypeName = bestCandidate.typeName;
                accessorMethodName = bestCandidate.methodName;
            }
        }

        // Step 2: Scan IL to collect all unique offsets
        var patchModule = ModuleDefMD.Load(dllPath);

        MethodDef? accessorMethodDef = null;
        foreach (var type in patchModule.Types.Where(t => t.Name.String == accessorTypeName))
        {
            accessorMethodDef = type.Methods.FirstOrDefault(m =>
                m.Name.String == accessorMethodName && m.IsStatic);
            if (accessorMethodDef != null) break;
        }

        if (accessorMethodDef == null)
            throw new InvalidOperationException("Could not find accessor method in patch module.");

        var allOffsets = new HashSet<int>();
        foreach (var type in patchModule.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var instructions = method.Body.Instructions;
                for (int i = 1; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    if (instr.OpCode != OpCodes.Call || instr.Operand is not IMethod called ||
                        called.MDToken != accessorMethodDef.MDToken)
                        continue;

                    int? offset = GetLdcI4Value(instructions[i - 1]);
                    if (offset != null)
                        allOffsets.Add(offset.Value);
                }
            }
        }

        // Step 3: Resolve strings via reflection
        var asm = Assembly.LoadFrom(dllPath);
        Type[] loadedTypes;
        try
        {
            loadedTypes = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            loadedTypes = ex.Types.Where(t => t != null).ToArray()!;
        }

        var runtimeAccessorType = loadedTypes.FirstOrDefault(t => t?.Name == accessorTypeName)
            ?? throw new InvalidOperationException($"Type '{accessorTypeName}' not found among loaded types.");

        var runtimeAccessorMethod = runtimeAccessorType.GetMethod(accessorMethodName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{accessorMethodName}' not found on type '{accessorTypeName}'.");

        var strings = new Dictionary<int, string>();
        foreach (int offset in allOffsets.OrderBy(o => o))
        {
            try
            {
                string? result = (string?)runtimeAccessorMethod.Invoke(null, new object[] { offset });
                if (result != null)
                    strings[offset] = result;
            }
            catch
            {
                // Skip unresolvable offsets
            }
        }

        // Step 4: Patch IL
        int patchCount = 0;
        foreach (var type in patchModule.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var instructions = method.Body.Instructions;
                for (int i = 1; i < instructions.Count; i++)
                {
                    var instr = instructions[i];
                    if (instr.OpCode != OpCodes.Call || instr.Operand is not IMethod called ||
                        called.MDToken != accessorMethodDef.MDToken)
                        continue;

                    int? offset = GetLdcI4Value(instructions[i - 1]);
                    if (offset == null) continue;

                    if (strings.TryGetValue(offset.Value, out string? str))
                    {
                        instructions[i - 1].OpCode = OpCodes.Nop;
                        instructions[i - 1].Operand = null;
                        instr.OpCode = OpCodes.Ldstr;
                        instr.Operand = str;
                        patchCount++;
                    }
                }
            }
        }

        // Step 5: Remove encrypted resources
        var embeddedResources = patchModule.Resources.OfType<EmbeddedResource>().ToList();
        var resToRemove = new List<EmbeddedResource>();
        foreach (var res in embeddedResources)
        {
            var data = res.CreateReader().ReadRemainingBytes();
            bool isStandardResources = data.Length >= 4 && data[0] == 0xCE && data[1] == 0xCA && data[2] == 0xEF && data[3] == 0xBE;
            if (!isStandardResources)
                resToRemove.Add(res);
        }
        foreach (var res in resToRemove)
            patchModule.Resources.Remove(res);

        // Step 6: Save
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        patchModule.Write(outputPath);

        return new DeobfuscationResult(patchCount, resToRemove.Count, fexTypeNames.Count);
    }

    private static int? GetLdcI4Value(Instruction instr)
    {
        if (instr.OpCode == OpCodes.Ldc_I4) return (int)instr.Operand;
        if (instr.OpCode == OpCodes.Ldc_I4_S) return (sbyte)instr.Operand;
        if (instr.OpCode == OpCodes.Ldc_I4_0) return 0;
        if (instr.OpCode == OpCodes.Ldc_I4_1) return 1;
        if (instr.OpCode == OpCodes.Ldc_I4_2) return 2;
        if (instr.OpCode == OpCodes.Ldc_I4_3) return 3;
        if (instr.OpCode == OpCodes.Ldc_I4_4) return 4;
        if (instr.OpCode == OpCodes.Ldc_I4_5) return 5;
        if (instr.OpCode == OpCodes.Ldc_I4_6) return 6;
        if (instr.OpCode == OpCodes.Ldc_I4_7) return 7;
        if (instr.OpCode == OpCodes.Ldc_I4_8) return 8;
        if (instr.OpCode == OpCodes.Ldc_I4_M1) return -1;
        return null;
    }
}
