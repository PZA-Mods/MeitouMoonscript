// PZA's Meitou UI. Author: PZA. Licence: GPLv3 or later.
// The "tiny button" of the PZA UI rules (6: a small form one click away, adjustable opacity), as a HUD piece on Meitou's documented mod UI
// (ModContext.AddUi, the hud.overlay slot), because a MyGUI window cannot be that small or faint. Lifted from PZA's SlowMo for Meitou 0.1.3, where
// every part was Confirmed in game (2026-11-16): drag it anywhere (place saved as a fraction of the screen), click = Click on a release
// without a drag (Meitou fires a button's action on the PRESS, so the button has none and the click is taken from the pointer calls), mouse
// wheel over it = opacity 0.35 .. 1 (a wheel there no longer zooms the camera), an optional "+" beside it (OpenFull). Meitou gives a press,
// move, release and wheel over the interface to PlayerInterface.MouseDown / MouseMove / MouseUp / UiScroll and keeps them out of the control
// tick's input: PzaUi patches those four when a tiny button is added.
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Game.Ui;

namespace PZA.MeitouUi;

public sealed class PzaTinyButton(string id)
{
    public string Id { get; } = id;
    /// <summary>The caption, read every frame (e.g. "slow 0.30x").</summary>
    public Func<string> Caption { get; set; } = () => "";
    /// <summary>Shown now (e.g. only while the full window is closed).</summary>
    public Func<bool> Visible { get; set; } = () => true;
    /// <summary>A click (press and release without a drag).</summary>
    public Action? Click { get; set; }
    /// <summary>A second click within 400 ms and 6 px of the first (0.2.4; PZA UI rules: a double click on the tiny button opens the full
    /// window, as the RE_Kenshi plugins). Runs instead of the second Click; the first Click has already run, so the handler undoes it if
    /// it changes something (null: every click is a Click).</summary>
    public Action? DoubleClick { get; set; }
    /// <summary>The "+" beside it (null: no "+").</summary>
    public Action? OpenFull { get; set; }
    /// <summary>The top-left as a fraction of the screen (kept on screen); default the top-right corner, out of the HUD's way.</summary>
    public Func<Vector2> LoadPlace { get; set; } = () => new(1f, 0.06f);
    public Action<Vector2>? SavePlace { get; set; }
    public Func<double> LoadOpacity { get; set; } = () => 1;
    public Action<double>? SaveOpacity { get; set; }
    /// <summary>Sizes at 1080p (scaled with the screen height in steps of 0.25).</summary>
    public float Width { get; set; } = 120;
    public float PlusWidth { get; set; } = 30;
    public float Height { get; set; } = 30;

    // what the last layout drew (screen pixels), for the pointer calls on the same thread
    /// <summary>What the last layout drew (screen pixels): read by the pointer calls on the same thread (public for offline checks).</summary>
    public UiRect? ButtonRect { get; internal set; }
    public UiRect? AllRect { get; internal set; }
    public UiRect Viewport { get; internal set; }
    public Vector2 Size { get; internal set; }
    public Vector2? DragPos { get; internal set; }

    bool _dragging, _moved;
    Vector2 _grab, _pressAt, _lastClickAt;
    long _lastClickMs = long.MinValue / 2;
    int _logs;

    static bool Inside(UiRect r, Vector2 m) => m.X >= r.X && m.Y >= r.Y && m.X < r.X + r.Width && m.Y < r.Y + r.Height;

    public void PointerDown(MouseButton button, Vector2 m)
    {
        if (button != MouseButton.Left || _dragging || ButtonRect is not { } r || !Inside(r, m)) return;
        _dragging = true; _moved = false; _pressAt = m;
        _grab = m - new Vector2(r.X, r.Y);
    }

    public void PointerMove(Vector2 m)
    {
        if (!_dragging) return;
        if (!_moved && Vector2.Distance(m, _pressAt) > 4) _moved = true;
        if (_moved) DragPos = m - _grab;
    }

    public void PointerUp(MouseButton button, Vector2 m)
    {
        if (button != MouseButton.Left || !_dragging) return;
        PointerMove(m);
        _dragging = false;
        if (!_moved)
        {
            long now = Environment.TickCount64;
            bool dbl = DoubleClick is not null && now - _lastClickMs < 400 && Vector2.Distance(m, _lastClickAt) < 6;
            _lastClickMs = dbl ? long.MinValue / 2 : now;
            _lastClickAt = m;
            if (dbl)
            {
                if (++_logs <= 20) PzaUi.Log($"PZA's Meitou UI: tiny button '{Id}' double-clicked");
                DoubleClick!.Invoke();
            }
            else Click?.Invoke();
            return;
        }
        if (DragPos is { } raw && Viewport.Width > 0 && Viewport.Height > 0)
        {
            var p = new Vector2(Math.Clamp(raw.X, 0, Math.Max(0, Viewport.Width - Size.X)), Math.Clamp(raw.Y, 0, Math.Max(0, Viewport.Height - Size.Y)));
            var frac = new Vector2(p.X / Viewport.Width, p.Y / Viewport.Height);
            SavePlace?.Invoke(frac);
            if (++_logs <= 20) PzaUi.Log($"PZA's Meitou UI: tiny button '{Id}' moved to {p.X:0},{p.Y:0} ({frac.X * 100:0.#}%, {frac.Y * 100:0.#}%)");
        }
        _moved = false;
        DragPos = null;
    }

    /// <summary>A wheel notch over the button (or its +): opacity in 0.1 steps; true = ours.</summary>
    public bool Scroll(Vector2 m, float steps)
    {
        if (steps == 0 || !Visible() || AllRect is not { } t || !Inside(t, m)) return false;
        double now = LoadOpacity();
        double a = Math.Clamp(Math.Round((now + (steps > 0 ? 0.1 : -0.1)) / 0.05) * 0.05, 0.35, 1);
        if (Math.Abs(a - now) > 1e-9)
        {
            SaveOpacity?.Invoke(a);
            if (++_logs <= 20) PzaUi.Log($"PZA's Meitou UI: tiny button '{Id}' opacity {a:0.00}");
        }
        return true;
    }

    /// <summary>The top-left from a fraction of the screen (or a drag), kept fully on screen.</summary>
    public static Vector2 Place(UiRect viewport, Vector2 frac, Vector2 size, Vector2? drag)
    {
        float x = drag?.X ?? viewport.Width * Math.Clamp(frac.X, 0, 1);
        float y = drag?.Y ?? viewport.Height * Math.Clamp(frac.Y, 0, 1);
        return new(MathF.Round(Math.Clamp(x, 0, Math.Max(0, viewport.Width - size.X))), MathF.Round(Math.Clamp(y, 0, Math.Max(0, viewport.Height - size.Y))));
    }
}

/// <summary>The mod's HUD extension: draws its tiny buttons (Meitou rejects two extensions with one id, so the id carries the mod's).</summary>
public sealed class PzaHud(string modId) : IUiExtension
{
    public string Id { get; } = "pzaui-" + new string(modId.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    readonly string[] _styles = new string[14];
    string _plus = "";

    /// <summary>Layout scale from the screen height: 1080p = 1, steps of 0.25, 1 to 3 (PZA UI rules 7).</summary>
    public static float Scale(float height) => Math.Clamp(MathF.Round(height / 1080f * 4) / 4, 1, 3);

    public void Register(UiRegistration r)
    {
        for (int i = 0; i < _styles.Length; i++)
        {
            float a = 0.35f + 0.05f * i;   // no skin: a texture skin would ignore the alpha
            _styles[i] = r.RegisterStyle("tiny" + i, new UiStyle { TextAlign = "Center", Background = new Vector4(0.16f, 0.15f, 0.13f, 0.9f * a),
                Hover = new Vector4(0.24f, 0.22f, 0.18f, 0.95f * a), Text = new Vector4(0.92f, 0.92f, 0.88f, a), TextPadding = new UiInsets(4, 2, 4, 2) });
        }
        // one "+" action for every tiny button: the node's Argument names the button
        _plus = r.RegisterAction("plus", ctx =>
        {
            var t = PzaUi.TinyButtons.FirstOrDefault(x => x.Id == ctx.Argument);
            if (t is not null) Guard.Run(t.Id, "plus", () => t.OpenFull?.Invoke());
        });
        string control = r.RegisterControl("tiny", Build);
        r.Contribute(UiSlots.Overlay, c => c.CreateControl(control));
    }

    UiNode Build(UiFrameContext c)
    {
        var root = new UiNode(Id + ".root") { Placement = UiPlacement.Fill, Pickable = false };
        float k = Scale(c.Viewport.Height);
        bool inGame = PzaUi.InGame;   // never on the title screen
        foreach (var t in PzaUi.TinyButtons)
        {
            t.ButtonRect = t.AllRect = null;
            if (!inGame) continue;
            t.Viewport = c.Viewport;
            if (Guard.Dead(t.Id)) continue;
            bool visible = false;
            Guard.Run(t.Id, "visible", () => visible = t.Visible());
            if (!visible) continue;
            float w = MathF.Round(t.Width * k), pw = t.OpenFull is null ? 0 : MathF.Round(t.PlusWidth * k), gap = pw > 0 ? MathF.Round(4 * k) : 0, h = MathF.Round(t.Height * k);
            t.Size = new(w + gap + pw, h);
            var at = PzaTinyButton.Place(c.Viewport, t.LoadPlace(), t.Size, t.DragPos);
            string style = _styles[Math.Clamp((int)Math.Round((t.LoadOpacity() - 0.35) / 0.05), 0, _styles.Length - 1)];
            string caption = "";
            Guard.Run(t.Id, "caption", () => caption = t.Caption());
            // no action: the click is taken on the release (PzaTinyButton.PointerUp)
            root.Children.Add(new UiNode(Id + "." + t.Id, UiNodeKind.Button) { Style = style, Text = caption, Placement = new UiPlacement(w, h, at.X, at.Y) });
            if (pw > 0)
                root.Children.Add(new UiNode(Id + "." + t.Id + ".plus", UiNodeKind.Button) { Style = style, Text = "+", Action = _plus, Argument = t.Id,
                    Placement = new UiPlacement(pw, h, at.X + w + gap, at.Y) });
            t.ButtonRect = new UiRect(at.X, at.Y, w, h);
            t.AllRect = new UiRect(at.X, at.Y, t.Size.X, h);
        }
        return root;
    }
}
