// MeitouMoonscript. Author: PZA. Licence: GPLv3 or later.
// What the terminal's Lua may read about the world, copied once a tick in a serial step (SlowWorld) so the terminal, which runs on the
// interface side, never touches the World (MeitouClient docs/modding.md "Determinism contract"). It changes nothing and holds no state the
// world needs: not hashed, not saved. The selected ids come from the interface (PlayerInterface.SelectedIds, read each frame).
using System.Collections.Concurrent;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using PZA.Moonscript.Repl;

namespace PZA.Moonscript;

public sealed class WorldSnapshot : ITickSystem, ICombatHitObserver
{
    public const int MaxSquad = 8;
    const int MaxQueuedEvents = 64;

    static volatile object? s_hours;      // boxed double, null before a world runs
    static volatile object? s_cats;       // boxed long, null before a world runs
    static volatile string? s_selected;
    static volatile FighterSnap[] s_squad = [];
    static volatile CharacterId[] s_squadIds = [];   // the same characters as s_squad, in order (the battle window draws their faces)
    static volatile CharacterId[] s_selectedIds = [];
    static readonly ConcurrentQueue<GameEvent> s_events = new();
    int _lastWholeHour = -1, _lastDay = -1;
    public static long Ticks;

    public static double? Hours => s_hours is double h ? h : null;
    public static long? Cats => s_cats is long c ? c : null;
    public static string? SelectedName => s_selected;
    public static IReadOnlyList<FighterSnap> Squad => s_squad;
    public static IReadOnlyList<CharacterId> SquadIds => s_squadIds;

    /// <summary>Set from the interface each frame.</summary>
    public static void SetSelection(IReadOnlyList<CharacterId> ids) => s_selectedIds = [.. ids];

    /// <summary>Drain narrow read-only game events for PackHost on the UI frame.</summary>
    public static List<GameEvent> DrainEvents()
    {
        var list = new List<GameEvent>();
        while (s_events.TryDequeue(out var ev)) list.Add(ev);
        return list;
    }

    static void EnqueueEvent(string name, Dictionary<string, object?> payload)
    {
        while (s_events.Count >= MaxQueuedEvents) s_events.TryDequeue(out _);
        s_events.Enqueue(new GameEvent(name, payload));
    }

    /// <summary>A new world: nothing from the old one carries over.</summary>
    public WorldSnapshot()
    {
        s_hours = null; s_cats = null; s_selected = null; s_squad = []; s_squadIds = [];
        while (s_events.TryDequeue(out _)) { }
    }

    public void HitResolved(World world, in CombatHit hit)
    {
        try
        {
            var table = world.Characters;
            var ac = !hit.Attacker.IsNone && table.TryResolveNext(hit.Attacker, out int aslot) ? table.Cold(aslot) : null;
            var vc = !hit.Victim.IsNone && table.TryResolveNext(hit.Victim, out int vslot) ? table.Cold(vslot) : null;
            string aname = ac?.Name?.Trim() is { Length: > 0 } a ? a : "Unknown";
            string vname = vc?.Name?.Trim() is { Length: > 0 } v ? v : "Unknown";
            bool ranged = hit.Source == CombatHitSource.Projectile;
            EnqueueEvent("hit", new Dictionary<string, object?>
            {
                ["attacker"] = aname,
                ["victim"] = vname,
                ["attacker_player"] = ac?.IsPlayer ?? false,
                ["victim_player"] = vc?.IsPlayer ?? false,
                ["ranged"] = ranged,
            });
            if (vc?.Medical is { } m && (m.Unconscious || m.Dead))
            {
                EnqueueEvent("character_down", new Dictionary<string, object?>
                {
                    ["name"] = vname,
                    ["is_player"] = vc.IsPlayer,
                    ["dead"] = m.Dead,
                    ["by"] = aname,
                });
            }
        }
        catch (Exception) { }
    }

    public void SlowWorld(World world)
    {
        Ticks++;
        double gh = world.GameHours;
        s_hours = gh;
        if (double.IsFinite(gh) && gh >= 0)
        {
            int wholeHour = (int)Math.Floor(gh);
            int day = Math.Max(1, wholeHour / 24);
            int hourOfDay = ((wholeHour % 24) + 24) % 24;
            if (_lastWholeHour >= 0 && wholeHour > _lastWholeHour)
                EnqueueEvent("hour", new Dictionary<string, object?> { ["day"] = day, ["hour"] = hourOfDay });
            if (_lastDay >= 0 && day > _lastDay)
                EnqueueEvent("day", new Dictionary<string, object?> { ["day"] = day });
            _lastWholeHour = wholeHour;
            _lastDay = day;
        }
        try { s_cats = world.Player.Exists ? world.Player.Money : null; }
        catch (Exception) { s_cats = null; }

        var ids = s_selectedIds;
        if (ids.Length == 0)
        {
            try { if (world.Player.Selection.Count > 0) ids = [.. world.Player.Selection]; }
            catch (Exception) { }
        }

        string? name = null;
        var list = new List<FighterSnap>(MaxSquad);
        var idList = new List<CharacterId>(MaxSquad);
        var seenSlots = new HashSet<int>();
        int focusSquad = -1;

        try
        {
            var table = world.Characters;
            foreach (var id in ids)
            {
                if (list.Count >= MaxSquad) break;
                if (!table.TryResolveNext(id, out int slot) || !seenSlots.Add(slot)) continue;
                if (table.Cold(slot) is not { } cold) continue;
                if (name is null) name = cold.Name.Trim();
                if (focusSquad < 0 && cold.IsPlayer && cold.SquadId >= 0) focusSquad = cold.SquadId;
                list.Add(ReadFighter(cold));
                idList.Add(table.IdOf(slot));
            }

            if (focusSquad < 0)
            {
                try { focusSquad = world.Player.Squad; }
                catch (Exception) { focusSquad = -1; }
            }

            // Fill remaining slots (up to MaxSquad) from the active squad's living player characters
            if (list.Count < MaxSquad)
            {
                int hw = table.HighWater;
                var next = table.Next;
                for (int slot = 0; slot < hw && list.Count < MaxSquad; slot++)
                {
                    if (!next[slot].Alive || seenSlots.Contains(slot)) continue;
                    var cold = table.Cold(slot);
                    if (cold is null || !cold.IsPlayer || cold.DiedTick >= 0) continue;
                    if (focusSquad >= 0 && cold.SquadId != focusSquad) continue;
                    seenSlots.Add(slot);
                    list.Add(ReadFighter(cold));
                    idList.Add(table.IdOf(slot));
                }
            }
        }
        catch (Exception)
        {
            // display-only snapshot: never throw into the simulation tick
        }

        s_selected = name;
        s_squad = list.Count == 0 ? [] : [.. list];
        s_squadIds = idList.Count == list.Count ? [.. idList] : [];
    }

    static FighterSnap ReadFighter(CharacterCold cold)
    {
        string name = string.IsNullOrWhiteSpace(cold.Name) ? "Drifter" : cold.Name.Trim();
        string race = cold.Race is { Name.Length: > 0 } r ? r.Name : "Greenlander";
        bool robot = cold.Race is { BleedRate: <= 0.001f };

        double hp = 100, maxHp = 100, bleed = 0;
        bool down = false;
        if (cold.Medical is { } m)
        {
            bleed = Math.Max(0, m.Bleeding);
            down = m.Unconscious || m.Dead;
            if (m.Parts.Count > 0)
            {
                double curSum = 0, maxSum = 0;
                int n = Math.Min(m.Parts.Count, 16);
                for (int i = 0; i < n; i++)
                {
                    var p = m.Parts[i];
                    double pm = Math.Max(1, p.MaxHp);
                    double pc = Math.Clamp(p.Flesh - p.Stun, 0, pm);
                    curSum += pc;
                    maxSum += pm;
                }
                hp = curSum / n;
                maxHp = maxSum / n;
            }
        }

        double str = 1, dex = 1, tough = 1, atk = 1, def = 1, med = 1, robo = 1, ma = 1, dodge = 1;
        if (cold.Stats is { } st)
        {
            str = CleanStat(st[StatsEnumerated.Strength]);
            dex = CleanStat(st[StatsEnumerated.Dexterity]);
            tough = CleanStat(st[StatsEnumerated.Toughness]);
            atk = CleanStat(st[StatsEnumerated.MeleeAttack]);
            def = CleanStat(st[StatsEnumerated.MeleeDefence]);
            med = CleanStat(st[StatsEnumerated.Medic]);
            robo = CleanStat(st[StatsEnumerated.Robotics]);
            ma = CleanStat(st[StatsEnumerated.MartialArts]);
            dodge = CleanStat(st[StatsEnumerated.Dodge]);
        }

        string weapon = "Unarmed";
        int weaponLevel = 0;
        bool ranged = false;
        int armourCount = 0;
        double armourSum = 0;
        if (cold.Inventory is { } inv)
        {
            ItemInstance? backWeapon = null, hipWeapon = null;
            foreach (var item in inv.Items)
            {
                if (item.Type is FcsRecordType.Weapon or FcsRecordType.Crossbow)
                {
                    if (item.Section == "back" && backWeapon is null) backWeapon = item;
                    else if (item.Section == "hip" && hipWeapon is null) hipWeapon = item;
                }
                else if (item.Type == FcsRecordType.Armour
                         && item.Section is "head" or "armour" or "shirt" or "legs" or "boots")
                {
                    armourCount++;
                    armourSum += Math.Clamp(item.Level, 0, 100);
                }
            }
            var chosen = backWeapon ?? hipWeapon;
            if (chosen is not null)
            {
                weapon = string.IsNullOrWhiteSpace(chosen.Name) ? "Weapon" : chosen.Name.Trim();
                weaponLevel = Math.Clamp(chosen.Level, 0, 100);
                ranged = chosen.Type == FcsRecordType.Crossbow;
            }
        }

        double armourLevel = armourCount > 0 ? armourSum / armourCount : 0;
        return new FighterSnap(name, race, robot, hp, maxHp, bleed, down,
            str, dex, tough, atk, def, med, robo, ma, dodge,
            weapon, weaponLevel, ranged, armourCount, armourLevel);
    }

    static double CleanStat(float v) => float.IsFinite(v) ? Math.Clamp(v, 0, 1000) : 1;
}
