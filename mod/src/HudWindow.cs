// MeitouMoonscript (Author: PZA, Licence: GPLv3 or later): declarative Lua HUD overlay window (`ui.hud`).
// Transparent, non-blocking overlay that opens automatically when any Lua pack places HUD elements and closes when empty.
// Auto-hides with F7 via PzaUi; every slot is positioned dynamically in screen fractions (0..1).
using System.Globalization;
using Meitou.Data.Ui;
using PZA.MeitouUi;
using PZA.Moonscript.Repl;

namespace PZA.Moonscript;

public static class HudWindow
{
    public const string Id = "pza.moonscript.hud";
    public static PzaWindow Window = null!;
    public static HudState State { get; } = new();

    static string SlotName(int i) => "hud" + i.ToString(CultureInfo.InvariantCulture);

    static string LayoutXml()
    {
        var parts = new List<string>(HudState.MaxTotal);
        for (int i = 0; i < HudState.MaxTotal; i++)
            parts.Add($"<Widget type=\"TextBox\" skin=\"Kenshi_TextboxStandardText\" position_real=\"0 0 0.01 0.01\" name=\"{SlotName(i)}\">"
                + "<Property key=\"TextAlign\" value=\"Center VCenter\"/>"
                + "<Property key=\"NeedMouse\" value=\"false\"/>"
                + "</Widget>");
        return "<MyGUI type=\"Layout\"><Widget type=\"Widget\" skin=\"PanelEmpty\" position_real=\"0 0 1 1\" layer=\"Back\" name=\"HudRoot\">"
            + "<Property key=\"NeedMouse\" value=\"false\"/>"
            + string.Concat(parts)
            + "</Widget></MyGUI>";
    }

    public static void Setup()
    {
        Window = new PzaWindow(Id, LayoutXml) { Bind = Bind };
        PzaUi.Add(Window);
    }

    public static void EachFrame()
    {
        if (Window is null) return;
        bool hasItems = State.Snapshot().Count > 0;
        if (hasItems && !Window.IsOpen) Window.Open();
        else if (!hasItems && Window.IsOpen) Window.Close();
    }

    static void Bind(UiBindings b)
    {
        var items = State.Snapshot();
        var vp = PzaUi.Host?.LastFrame.FirstOrDefault(f => f.Id == Id)?.Controls.FirstOrDefault(c => c.Name == "HudRoot")?.Bounds;
        float vw = vp is { Width: > 1 } r ? r.Width : 1920f;
        float vh = vp is { Height: > 1 } r2 ? r2.Height : 1080f;
        for (int i = 0; i < HudState.MaxTotal; i++)
        {
            string name = SlotName(i);
            if (i < items.Count)
            {
                var it = items[i];
                b.Text[name] = it.Text.Length == 0 ? " " : it.Text;
                PzaWindow.Props(b, name, ("TextColour", it.Color), ("Alpha", "1"));
                b.Positions[name] = new UiRect((float)(it.X * vw), (float)(it.Y * vh), (float)(it.W * vw), (float)(it.H * vh));
            }
            else
            {
                b.Text[name] = " ";
                PzaWindow.Props(b, name, ("Alpha", "0"));
                b.Positions[name] = new UiRect(0, 0, 1, 1);
            }
        }
    }
}
