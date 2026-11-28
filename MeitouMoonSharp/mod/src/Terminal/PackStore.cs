// MeitouMoonscript: the saved state of Lua packs (and the frozen KenshiLua layer's save bags), held per save. Author: PZA. Licence: GPLv3 or later.
//
// Claim C7b (tools/euler/CLAIMS.md): a pack's state belongs to the save it was made in. Until 0.12.0 it was one file per install
// (pack_states/<id>.bin), so a new game or another save started with the last save's state. Now the store is plain bytes per key, written
// into the save's own Meitou mod data by PackSaveSystem (IModSaveData) and handed back at a load; a new game starts with an empty store.
// Thread rule: the interface side (packs) and the world side (the save system) both touch it, so every member takes the lock.
namespace PZA.Moonscript.Repl;

public sealed class PackStore
{
    public const uint Magic = 0x4B50534D;        // "MSPK"
    public const byte FormatVersion = 1;
    public const int MaxEntries = 512;
    public const int MaxKeyLength = 80;
    public const int MaxEntryBytes = 256 * 1024;

    readonly object _lock = new();
    Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public int Count { get { lock (_lock) return _entries.Count; } }

    public static string Key(string id) => id.ToLowerInvariant();

    public byte[]? Get(string key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var v) ? (byte[])v.Clone() : null;
    }

    /// <summary>Keeps a copy of <paramref name="data"/>; false (nothing changed) for an empty, too long or too large entry, or a full store.</summary>
    public bool Set(string key, byte[] data)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || data.Length == 0 || data.Length > MaxEntryBytes) return false;
        lock (_lock)
        {
            if (!_entries.ContainsKey(key) && _entries.Count >= MaxEntries) return false;
            _entries[key] = (byte[])data.Clone();
            return true;
        }
    }

    public IReadOnlyList<string> Keys() { lock (_lock) return _entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }

    /// <summary>A copy of every entry; the caller may keep it.</summary>
    public Dictionary<string, byte[]> Snapshot()
    {
        lock (_lock) return _entries.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>Replaces every entry (a new world: empty for a new game, the save's own entries for a load).</summary>
    public void Replace(Dictionary<string, byte[]> entries)
    {
        var copy = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (k, v) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (copy.Count >= MaxEntries) break;
            if (!string.IsNullOrEmpty(k) && k.Length <= MaxKeyLength && v.Length > 0 && v.Length <= MaxEntryBytes) copy[k] = (byte[])v.Clone();
        }
        lock (_lock) _entries = copy;
    }

    /// <summary>The save format: magic, version, count, then (key, length, bytes) in key order, so equal stores write equal bytes.</summary>
    public static void Write(Stream stream, Dictionary<string, byte[]> entries)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var keys = entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(keys.Count);
        foreach (var k in keys)
        {
            w.Write(k);
            w.Write(entries[k].Length);
            w.Write(entries[k]);
        }
    }

    /// <summary>Reads what <see cref="Write"/> wrote; any other bytes throw FormatException and nothing is kept (all or nothing).</summary>
    public static Dictionary<string, byte[]> Read(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic) throw new FormatException("not Moonscript pack data");
            byte v = r.ReadByte();
            if (v != FormatVersion) throw new FormatException($"pack data version {v} (this build reads {FormatVersion})");
            int n = r.ReadInt32();
            if (n < 0 || n > MaxEntries) throw new FormatException($"pack data with {n} entries");
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                string k = r.ReadString();
                if (k.Length == 0 || k.Length > MaxKeyLength) throw new FormatException("bad pack key");
                int len = r.ReadInt32();
                if (len <= 0 || len > MaxEntryBytes) throw new FormatException($"pack entry '{k}' of {len} bytes");
                byte[] data = r.ReadBytes(len);
                if (data.Length != len) throw new FormatException("pack data cut short");
                if (!map.TryAdd(k, data)) throw new FormatException($"pack key '{k}' twice");
            }
            if (stream.CanSeek && stream.Position != stream.Length) throw new FormatException("bytes after the pack data");
            return map;
        }
        catch (EndOfStreamException) { throw new FormatException("pack data cut short"); }
        catch (System.Text.DecoderFallbackException) { throw new FormatException("bad pack key text"); }
        catch (IOException e) { throw new FormatException(e.Message); }
    }
}
