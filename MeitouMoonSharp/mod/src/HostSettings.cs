// MeitouMoonscript. Author: PZA. Licence: GPLv3 or later.
// The Settings > Mods group (MeitouClient docs/modding.md "Settings"), also listed by the terminal's /config. Window places are kept by
// PZA's Meitou UI's PzaSettings.Keep in pza.moonscript.settings.json.
using Meitou.Modding;

namespace PZA.Moonscript;

public sealed class HostSettings
{
    [Setting("Typing animation", Hint = "The console types its answers out a few letters at a time. Off: answers appear at once.")]
    public bool Typing { get; set; } = true;

    [Setting("Typing speed", Hint = "Letters a frame while the console types (1 slow, 40 fast).", Min = 1, Max = 40, Step = 1)]
    public double TypingSpeed { get; set; } = 6;

    [Setting("Banner", Hint = "The console's title banner when it opens.")]
    public bool Banner { get; set; } = true;

    [Setting("Console budget per call", Hint = "Thousands of Lua instructions one console line may run before it is stopped (1 to 2000).", Min = 1, Max = 2000, Step = 1)]
    public double ReplBudgetK { get; set; } = 200;

    [Setting("Window place across", Hint = "Where you left the console (a fraction of the screen from its first place). Drag its title to move it.", Min = -1, Max = 1, Step = 0.001)]
    public double WindowPlaceX { get; set; }

    [Setting("Window place down", Hint = "As above, down.", Min = -1, Max = 1, Step = 0.001)]
    public double WindowPlaceY { get; set; }

    [Setting("Battle window across", Hint = "Where you left the battle window (a fraction of the screen from its first place).", Min = -1, Max = 1, Step = 0.001)]
    public double BattlePlaceX { get; set; }

    [Setting("Battle window down", Hint = "As above, down.", Min = -1, Max = 1, Step = 0.001)]
    public double BattlePlaceY { get; set; }

    [Setting("Auto-start third-party packs", Hint = "Automatically start Lua packs found in other mods' moonscript/ folders. Off: start them yourself with /start <pack>.")]
    public bool AutoStartThirdPartyPacks { get; set; } = false;

    [Setting("Auto-start KenshiLua mods", Hint = "Meitou before 0.24.0 only: start KenshiLua mods enabled in data/mods.cfg. Meitou 0.24.0 and later run them itself, so this layer stays off there.")]
    public bool AutoStartKenshiLua { get; set; } = true;
}
