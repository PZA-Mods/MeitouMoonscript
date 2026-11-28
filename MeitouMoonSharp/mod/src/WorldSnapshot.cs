// MeitouMoonscript. Author: PZA. Licence: GPLv3 or later.
// What the terminal's Lua may read about the world, copied once a tick in a serial step (SlowWorld) so the terminal, which runs on the
// interface side, never touches the World (MeitouClient docs/modding.md "Determinism contract"). It changes nothing and holds no state the
// world needs: not hashed, not saved. The selected ids come from the interface (PlayerInterface.SelectedIds, read each frame).
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;
using PZA.Moonscript.Repl;

namespace PZA.Moonscript;

public sealed class WorldSnapshot : ITickSystem, ICombatHitObserver
{
    public const int MaxSquad = 8;
    const int MaxQueuedEvents = 64;
    const int MaxQueuedActions = 64;
    const int MaxScannedChars = 512;

    static volatile object? s_hours;      // boxed double, null before a world runs
    static volatile object? s_cats;       // boxed long, null before a world runs
    static volatile string? s_selected;
    static volatile FighterSnap[] s_squad = [];
    static volatile CharacterId[] s_squadIds = [];   // the same characters as s_squad, in order (the battle window draws their faces)
    static volatile CharacterId[] s_selectedIds = [];
    static volatile WorldScanSnapshot s_scan = WorldScanSnapshot.Empty;
    static volatile CameraSnap? s_camera;
    static readonly ConcurrentQueue<GameEvent> s_events = new();
    static readonly ConcurrentQueue<WorldAction> s_actions = new();
    readonly FactionRelations? _relations;
    int _lastWholeHour = -1, _lastDay = -1;
    public static long Ticks;

    public static double? Hours => s_hours is double h ? h : null;
    public static long? Cats => s_cats is long c ? c : null;
    public static string? SelectedName => s_selected;
    public static IReadOnlyList<FighterSnap> Squad => s_squad;
    public static IReadOnlyList<CharacterId> SquadIds => s_squadIds;
    public static WorldScanSnapshot Scan => s_scan;
    public static CameraSnap? Camera => s_camera;

    /// <summary>Queue a safe world action from PackHost (drained on the next SlowWorld simulation tick).</summary>
    public static void EnqueueAction(WorldAction action)
    {
        if (s_actions.Count >= MaxQueuedActions) return;
        s_actions.Enqueue(action);
    }

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
    public WorldSnapshot(FactionRelations? relations = null)
    {
        _relations = relations;
        s_hours = null; s_cats = null; s_selected = null; s_squad = []; s_squadIds = []; s_scan = WorldScanSnapshot.Empty; s_camera = null;
        while (s_events.TryDequeue(out _)) { }
        while (s_actions.TryDequeue(out _)) { }
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
            double cut = Math.Max(0, hit.Damage.Cut), blunt = Math.Max(0, hit.Damage.Blunt), pierce = Math.Max(0, hit.Damage.Pierce);
            double totalDmg = cut + blunt + pierce;
            var pos = hit.VictimPosition;
            bool ko = vc?.Medical?.Unconscious ?? false;
            bool death = vc?.Medical?.Dead ?? false;
            EnqueueEvent("hit", new Dictionary<string, object?>
            {
                ["attacker"] = aname,
                ["target"] = vname,
                ["victim"] = vname,
                ["attacker_player"] = ac?.IsPlayer ?? false,
                ["victim_player"] = vc?.IsPlayer ?? false,
                ["damage"] = totalDmg,
                ["cut"] = cut,
                ["blunt"] = blunt,
                ["pierce"] = pierce,
                ["part"] = hit.Part,
                ["x"] = (double)pos.X,
                ["y"] = (double)pos.Y,
                ["z"] = (double)pos.Z,
                ["ko"] = ko,
                ["death"] = death,
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

        ApplyQueuedActions(world);
        CaptureWorldScan(world);
        CaptureCamera(world);
    }

    static void CaptureCamera(World world)
    {
        try
        {
            if (world.View is { } v)
            {
                var fwd = v.Forward;
                float len = fwd.Length();
                if (len > 1e-5f) fwd /= len;
                double yaw = (Math.Atan2(fwd.X, fwd.Z) * 180.0 / Math.PI + 360.0) % 360.0;
                double pitch = Math.Asin(Math.Clamp((double)fwd.Y, -1.0, 1.0)) * 180.0 / Math.PI;
                double aspect = float.IsFinite(v.Aspect) && v.Aspect > 0.1f ? v.Aspect : (16.0 / 9.0);
                s_camera = new CameraSnap(v.Eye.X, v.Eye.Y, v.Eye.Z, fwd.X, fwd.Y, fwd.Z, yaw, pitch, v.FovY, aspect);
            }
        }
        catch (Exception) { }
    }

    static void ApplyQueuedActions(World world)
    {
        int processed = 0;
        long tick = world.Tick;
        while (processed < MaxQueuedActions && s_actions.TryDequeue(out var act))
        {
            processed++;
            try
            {
                switch (act.Kind)
                {
                    case "door.toggle" when !string.IsNullOrEmpty(act.TargetId):
                        world.Commands.Enqueue(new ToggleDoorsOrder(act.TargetId) { Tick = tick });
                        break;
                    case "door.set_open" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        var doors = world.Fixtures.OfBuilding(act.TargetId);
                        bool anyNeedsToggle = false;
                        foreach (var f in doors)
                        {
                            if (f.Kind != FixtureKind.Door || f.DoorBroken) continue;
                            bool isOpen = f.Motion is DoorMotion.Open or DoorMotion.Opening || f.OpenAmount > 0.5f;
                            if (isOpen != act.Flag) { anyNeedsToggle = true; break; }
                        }
                        if (anyNeedsToggle)
                            world.Commands.Enqueue(new ToggleDoorsOrder(act.TargetId) { Tick = tick });
                        break;
                    }
                    case "door.toggle_lock" when !string.IsNullOrEmpty(act.TargetId):
                        world.Commands.Enqueue(new ToggleDoorLocksOrder(act.TargetId) { Tick = tick });
                        break;
                    case "door.set_locked" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        var doors = world.Fixtures.OfBuilding(act.TargetId);
                        bool anyNeedsToggle = false;
                        foreach (var f in doors)
                        {
                            if (f.Kind != FixtureKind.Door || f.DoorBroken) continue;
                            bool isLocked = f.Lock?.EffectivelyLocked ?? false;
                            if (isLocked != act.Flag) { anyNeedsToggle = true; break; }
                        }
                        if (anyNeedsToggle)
                            world.Commands.Enqueue(new ToggleDoorLocksOrder(act.TargetId) { Tick = tick });
                        break;
                    }
                    case "order.move" when act.CharIds is { Count: > 0 }:
                        world.Commands.Enqueue(new MoveOrder(ToCharIds(act.CharIds), new Vector3((float)act.X, (float)act.Y, (float)act.Z)) { Tick = tick });
                        break;
                    case "order.stop" when act.CharIds is { Count: > 0 }:
                        world.Commands.Enqueue(new StopCommand(ToCharIds(act.CharIds)) { Tick = tick });
                        break;
                    case "order.follow" when act.CharIds is { Count: > 0 }:
                        world.Commands.Enqueue(new FollowOrder(ToCharIds(act.CharIds), new CharacterId(act.TargetSlot, act.TargetGen)) { Tick = tick, Guard = act.Flag });
                        break;
                    case "order.attack" when act.CharIds is { Count: > 0 }:
                        world.Commands.Enqueue(new AttackOrder(ToCharIds(act.CharIds), new CharacterId(act.TargetSlot, act.TargetGen)) { Tick = tick });
                        break;
                    case "order.squad_ai" when !string.IsNullOrEmpty(act.Text) && TryParseSquadAi(act.Text, out var opt):
                        world.Commands.Enqueue(new SquadAiCommand(opt, act.Flag) { Tick = tick });
                        break;
                    case "world.floater" when !string.IsNullOrEmpty(act.Text):
                        world.Floaters?.Enqueue(Floater.Label(new Vector3((float)act.X, (float)act.Y, (float)act.Z), act.Text));
                        break;
                    case "world.add_cats" when world.Player.Exists:
                    {
                        long delta = Math.Clamp(act.Amount, -100_000, 100_000);
                        world.Player.Money = Math.Max(0, world.Player.Money + delta);
                        break;
                    }
                    case "item.give" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        var cid = new CharacterId(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Inventory is { } inv
                            && world.Fixtures.Items is { } factory)
                        {
                            int count = Math.Clamp(act.Amount <= 0 ? 1 : act.Amount, 1, 20);
                            int lvl = Math.Clamp(act.Level, 0, 100);
                            for (int i = 0; i < count; i++)
                            {
                                var item = factory.Create(act.TargetId, lvl, 1f);
                                if (item is null || !inv.Add(item, true)) break;
                            }
                        }
                        break;
                    }
                    case "item.remove" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        var cid = new CharacterId(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Inventory is { } inv)
                        {
                            int left = Math.Clamp(act.Amount <= 0 ? 1 : act.Amount, 1, 50);
                            while (left > 0)
                            {
                                ItemInstance? match = null;
                                foreach (var it in inv.Items)
                                {
                                    if (string.Equals(it.Record, act.TargetId, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(it.Name, act.TargetId, StringComparison.OrdinalIgnoreCase))
                                    { match = it; break; }
                                }
                                if (match is null || !inv.RemoveOne(match)) break;
                                left--;
                            }
                        }
                        break;
                    }
                    case "medical.amputate":
                    {
                        var cid = new CharacterId(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Medical is { } med)
                        {
                            string want = act.Amount switch { 0 => "left arm", 1 => "right arm", 2 => "left leg", 3 => "right leg", _ => "" };
                            foreach (var pt in med.Parts)
                            {
                                string pid = pt.Template?.StringId ?? "";
                                if (want.Length > 0 && pid.Contains(want, StringComparison.OrdinalIgnoreCase))
                                {
                                    pt.Limb = Meitou.Simulation.Bodies.LimbState.Stump;
                                    pt.Flesh = Math.Min(pt.Flesh, -pt.MaxHp);
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    case "medical.set_blood":
                    {
                        var cid = new CharacterId(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Medical is { } med
                            && double.IsFinite(act.X))
                        {
                            med.Blood = (float)Math.Clamp(act.X, -200, 200);
                        }
                        break;
                    }
                    case "medical.add_fed":
                    {
                        var cid = new CharacterId(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Medical is { } med
                            && double.IsFinite(act.X))
                        {
                            med.Fed = Math.Clamp(med.Fed + (float)Math.Clamp(act.X, -10, 10), 0, 100);
                        }
                        break;
                    }
                }
            }
            catch (Exception) { }
        }
    }

    static CharacterId[] ToCharIds(IReadOnlyList<(int Slot, int Gen)> raw)
    {
        var arr = new CharacterId[raw.Count];
        for (int i = 0; i < raw.Count; i++) arr[i] = new CharacterId(raw[i].Slot, raw[i].Gen);
        return arr;
    }

    static bool TryParseSquadAi(string name, out SquadAi opt)
    {
        opt = name.Trim().ToLowerInvariant() switch
        {
            "ditch" => SquadAi.Ditch,
            "sleep" => SquadAi.Sleep,
            "sit" => SquadAi.Sit,
            "heal" => SquadAi.Heal,
            "help" => SquadAi.Help,
            "rescue" => SquadAi.Rescue,
            "share" => SquadAi.Share,
            "feed" => SquadAi.Feed,
            "shoot_first" => SquadAi.ShootFirst,
            "heal_neutrals" => SquadAi.HealNeutrals,
            "heal_all" => SquadAi.HealAll,
            _ => SquadAi.None,
        };
        return opt != SquadAi.None;
    }

    void CaptureWorldScan(World world)
    {
        try
        {
            int playerFaction = world.Player.Exists ? world.Player.Faction : -1;

            var chars = new List<CharSnap>();
            var invs = new Dictionary<(int Slot, int Gen), IReadOnlyList<ItemSnap>>();
            var table = world.Characters;
            var next = table.Next;
            int hw = table.HighWater;
            for (int slot = 0; slot < hw && chars.Count < MaxScannedChars; slot++)
            {
                ref readonly var hot = ref next[slot];
                if (!hot.Alive || hot.Dormant) continue;
                var cold = table.Cold(slot);
                if (cold is null) continue;
                var id = table.IdOf(slot);
                bool uncon = cold.Medical?.Unconscious ?? false;
                bool dead = cold.Medical?.Dead ?? false;
                bool down = uncon || dead;
                List<BodyPartSnap>? parts = null;
                double blood = 100;
                if (cold.Medical is { } m)
                {
                    blood = float.IsFinite(m.Blood) ? Math.Clamp(m.Blood, 0, 200) : 100;
                    if (m.Parts.Count > 0)
                    {
                        int pn = Math.Min(m.Parts.Count, 12);
                        parts = new List<BodyPartSnap>(pn);
                        for (int pi = 0; pi < pn; pi++)
                        {
                            var pt = m.Parts[pi];
                            string pName = pt.Template?.StringId ?? $"part{pi}";
                            bool fatal = pi < 3
                                || pName.Contains("head", StringComparison.OrdinalIgnoreCase)
                                || pName.Contains("chest", StringComparison.OrdinalIgnoreCase)
                                || pName.Contains("stomach", StringComparison.OrdinalIgnoreCase);
                            parts.Add(new BodyPartSnap(pName, pt.Flesh, pt.Stun, Math.Max(1, pt.MaxHp), fatal, pt.Wear, pt.Rig));
                        }
                    }
                }
                double tough = cold.Stats is { } st ? CleanStat(st[StatsEnumerated.Toughness]) : 1;
                int rel = 0;
                bool isAlly = cold.IsPlayer || (playerFaction >= 0 && cold.Faction == playerFaction);
                bool isEnemy = !isAlly && cold.InCombat;
                if (_relations is not null && playerFaction >= 0 && cold.Faction >= 0)
                {
                    float r = _relations.Get(playerFaction, cold.Faction);
                    if (float.IsFinite(r))
                    {
                        rel = (int)MathF.Round(r);
                        if (!isAlly && r >= FactionRelations.AllyThreshold) isAlly = true;
                        if (!isAlly && r <= FactionRelations.HostileThreshold) isEnemy = true;
                    }
                }
                else
                {
                    rel = isAlly ? 100 : (isEnemy ? -50 : 0);
                }
                chars.Add(new CharSnap(
                    id.Slot, id.Generation,
                    string.IsNullOrWhiteSpace(cold.Name) ? "Drifter" : cold.Name.Trim(),
                    cold.Faction, cold.IsPlayer,
                    hot.Position.X, hot.Position.Y, hot.Position.Z,
                    hot.Health, down, dead, cold.InCombat, cold.SquadId,
                    parts, tough, blood, 100, uncon, rel, isEnemy, isAlly,
                    TemplateId: cold.RecordId ?? "",
                    IsAnimal: cold.Animal is not null,
                    Age: float.IsFinite(cold.Age) ? cold.Age : 1.0,
                    RaceId: cold.Race?.Name ?? ""));
                if (cold.IsPlayer && cold.Inventory is { } inv && inv.Items.Count > 0)
                {
                    int n = Math.Min(inv.Items.Count, 32);
                    var list = new List<ItemSnap>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var it = inv.Items[i];
                        list.Add(new ItemSnap(it.Record ?? "", it.Name ?? "", it.Type.ToString(), it.Section ?? "", it.Quantity, it.Level));
                    }
                    invs[(id.Slot, id.Generation)] = list;
                }
            }

            var doors = new List<DoorSnap>();
            foreach (var f in world.Fixtures.All)
            {
                if (f.Kind != FixtureKind.Door) continue;
                bool locked = f.Lock?.EffectivelyLocked ?? false;
                doors.Add(new DoorSnap(
                    f.Id, f.Building, f.Owner, playerFaction >= 0 && f.Owner == playerFaction,
                    f.Position.X, f.Position.Y, f.Position.Z,
                    f.OpenAmount, f.Motion.ToString().ToLowerInvariant(),
                    locked, f.DoorBroken, f.GateCode != 0));
            }

            var blds = new List<BuildingSnap>();
            foreach (var b in world.Buildings.All)
            {
                blds.Add(new BuildingSnap(
                    b.Id, b.Name ?? b.Record ?? "", b.Record ?? "",
                    b.Owner, playerFaction >= 0 && b.Owner == playerFaction,
                    b.Position.X, b.Position.Y, b.Position.Z,
                    b.Complete, b.Progress));
            }

            s_scan = new WorldScanSnapshot(chars, doors, blds, invs);
        }
        catch (Exception) { }
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
