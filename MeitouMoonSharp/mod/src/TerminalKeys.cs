// MeitouMoonscript. Author: PZA. Licence: GPLv3 or later.
// Shift+Y opens / closes the REPL window: a postfix on InputBindings.Resolve(TickInput) sees the control tick's keys; a fresh press of Y with Shift held (no Ctrl / Alt; never while a text box has
// the keyboard, so typing a capital Y in the terminal itself never closes it) is ours. Plain Y stays Meitou's (ToggleCrafting in
// PlayerInterface.WindowHotkey): prefixes on PlayerInterface.UiKey / WindowHotkey and GameHotkeys.Key skip the game's handler only for Shift+Y.
// Shift+Y: the only Shift + letter no PZA Meitou mod uses (2026-11-22; the Direct Control letters are never used).
using HarmonyLib;
using Meitou.Game.Host.Input;

namespace PZA.Moonscript;

public static class ReplKeys
{
    public static long Fired, Blocked;
    public static Action? Pressed;
    public static Action<int>? OnKeyDown;
    static bool s_uiShift;

    static readonly (Key Key, int Vk)[] s_forwardKeys =
    [
        (Key.A, 0x41), (Key.B, 0x42), (Key.C, 0x43), (Key.D, 0x44), (Key.E, 0x45), (Key.F, 0x46), (Key.G, 0x47),
        (Key.H, 0x48), (Key.I, 0x49), (Key.J, 0x4A), (Key.K, 0x4B), (Key.L, 0x4C), (Key.M, 0x4D), (Key.N, 0x4E),
        (Key.O, 0x4F), (Key.P, 0x50), (Key.Q, 0x51), (Key.R, 0x52), (Key.S, 0x53), (Key.T, 0x54), (Key.U, 0x55),
        (Key.V, 0x56), (Key.W, 0x57), (Key.X, 0x58), (Key.Y, 0x59), (Key.Z, 0x5A),
        (Key.D0, 0x30), (Key.D1, 0x31), (Key.D2, 0x32), (Key.D3, 0x33), (Key.D4, 0x34),
        (Key.D5, 0x35), (Key.D6, 0x36), (Key.D7, 0x37), (Key.D8, 0x38), (Key.D9, 0x39),
        (Key.Space, 0x20), (Key.Tab, 0x09), (Key.Escape, 0x1B),
    ];

    public static List<string> Install(Harmony h)
    {
        var lines = new List<string>();
        var game = typeof(global::Meitou.Game.Ui.MyGuiScreenHost).Assembly;
        Try(h, lines, AccessTools.Method(typeof(InputBindings), "Resolve", [typeof(TickInput)]), nameof(ResolvePostfix), false, "InputBindings.Resolve (Shift+Y)");
        var hotkeys = game.GetType("Meitou.Game.Ui.GameHotkeys");
        Try(h, lines, hotkeys is null ? null : AccessTools.Method(hotkeys, "Key", [typeof(Key), typeof(bool)]), nameof(KeyPrefix), true, "GameHotkeys.Key (plain Y stays Meitou's)");
        var pi = game.GetType("Meitou.Game.PlayerInterface");
        TryBoth(h, lines, pi is null ? null : AccessTools.Method(pi, "UiKey", [typeof(Key), typeof(bool), typeof(bool)]), nameof(UiKeyPrefix), nameof(UiKeyPostfix), "PlayerInterface.UiKey (Shift state for WindowHotkey)");
        Try(h, lines, pi is null ? null : AccessTools.Method(pi, "WindowHotkey", [typeof(Key)]), nameof(WindowHotkeyPrefix), true, "PlayerInterface.WindowHotkey (plain Y stays Crafting)");
        return lines;
    }

    static void Try(Harmony h, List<string> lines, System.Reflection.MethodInfo? target, string ours, bool prefix, string what)
    {
        if (target is null) { lines.Add("MISSING " + what + ": the hotkey is off (the debug menu still opens the terminal)"); return; }
        try
        {
            var hm = new HarmonyMethod(AccessTools.Method(typeof(ReplKeys), ours));
            if (prefix) h.Patch(target, prefix: hm);
            else h.Patch(target, postfix: hm);
            lines.Add("patched " + what);
        }
        catch (Exception e)
        {
            lines.Add($"MISSING {what}: the patch failed ({e.GetType().Name})");
        }
    }

    static void TryBoth(Harmony h, List<string> lines, System.Reflection.MethodInfo? target, string pre, string post, string what)
    {
        if (target is null) { lines.Add("MISSING " + what + ": the hotkey is off (the debug menu still opens the terminal)"); return; }
        try
        {
            h.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ReplKeys), pre)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ReplKeys), post)));
            lines.Add("patched " + what);
        }
        catch (Exception e)
        {
            lines.Add($"MISSING {what}: the patch failed ({e.GetType().Name})");
        }
    }

    /// <summary>Whether PlayerInterface.WindowHotkey must stand down so Shift+Y reaches InputBindings.Resolve (plain Y stays Crafting; Ctrl+Shift+Y stays Meitou's).</summary>
    public static bool ShouldBlockWindowHotkey(Key key, bool shift, bool ctrl) => shift && !ctrl && key == Key.Y;

    static void UiKeyPrefix(bool ctrl, bool shift) => s_uiShift = shift && !ctrl;
    static void UiKeyPostfix() => s_uiShift = false;

    static bool WindowHotkeyPrefix(Key key, ref bool __result)
    {
        if (!ShouldBlockWindowHotkey(key, s_uiShift, false)) return true;
        Blocked++;
        __result = false;
        return false;
    }

    static void ResolvePostfix(TickInput input)
    {
        try
        {
            if (PZA.MeitouUi.PzaUi.Typing) return;
            bool shift = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);
            bool ctrlAlt = input.IsDown(Key.LeftControl) || input.IsDown(Key.RightControl) || input.IsDown(Key.LeftAlt) || input.IsDown(Key.RightAlt);
            if (shift && !ctrlAlt && input.WasPressed(Key.Y))
            {
                Fired++;
                Pressed?.Invoke();
                return;
            }
            if (OnKeyDown is { } kd)
            {
                foreach (var (k, vk) in s_forwardKeys)
                    if (input.WasPressed(k)) kd(vk);
            }
        }
        catch (Exception e)
        {
            HostMod.Line("keys: " + e.Message);
        }
    }

    static bool KeyPrefix(Key key, bool shift)
    {
        if (!(shift && key == Key.Y)) return true;
        Blocked++;
        return false;
    }
}
