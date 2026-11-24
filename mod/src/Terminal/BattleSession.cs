// MeitouMoonscript: the turn-based battle's C# side, game-free (the offline check and the fuzz harness drive it). Author: PZA.
// Licence: GPLv3 or later.
//
// The rules are scripts/fight.lua (claims B1-B6, tools/euler/CLAIMS.md); this keeps the script, the two-step choice the window needs
// (an action, then its target) and nothing else. Interface side, like the terminal: never saved, never in the world's state.
// Rules the fuzz target battle.drive checks: a pending action is cleared by any other action and by the end of a fight; a target click
// with nothing pending does nothing; every call the window makes is one fight_act at most.
using MoonSharp.Interpreter;
using PZA.Moonscript.Core;

namespace PZA.Moonscript.Repl;

public sealed record BattleEvent(string Kind, string AttSide, int AttSlot, string TgtSide, int TgtSlot, int Dmg, bool Crit, bool Ko, string Text);

public sealed class BattleSession
{
    readonly string _script;
    readonly Func<IReadOnlyList<FighterSnap>> _squad;
    readonly Action<string> _log;
    LuaBox? _box;
    ulong _seed = 1;

    public string? Pending { get; private set; }   // "attack", "heavy" or "bandage": waiting for a target
    public string? Note { get; private set; }      // the last refusal or hint, shown in the window
    public ScriptValue View { get; private set; } = ScriptValue.Nil;
    public int Acts { get; private set; }
    public IReadOnlyList<BattleEvent> LastEvents { get; private set; } = [];
    public long EventEpoch { get; private set; }

    /// <summary>The squad indexes the fight took (fight.lua takes the first four who are not down), in order; empty = the lore squad.</summary>
    public IReadOnlyList<int> LiveIndexes { get; private set; } = [];
    public bool LiveSquad => LiveIndexes.Count > 0;

    public BattleSession(string scriptPath, Func<IReadOnlyList<FighterSnap>> squad, Action<string> log)
    {
        _script = scriptPath; _squad = squad; _log = log;
    }

    public bool Running => View["status"].String is "choose";
    public string Status => View["status"].String ?? "none";

    /// <summary>A new fight against encounter `key`. The seed moves on each fight (the same session replays nothing twice).</summary>
    public void Start(string key)
    {
        Pending = null; Note = null;
        string text;
        try { text = File.ReadAllText(_script); }
        catch (Exception e) { Note = "can't read fight.lua: " + e.GetType().Name; View = ScriptValue.Nil; return; }
        var squad = _squad();
        var picked = new List<int>();
        for (int i = 0; i < squad.Count && picked.Count < 4; i++) if (!squad[i].Down) picked.Add(i);
        LiveIndexes = picked;
        _box = new LuaBox("battle", new ScriptLimits(), _seed++, _ => { }, _log);
        var box = _box;
        box.Expose("world", "squad", _ => ReplSession.ToLuaSquad(box.RawScript, squad));   // the same list the indexes above came from
        box.Load("fight", text);
        if (!box.Faulted) box.Call("fight_new", DynValue.NewString(key));
        Refresh();
    }

    /// <summary>A command button: guard and flee act at once; attack, heavy and bandage wait for a target.</summary>
    public void Command(string action)
    {
        if (!Running) return;
        if (action is "guard" or "flee") { Pending = null; Act(action, 0); return; }
        Pending = Pending == action ? null : action;
        Note = Pending is null ? null : Pending == "bandage" ? "Click who to bandage." : "Click a foe.";
    }

    /// <summary>A click on a foe (side "foe") or a squad member (side "party"), 1-based.</summary>
    public void Target(string side, int slot)
    {
        if (!Running || Pending is null) return;
        bool foe = Pending is "attack" or "heavy";
        if (foe != (side == "foe")) { Note = foe ? "Click a foe." : "Click who to bandage."; return; }
        string a = Pending;
        Pending = null;
        Act(a, slot);
    }

    void Act(string action, int target)
    {
        if (_box is null || _box.Faulted) return;
        Acts++;
        var r = ScriptValue.FromLua(_box.Call("fight_act", DynValue.NewString(action), DynValue.NewNumber(target)));
        Note = r["ok"].Boolean ? null : r["why"].String;
        Refresh();
    }

    void Refresh()
    {
        if (_box is null) return;
        if (_box.Faulted) { Note = $"the battle script stopped: {_box.Fault}: {_box.FaultDetail}"; View = ScriptValue.Nil; Pending = null; LastEvents = []; return; }
        View = ScriptValue.FromLua(_box.Call("fight_view"));
        var evs = new List<BattleEvent>();
        if (View["events"].Table is { } tbl)
        {
            for (int i = 1; i <= tbl.Count; i++)
            {
                var e = View["events"][i];
                if (e.Kind != ScriptKind.Table) continue;
                evs.Add(new BattleEvent(
                    e["kind"].String ?? "",
                    e["att_side"].String ?? "", (int)e["att_slot"].Number,
                    e["tgt_side"].String ?? "", (int)e["tgt_slot"].Number,
                    (int)e["dmg"].Number, e["crit"].Boolean, e["ko"].Boolean,
                    e["text"].String ?? ""));
            }
        }
        LastEvents = evs;
        EventEpoch++;
        if (!Running) Pending = null;
    }

    public LuaBox? Box => _box;   // tests only
}
