using System.Buffers.Binary;
using System.Text;

namespace PcDiag.Collectors.Stability;

/// <summary>
/// Reads what a Windows kernel crash dump (a 64-bit "PAGEDU64" dump with a triage section) says without a debugger: the bug
/// check, its parameters, and the drivers loaded, so the address a bug check names can be put in a module. Checked against
/// WinDbg on the dev PC's dumps (Oct 2026): the 0x1E and 0x7E dumps both pointed into ntoskrnl.exe, as WinDbg's "nt" did.
/// Anything that does not look like such a dump gives null.
/// </summary>
public static class Minidump
{
    /// <summary>The header and the triage section are well within this.</summary>
    private const int MostRead = 4 * 1024 * 1024;

    private const int TriageOffset = 0x2000;

    /// <summary>What a dump says.</summary>
    public sealed record Facts(uint BugCheck, IReadOnlyList<ulong> Parameters, IReadOnlyList<LoadedModule> Modules)
    {
        /// <summary>The module an address is in, or null.</summary>
        public string? ModuleAt(ulong address) => Modules.FirstOrDefault(m => address >= m.Base && address < m.Base + m.Size)?.Name;
    }

    /// <summary>A loaded driver or kernel image.</summary>
    public sealed record LoadedModule(ulong Base, uint Size, string Name);

    /// <summary>Reads a dump file; null when it cannot be read or is not a 64-bit kernel dump.</summary>
    public static Facts? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(file.Length, MostRead)];
            file.ReadExactly(buffer);
            return Parse(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a dump's first bytes; null when they are not a 64-bit kernel dump. Exposed for tests.</summary>
    public static Facts? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < TriageOffset + 0x50 || !b[..8].SequenceEqual("PAGEDU64"u8))
        {
            return null;
        }

        var code = U32(b, 0x38);
        var parameters = new ulong[4];
        for (var i = 0; i < 4; i++)
        {
            parameters[i] = U64(b, 0x40 + (8 * i));
        }

        var modules = new List<LoadedModule>();
        var driversAt = (int)U32(b, TriageOffset + 0x30);
        var count = (int)U32(b, TriageOffset + 0x34);
        if (driversAt > 0 && count is > 0 and < 4096 && EntrySize(b, driversAt) is { } entry)
        {
            for (var i = 0; i < count; i++)
            {
                var at = driversAt + (i * entry);
                if (at + 8 + 0x48 > b.Length)
                {
                    break;
                }

                var nameAt = (int)U32(b, at);
                var start = U64(b, at + 8 + 0x30);
                var size = U32(b, at + 8 + 0x40);
                if (Kernel(start) && Name(b, nameAt) is { } name)
                {
                    modules.Add(new LoadedModule(start, size, name));
                }
            }
        }

        return new Facts(code, parameters, modules);
    }

    /// <summary>The size of a driver list entry (the name offset and a KLDR_DATA_TABLE_ENTRY, which grew over Windows' versions).</summary>
    private static int? EntrySize(ReadOnlySpan<byte> b, int driversAt)
    {
        foreach (var candidate in (int[])[0xA8, 0xA0, 0xB0, 0x98, 0xC8, 0xD0])
        {
            if (driversAt + candidate + 8 + 0x38 <= b.Length && Kernel(U64(b, driversAt + 8 + 0x30)) && Kernel(U64(b, driversAt + candidate + 8 + 0x30)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? Name(ReadOnlySpan<byte> b, int at)
    {
        if (at <= 0 || at + 4 > b.Length)
        {
            return null;
        }

        var characters = (int)U32(b, at);
        return characters is > 0 and < 512 && at + 4 + (characters * 2) <= b.Length ? Encoding.Unicode.GetString(b.Slice(at + 4, characters * 2)) : null;
    }

    private static bool Kernel(ulong address) => address >> 48 == 0xFFFF;

    private static uint U32(ReadOnlySpan<byte> b, int at) => at >= 0 && at + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b[at..]) : 0;

    private static ulong U64(ReadOnlySpan<byte> b, int at) => at >= 0 && at + 8 <= b.Length ? BinaryPrimitives.ReadUInt64LittleEndian(b[at..]) : 0;
}
