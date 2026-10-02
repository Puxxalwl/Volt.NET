namespace Volt;

/// <summary>Result of validating a WASM island module against the CONTRACT.md module contract.</summary>
public sealed class WasmContractResult
{
    public required List<string> Errors { get; init; }
    public required List<string> Exports { get; init; }
    public bool Valid => Errors.Count == 0;
}

/// <summary>
/// M4: parses a WebAssembly binary and checks the island module contract
/// (see CONTRACT.md): exports memory, alloc(i32)→i32, volt_dispatch(i32×8)→i32,
/// volt_render(i32,i32)→i32. Used by `volt wasm validate` and tests.
/// </summary>
public static class WasmContractValidator
{
    private const byte TypeSection = 1;
    private const byte FunctionSection = 3;
    private const byte ExportSection = 7;

    private const byte KindFunc = 0;
    private const byte KindMemory = 2;

    public static WasmContractResult Validate(byte[] wasm)
    {
        var errors = new List<string>();
        var exports = new List<string>();
        var reader = new WasmReader(wasm, errors);

        if (wasm.Length < 8 || wasm[0] != 0x00 || wasm[1] != 0x61 || wasm[2] != 0x73 || wasm[3] != 0x6D)
        {
            errors.Add("not a wasm module (missing \\0asm magic)");
            return new WasmContractResult { Errors = errors, Exports = exports };
        }
        if (wasm[4] != 1 || wasm[5] != 0 || wasm[6] != 0 || wasm[7] != 0)
            errors.Add($"unsupported wasm version {wasm[4]}.{wasm[5]}.{wasm[6]}.{wasm[7]} (expected 1.0.0.0)");

        List<FunctionType>? types = null;
        var functionTypes = new List<int>();
        var exportEntries = new List<WasmExport>();

        reader.Position = 8;
        while (reader.Position < wasm.Length && errors.Count < 16)
        {
            byte sectionId = reader.ReadByte();
            if (sectionId > 12)
            {
                errors.Add($"unknown section id {sectionId}");
                break;
            }
            ulong size = reader.ReadU32L();
            int end = checked(reader.Position + (int)size);
            if (end > wasm.Length)
            {
                errors.Add($"section {sectionId} overruns the module ({size} bytes at offset {reader.Position})");
                break;
            }

            switch (sectionId)
            {
                case TypeSection:
                    types = ReadTypeSection(reader, end, errors);
                    break;
                case FunctionSection:
                    functionTypes = ReadIndexVector(reader, end, "function");
                    break;
                case ExportSection:
                    exportEntries = ReadExportSection(reader, end, exports);
                    break;
                // memory/global/code/data sections are not needed for signature validation
            }
            reader.Position = end;
        }
        if (reader.Position < wasm.Length && errors.Count == 0)
            errors.Add("trailing bytes after the last section");

        if (types is null)
        {
            errors.Add("no type section: module has no function signatures");
            return new WasmContractResult { Errors = errors, Exports = exports };
        }

        // ---- contract checks ----
        bool hasMemory = false;
        string? allocSig = null, dispatchSig = null, renderSig = null;

        foreach (var entry in exportEntries)
        {
            switch (entry.Name)
            {
                case "memory":
                    if (entry.Kind != KindMemory) errors.Add("'memory' export must have kind memory (2), got " + entry.Kind);
                    else hasMemory = true;
                    break;
                case "alloc" or "volt_dispatch" or "volt_render":
                    if (entry.Kind != KindFunc)
                    {
                        errors.Add($"'{entry.Name}' export must be a function (kind 0), got kind {entry.Kind}");
                        break;
                    }
                    var sig = SignatureOf(functionTypes, types, entry.Index, errors, entry.Name);
                    if (entry.Name == "alloc") allocSig = sig;
                    else if (entry.Name == "volt_dispatch") dispatchSig = sig;
                    else renderSig = sig;
                    break;
            }
        }

        if (!hasMemory) errors.Add("no 'memory' export");
        if (allocSig is null) errors.Add("no 'alloc' export");
        else if (allocSig != "i32→i32") errors.Add($"alloc must be (i32)→i32, got ({allocSig})");

        if (dispatchSig is null) errors.Add("no 'volt_dispatch' export");
        else if (dispatchSig != "i32,i32,i32,i32,i32,i32,i32,i32→i32") errors.Add($"volt_dispatch must be (i32×8)→i32, got ({dispatchSig})");

        if (renderSig is null) errors.Add("no 'volt_render' export");
        else if (renderSig != "i32,i32→i32") errors.Add($"volt_render must be (i32,i32)→i32, got ({renderSig})");

        return new WasmContractResult { Errors = errors, Exports = exports };
    }

    private static string? SignatureOf(List<int> functionTypes, List<FunctionType> types, int funcIndex, List<string> errors, string name)
    {
        if (funcIndex >= functionTypes.Count)
        {
            errors.Add($"'{name}' exports function index {funcIndex} but only {functionTypes.Count} functions are declared");
            return null;
        }
        int typeIndex = functionTypes[funcIndex];
        if (typeIndex >= types.Count)
        {
            errors.Add($"'{name}' references type index {typeIndex} out of range ({types.Count} types)");
            return null;
        }
        var type = types[typeIndex];
        return string.Join(",", type.Parameters) + "→" + string.Join(",", type.Results);
    }

    private static List<FunctionType> ReadTypeSection(WasmReader reader, long end, List<string> errors)
    {
        var types = new List<FunctionType>();
        ulong count = reader.ReadU32L();
        for (ulong i = 0; i < count && reader.Position < end; i++)
        {
            byte form = reader.ReadByte();
            if (form != 0x60)
            {
                errors.Add($"type {i}: expected functype 0x60, got 0x{form:X2}");
                return types;
            }
            var parameters = reader.ReadValueTypeVector();
            var results = reader.ReadValueTypeVector();
            types.Add(new FunctionType(parameters, results));
        }
        return types;
    }

    private static List<int> ReadIndexVector(WasmReader reader, long end, string what)
    {
        var indices = new List<int>();
        ulong count = reader.ReadU32L();
        for (ulong i = 0; i < count && reader.Position < end; i++)
            indices.Add((int)reader.ReadU32L());
        return indices;
    }

    private static List<WasmExport> ReadExportSection(WasmReader reader, long end, List<string> exports)
    {
        var entries = new List<WasmExport>();
        ulong count = reader.ReadU32L();
        for (ulong i = 0; i < count && reader.Position < end; i++)
        {
            int nameLen = (int)reader.ReadU32L();
            var name = reader.ReadName(nameLen);
            byte kind = reader.ReadByte();
            int index = (int)reader.ReadU32L();
            entries.Add(new WasmExport(name, kind, index));
            exports.Add(kind switch
            {
                KindFunc => $"func   {name} (#{index})",
                1 => $"table  {name} (#{index})",
                KindMemory => $"memory {name} (#{index})",
                3 => $"global {name} (#{index})",
                _ => $"kind{kind} {name} (#{index})",
            });
        }
        return entries;
    }

    private readonly record struct WasmExport(string Name, byte Kind, int Index);
    private readonly record struct FunctionType(List<string> Parameters, List<string> Results);

    private sealed class WasmReader(byte[] data, List<string> errors)
    {
        public int Position;

        public byte ReadByte()
        {
            if (Position >= data.Length) { errors.Add("unexpected end of module"); return 0; }
            return data[Position++];
        }

        public uint ReadU32L()
        {
            uint value = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = ReadByte();
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
            }
            errors.Add("malformed u32 LEB128");
            return value;
        }

        public List<string> ReadValueTypeVector()
        {
            var types = new List<string>();
            ulong count = ReadU32L();
            for (ulong i = 0; i < count; i++)
                types.Add(ReadByte() switch
                {
                    0x7F => "i32",
                    0x7E => "i64",
                    0x7D => "f32",
                    0x7C => "f64",
                    _ => "unknown",
                });
            return types;
        }

        public string ReadName(int length)
        {
            if (length < 0 || Position + length > data.Length)
            {
                errors.Add("export name overruns the module");
                return "";
            }
            var s = System.Text.Encoding.UTF8.GetString(data, Position, length);
            Position += length;
            return s;
        }
    }
}
