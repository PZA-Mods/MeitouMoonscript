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
    static bool s_uiShift;

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
            bool shift = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);
            bool ctrlAlt = input.IsDown(Key.LeftControl) || input.IsDown(Key.RightControl) || input.IsDown(Key.LeftAlt) || input.IsDown(Key.RightAlt);
            if (!shift || ctrlAlt || PZA.MeitouUi.PzaUi.Typing) return;
            if (input.WasPressed(Key.Y)) { Fired++; Pressed?.Invoke(); }
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
