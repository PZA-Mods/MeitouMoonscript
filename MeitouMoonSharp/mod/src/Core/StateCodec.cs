// MeitouMoonscript: a script's saved state, exactly. Author: PZA. Licence: GPLv3 or later.
//
// Claim C7: for every value v of the data domain (nil, booleans, numbers, strings, tables of those, shared and cyclic), Decode(Encode(v))
// is v: the same doubles to the bit (-0.0, NaN payloads, infinities), the same UTF-16 code units (lone surrogates kept), the same sharing
// and cycles, and each table's pairs in the same order (MoonSharp iterates in insertion order, and decoding inserts in encoded order).
// A removed key can stay in a MoonSharp table as a dead entry (value nil), invisible to Lua but deciding where the key lands when it is
// set again; whether one is left depends on the table's history (Euler pass 2026-11-22, c7.probe / c7.dbg), so no save could rebuild
// it. Instead the state is made canonical: LiveSize clears dead entries from every table it walks at the end of every call, so between
// calls (where saves happen) there are none, and a saved-and-loaded run and an unsaved one hold the same tables (claim C7.continue).
// Not in the domain, saved as nil and counted in Dropped: functions, coroutines, userdata, metatables. Decode reads untrusted bytes
// (a save file): it throws only FormatException, never allocates more than the input can describe, and nests at most MaxDepth.
using System.Buffers.Binary;
using MoonSharp.Interpreter;

namespace PZA.Moonscript.Core;

public static class StateCodec
{
    public const int MaxDepth = 200;
    const byte TNil = 0, TFalse = 1, TTrue = 2, TNum = 3, TStr = 4, TTab = 5, TRef = 6;

    public sealed class Writer
    {
        readonly MemoryStream _m = new();
        readonly Dictionary<Table, int> _ids = new(ReferenceEqualityComparer.Instance);
        public int Dropped;

        public byte[] ToArray() => _m.ToArray();

        public void U64(ulong x) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, x); _m.Write(b); }

        public void Len(int n)
        {
            uint x = (uint)n;
            while (x >= 0x80) { _m.WriteByte((byte)(x | 0x80)); x >>= 7; }
            _m.WriteByte((byte)x);
        }

        public void Value(DynValue? v, int depth = 0)
        {
            if (v == null) { _m.WriteByte(TNil); return; }
            switch (v.Type)
            {
                case DataType.Nil: case DataType.Void: _m.WriteByte(TNil); return;
                case DataType.Boolean: _m.WriteByte(v.Boolean ? TTrue : TFalse); return;
                case DataType.Number: _m.WriteByte(TNum); U64((ulong)BitConverter.DoubleToInt64Bits(v.Number)); return;
                case DataType.String:
                    _m.WriteByte(TStr); Len(v.String.Length);
                    foreach (char c in v.String) { _m.WriteByte((byte)c); _m.WriteByte((byte)(c >> 8)); }
                    return;
                case DataType.Table:
                    if (_ids.TryGetValue(v.Table, out int id)) { _m.WriteByte(TRef); Len(id); return; }
                    if (depth >= MaxDepth) { Dropped++; _m.WriteByte(TNil); return; }
                    _ids[v.Table] = _ids.Count;
                    if (v.Table.MetaTable != null) Dropped++;
                    var pairs = v.Table.Pairs.Where(p => Storable(p.Key) && !p.Value.IsNil()).ToList();
                    Dropped += v.Table.Pairs.Count(p => !p.Value.IsNil()) - pairs.Count;
                    _m.WriteByte(TTab); Len(pairs.Count);
                    foreach (var p in pairs) { Value(p.Key, depth + 1); Value(p.Value, depth + 1); }
                    return;
                default: Dropped++; _m.WriteByte(TNil); return;
            }
        }

        /// <summary>A key that survives: not a function etc. (its value would become nil and the key a nil key).</summary>
        static bool Storable(DynValue k) => k.Type is DataType.Boolean or DataType.Number or DataType.String or DataType.Table;
    }

    public sealed class Reader(byte[] data, Script script)
    {
        int _p;
        readonly List<Table> _tables = [];

        public bool AtEnd => _p == data.Length;

        byte Byte() => _p < data.Length ? data[_p++] : throw new FormatException("state ends early");

        public ulong U64()
        {
            if (data.Length - _p < 8) throw new FormatException("state ends early");
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_p, 8)); _p += 8; return x;
        }

        public int Len()
        {
            uint x = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = Byte();
                x |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return x <= int.MaxValue ? (int)x : throw new FormatException("length too large");
            }
            throw new FormatException("bad length");
        }

        public DynValue Value(int depth = 0)
        {
            if (depth > MaxDepth) throw new FormatException("state nests too deep");
            switch (Byte())
            {
                case TNil: return DynValue.Nil;
                case TFalse: return DynValue.False;
                case TTrue: return DynValue.True;
                case TNum: return DynValue.NewNumber(BitConverter.Int64BitsToDouble((long)U64()));
                case TStr:
                {
                    int n = Len();
                    if ((long)n * 2 > data.Length - _p) throw new FormatException("string longer than the state");
                    var chars = new char[n];
                    for (int i = 0; i < n; i++) chars[i] = (char)(data[_p + 2 * i] | data[_p + 2 * i + 1] << 8);
                    _p += 2 * n;
                    return DynValue.NewString(new string(chars));
                }
                case TTab:
                {
                    int n = Len();
                    if ((long)n * 2 > data.Length - _p) throw new FormatException("table longer than the state");
                    var t = new Table(script);
                    _tables.Add(t);
                    for (int i = 0; i < n; i++)
                    {
                        var k = Value(depth + 1);
                        var v = Value(depth + 1);
                        if (k.IsNil() || (k.Type == DataType.Number && double.IsNaN(k.Number))) throw new FormatException("nil or NaN key");
                        if (v.IsNil()) throw new FormatException("nil value");
                        t.Set(k, v);
                    }
                    return DynValue.NewTable(t);
                }
                case TRef:
                {
                    int id = Len();
                    return id < _tables.Count ? DynValue.NewTable(_tables[id]) : throw new FormatException("reference to an unknown table");
                }
                default: throw new FormatException("unknown tag");
            }
        }
    }

    /// <summary>FNV-1a 64 over bytes.</summary>
    public static ulong Fnv(ReadOnlySpan<byte> b, ulong h = 14695981039346656037UL)
    {
        foreach (byte x in b) { h ^= x; h *= 1099511628211UL; }
        return h;
    }
}
