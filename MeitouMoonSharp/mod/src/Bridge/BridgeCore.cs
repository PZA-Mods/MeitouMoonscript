// MeitouMoonscript: the org bridge's game-free core. Author: PZA. Licence: GPLv3 or later.
//
// Read-only views of three org mods' public APIs for Lua packs: Dustlife (DustlifeApi), PZA's Composer (ComposerApi) and The Wasteland
// Remembers (WastelandApi). Each API is a public static class with plain types, found by name; no mod depends on another. The claims O1-O5
// (tools/euler/CLAIMS.md "The org bridge") are written exactly there; this file is what they are about:
//   O1 ApiReader binds only the read list's members (ReadList).
//   O2 Plain.Of turns any CLR object into a capped plain ScriptValue; ScriptValue.ToLua builds new tables on every call.
//   O3 Feed is the news cursor of the definition.
//   O4 BridgePoller.Poll is the only caller of an API; BridgeFunctions read only the published snapshot.
//   O5 absent or throwing APIs answer empty and quietly.
// No Meitou types here: the Euler check and the fuzz harness compile this file alone.
using System.Collections;
using System.Reflection;
using PZA.Moonscript.Core;

namespace PZA.Moonscript.Bridge;

/// <summary>O2: the plain value P(o, d) of the definition.</summary>
public static class Plain
{
    public const int MaxString = 256, MaxDepth = 3, MaxFields = 24, MaxKey = 32;

    public static ScriptValue Of(object? o, int depth = 0)
    {
        switch (o)
        {
            case null: return ScriptValue.Nil;
            case bool b: return ScriptValue.Of(b);
            case string s: return ScriptValue.Of(Cut(s));
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
            {
                double d = Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);
                return double.IsFinite(d) ? ScriptValue.Of(d) : ScriptValue.Nil;
            }
            case decimal m:
            {
                double d = (double)m;
                return double.IsFinite(d) ? ScriptValue.Of(d) : ScriptValue.Nil;
            }
        }
        if (depth >= MaxDepth) return ScriptValue.Nil;
        if (o is IReadOnlyDictionary<string, object> dict)
        {
            var pairs = new List<KeyValuePair<ScriptValue, ScriptValue>>();
            foreach (var k in SortedKeys(dict))
            {
                if (pairs.Count >= MaxFields) break;
                if (k.Length is < 1 or > MaxKey) continue;
                object? v;
                try { v = dict[k]; } catch (Exception e) when (e is not OutOfMemoryException) { continue; }
                var pv = Of(v, depth + 1);
                if (!pv.IsNil) pairs.Add(new(ScriptValue.Of(k), pv));
            }
            return ScriptValue.Of(pairs);
        }
        if (o is IEnumerable seq)
        {
            var items = new List<ScriptValue>();
            var it = seq.GetEnumerator();
            int looked = 0;
            while (items.Count < MaxFields && looked < 4 * MaxFields + 64 && it.MoveNext())
            {
                looked++;
                var pv = Of(it.Current, depth + 1);
                if (!pv.IsNil) items.Add(pv);
            }
            return ScriptValue.List([.. items]);
        }
        return ScriptValue.Nil;
    }

    static List<string> SortedKeys(IReadOnlyDictionary<string, object> dict)
    {
        var keys = new List<string>();
        foreach (var k in dict.Keys) if (k is not null) keys.Add(k);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    public static string Cut(string s)
    {
        if (s.Length <= MaxString) return s;
        int n = MaxString;
        if (char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }
}

/// <summary>O3: one news source's cursor and its last N rows.</summary>
public sealed class Feed
{
    public const int Keep = 300, MaxRead = 64, DefaultRead = 16;

    readonly List<(long Seq, ScriptValue Row)> _rows = [];
    long _generation = long.MinValue;

    public long Cursor { get; private set; }
    public long Generation => _generation;
    public int Count => _rows.Count;

    /// <summary>Take a fetched batch. rows: the API's rows (each with a "Seq"); generation: the API's world generation.</summary>
    public void Poll(long generation, IEnumerable<IReadOnlyDictionary<string, object>>? rows)
    {
        if (generation != _generation) { _rows.Clear(); Cursor = 0; _generation = generation; }
        if (rows is null) return;
        var firstOf = new Dictionary<long, IReadOnlyDictionary<string, object>>();   // the first row of each seq, in the batch's order
        foreach (var r in rows)
        {
            if (r is null || !r.TryGetValue("Seq", out var so) || !SeqOf(so, out long seq) || seq <= Cursor) continue;
            firstOf.TryAdd(seq, r);
        }
        if (firstOf.Count == 0) return;
        var seqs = firstOf.Keys.ToList();
        seqs.Sort();
        foreach (long seq in seqs) _rows.Add((seq, Plain.Of(firstOf[seq])));
        if (_rows.Count > Keep) _rows.RemoveRange(0, _rows.Count - Keep);
        Cursor = seqs[^1];
    }

    /// <summary>A Seq value that is an integer number (any CLR integer type, or a double with no fraction in the long range).</summary>
    public static bool SeqOf(object? o, out long seq)
    {
        seq = 0;
        switch (o)
        {
            case long l: seq = l; return true;
            case int i: seq = i; return true;
            case short s: seq = s; return true;
            case uint u: seq = u; return true;
            case ulong ul when ul <= long.MaxValue: seq = (long)ul; return true;
            case double d when double.IsFinite(d) && Math.Floor(d) == d && d >= long.MinValue && d < 9.2e18: seq = (long)d; return true;
            default: return false;
        }
    }

    /// <summary>An immutable copy for a snapshot.</summary>
    public FeedView View() => new([.. _rows.Select(r => r.Seq)], [.. _rows.Select(r => r.Row)]);
}

/// <summary>A feed's rows frozen in a snapshot.</summary>
public sealed class FeedView(long[] seqs, ScriptValue[] rows)
{
    public static readonly FeedView Empty = new([], []);

    public int Count => seqs.Length;

    /// <summary>After(s, n) of the definition.</summary>
    public ScriptValue After(double since, double limit)
    {
        int n = double.IsFinite(limit) ? (int)Math.Clamp(Math.Floor(limit), 1, Feed.MaxRead) : Feed.DefaultRead;
        double s = double.IsFinite(since) ? since : 0;
        var list = new List<ScriptValue>(Math.Min(n, seqs.Length));
        for (int i = 0; i < seqs.Length && list.Count < n; i++)
            if (seqs[i] > s) list.Add(rows[i]);
        return ScriptValue.List([.. list]);
    }
}

/// <summary>O1 / O5: one bridged API, bound by reflection to its read list only.</summary>
public sealed class ApiReader
{
    public const int RetryEvery = 600;

    /// <summary>The read list of the definition: member name and parameter types (null: a property).</summary>
    public static readonly IReadOnlyDictionary<string, (string Member, Type[]? Params)[]> ReadList = new Dictionary<string, (string, Type[]?)[]>
    {
        ["dustlife"] = [("Generation", null), ("Active", null), ("News", [typeof(long)]), ("Places", [])],
        ["composer"] = [("Generation", null), ("Active", null), ("Drama", null), ("Tension", [typeof(double)]), ("Phase", [typeof(double)]), ("Shown", [typeof(long)])],
        ["twr"] = [("Ready", null), ("HeatTowns", []), ("Rivals", []), ("Hunts", [])],
    };

    public static readonly IReadOnlyDictionary<string, (string Type, string Assembly, int MinVersion)> Targets = new Dictionary<string, (string, string, int)>
    {
        ["dustlife"] = ("Dustlife.DustlifeApi", "Dustlife", 2),
        ["composer"] = ("PZA.Composer.Meitou.ComposerApi", "PZAComposer.Meitou", 1),
        ["twr"] = ("LivingWasteland.MeitouMod.WastelandApi", "LivingWastelandMeitou", 1),
    };

    readonly string _key;
    readonly Func<string, string, Type?> _find;
    readonly Action<string> _log;
    readonly Dictionary<string, MemberInfo> _bound = new(StringComparer.Ordinal);
    long _nextLook;

    /// <summary>Every member name this reader ever bound (the O1 check reads it).</summary>
    public HashSet<string> Resolved { get; } = new(StringComparer.Ordinal);

    public bool Available { get; private set; }
    public int Version { get; private set; }

    public ApiReader(string key, Func<string, string, Type?> find, Action<string> log) { _key = key; _find = find; _log = log; }

    /// <summary>Bind now if absent and due (at most once every RetryEvery polls).</summary>
    public void Look(long poll)
    {
        if (Available || poll < _nextLook) return;
        _nextLook = poll + RetryEvery;
        var (typeName, asm, min) = Targets[_key];
        Type? t;
        try { t = _find(typeName, asm); } catch (Exception e) when (e is not OutOfMemoryException) { t = null; }
        if (t is null) return;
        int version;
        try
        {
            var f = t.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            if (f?.GetValue(null) is not int v) return;
            Resolved.Add("Version");
            version = v;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return; }
        if (version < min)
        {
            _log($"bridge: {typeName} version {version} is below {min}; {_key} reads stay empty");
            return;
        }
        _bound.Clear();
        foreach (var (member, ps) in ReadList[_key])
        {
            MemberInfo? m = ps is null
                ? t.GetProperty(member, BindingFlags.Public | BindingFlags.Static)
                : t.GetMethod(member, BindingFlags.Public | BindingFlags.Static, null, ps, null);
            if (m is null) continue;
            _bound[member] = m;
            Resolved.Add(member);
        }
        Version = version;
        Available = true;
        _log($"bridge: {_key} found ({typeName} v{version}, {_bound.Count} read member(s))");
    }

    /// <summary>A read-list property or method's value; null when absent, unbound or throwing (O5).</summary>
    public object? Read(string member, params object[] args)
    {
        if (!Available || !_bound.TryGetValue(member, out var m)) return null;
        try
        {
            return m switch
            {
                PropertyInfo p => p.GetValue(null),
                MethodInfo mi => mi.Invoke(null, args),
                _ => null,
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Find a type by name: assembly-qualified first, then among the loaded assemblies (mods load in their own contexts).</summary>
    public static Type? FindLoaded(string typeName, string assembly)
    {
        var t = Type.GetType(typeName + ", " + assembly, throwOnError: false);
        if (t is not null) return t;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(a.GetName().Name, assembly, StringComparison.Ordinal)) continue;
            t = a.GetType(typeName, throwOnError: false);
            if (t is not null) return t;
        }
        return null;
    }
}

/// <summary>What a pack may read: built in the serial step, never changed after (O4).</summary>
public sealed record BridgeSnapshot(
    bool Dustlife, bool DustlifeActive, long DustlifeGeneration, FeedView News, ScriptValue Places, IReadOnlyDictionary<string, string> Holders,
    bool Composer, bool ComposerActive, string Drama, string Phase, double Tension, FeedView Shown,
    bool Twr, bool TwrReady, ScriptValue HeatTowns, IReadOnlyDictionary<string, (double Heat, string Tier)> Heat, ScriptValue Rivals, ScriptValue Hunts)
{
    public static readonly BridgeSnapshot Empty = new(
        false, false, 0, FeedView.Empty, ScriptValue.List(), new Dictionary<string, string>(),
        false, false, "", "", 0, FeedView.Empty,
        false, false, ScriptValue.List(), new Dictionary<string, (double, string)>(), ScriptValue.List(), ScriptValue.List());
}

/// <summary>O4: the only caller of the bridged APIs. Call Poll from a serial step; read Current anywhere.</summary>
public sealed class BridgePoller
{
    /// <summary>News and beats every NewsEvery polls; places, heat, rivals and hunts every SlowEvery polls.</summary>
    public const int NewsEvery = 10, SlowEvery = 120;

    public readonly ApiReader Dustlife, Composer, Twr;
    readonly Feed _news = new(), _shown = new();
    volatile BridgeSnapshot _current = BridgeSnapshot.Empty;
    long _polls;
    ScriptValue _places = ScriptValue.List(), _heatTowns = ScriptValue.List(), _rivals = ScriptValue.List(), _hunts = ScriptValue.List();
    IReadOnlyDictionary<string, string> _holders = new Dictionary<string, string>();
    IReadOnlyDictionary<string, (double, string)> _heat = new Dictionary<string, (double, string)>();

    public BridgePoller(Func<string, string, Type?> find, Action<string> log)
    {
        Dustlife = new("dustlife", find, log);
        Composer = new("composer", find, log);
        Twr = new("twr", find, log);
    }

    public BridgeSnapshot Current => _current;
    public long Polls => _polls;

    public void Poll(double hours)
    {
        long n = _polls++;
        Dustlife.Look(n); Composer.Look(n); Twr.Look(n);
        bool news = n % NewsEvery == 0, slow = n % SlowEvery == 0;
        double now = double.IsFinite(hours) ? hours : 0;

        bool dActive = Dustlife.Read("Active") is true;
        long dGen = Dustlife.Read("Generation") is long g ? g : 0;
        if (news) _news.Poll(dGen, Rows(Dustlife.Read("News", _news.Generation == dGen ? _news.Cursor : 0L)));
        if (slow)
        {
            var places = Rows(Dustlife.Read("Places"));
            _places = Plain.Of(places);
            _holders = HolderMap(places);
        }

        bool cActive = Composer.Read("Active") is true;
        long cGen = Composer.Read("Generation") is long cg ? cg : 0;
        string drama = Composer.Read("Drama") as string ?? "";
        string phase = Composer.Read("Phase", now) as string ?? "";
        double tension = Composer.Read("Tension", now) is double t && double.IsFinite(t) ? t : 0;
        if (news) _shown.Poll(cGen, Rows(Composer.Read("Shown", _shown.Generation == cGen ? _shown.Cursor : 0L)));

        bool ready = Twr.Read("Ready") is true;
        if (slow)
        {
            var towns = Rows(Twr.Read("HeatTowns"));
            _heatTowns = Plain.Of(towns);
            _heat = HeatMap(towns);
            _rivals = Plain.Of(Twr.Read("Rivals"));
            _hunts = Plain.Of(Twr.Read("Hunts"));
        }

        _current = new BridgeSnapshot(
            Dustlife.Available, dActive, dGen, _news.View(), _places, _holders,
            Composer.Available, cActive, Plain.Cut(drama), Plain.Cut(phase), tension, _shown.View(),
            Twr.Available, ready, _heatTowns, _heat, _rivals, _hunts);
    }

    /// <summary>The API's rows as plain dictionaries; anything else is left out.</summary>
    static List<IReadOnlyDictionary<string, object>> Rows(object? o)
    {
        var list = new List<IReadOnlyDictionary<string, object>>();
        if (o is not IEnumerable seq || o is string) return list;
        try
        {
            foreach (var x in seq)
            {
                if (x is IReadOnlyDictionary<string, object> d) list.Add(d);
                if (list.Count >= Feed.Keep * 2) break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { list.Clear(); }
        return list;
    }

    static Dictionary<string, string> HolderMap(List<IReadOnlyDictionary<string, object>> places)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in places)
        {
            string holder = p.TryGetValue("Holder", out var h) && h is string hs ? Plain.Cut(hs) : "";
            foreach (var key in new[] { "Node", "Town", "Name" })
                if (p.TryGetValue(key, out var k) && k is string ks && ks.Length > 0) map.TryAdd(Plain.Cut(ks), holder);
        }
        return map;
    }

    static Dictionary<string, (double, string)> HeatMap(List<IReadOnlyDictionary<string, object>> towns)
    {
        var map = new Dictionary<string, (double, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in towns)
        {
            if (!t.TryGetValue("Town", out var k) || k is not string town || town.Length == 0) continue;
            var h = t.TryGetValue("Heat", out var ho) ? Plain.Of(ho) : ScriptValue.Nil;
            string tier = t.TryGetValue("Tier", out var tr) && tr is string ts ? Plain.Cut(ts) : "";
            map.TryAdd(Plain.Cut(town), (h.Kind == ScriptKind.Number ? h.Number : 0, tier));
        }
        return map;
    }
}

/// <summary>O2 / O4: the Lua functions of the three capabilities. They read only the snapshot they are given.</summary>
public static class BridgeFunctions
{
    public const string DustlifeCap = "dustlife.read", ComposerCap = "composer.read", TwrCap = "twr.read";

    static double Num(IReadOnlyList<ScriptValue> a, int i, double fallback) =>
        i < a.Count && a[i].Kind == ScriptKind.Number ? a[i].Number : fallback;

    static string Str(IReadOnlyList<ScriptValue> a, int i) =>
        i < a.Count && a[i].Kind == ScriptKind.String ? a[i].String! : "";

    public static IReadOnlyList<HostFunction> Dustlife(Func<BridgeSnapshot> snap) =>
    [
        new("dustlife", "available", _ => ScriptValue.Of(snap().Dustlife)),
        new("dustlife", "active", _ => ScriptValue.Of(snap().DustlifeActive)),
        new("dustlife", "generation", _ => ScriptValue.Of(snap().DustlifeGeneration)),
        new("dustlife", "news", a => snap().News.After(Num(a, 0, 0), Num(a, 1, double.NaN))),
        new("dustlife", "places", _ => snap().Places),
        new("dustlife", "holder", a => snap().Holders.TryGetValue(Str(a, 0), out var h) ? ScriptValue.Of(h) : ScriptValue.Of("")),
    ];

    public static IReadOnlyList<HostFunction> Composer(Func<BridgeSnapshot> snap) =>
    [
        new("composer", "available", _ => ScriptValue.Of(snap().Composer)),
        new("composer", "active", _ => ScriptValue.Of(snap().ComposerActive)),
        new("composer", "drama", _ => ScriptValue.Of(snap().Drama)),
        new("composer", "phase", _ => ScriptValue.Of(snap().Phase)),
        new("composer", "tension", _ => ScriptValue.Of(snap().Tension)),
        new("composer", "shown", a => snap().Shown.After(Num(a, 0, 0), Num(a, 1, double.NaN))),
    ];

    public static IReadOnlyList<HostFunction> Twr(Func<BridgeSnapshot> snap) =>
    [
        new("twr", "available", _ => ScriptValue.Of(snap().Twr)),
        new("twr", "ready", _ => ScriptValue.Of(snap().TwrReady)),
        new("twr", "heat", a => ScriptValue.Of(snap().Heat.TryGetValue(Str(a, 0), out var h) ? h.Heat : 0)),
        new("twr", "tier", a => ScriptValue.Of(snap().Heat.TryGetValue(Str(a, 0), out var h) ? h.Tier : "quiet")),
        new("twr", "towns", _ => snap().HeatTowns),
        new("twr", "rivals", _ => snap().Rivals),
        new("twr", "hunts", _ => snap().Hunts),
    ];
}
