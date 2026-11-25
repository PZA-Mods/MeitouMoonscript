// MeitouMoonscript (Author: PZA, Licence: GPLv3 or later): game-free snapshots and queued world actions for Lua packs.
// Read-only snapshots flow from SlowWorld -> PackHost; queued WorldActions flow from PackHost -> SlowWorld -> World.Commands.
namespace PZA.Moonscript.Repl;

public sealed record CharSnap(int Slot, int Gen, string Name, int Faction, bool IsPlayer,
                              double X, double Y, double Z, double Hp, bool Down, bool Dead, bool InCombat, int SquadId);

public sealed record DoorSnap(string Id, string Building, int Owner, bool IsPlayer,
                              double X, double Y, double Z, double OpenAmount, string Motion, bool Locked, bool Broken, bool IsGate);

public sealed record BuildingSnap(string Id, string Name, string Record, int Owner, bool IsPlayer,
                                  double X, double Y, double Z, bool Complete, double Progress);

public sealed record WorldScanSnapshot(IReadOnlyList<CharSnap> Characters, IReadOnlyList<DoorSnap> Doors, IReadOnlyList<BuildingSnap> Buildings)
{
    public static readonly WorldScanSnapshot Empty = new([], [], []);
}

public sealed record WorldAction(
    string Kind,
    string? TargetId = null,
    IReadOnlyList<(int Slot, int Gen)>? CharIds = null,
    int TargetSlot = 0,
    int TargetGen = 0,
    double X = 0,
    double Y = 0,
    double Z = 0,
    bool Flag = false,
    string? Text = null);

public sealed record CameraSnap(double X, double Y, double Z, double FwdX, double FwdY, double FwdZ, double Yaw, double Pitch, double FovY);

public sealed record HudItem(string OwnerPack, string Id, double X, double Y, double W, double H, string Text, string Color);

/// <summary>Game-free, bounded HUD state owned by Lua packs (cleaned up automatically when a pack stops).</summary>
public sealed class HudState
{
    public const int MaxPerPack = 16;
    public const int MaxTotal = 24;

    readonly object _gate = new();
    readonly Dictionary<string, HudItem> _items = new(StringComparer.OrdinalIgnoreCase);

    static string Key(string pack, string id) => pack.ToLowerInvariant() + ":" + id.ToLowerInvariant();

    public bool SetText(string pack, string id, double x, double y, double w, double h, string text, string color)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 48) return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)) return false;
        x = Math.Clamp(x, 0, 0.98); y = Math.Clamp(y, 0, 0.98);
        w = Math.Clamp(w, 0.02, 1.0 - x); h = Math.Clamp(h, 0.02, 1.0 - y);
        text = text.Length > 160 ? text[..160] : text;
        if (string.IsNullOrWhiteSpace(color) || !color.StartsWith('#') || color.Length != 7) color = "#F0E6D2";
        string k = Key(pack, id);
        lock (_gate)
        {
            if (!_items.ContainsKey(k))
            {
                if (_items.Count >= MaxTotal) return false;
                if (_items.Values.Count(it => it.OwnerPack.Equals(pack, StringComparison.OrdinalIgnoreCase)) >= MaxPerPack) return false;
            }
            _items[k] = new HudItem(pack, id, x, y, w, h, text, color);
            return true;
        }
    }

    public bool Remove(string pack, string id)
    {
        lock (_gate) return _items.Remove(Key(pack, id));
    }

    public void ClearPack(string pack)
    {
        lock (_gate)
        {
            var keys = _items.Where(kv => kv.Value.OwnerPack.Equals(pack, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var k in keys) _items.Remove(k);
        }
    }

    public IReadOnlyList<HudItem> Snapshot()
    {
        lock (_gate)
            return _items.Values.OrderBy(it => it.OwnerPack, StringComparer.OrdinalIgnoreCase)
                .ThenBy(it => it.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
