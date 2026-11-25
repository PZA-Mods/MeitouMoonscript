// PZA's Meitou UI. Author: PZA. Licence: GPLv3 or later.
// A character's own face in a PZA window: the LIVE character (its own head, look and gear, recruits included), drawn by Meitou's portrait
// renderer exactly as the squad bar draws it, into a cell of Meitou's portrait atlas, shown by an ImageBox through the window's binding.
//
// How (MeitouClient 0.20.1, unchanged to clone 5108032):
//   Each frame the host fills the renderer's portrait requests (squad bar, trade rows, conversation, code mods' record portraits), then
//   GameHost.GatherPortraitKeys(renderer) collects their keys, so even a character out of reach is posed. A prefix on that call adds our requests
//   there, so both renderers (Vulkan and Metal: both call it) draw them.
//   Cells: the atlas's record cells (32..63). Meitou hands record portraits the LOWEST free ones, so we take the HIGHEST that no request uses this
//   frame; a face whose cell another request (Meitou's or another PZA mod's copy of this library) takes moves to the next free one, and the
//   window shows the new cell from its next bind. A cell drawn over is drawn again when its owner asks for it.
// Limits: a character not in the world's snapshot (not loaded) shows nothing; at most MaxFaces faces at once per mod; the picture appears
//   once the renderer has drawn it (a frame or two). If Meitou lacks a member, Install logs "no faces" and windows work without them.
//
//   in the layout:  PzaLayout.Portrait("Face0", 0.03, 0.03, 0.2, 0.3)
//   in Bind:        PzaPortrait.Face(b, "Face0", characterId);   // or PzaPortrait.Clear(b, "Face0")
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Characters;
using Meitou.Simulation;

namespace PZA.MeitouUi;

public static class PzaPortrait
{
    /// <summary>How many different faces one mod shows at once (the atlas has 32 record cells, shared with Meitou and other mods).</summary>
    public const int MaxFaces = 12;
    /// <summary>A face not bound for this many frames gives its cell up.</summary>
    const int KeepFrames = 10;

    /// <summary>Whether faces work in this game (Install resolved and patched everything for them).</summary>
    public static bool Available { get; private set; }

    sealed class Wanted { public int Cell; public long LastBound; }
    static readonly Dictionary<CharacterId, Wanted> s_faces = [];
    static readonly object s_lock = new();
    static long s_frame;
    static int s_moves;

    // Meitou's members (looked up by name; internal types)
    static MethodInfo? s_gather, s_portraitKey, s_portraitSerial;
    static PropertyInfo? s_requests, s_portraitBar, s_portraitSize;
    static int s_cell = 256, s_columns = 16, s_firstRecordCell = 32, s_cells = 64;

    /// <summary>Shows <paramref name="id"/>'s face on the ImageBox <paramref name="name"/> (made by PzaLayout.Portrait). Call it in Bind.</summary>
    public static void Face(UiBindings b, string name, CharacterId id)
    {
        if (!Available || id.IsNone) { Clear(b, name); return; }
        int cell;
        lock (s_lock)
        {
            if (!s_faces.TryGetValue(id, out var w))
            {
                if (s_faces.Count >= MaxFaces) DropOldest();
                if (s_faces.Count >= MaxFaces) { Clear(b, name); return; }
                w = new Wanted { Cell = FreeCell(null) };
                s_faces[id] = w;
            }
            w.LastBound = s_frame;
            cell = w.Cell;
        }
        if (cell < 0) { Clear(b, name); return; }
        PzaWindow.Props(b, name, ("ImageTexture", "meitou:portraits"), ("ImageFlipV", "true"), ("ImageRect", Rect(cell)));
    }

    /// <summary>An empty portrait frame (the binding is new each frame, so this only takes back a Face set earlier in the same Bind).</summary>
    public static void Clear(UiBindings b, string name)
    {
        if (b.Properties.TryGetValue(name, out var old) && old.ContainsKey("ImageTexture"))
            b.Properties[name] = old.Where(kv => kv.Key is not ("ImageTexture" or "ImageFlipV" or "ImageRect")).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>The atlas rectangle of a cell, as Meitou's PortraitBar.PortraitRect gives it (the drawn square in the cell's corner).</summary>
    public static string Rect(int cell)
    {
        int size = s_cell / 2;
        if (PzaUi.Player is { } p && s_portraitBar?.GetValue(p) is { } bar && s_portraitSize?.GetValue(bar) is int s && s > 0) size = s;
        int x = cell % s_columns * s_cell, y = cell / s_columns * s_cell;
        return FormattableString.Invariant($"{x} {y} {size} {size}");
    }

    static void DropOldest()
    {
        var old = s_faces.Where(kv => s_frame - kv.Value.LastBound > 0).OrderBy(kv => kv.Value.LastBound).FirstOrDefault();
        if (old.Value is not null) s_faces.Remove(old.Key);
    }

    /// <summary>The highest record cell that no request in <paramref name="taken"/> and none of our faces uses; -1 when none is free.</summary>
    static int FreeCell(HashSet<int>? taken)
    {
        for (int c = s_cells - 1; c >= s_firstRecordCell; c--)
            if ((taken is null || !taken.Contains(c)) && !s_faces.Values.Any(w => w.Cell == c)) return c;
        return -1;
    }

    /// <summary>Resolves Meitou's portrait members and patches GameHost.GatherPortraitKeys; false (and a line) when something is missing.</summary>
    internal static bool Install(Harmony harmony)
    {
        var missing = new List<string>();
        var game = typeof(Meitou.Game.Ui.MyGuiScreenHost).Assembly;
        var host = game.GetType("Meitou.Game.GameHost");
        var renderer = game.GetType("Meitou.Game.Rendering.Characters.CharacterRenderer");
        if (host is null || renderer is null) missing.Add("GameHost / CharacterRenderer");
        else
        {
            s_gather = AccessTools.Method(host, "GatherPortraitKeys", [renderer]);
            s_portraitKey = AccessTools.Method(host, "PortraitKey", [typeof(CharacterId)]);
            s_requests = AccessTools.Property(renderer, "PortraitRequests");
            if (s_gather is null) missing.Add("GameHost.GatherPortraitKeys");
            if (s_portraitKey is null) missing.Add("GameHost.PortraitKey");
            if (s_requests?.PropertyType != typeof(List<PortraitRequest>)) missing.Add("CharacterRenderer.PortraitRequests");
            s_cell = Const(renderer, "PortraitCell", 256);
            s_columns = Const(renderer, "PortraitColumns", 16);
            s_firstRecordCell = Const(renderer, "LivePortraitCells", 32);
            s_cells = s_columns * Const(renderer, "PortraitRows", 4);
        }
        s_portraitBar = AccessTools.Property(Access.PlayerInterface, "PortraitBar");
        var barType = s_portraitBar?.PropertyType;
        s_portraitSize = barType is null ? null : AccessTools.Property(barType, "PortraitSize");
        s_portraitSerial = barType is null ? null : AccessTools.Method(barType, "PortraitSerial", [typeof(CharacterId)]);
        if (s_portraitBar is null || s_portraitSerial is null) missing.Add("PlayerInterface.PortraitBar.PortraitSerial");
        if (missing.Count > 0)
        {
            PzaUi.Log("PZA's Meitou UI: no character faces, the game lacks " + string.Join("; ", missing));
            return false;
        }
        harmony.Patch(s_gather, prefix: new HarmonyMethod(AccessTools.Method(typeof(PzaPortrait), nameof(GatherPrefix))));
        Available = true;
        return true;
    }

    static int Const(Type t, string name, int fallback) =>
        t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is { IsLiteral: true } f && f.GetRawConstantValue() is int v ? v : fallback;

    static int s_logged;

    static void GatherPrefix(object __instance, object? characters)
    {
        if (characters is null || s_requests?.GetValue(characters) is not List<PortraitRequest> requests) return;
        try { AddRequests(requests, id => (long)s_portraitKey!.Invoke(__instance, [id])!, Serial); }
        catch (Exception e) { Guard.Fault("faces", "portrait requests", e); }
    }

    static uint Serial(CharacterId id) =>
        PzaUi.Player is { } p && s_portraitBar?.GetValue(p) is { } bar && s_portraitSerial?.Invoke(bar, [id]) is uint s ? s : 0;

    /// <summary>Adds our faces to this frame's requests (public for the offline check, which passes its own key and serial).</summary>
    public static void AddRequests(List<PortraitRequest> requests, Func<CharacterId, long> key, Func<CharacterId, uint> serial)
    {
        lock (s_lock)
        {
            s_frame++;
            foreach (var gone in s_faces.Where(kv => s_frame - kv.Value.LastBound > KeepFrames).Select(kv => kv.Key).ToList()) s_faces.Remove(gone);
            if (s_faces.Count == 0 || Guard.Dead("faces")) return;
            // a frame's requests can be gathered twice (both host paths); ours are added once
            var taken = new HashSet<int>();
            foreach (var r in requests) taken.Add(r.Cell);
            foreach (var (id, w) in s_faces)
            {
                long k = key(id);
                if (requests.Any(r => r.Cell == w.Cell && r.Key == k)) continue;
                if (w.Cell < 0 || taken.Contains(w.Cell))
                {
                    int was = w.Cell;
                    w.Cell = -1;
                    w.Cell = FreeCell(taken);
                    if (s_moves++ < 5) PzaUi.Log($"PZA's Meitou UI: a face moved from atlas cell {was} to {w.Cell} (the old one is in use)");
                    if (w.Cell < 0) continue;
                }
                requests.Add(new PortraitRequest(k, w.Cell, serial(id)));
                taken.Add(w.Cell);
            }
            if (s_logged++ == 0) PzaUi.Log($"PZA's Meitou UI: first face request ({s_faces.Count} face(s), cells {string.Join(",", s_faces.Values.Select(v => v.Cell.ToString(CultureInfo.InvariantCulture)))})");
        }
    }

    /// <summary>For the offline check: the cell a face holds (-1 = none).</summary>
    public static int CellOf(CharacterId id) { lock (s_lock) return s_faces.TryGetValue(id, out var w) ? w.Cell : -1; }

    /// <summary>For the offline check: forget every face.</summary>
    public static void Reset() { lock (s_lock) { s_faces.Clear(); s_frame = 0; } }
}
