# String Deobfuscator

A WPF desktop tool that removes `fex#`-style string obfuscation from .NET assemblies. It uses [dnlib](https://github.com/0xd4d/dnlib) for IL inspection/manipulation and `System.Reflection` to invoke the obfuscator's own decryption at runtime.

## Features

- Drag-and-drop or browse for DLL files
- Batch processing with per-file status indicators
- Automatic detection of the string accessor method
- Replaces obfuscated `ldc.i4 + call` patterns with inline `ldstr` instructions
- Strips encrypted embedded resources

## Requirements

- .NET 9.0 SDK (Windows)

## Build & Run

```bash
dotnet build
dotnet run
```

## How It Works

1. **Discover accessor** — Scans for `fex#`-prefixed types containing a `static string(int)` method. When multiple candidates exist, picks the one with the most call sites from non-fex# code.
2. **Collect offsets** — Walks all IL to find `ldc.i4 N; call accessor` patterns and gathers unique integer keys.
3. **Resolve strings** — Loads the assembly via `Assembly.LoadFrom`, invokes the real accessor via reflection for each key to get plaintext strings.
4. **Patch IL** — Replaces each `ldc.i4 + call` pair with `nop + ldstr "plaintext"`.
5. **Remove encrypted resource** — Strips embedded resources that don't have the standard .NET resource magic bytes (`CECEEFBE`).
6. **Save** — Writes the patched module to the `deobfuscated/` output directory.

## Notes

- The `fex#` types are intentionally left in the output assembly as dead code — removing them requires full reference analysis and is error-prone.
- The target DLL's dependencies must be resolvable at runtime since string resolution uses reflection.
