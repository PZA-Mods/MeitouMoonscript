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
