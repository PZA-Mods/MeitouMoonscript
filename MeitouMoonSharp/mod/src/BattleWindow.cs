// MeitouMoonscript. Author: PZA. Licence: GPLv3 or later.
// The FF-style animated turn-based battle window on PZA's Meitou UI:
//   - Top stage: Kenshi desert backdrop (ui/stage_desert.png), staggered animated pixel-art foes on the left, staggered party sprites
//     facing left on the right, idle breathing frames (ImageRect), step-forward + turn pointer on the active fighter, smooth eased dashes
//     to the target (UiBindings.Positions), slash FX flash (fx_slash.png), hit recoil/shake, and rising floating damage numbers.
//   - Bottom panel: classic FF command row + target buttons + party status + paced action log + encounter buttons.
// Rules live in scripts/fight.lua through BattleSession. Opened by /battle [hungry|dust|beak] or F10 > Mods.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;
using PZA.MeitouUi;
using PZA.Moonscript.Core;
using PZA.Moonscript.Repl;
using L = PZA.MeitouUi.PzaLayout;

namespace PZA.Moonscript;

public static class BattleWindow
{
    public const string Id = "pza.moonscript.battle";
    public const int Foes = 5, Party = 4, LogRows = 6;
    public static PzaWindow Window = null!;
    public static BattleSession? Session;
    static string s_folder = "";
    public static Func<IReadOnlyList<CharacterId>> SquadIds = () => [];

    // ---------------------------------------------------------------- stage animation state (60+ FPS, UI-side only)
    static readonly Stopwatch s_clock = Stopwatch.StartNew();
    static double s_lastSec;
    static long s_seenEpoch;
    static readonly Queue<BattleEvent> s_animQueue = new();
    static BattleEvent? s_activeEv;
    static double s_evTime;
    const double BeatDuration = 0.52;   // seconds per animated action beat

    // Staggered FF-style base coordinates inside the tab page (fractions of page size)
    static readonly (double X, double Y)[] FoePos =
    [
        (0.19, 0.13),   // front-top (Leader / Boss)
        (0.07, 0.10),   // back-top
        (0.19, 0.25),   // front-mid
        (0.07, 0.22),   // back-mid
        (0.14, 0.34),   // front-low
    ];
    static readonly (double X, double Y)[] PartyPos =
    [
        (0.76, 0.10),
        (0.81, 0.18),
        (0.76, 0.26),
        (0.81, 0.34),
    ];
    const double SpriteW = 0.088, SpriteH = 0.135;

    static string N(string s, int i) => s + i.ToString(CultureInfo.InvariantCulture);
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static string[] Page()
    {
        var w = new List<string>
        {
            L.Text("lbTitle", 0.02, 0.006, 0.96, 0.044),
            // 2D FF battle stage backdrop
            "<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.015 0.055 0.97 0.42\" name=\"StageBg\"/>",
        };
        // Foe sprites (left side, staggered)
        for (int i = 1; i <= Foes; i++)
        {
            var (x, y) = FoePos[i - 1];
            w.Add($"<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"{F(x)} {F(y)} {F(SpriteW)} {F(SpriteH)}\" name=\"{N("fimg", i)}\"/>");
            w.Add(L.Text(N("fglyph", i), x, y, SpriteW, SpriteH, align: "Center"));
        }
        // Party sprites (right side, facing left)
        for (int i = 1; i <= Party; i++)
        {
            var (x, y) = PartyPos[i - 1];
            w.Add($"<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"{F(x)} {F(y)} {F(SpriteW)} {F(SpriteH)}\" name=\"{N("pspr", i)}\"/>");
        }
        // Stage overlays: turn pointer, slash FX, floating damage popup, action banner
        w.Add(L.Text("lbTurnArrow", 0.70, 0.12, 0.06, 0.045, align: "Center"));
        w.Add("<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.45 0.20 0.09 0.13\" name=\"fxSlash\"/>");
        w.Add(L.Text("lbPop", 0.42, 0.16, 0.16, 0.05, align: "Center"));

        // Targetable foe buttons strip right under the stage
        for (int i = 1; i <= Foes; i++)
        {
            double x = 0.015 + (i - 1) * 0.106;
            w.Add(L.Button(N("foe", i), x, 0.485, 0.10, 0.048));
            w.Add(L.Text(N("fhp", i), x, 0.535, 0.10, 0.032, align: "Center"));
        }
        // Party status cards (right half, classic FF bottom-right box)
        for (int i = 1; i <= Party; i++)
        {
            double y = 0.485 + (i - 1) * 0.072;
            w.Add(L.Portrait(N("pface", i), 0.56, y, 0.044, 0.064));
            w.Add(L.Button(N("pc", i), 0.61, y, 0.16, 0.032));
            w.Add(L.Text(N("pst", i), 0.775, y, 0.205, 0.032));
            w.Add(L.Text(N("php", i), 0.61, y + 0.033, 0.37, 0.03));
        }

        // Command buttons (left half below foe strip)
        string[] cmds = ["cmdAttack", "cmdHeavy", "cmdGuard", "cmdBandage", "cmdFlee"];
        for (int i = 0; i < cmds.Length; i++)
        {
            double cx = 0.015 + (i % 3) * 0.178;
            double cy = 0.578 + (i / 3) * 0.058;
            w.Add(L.Button(cmds[i], cx, cy, 0.17, 0.05));
        }

        w.Add(L.Text("lbNote", 0.015, 0.698, 0.53, 0.036));
        w.Add("<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.015 0.738 0.97 0.145\" name=\"LogPanel\"/>");
        for (int i = 1; i <= LogRows; i++)
            w.Add(L.Text(N("log", i), 0.025, 0.742 + (i - 1) * 0.022, 0.95, 0.022));

        w.Add(L.Button("newHungry", 0.015, 0.895, 0.305, 0.055));
        w.Add(L.Button("newDust", 0.345, 0.895, 0.305, 0.055));
        w.Add(L.Button("newBeak", 0.675, 0.895, 0.305, 0.055));
        return [.. w];
    }

    static string LayoutXml() => L.Doc(L.WindowMin("Root", "MOONSCRIPT BATTLE", 0.14, 0.06, 0.72, 0.86,
        L.Tabs("Tabs", 0.02, 0.02, 0.96, 0.96, ("TabBattle", Page()), L.HelpList("TabHelp", "lstHelp"))));

    public static void Setup(string folder, HostSettings settings, Action<string, object> set)
    {
        s_folder = folder;
        Window = new PzaWindow(Id, LayoutXml)
        {
            Minimizable = true,
            Bind = Bind,
            Event = Event,
            LoadPlace = () => new Vector2((float)settings.BattlePlaceX, (float)settings.BattlePlaceY),
            SavePlace = p => { set(nameof(HostSettings.BattlePlaceX), Math.Round((double)p.X, 3)); set(nameof(HostSettings.BattlePlaceY), Math.Round((double)p.Y, 3)); },
        };
        PzaUi.Add(Window);
    }

    /// <summary>Open the window on a new fight (the terminal's /battle, the debug menu).</summary>
    public static void Start(string key)
    {
        s_animQueue.Clear();
        s_activeEv = null;
        s_evTime = 0;
        Session?.Start(key is "dust" or "beak" ? key : "hungry");
        if (Session is not null) s_seenEpoch = Session.EventEpoch;
        Window.Open();
    }

    /// <summary>Called every frame from PzaUi.EachFrame to advance sprite dash / slash / popup tweens smoothly.</summary>
    public static void EachFrame()
    {
        double now = s_clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - s_lastSec, 0, 0.1);
        s_lastSec = now;

        if (Session is { } s && s.EventEpoch != s_seenEpoch)
        {
            s_seenEpoch = s.EventEpoch;
            foreach (var ev in s.LastEvents) s_animQueue.Enqueue(ev);
        }

        if (s_activeEv is not null)
        {
            s_evTime += dt;
            if (s_evTime >= BeatDuration) { s_activeEv = null; s_evTime = 0; }
        }
        if (s_activeEv is null && s_animQueue.Count > 0)
        {
            s_activeEv = s_animQueue.Dequeue();
            s_evTime = 0;
        }
    }

    static float OutQuad(float t) { t = Math.Clamp(t, 0, 1); return t * (2 - t); }

    static string Bar(double hp, double max)
    {
        int n = (int)Math.Round(10 * Math.Clamp(hp / Math.Max(1, max), 0, 1));
        return new string('|', n) + new string('.', 10 - n) + " " + hp.ToString("0", CultureInfo.InvariantCulture) + "/" + max.ToString("0", CultureInfo.InvariantCulture);
    }

    static string HpColour(double hp, double max) => hp <= 0 ? "#8A9099" : hp / max > 0.5 ? "#6AB04C" : hp / max > 0.25 ? "#F0B45A" : "#FF6B6B";

    static string PartySpriteKey(string name, string race)
    {
        string r = (race + " " + name).ToLowerInvariant();
        if (r.Contains("skeleton") || r.Contains("sadneil") || r.Contains("burn")) return "skeleton";
        if (r.Contains("shek") || r.Contains("ruka") || r.Contains("kang")) return "shek";
        if (r.Contains("hive") || r.Contains("beep") || r.Contains("green")) return "hiver";
        return "drifter";
    }

    static UiRect StageRect(UiRect page, double rx, double ry, double rw, double rh) =>
        new(page.X + (float)(rx * page.Width), page.Y + (float)(ry * page.Height), (float)(rw * page.Width), (float)(rh * page.Height));

    static void Bind(UiBindings b)
    {
        PzaWindow.TabText(b, "Tabs", 0, "BATTLE");
        PzaWindow.TabText(b, "Tabs", 1, "HELP");
        Window.Paragraphs(b, "lstHelp", Help);
        string ui = Path.Combine(s_folder, "ui");
        string stageTex = File.Exists(Path.Combine(ui, "stage_desert.png")) ? Path.Combine(ui, "stage_desert.png") : Path.Combine(ui, "panel.png");
        PzaWindow.Props(b, "StageBg", ("ImageTexture", "file:" + stageTex));
        PzaWindow.Props(b, "LogPanel", ("ImageTexture", "file:" + Path.Combine(ui, "panel.png")));
        if (Session is not { } s) return;

        // Pull any freshly emitted events even when Bind runs in an offline check without EachFrame
        if (s.EventEpoch != s_seenEpoch)
        {
            s_seenEpoch = s.EventEpoch;
            foreach (var ev in s.LastEvents) s_animQueue.Enqueue(ev);
            if (s_activeEv is null && s_animQueue.Count > 0) { s_activeEv = s_animQueue.Dequeue(); s_evTime = 0; }
        }

        var v = s.View;
        string status = s.Status;
        var party = v["party"].Table ?? [];
        var foes = v["foes"].Table ?? [];
        int turn = (int)v["turn"].Number;
        string who = turn >= 1 && turn <= party.Count ? v["party"][turn]["name"].String ?? "" : "";

        b.Text["lbTitle"] = status == "choose" && s_activeEv is { Text.Length: > 0 } evBanner
            ? evBanner.Text
            : status switch
            {
                "choose" => $"{v["title"].String}  -  round {v["round"].Number:0}  -  {who}'s turn",
                "won" => $"{v["title"].String}  -  WON in {v["round"].Number:0} rounds",
                "lost" => $"{v["title"].String}  -  your squad is down",
                "fled" => $"{v["title"].String}  -  you got away",
                "draw" => $"{v["title"].String}  -  both sides lost interest",
                _ => "Choose a fight below",
            };
        PzaWindow.Props(b, "lbTitle", ("TextColour", s_activeEv is not null ? "#F0B45A" : status switch { "won" => "#5FD7AF", "lost" => "#FF6B6B", _ => "#F0B45A" }));

        // Resolve page pixel bounds from last arranged frame so we can move sprites smoothly via UiBindings.Positions
        UiRect? pageBounds = null;
        if (PzaUi.Host?.LastFrame.FirstOrDefault(fr => fr.Id == Id) is { } frame)
            pageBounds = frame.Controls.FirstOrDefault(c => c.Name == "TabBattle")?.Bounds;

        int idleFrame = ((int)(s_clock.Elapsed.TotalSeconds * 2.2)) & 1;
        float evP = s_activeEv is not null ? Math.Clamp((float)(s_evTime / BeatDuration), 0, 1) : 0;
        bool inImpact = s_activeEv is not null && evP is >= 0.32f and <= 0.72f;
        float dashT = s_activeEv is null ? 0
            : evP < 0.35f ? OutQuad(evP / 0.35f)
            : evP < 0.65f ? 1f
            : 1f - OutQuad((evP - 0.65f) / 0.35f);

        // Compute animated foe sprites (left side)
        for (int i = 1; i <= Foes; i++)
        {
            bool there = i <= foes.Count;
            var f = there ? v["foes"][i] : ScriptValue.Nil;
            string name = there ? f["name"].String ?? "" : "";
            bool ko = there && f["ko"].Boolean;
            PzaWindow.Enable(b, N("foe", i));
            b.Text[N("foe", i)] = there ? (ko ? "(down)" : name) : " ";
            b.Text[N("fhp", i)] = there ? Bar(f["hp"].Number, f["max"].Number) : " ";
            PzaWindow.Props(b, N("fhp", i), ("TextColour", there ? HpColour(f["hp"].Number, f["max"].Number) : "#8A9099"));

            string key = f["pic"].String ?? Portrait(name);
            string sprPath = Path.Combine(ui, "spr_foe_" + key + ".png");
            bool hasSpr = there && File.Exists(sprPath);
            bool isAtt = s_activeEv is { AttSide: "foe" } ae && ae.AttSlot == i;
            bool isTgt = s_activeEv is { TgtSide: "foe" } te && te.TgtSlot == i;
            int frIdx = ko ? 3 : (isAtt && evP is >= 0.25f and <= 0.68f) ? 2 : ((idleFrame + i) & 1);
            float alpha = !there ? 0f : ko ? 0.55f : (isTgt && inImpact && s_activeEv!.Kind == "hit" && ((int)(evP * 24) & 1) == 0) ? 0.45f : 1f;

            PzaWindow.Props(b, N("fimg", i),
                ("ImageTexture", hasSpr ? "file:" + sprPath : "file:" + Path.Combine(ui, "panel.png")),
                ("ImageRect", $"{frIdx * 64} 0 64 64"),
                ("Alpha", alpha.ToString("0.##", CultureInfo.InvariantCulture)));
            b.Text[N("fglyph", i)] = there && !hasSpr ? (ko ? "x_x" : "!") : " ";

            if (pageBounds is { } pb && there)
            {
                var (bx, by) = FoePos[i - 1];
                double dx = 0, dy = 0;
                if (isAtt && s_activeEv is { Kind: "hit" or "miss" } ev && ev.TgtSlot >= 1 && ev.TgtSlot <= Party)
                {
                    var (tx, ty) = PartyPos[ev.TgtSlot - 1];
                    dx = ((tx - 0.12) - bx) * dashT;
                    dy = (ty - by) * dashT;
                }
                else if (isTgt && inImpact && s_activeEv!.Kind == "hit")
                {
                    dx = -0.014 * Math.Sin(evP * 40.0);
                }
                b.Positions[N("fimg", i)] = StageRect(pb, bx + dx, by + dy, SpriteW, SpriteH);
            }
            PzaWindow.Tooltip(b, N("foe", i), there ? $"{name}: attack {f["atk"].Number:0}, defence {f["def"].Number:0}, speed {f["spd"].Number:0}. Click to hit after ATTACK or HEAVY BLOW." : "");
        }

        // Compute animated party sprites (right side) + party status rows
        var ids = SquadIds();
        for (int i = 1; i <= Party; i++)
        {
            bool there = i <= party.Count;
            var p = there ? v["party"][i] : ScriptValue.Nil;
            string pname = there ? p["name"].String ?? "" : "";
            string prace = there ? p["race"].String ?? "" : "";
            bool ko = there && p["ko"].Boolean;

            PzaWindow.Enable(b, N("pc", i));
            b.Text[N("pc", i)] = there ? pname : " ";
            b.Text[N("php", i)] = there ? Bar(p["hp"].Number, p["max"].Number) : " ";
            PzaWindow.Props(b, N("php", i), ("TextColour", there ? HpColour(p["hp"].Number, p["max"].Number) : "#8A9099"));
            string st = !there ? " " : ko ? "DOWN" : status == "choose" && i == turn ? "YOUR TURN" : p["guard"].Boolean ? "guarding" : p["tired"].Number > 0 ? "recovering" : " ";
            b.Text[N("pst", i)] = st;
            PzaWindow.Props(b, N("pst", i), ("TextColour", st == "YOUR TURN" ? "#5FD7AF" : st == "DOWN" ? "#FF6B6B" : "#C9B78F"));

            int at = i <= s.LiveIndexes.Count ? s.LiveIndexes[i - 1] : -1;
            if (there && at >= 0 && at < ids.Count) PzaPortrait.Face(b, N("pface", i), ids[at]);
            else PzaPortrait.Clear(b, N("pface", i));

            string pSpr = Path.Combine(ui, "spr_party_" + PartySpriteKey(pname, prace) + ".png");
            bool hasSpr = there && File.Exists(pSpr);
            bool isAtt = s_activeEv is { AttSide: "party" } ae && ae.AttSlot == i;
            bool isTgt = s_activeEv is { TgtSide: "party" } te && te.TgtSlot == i;
            int frIdx = ko ? 3 : (isAtt && evP is >= 0.25f and <= 0.68f) ? 2 : ((idleFrame + i) & 1);
            float alpha = !there ? 0f : ko ? 0.55f : (isTgt && inImpact && s_activeEv!.Kind == "hit" && ((int)(evP * 24) & 1) == 0) ? 0.45f : 1f;

            PzaWindow.Props(b, N("pspr", i),
                ("ImageTexture", hasSpr ? "file:" + pSpr : "file:" + Path.Combine(ui, "panel.png")),
                ("ImageRect", $"{frIdx * 64} 0 64 64"),
                ("Alpha", alpha.ToString("0.##", CultureInfo.InvariantCulture)));

            if (pageBounds is { } pb && there)
            {
                var (bx, by) = PartyPos[i - 1];
                double dx = (status == "choose" && i == turn && s_activeEv is null) ? -0.045 : 0;   // step forward on your turn!
                double dy = 0;
                if (isAtt && s_activeEv is { Kind: "hit" or "miss" } ev && ev.TgtSlot >= 1 && ev.TgtSlot <= Foes)
                {
                    var (tx, ty) = FoePos[ev.TgtSlot - 1];
                    dx = ((tx + 0.12) - bx) * dashT;
                    dy = (ty - by) * dashT;
                }
                else if (isTgt && inImpact && s_activeEv!.Kind == "hit")
                {
                    dx = 0.014 * Math.Sin(evP * 40.0);
                }
                b.Positions[N("pspr", i)] = StageRect(pb, bx + dx, by + dy, SpriteW, SpriteH);
            }
        }

        // Turn pointer arrow next to active party member
        bool showTurn = status == "choose" && turn >= 1 && turn <= Party && s_activeEv is null;
        b.Text["lbTurnArrow"] = showTurn ? "<<" : " ";
        PzaWindow.Props(b, "lbTurnArrow", ("TextColour", "#5FD7AF"));
        if (pageBounds is { } pb2 && showTurn)
        {
            var (px, py) = PartyPos[turn - 1];
            double bob = 0.008 * Math.Sin(s_clock.Elapsed.TotalSeconds * 7.0);
            b.Positions["lbTurnArrow"] = StageRect(pb2, px + SpriteW - 0.035 + bob, py + 0.04, 0.05, 0.045);
        }

        // Slash FX & rising floating damage/heal popup
        bool showSlash = s_activeEv is { Kind: "hit" } && inImpact;
        PzaWindow.Props(b, "fxSlash",
            ("ImageTexture", "file:" + Path.Combine(ui, File.Exists(Path.Combine(ui, "fx_slash.png")) ? "fx_slash.png" : "panel.png")),
            ("Alpha", showSlash ? "0.95" : "0"));

        if (s_activeEv is { } ev2 && evP >= 0.30f)
        {
            string pop = ev2.Kind switch
            {
                "hit" => ev2.Crit ? $"CRIT -{ev2.Dmg}!" : $"-{ev2.Dmg}",
                "miss" => "MISS",
                "heal" => $"+{ev2.Dmg}",
                "guard" => "GUARD",
                _ => " ",
            };
            string popCol = ev2.Kind switch
            {
                "hit" => ev2.Crit ? "#FFDE59" : "#FF6B6B",
                "heal" => "#5FD7AF",
                "guard" => "#9AE6FF",
                _ => "#E6E6E6",
            };
            b.Text["lbPop"] = pop;
            PzaWindow.Props(b, "lbPop", ("TextColour", popCol));
            if (pageBounds is { } pb3)
            {
                var (tx, ty) = ev2.TgtSide == "foe" && ev2.TgtSlot >= 1 && ev2.TgtSlot <= Foes ? FoePos[ev2.TgtSlot - 1]
                    : ev2.TgtSlot >= 1 && ev2.TgtSlot <= Party ? PartyPos[ev2.TgtSlot - 1]
                    : (0.45, 0.20);
                double rise = 0.065 * OutQuad((evP - 0.30f) / 0.70f);
                b.Positions["lbPop"] = StageRect(pb3, tx - 0.02, ty - 0.02 - rise, 0.14, 0.045);
                if (showSlash) b.Positions["fxSlash"] = StageRect(pb3, tx, ty, SpriteW, SpriteH);
            }
        }
        else
        {
            b.Text["lbPop"] = " ";
        }

        // Command buttons
        bool on = status == "choose";
        string Cap(string act, string text) => s.Pending == act ? "> " + text + " <" : text;
        b.Text["cmdAttack"] = Cap("attack", "ATTACK");
        b.Text["cmdHeavy"] = Cap("heavy", "HEAVY BLOW");
        b.Text["cmdGuard"] = "GUARD";
        b.Text["cmdBandage"] = Cap("bandage", $"BANDAGE ({v["bandages"].Number:0})");
        b.Text["cmdFlee"] = "FLEE";
        foreach (var c in new[] { "cmdAttack", "cmdHeavy", "cmdGuard", "cmdBandage", "cmdFlee" }) if (on) PzaWindow.Enable(b, c);
        PzaWindow.Tooltip(b, "cmdAttack", "A normal hit. Then click a foe.");
        PzaWindow.Tooltip(b, "cmdHeavy", "1.5 times the damage, a little harder to land; the fighter needs two turns to recover. Then click a foe.");
        PzaWindow.Tooltip(b, "cmdGuard", "Halves the damage this fighter takes until their next turn.");
        PzaWindow.Tooltip(b, "cmdBandage", "Heals a quarter of a fighter's health plus 5, from the squad's 3 bandages. Then click who.");
        PzaWindow.Tooltip(b, "cmdFlee", "Try to run: the faster your squad is than the foes, the better the chance. A failed try costs the turn.");

        b.Text["lbNote"] = s.Note ?? (on ? "Choose a move." : status == "none" ? "Pick a fight below." : "Pick a new fight below.");
        PzaWindow.Props(b, "lbNote", ("TextColour", s.Note is null ? "#C9B78F" : "#F0B45A"));
        var log = v["log"].Table ?? [];
        for (int i = 1; i <= LogRows; i++)
        {
            int at = log.Count - LogRows + i;
            b.Text[N("log", i)] = at >= 1 ? v["log"][at].String ?? " " : " ";
            PzaWindow.Props(b, N("log", i), ("TextColour", i == LogRows ? "#E6E6E6" : "#9AA3AD"));
        }
        foreach (var (n, t) in new[] { ("newHungry", "5 HUNGRY BANDITS"), ("newDust", "DUST BANDITS"), ("newBeak", "A BEAK THING") })
        {
            PzaWindow.Enable(b, n);
            b.Text[n] = t;
        }
    }

    /// <summary>The sprite/portrait key a foe's name uses.</summary>
    public static string Portrait(string name) => name.ToLowerInvariant() switch
    {
        var n when n.Contains("hungry") => "hungry",
        var n when n.Contains("dust boss") => "dustboss",
        var n when n.Contains("dust") => "dust",
        var n when n.Contains("beak") => "beak",
        _ => "unknown",
    };

    static void Event(MyGuiEvent e)
    {
        var s = Session;
        if (s is null) return;
        string a = e.Action.Length > 0 ? e.Action : e.Widget;
        switch (a)
        {
            case "cmdAttack": s.Command("attack"); return;
            case "cmdHeavy": s.Command("heavy"); return;
            case "cmdGuard": s.Command("guard"); return;
            case "cmdBandage": s.Command("bandage"); return;
            case "cmdFlee": s.Command("flee"); return;
            case "newHungry": Start("hungry"); return;
            case "newDust": Start("dust"); return;
            case "newBeak": Start("beak"); return;
        }
        if (a.StartsWith("foe", StringComparison.Ordinal) && int.TryParse(a.AsSpan(3), out int fi)) s.Target("foe", fi);
        else if (a.StartsWith("pc", StringComparison.Ordinal) && int.TryParse(a.AsSpan(2), out int pi)) s.Target("party", pi);
    }

    const string Help =
        "An animated Final Fantasy-style turn-based battle, by PZA, played by a Lua script (scripts/fight.lua in this mod's folder: edit it and start a new fight to try your change).\n\n" +
        "Foes line up on the left and your squad on the right over the Kenshi desert stage. On your turn the active fighter steps forward: click ATTACK or HEAVY BLOW and then click a foe, BANDAGE and click a squadmate, GUARD, or FLEE. Fighters dash across the arena with slash effects and floating damage numbers.\n\n" +
        "Your squad is your selection and active squad (up to four standing). Without a game loaded it is Beep, Ruka and Sadneil. Nothing happens to your world: KO, not death.\n\n" +
        "Open it with /battle in the terminal (Shift+Y), /battle dust or /battle beak, or from F10 > Mods. F7 hides it with the HUD; drag the title to move it.";
}
