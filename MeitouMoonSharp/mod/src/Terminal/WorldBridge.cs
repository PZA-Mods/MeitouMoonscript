// MeitouMoonscript (Author: PZA, Licence: GPLv3 or later): game-free snapshots and queued world actions for Lua packs.
// Read-only snapshots flow from SlowWorld -> PackHost; queued WorldActions flow from PackHost -> SlowWorld -> World.Commands.
namespace PZA.Moonscript.Repl;

public sealed record BodyPartSnap(string Name, double Flesh, double Stun, double MaxHp, bool Fatal, double Wear = 0, double JuryRig = 0);

public sealed record CharSnap(int Slot, int Gen, string Name, int Faction, bool IsPlayer,
                              double X, double Y, double Z, double Hp, bool Down, bool Dead, bool InCombat, int SquadId,
                              IReadOnlyList<BodyPartSnap>? Parts = null, double Toughness = 1, double Blood = 100,
                              double MaxBlood = 100, bool Unconscious = false, int Relation = 0, bool IsEnemy = false,
                              bool IsAlly = false, bool IsTrader = false, int Bounty = 0, string TemplateId = "");

public sealed record DoorSnap(string Id, string Building, int Owner, bool IsPlayer,
                              double X, double Y, double Z, double OpenAmount, string Motion, bool Locked, bool Broken, bool IsGate);

public sealed record BuildingSnap(string Id, string Name, string Record, int Owner, bool IsPlayer,
                                  double X, double Y, double Z, bool Complete, double Progress);

public sealed record ItemSnap(string Record, string Name, string Type, string Section, int Qty, int Level);

public sealed record WorldScanSnapshot(IReadOnlyList<CharSnap> Characters, IReadOnlyList<DoorSnap> Doors, IReadOnlyList<BuildingSnap> Buildings,
                                       IReadOnlyDictionary<(int Slot, int Gen), IReadOnlyList<ItemSnap>>? Inventories = null)
{
    public static readonly WorldScanSnapshot Empty = new([], [], [], new Dictionary<(int, int), IReadOnlyList<ItemSnap>>());
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
    string? Text = null,
    int Amount = 0,
    int Level = 0);

public sealed record CameraSnap(double X, double Y, double Z, double FwdX, double FwdY, double FwdZ, double Yaw, double Pitch, double FovY, double Aspect = 16.0 / 9.0);

public sealed record HudItem(string OwnerPack, string Id, double X, double Y, double W, double H, string Text, string Color, bool IsButton = false, double Alpha = 1.0, string Align = "Center VCenter");

public sealed record HudImage(string OwnerPack, string Id, double X, double Y, double W, double H, string Texture, string Rect, string Color, double Alpha, int Order);

/// <summary>Game-free, bounded HUD state owned by Lua packs and KenshiLua MyGUI overlays (cleaned up automatically when a pack/mod stops).</summary>
public sealed class HudState
{
    public const int MaxPerPack = 48;
    public const int MaxTotal = 64;
    public const int MaxButtons = 8;
    public const int MaxImagesPerPack = 192;
    public const int MaxTotalImages = 256;

    readonly object _gate = new();
    readonly Dictionary<string, HudItem> _items = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HudImage> _images = new(StringComparer.OrdinalIgnoreCase);
    int _imageSeq;

    static string Key(string pack, string id) => pack.ToLowerInvariant() + ":" + id.ToLowerInvariant();

    public bool SetText(string pack, string id, double x, double y, double w, double h, string text, string color, double alpha = 1.0, string align = "Center VCenter") =>
        SetItem(pack, id, x, y, w, h, text, color, isButton: false, alpha, align);

    public bool SetButton(string pack, string id, double x, double y, double w, double h, string label, string color) =>
        SetItem(pack, id, x, y, w, h, label, color, isButton: true, 1.0, "Center VCenter");

    bool SetItem(string pack, string id, double x, double y, double w, double h, string text, string color, bool isButton, double alpha, string align)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)) return false;
        x = Math.Clamp(x, -0.25, 1.0); y = Math.Clamp(y, -0.25, 1.0);
        w = Math.Clamp(w, 0.002, 1.0); h = Math.Clamp(h, 0.002, 1.0);
        alpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1.0;
        text = text.Length > 160 ? text[..160] : text;
        if (string.IsNullOrWhiteSpace(color) || !color.StartsWith('#') || color.Length != 7) color = "#F0E6D2";
        if (string.IsNullOrWhiteSpace(align)) align = "Center VCenter";
        string k = Key(pack, id);
        lock (_gate)
        {
            bool replacing = _items.TryGetValue(k, out var existing);
            if (!replacing)
            {
                if (_items.Values.Count(it => it.OwnerPack.Equals(pack, StringComparison.OrdinalIgnoreCase)) >= MaxPerPack) return false;
            }
            if (isButton)
            {
                int btnCount = _items.Values.Count(it => it.IsButton) - (replacing && existing!.IsButton ? 1 : 0);
                if (btnCount >= MaxButtons) return false;
            }
            else
            {
                int txtCount = _items.Values.Count(it => !it.IsButton) - (replacing && !existing!.IsButton ? 1 : 0);
                if (txtCount >= MaxTotal) return false;
            }
            _items[k] = new HudItem(pack, id, x, y, w, h, text, color, isButton, alpha, align);
            return true;
        }
    }

    public bool SetImage(string pack, string id, double x, double y, double w, double h, string texture, string rect, string color, double alpha)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || string.IsNullOrWhiteSpace(texture)) return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)) return false;
        x = Math.Clamp(x, -0.25, 1.0); y = Math.Clamp(y, -0.25, 1.0);
        w = Math.Clamp(w, 0.001, 1.0); h = Math.Clamp(h, 0.001, 1.0);
        alpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1.0;
        if (string.IsNullOrWhiteSpace(color) || !color.StartsWith('#') || color.Length != 7) color = "#FFFFFF";
        string k = Key(pack, id);
        lock (_gate)
        {
            bool replacing = _images.TryGetValue(k, out var existing);
            if (!replacing)
            {
                if (_images.Count >= MaxTotalImages) return false;
                if (_images.Values.Count(it => it.OwnerPack.Equals(pack, StringComparison.OrdinalIgnoreCase)) >= MaxImagesPerPack) return false;
            }
            int order = replacing ? existing!.Order : ++_imageSeq;
            _images[k] = new HudImage(pack, id, x, y, w, h, texture, rect ?? "", color, alpha, order);
            return true;
        }
    }

    public bool Remove(string pack, string id)
    {
        string k = Key(pack, id);
        lock (_gate)
        {
            bool a = _items.Remove(k);
            bool b = _images.Remove(k);
            return a || b;
        }
    }

    public void ClearPack(string pack)
    {
        lock (_gate)
        {
            var keys = _items.Where(kv => kv.Value.OwnerPack.Equals(pack, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var k in keys) _items.Remove(k);
            var ikeys = _images.Where(kv => kv.Value.OwnerPack.Equals(pack, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var k in ikeys) _images.Remove(k);
        }
    }

    public bool HasAny
    {
        get { lock (_gate) return _items.Count > 0 || _images.Count > 0; }
    }

    public IReadOnlyList<HudItem> Snapshot()
    {
        lock (_gate)
            return _items.Values.OrderBy(it => it.OwnerPack, StringComparer.OrdinalIgnoreCase)
                .ThenBy(it => it.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<HudImage> ImageSnapshot()
    {
        lock (_gate)
            return _images.Values.OrderBy(it => it.Order).ToList();
    }

    public IReadOnlyList<HudItem> TextSnapshot()
    {
        lock (_gate)
            return _items.Values.Where(it => !it.IsButton)
                .OrderBy(it => it.OwnerPack, StringComparer.OrdinalIgnoreCase)
                .ThenBy(it => it.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<HudItem> ButtonSnapshot()
    {
        lock (_gate)
            return _items.Values.Where(it => it.IsButton)
                .OrderBy(it => it.OwnerPack, StringComparer.OrdinalIgnoreCase)
                .ThenBy(it => it.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
