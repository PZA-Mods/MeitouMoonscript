// MeitouMoonscript (Author: PZA, Licence: GPLv3 or later): declarative Lua HUD overlay window (`ui.hud` & KenshiLua MyGUI).
// Transparent overlay that opens automatically when any Lua pack or KenshiLua mod places HUD elements (images, text, bars, or buttons) and closes when empty.
// Auto-hides with F7 via PzaUi; every slot is positioned dynamically in screen fractions (0..1).
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using PZA.MeitouUi;
using PZA.Moonscript.Repl;

namespace PZA.Moonscript;

public static class HudWindow
{
    public const string Id = "pza.moonscript.hud";
    public static PzaWindow Window = null!;
    public static HudState State { get; } = new();
    public static int ViewWidth { get; private set; } = 1920;
    public static int ViewHeight { get; private set; } = 1080;
    /// <summary>Called when a clickable HUD button is pressed: (packId, buttonId).</summary>
    public static Action<string, string>? OnButtonClick;
    static IReadOnlyList<HudItem> s_boundButtons = [];

    static string ImgName(int i) => "hudImg" + i.ToString(CultureInfo.InvariantCulture);
    static string SlotName(int i) => "hud" + i.ToString(CultureInfo.InvariantCulture);
    static string BtnName(int i) => "hudBtn" + i.ToString(CultureInfo.InvariantCulture);

    static string LayoutXml()
    {
        var parts = new List<string>(HudState.MaxTotalImages + HudState.MaxTotal + HudState.MaxButtons);
        // Images first so atlas sprites/bars draw behind text labels
        for (int i = 0; i < HudState.MaxTotalImages; i++)
            parts.Add($"<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0 0 0.001 0.001\" name=\"{ImgName(i)}\">"
                + "<Property key=\"NeedMouse\" value=\"false\"/>"
                + "<Property key=\"Alpha\" value=\"0\"/>"
                + "</Widget>");
        for (int i = 0; i < HudState.MaxTotal; i++)
            parts.Add($"<Widget type=\"TextBox\" skin=\"Kenshi_TextboxStandardText\" position_real=\"0 0 0.01 0.01\" name=\"{SlotName(i)}\">"
                + "<Property key=\"TextAlign\" value=\"Center VCenter\"/>"
                + "<Property key=\"NeedMouse\" value=\"false\"/>"
                + "</Widget>");
        for (int i = 0; i < HudState.MaxButtons; i++)
            parts.Add($"<Widget type=\"Button\" skin=\"Kenshi_Button1\" position_real=\"0 0 0.01 0.01\" name=\"{BtnName(i)}\"/>");
        return "<MyGUI type=\"Layout\"><Widget type=\"Widget\" skin=\"PanelEmpty\" position_real=\"0 0 1 1\" layer=\"Back\" name=\"HudRoot\">"
            + "<Property key=\"NeedMouse\" value=\"false\"/>"
            + string.Concat(parts)
            + "</Widget></MyGUI>";
    }

    public static void Setup()
    {
        Window = new PzaWindow(Id, LayoutXml) { Bind = Bind, Event = Event };
        PzaUi.Add(Window);
    }

    public static void EachFrame()
    {
        if (Window is null) return;
        bool hasItems = State.HasAny;
        if (hasItems && !Window.IsOpen) Window.Open();
        else if (!hasItems && Window.IsOpen) Window.Close();
    }

    static void Bind(UiBindings b)
    {
        var imgs = State.ImageSnapshot();
        var texts = State.TextSnapshot();
        var btns = State.ButtonSnapshot();
        s_boundButtons = btns;
        var vp = PzaUi.Host?.LastFrame.FirstOrDefault(f => f.Id == Id)?.Controls.FirstOrDefault(c => c.Name == "HudRoot")?.Bounds;
        float vw = vp is { Width: > 1 } r ? r.Width : 1920f;
        float vh = vp is { Height: > 1 } r2 ? r2.Height : 1080f;
        ViewWidth = Math.Max(320, (int)MathF.Round(vw));
        ViewHeight = Math.Max(200, (int)MathF.Round(vh));

        for (int i = 0; i < HudState.MaxTotalImages; i++)
        {
            string name = ImgName(i);
            if (i < imgs.Count)
            {
                var it = imgs[i];
                string alphaStr = it.Alpha.ToString("0.###", CultureInfo.InvariantCulture);
                if (it.Rect.Length > 0)
                    PzaWindow.Props(b, name, ("ImageTexture", it.Texture), ("ImageRect", it.Rect), ("Colour", it.Color), ("Alpha", alphaStr));
                else
                    PzaWindow.Props(b, name, ("ImageTexture", it.Texture), ("Colour", it.Color), ("Alpha", alphaStr));
                b.Positions[name] = new UiRect((float)(it.X * vw), (float)(it.Y * vh), Math.Max(1f, (float)(it.W * vw)), Math.Max(1f, (float)(it.H * vh)));
            }
            else
            {
                PzaWindow.Props(b, name, ("Alpha", "0"));
                b.Positions[name] = new UiRect(0, 0, 1, 1);
            }
        }
        for (int i = 0; i < HudState.MaxTotal; i++)
        {
            string name = SlotName(i);
            if (i < texts.Count)
            {
                var it = texts[i];
                b.Text[name] = it.Text.Length == 0 ? " " : it.Text;
                PzaWindow.Props(b, name, ("TextColour", it.Color), ("Alpha", it.Alpha.ToString("0.###", CultureInfo.InvariantCulture)), ("TextAlign", it.Align));
                b.Positions[name] = new UiRect((float)(it.X * vw), (float)(it.Y * vh), Math.Max(1f, (float)(it.W * vw)), Math.Max(1f, (float)(it.H * vh)));
            }
            else
            {
                b.Text[name] = " ";
                PzaWindow.Props(b, name, ("Alpha", "0"));
                b.Positions[name] = new UiRect(0, 0, 1, 1);
            }
        }
        for (int i = 0; i < HudState.MaxButtons; i++)
        {
            string name = BtnName(i);
            if (i < btns.Count)
            {
                var it = btns[i];
                PzaWindow.Enable(b, name);
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

    static void Event(MyGuiEvent e)
    {
        if (e.Widget is null || !e.Widget.StartsWith("hudBtn", StringComparison.Ordinal)) return;
        if (!int.TryParse(e.Widget[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) return;
        var btns = s_boundButtons;
        if (idx >= 0 && idx < btns.Count)
            OnButtonClick?.Invoke(btns[idx].OwnerPack, btns[idx].Id);
    }
}
