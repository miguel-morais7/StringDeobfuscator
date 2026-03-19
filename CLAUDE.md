# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

StringDeobfuscator is a .NET 9.0 console tool that removes `fex#`-style string obfuscation from .NET assemblies. It uses **dnlib** (v4.5.0) for IL inspection/manipulation and **System.Reflection** to invoke the obfuscator's own decryption at runtime.

## Build & Run

```bash
dotnet build
dotnet run -- <path-to-dll> [output-file]
```

Output defaults to `bin/Debug/net9.0/deobfuscated/<dll-name>`.

## Architecture

The entire tool is a single top-level `Program.cs` with 6 sequential steps:

1. **Discover accessor** — Scan for `fex#`-prefixed types containing a `static string(int)` method. When multiple candidates exist, pick the one with the most call sites from non-fex# code.
2. **Collect offsets** — Walk all IL to find `ldc.i4 N; call accessor` patterns and gather unique integer keys.
3. **Resolve strings** — Load the assembly via `Assembly.LoadFrom`, invoke the real accessor via reflection for each key to get plaintext strings.
4. **Patch IL** — Replace each `ldc.i4 + call` pair with `nop + ldstr "plaintext"`.
5. **Remove encrypted resource** — Strip embedded resources that don't have the standard .NET resource magic bytes (`CECEEFBE`).
6. **Save** — Write the patched module via dnlib.

Key helper: `GetLdcI4Value` extracts the integer operand from all `ldc.i4` variants (including shorthand opcodes like `ldc.i4.0` through `ldc.i4.8` and `ldc.i4.m1`).

## Key Design Decisions

- The tool uses **two separate dnlib module loads**: one read-only scan pass to identify the accessor, then a second mutable pass for patching. This avoids state issues.
- `fex#` types are intentionally **not removed** from the output assembly — only the encrypted resource is stripped. Removing types requires full reference analysis and is error-prone.
- String resolution uses **runtime reflection** (loading the target DLL into the tool's process), which means the target DLL's dependencies must be resolvable at runtime.
