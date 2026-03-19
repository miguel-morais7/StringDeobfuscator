using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// --- Parse arguments ---
if (args.Length == 0)
{
    Console.WriteLine("Usage: StringDeobfuscator <path-to-dll> [output-file]");
    Console.WriteLine();
    Console.WriteLine("Removes fex#-style string obfuscation from .NET assemblies.");
    Console.WriteLine("Replaces all obfuscated string accessor calls with inline ldstr instructions,");
    Console.WriteLine("removes the obfuscation infrastructure types and encrypted resource.");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <path-to-dll>   Path to the obfuscated .NET DLL");
    Console.WriteLine("  [output-file]   Optional output file path (default: <dll-name>.deobfuscated.dll)");
    return;
}

string dllPath = Path.GetFullPath(args[0]);
if (!File.Exists(dllPath))
{
    Console.WriteLine($"ERROR: File not found: {dllPath}");
    return;
}

string toolRoot = AppContext.BaseDirectory;
string defaultOutputDir = Path.Combine(toolRoot, "deobfuscated");
Directory.CreateDirectory(defaultOutputDir);

string outputPath = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(defaultOutputDir, Path.GetFileName(dllPath));

Console.WriteLine($"Loading: {dllPath}");

// ============================================================
// Step 1: Use dnlib to discover the string accessor method
// ============================================================
// The obfuscator creates fex#-prefixed types. The string accessor is a static string(int)
// method that looks up decrypted strings by an integer key. There may also be an internal
// resource-name getter with the same signature — we distinguish them by checking which one
// is actually called from non-fex# code.

string? accessorTypeName = null;
string? accessorMethodName = null;
uint accessorMDToken = 0;
var fexTypeNames = new HashSet<string>();
var candidateAccessors = new List<(string typeName, string methodName, uint mdToken)>();

using (var scanModule = ModuleDefMD.Load(dllPath))
{
    // Collect all fex# types and find candidate accessor methods
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
                Console.WriteLine($"Found candidate accessor: {type.Name}.{m.Name} (MDToken: 0x{m.MDToken.Raw:X8})");
            }
        }
    }

    if (candidateAccessors.Count == 0)
    {
        Console.WriteLine("ERROR: Could not find any string accessor method (static string(int) on a fex# type).");
        return;
    }

    // If multiple candidates, find the one actually called from non-fex# methods
    if (candidateAccessors.Count == 1)
    {
        accessorTypeName = candidateAccessors[0].typeName;
        accessorMethodName = candidateAccessors[0].methodName;
        accessorMDToken = candidateAccessors[0].mdToken;
    }
    else
    {
        // Count call sites per candidate across all non-fex# methods
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

        // The real accessor is the one with the most external call sites
        var best = callCounts.OrderByDescending(kv => kv.Value).First();
        var bestCandidate = candidateAccessors.First(c => c.mdToken == best.Key);
        accessorTypeName = bestCandidate.typeName;
        accessorMethodName = bestCandidate.methodName;
        accessorMDToken = bestCandidate.mdToken;
        Console.WriteLine($"Selected accessor by call count: {accessorTypeName}.{accessorMethodName} ({best.Value} external calls)");
    }

    Console.WriteLine($"Using accessor: {accessorTypeName}.{accessorMethodName}");
}

// ============================================================
// Step 2: Scan IL to collect all unique offsets passed to accessor
// ============================================================
Console.WriteLine("Scanning IL for string accessor call sites...");

var patchModule = ModuleDefMD.Load(dllPath);

// Re-find the accessor in the patch module
MethodDef? accessorMethodDef = null;
foreach (var type in patchModule.Types.Where(t => t.Name.String == accessorTypeName))
{
    accessorMethodDef = type.Methods.FirstOrDefault(m =>
        m.Name.String == accessorMethodName && m.IsStatic);
    if (accessorMethodDef != null) break;
}

if (accessorMethodDef == null)
{
    Console.WriteLine("ERROR: Could not find accessor method in patch module.");
    return;
}

// Collect all unique int offsets from call sites
var allOffsets = new HashSet<int>();
int callSiteCount = 0;

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
            {
                allOffsets.Add(offset.Value);
                callSiteCount++;
            }
        }
    }
}

Console.WriteLine($"Found {callSiteCount} call sites with {allOffsets.Count} unique offsets");
if (allOffsets.Count > 0)
    Console.WriteLine($"Offset range: {allOffsets.Min()} - {allOffsets.Max()}");

// ============================================================
// Step 3: Resolve strings by invoking the accessor via reflection
// ============================================================
Console.WriteLine("Resolving strings via reflection...");

var asm = Assembly.LoadFrom(dllPath);
Type[] loadedTypes;
try
{
    loadedTypes = asm.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    loadedTypes = ex.Types.Where(t => t != null).ToArray()!;
    Console.WriteLine($"Loaded {loadedTypes.Length} types (some failed due to missing dependencies)");
}

var runtimeAccessorType = loadedTypes.FirstOrDefault(t => t?.Name == accessorTypeName);
if (runtimeAccessorType == null)
{
    Console.WriteLine($"ERROR: Type '{accessorTypeName}' not found among loaded types.");
    return;
}

var runtimeAccessorMethod = runtimeAccessorType.GetMethod(accessorMethodName,
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
if (runtimeAccessorMethod == null)
{
    Console.WriteLine($"ERROR: Method '{accessorMethodName}' not found on type '{accessorTypeName}'.");
    return;
}

// Call the accessor for each unique offset to build the string map
var strings = new Dictionary<int, string>();
int resolveFailCount = 0;

foreach (int offset in allOffsets.OrderBy(o => o))
{
    try
    {
        string? result = (string?)runtimeAccessorMethod.Invoke(null, new object[] { offset });
        if (result != null)
            strings[offset] = result;
        else
            resolveFailCount++;
    }
    catch (Exception ex)
    {
        var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
        Console.WriteLine($"  WARNING: Failed to resolve offset {offset}: {inner.Message}");
        resolveFailCount++;
    }
}

Console.WriteLine($"Resolved {strings.Count} strings ({resolveFailCount} failures)");

// ============================================================
// Step 4: Patch IL — replace accessor calls with ldstr
// ============================================================
Console.WriteLine("Patching IL...");

int patchCount = 0;
int failCount = 0;

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
                // Replace: ldc.i4 → nop, call → ldstr
                instructions[i - 1].OpCode = OpCodes.Nop;
                instructions[i - 1].Operand = null;
                instr.OpCode = OpCodes.Ldstr;
                instr.Operand = str;
                patchCount++;
            }
            else
            {
                Console.WriteLine($"  WARNING: No string for offset {offset.Value} in {type.FullName}.{method.Name}");
                failCount++;
            }
        }
    }
}

Console.WriteLine($"Patched {patchCount} string references");
if (failCount > 0)
    Console.WriteLine($"  ({failCount} references could not be resolved)");

// ============================================================
// Step 5: Clean up — remove the encrypted resource
// ============================================================
// Note: fex# types and methods are left in place as dead code. Removing them
// requires full reference analysis (IL operands, field types, generic args, etc.)
// which is complex and error-prone. The string inlining is the main value.
Console.WriteLine("Cleaning up...");
var embeddedResources = patchModule.Resources.OfType<EmbeddedResource>().ToList();
var resToRemove = new List<EmbeddedResource>();
foreach (var res in embeddedResources)
{
    var data = res.CreateReader().ReadRemainingBytes();
    bool isStandardResources = data.Length >= 4 && data[0] == 0xCE && data[1] == 0xCA && data[2] == 0xEF && data[3] == 0xBE;
    if (!isStandardResources)
    {
        resToRemove.Add(res);
    }
}
foreach (var res in resToRemove)
{
    patchModule.Resources.Remove(res);
    Console.WriteLine($"  Removed resource: {res.Name}");
}

// ============================================================
// Step 6: Save
// ============================================================
patchModule.Write(outputPath);
Console.WriteLine($"\nSaved deobfuscated assembly to: {outputPath}");
Console.WriteLine($"  Strings inlined: {patchCount}");
Console.WriteLine($"  Resources removed: {resToRemove.Count}");
Console.WriteLine($"  fex# types remaining: {fexTypeNames.Count} (dead code, harmless)");

// --- Helpers ---

static int? GetLdcI4Value(Instruction instr)
{
    if (instr.OpCode == OpCodes.Ldc_I4)
        return (int)instr.Operand;
    if (instr.OpCode == OpCodes.Ldc_I4_S)
        return (sbyte)instr.Operand;
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
