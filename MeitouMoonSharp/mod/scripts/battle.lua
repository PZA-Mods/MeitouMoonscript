-- MeitouMoonscript: Kenshi-themed Final Fantasy-style ATB terminal auto-battler (Slice 2: live squad snapshot).
-- Author: PZA. Licence: GPLv3 or later.
--
-- Usage in the Moonscript terminal (Shift+Y):
--   /run battle               fight the default encounter ("5 Hungry Bandits") using your live squad
--   battle("dust")            Dust Bandit Raid (Border Zone)
--   battle("beak")            Elder Beak Thing (Bonefields)
--   battle("holy")            Holy Nation Patrol (Okran's Pride)
--   battle("fog")             Fogmen Swarm (Fog Islands)
--   battle("catlon")          Mad Cat-Lon & Stormthralls (Ashlands, endgame)
--   battle("squad")           inspect your live squad's stats & gear from the game snapshot
--   battle("list")            list encounters and current squad mode
--   squad("Beep", "Ruka")     use a custom lore squad (up to 4 names)
--   squad("lore")             use the iconic lore squad (selected + Ruka, Beep, Sadneil)
--   squad("live") / squad()   return to live game squad (falls back to lore squad if no world loaded)
--
-- Pure Lua 5.2, zero external dependencies, seeded math.random only, read-only (changes nothing in the world).
-- Uses a lightweight ECS-style entity list (tiny-ecs pattern), plain-data unit tactics, and a single transient
-- coroutine beat stream (discarded on return: zero retained coroutine memory in LiveSize).

local function clamp(v, lo, hi)
  if v < lo then return lo elseif v > hi then return hi else return v end
end

local function pick(list)
  return list[math.random(1, #list)]
end

local function bar(cur, maxv, width)
  local ratio = clamp(cur / math.max(1, maxv), 0, 1)
  local fill = math.floor(ratio * width + 0.5)
  return string.rep("#", fill) .. string.rep(".", width - fill)
end

-- ---------------------------------------------------------------------------
-- Weapon profiler (derives cut/blunt/cleave/style from a live Kenshi weapon name + grade + stats)
-- ---------------------------------------------------------------------------

local function weapon_grade_label(lvl)
  if lvl >= 95 then return "Meitou"
  elseif lvl >= 75 then return "Edge"
  elseif lvl >= 50 then return "Mk"
  elseif lvl >= 30 then return "Catun"
  elseif lvl >= 15 then return "Refitted"
  elseif lvl > 0 then return "Rusted"
  else return "" end
end

local function profile_weapon(name, lvl, ranged, str, dex, ma)
  local low = string.lower(name or "unarmed")
  local grade_mult = 0.72 + clamp(lvl or 0, 0, 100) * 0.0085  -- Rusted ~0.8x, Catun ~1.0x, Edge ~1.4x, Meitou ~1.57x
  local str_m = 1 + clamp(str or 1, 0, 120) * 0.012
  local dex_m = 1 + clamp(dex or 1, 0, 120) * 0.012

  if ranged then
    return { cut = math.floor(13 * grade_mult + 0.5), blunt = 2, cleave = 1, ranged = true, tactic = "skirmisher", style = "ranged" }
  end
  if low == "unarmed" or low == "" then
    local base = 6 + clamp(ma or 1, 0, 120) * 0.34 + clamp(str or 1, 0, 120) * 0.14
    return { cut = math.floor(base * 0.25 + 0.5), blunt = math.floor(base * 0.75 + 0.5), cleave = 1, ranged = false, tactic = "skirmisher", style = "martial" }
  end
  if low:find("plank") or low:find("fragment") or low:find("falling sun") or low:find("exile") then
    return { cut = math.floor(7 * grade_mult * dex_m + 0.5), blunt = math.floor(15 * grade_mult * str_m + 0.5), cleave = 2, ranged = false, tactic = "bruiser", style = "heavy" }
  end
  if low:find("polearm") or low:find("naginata") or low:find("staff") or low:find("glaive") then
    return { cut = math.floor(10 * grade_mult * dex_m + 0.5), blunt = math.floor(8 * grade_mult * str_m + 0.5), cleave = 2, ranged = false, tactic = "balanced", style = "polearm" }
  end
  if low:find("paladin") or low:find("cleaver") or low:find("chopper") or low:find("combat cleaver") or low:find("flesh") then
    local tac = low:find("paladin") and "paladin" or "bruiser"
    return { cut = math.floor(12 * grade_mult * dex_m + 0.5), blunt = math.floor(10 * grade_mult * str_m + 0.5), cleave = low:find("paladin") and 2 or 1, ranged = false, tactic = tac, style = "hacker" }
  end
  if low:find("nodachi") or low:find("katana") or low:find("wakizashi") or low:find("ninja") or low:find("topper") then
    return { cut = math.floor(14 * grade_mult * dex_m + 0.5), blunt = math.floor(3 * grade_mult * str_m + 0.5), cleave = low:find("nodachi") and 2 or 1, ranged = false, tactic = "skirmisher", style = "katana" }
  end
  if low:find("club") or low:find("stick") or low:find("jitte") or low:find("mace") or low:find("hammer") then
    return { cut = math.floor(2 * grade_mult + 0.5), blunt = math.floor(13 * grade_mult * str_m + 0.5), cleave = 1, ranged = false, tactic = "bruiser", style = "blunt" }
  end
  -- Sabres & general blades
  return { cut = math.floor(12 * grade_mult * dex_m + 0.5), blunt = math.floor(6 * grade_mult * str_m + 0.5), cleave = 1, ranged = false, tactic = "balanced", style = "sabre" }
end

-- ---------------------------------------------------------------------------
-- Lore templates: fallback companions & iconic Kenshi encounters
-- ---------------------------------------------------------------------------

local COMPANIONS = {
  ruka = {
    name = "Ruka", race = "Shek", weapon = "Plank",
    hp = 125, atk = 24, def = 20, tough = 28, dex = 13, med = 8,
    cut = 6, blunt = 16, cleave = 2, bleed_res = 0.85, armour_mit = 0.14,
    tactic = "bruiser",
    barks = { "Come, flatskin!", "For Krat! Stand and fight!", "A Shek does not yield!" },
  },
  beep = {
    name = "Beep", race = "Hiver", weapon = "Ninja Blade",
    hp = 78, atk = 19, def = 15, tough = 12, dex = 24, med = 6,
    cut = 14, blunt = 3, cleave = 1, bleed_res = 1.15, armour_mit = 0.06,
    tactic = "skirmisher",
    barks = { "BEEP!", "Beep is the strongest warrior!", "Beep will not fall!" },
  },
  sadneil = {
    name = "Sadneil", race = "Skeleton", weapon = "Polearm",
    hp = 150, atk = 22, def = 24, tough = 30, dex = 15, med = 26,
    cut = 10, blunt = 10, cleave = 2, bleed_res = 0.0, armour_mit = 0.12,
    tactic = "medic",
    barks = { "We are all going to die.", "Pointless. Still, I swing.", "Patching you up. Again." },
  },
  hobbs = {
    name = "Hobbs", race = "Greenlander", weapon = "Sabre",
    hp = 96, atk = 18, def = 19, tough = 16, dex = 17, med = 14,
    cut = 12, blunt = 6, cleave = 1, bleed_res = 1.0, armour_mit = 0.10,
    tactic = "balanced",
    barks = { "Ever hear of the Banshee?!", "Watch the flank!", "Back in my day..." },
  },
  kang = {
    name = "Kang", race = "Shek", weapon = "Fragment Axe",
    hp = 130, atk = 26, def = 21, tough = 26, dex = 12, med = 6,
    cut = 5, blunt = 19, cleave = 2, bleed_res = 0.85, armour_mit = 0.18,
    tactic = "bruiser",
    barks = { "Weaklings.", "Taste iron.", "Is that all?" },
  },
  burn = {
    name = "Burn", race = "Skeleton", weapon = "Paladin's Cross",
    hp = 160, atk = 25, def = 25, tough = 32, dex = 14, med = 20,
    cut = 11, blunt = 13, cleave = 2, bleed_res = 0.0, armour_mit = 0.16,
    tactic = "medic",
    barks = { "Seen worse in the Floodlands.", "Hold the line.", "Systems nominal." },
  },
}

local ENCOUNTERS = {
  hungry = {
    title = "5 Hungry Bandits",
    zone = "Border Zone",
    intro = "\"Drop your food, drifter! We haven't eaten in days!\"",
    foes = {
      { name = "Hungry Leader", race = "Greenlander", weapon = "Iron Club",  hp = 68, atk = 11, def = 9,  tough = 10, dex = 14, med = 4, cut = 2,  blunt = 10, cleave = 1, bleed_res = 1.0, tactic = "swarm" },
      { name = "Hungry Bandit", race = "Greenlander", weapon = "Iron Stick", hp = 56, atk = 9,  def = 7,  tough = 8,  dex = 15, med = 2, cut = 1,  blunt = 8,  cleave = 1, bleed_res = 1.0, tactic = "swarm" },
      { name = "Hungry Bandit", race = "Greenlander", weapon = "Iron Stick", hp = 54, atk = 9,  def = 7,  tough = 8,  dex = 14, med = 2, cut = 1,  blunt = 8,  cleave = 1, bleed_res = 1.0, tactic = "swarm" },
      { name = "Starving Drifter", race = "Scorchlander", weapon = "Iron Stick", hp = 52, atk = 8, def = 6, tough = 7, dex = 16, med = 2, cut = 1, blunt = 8, cleave = 1, bleed_res = 1.0, tactic = "swarm" },
      { name = "Starving Drifter", race = "Greenlander", weapon = "Iron Stick", hp = 50, atk = 8, def = 6, tough = 7, dex = 15, med = 2, cut = 1, blunt = 7, cleave = 1, bleed_res = 1.0, tactic = "swarm" },
    },
  },
  dust = {
    title = "Dust Bandit Raid",
    zone = "The Hub Road",
    intro = "\"Cats or your skull - Dust King gets his cut either way!\"",
    foes = {
      { name = "Dust Boss",   race = "Greenlander",  weapon = "Horse Chopper", hp = 105, atk = 23, def = 20, tough = 22, dex = 17, med = 10, cut = 14, blunt = 7,  cleave = 1, bleed_res = 1.0, armour_mit = 0.15, tactic = "bruiser" },
      { name = "Dust Bowman", race = "Scorchlander", weapon = "Toothpick",     hp = 80,  atk = 20, def = 13, tough = 14, dex = 22, med = 8,  cut = 13, blunt = 2,  cleave = 1, bleed_res = 1.0, armour_mit = 0.08, tactic = "skirmisher", ranged = true },
      { name = "Dust Bandit", race = "Greenlander",  weapon = "Spiked Club",   hp = 88,  atk = 18, def = 16, tough = 17, dex = 16, med = 6,  cut = 7,  blunt = 11, cleave = 1, bleed_res = 1.0, armour_mit = 0.10, tactic = "balanced" },
      { name = "Dust Bandit", race = "Greenlander",  weapon = "Horse Chopper", hp = 86,  atk = 18, def = 15, tough = 16, dex = 16, med = 6,  cut = 11, blunt = 5,  cleave = 1, bleed_res = 1.0, armour_mit = 0.10, tactic = "balanced" },
    },
  },
  beak = {
    title = "Elder Beak Thing",
    zone = "Bonefields",
    intro = "A long neck rises over the ridge. Relax. Enjoy death.",
    foes = {
      { name = "Elder Beak Thing", race = "Gutter", weapon = "Crushing Beak", hp = 360, atk = 34, def = 22, tough = 40, dex = 26, med = 0, cut = 16, blunt = 18, cleave = 3, bleed_res = 0.7, armour_mit = 0.05, tactic = "beast" },
    },
  },
  holy = {
    title = "Holy Nation Patrol",
    zone = "Okran's Pride",
    intro = "\"Halt! Show us the Holy Flame, or answer to Okran's steel!\"",
    foes = {
      { name = "High Paladin", race = "Greenlander", weapon = "Paladin's Cross", hp = 128, atk = 28, def = 26, tough = 28, dex = 16, med = 14, cut = 14, blunt = 14, cleave = 2, bleed_res = 0.9, armour_mit = 0.24, tactic = "paladin" },
      { name = "Paladin",      race = "Greenlander", weapon = "Paladin's Cross", hp = 112, atk = 23, def = 22, tough = 22, dex = 16, med = 12, cut = 12, blunt = 11, cleave = 1, bleed_res = 0.9, armour_mit = 0.20, tactic = "paladin" },
      { name = "Holy Servant", race = "Greenlander", weapon = "Iron Staff",      hp = 72,  atk = 13, def = 12, tough = 12, dex = 17, med = 10, cut = 3,  blunt = 9,  cleave = 1, bleed_res = 1.0, armour_mit = 0.06, tactic = "medic" },
    },
  },
  fog = {
    title = "Fogmen Swarm",
    zone = "Fog Islands",
    intro = "WREEEEE! Pale shapes pour out of the blue mist...",
    foes = {
      { name = "Fog Prince", race = "Fogman", weapon = "Katana",     hp = 105, atk = 27, def = 22, tough = 20, dex = 25, med = 0, cut = 18, blunt = 4,  cleave = 1, bleed_res = 1.1, tactic = "skirmisher" },
      { name = "Fog Heavy",  race = "Fogman", weapon = "Iron Club",  hp = 92,  atk = 19, def = 17, tough = 20, dex = 18, med = 0, cut = 4,  blunt = 14, cleave = 1, bleed_res = 1.1, tactic = "bruiser" },
      { name = "Fogman",     race = "Fogman", weapon = "Iron Stick", hp = 56,  atk = 11, def = 9,  tough = 10, dex = 20, med = 0, cut = 2,  blunt = 8,  cleave = 1, bleed_res = 1.2, tactic = "swarm" },
      { name = "Fogman",     race = "Fogman", weapon = "Iron Stick", hp = 54,  atk = 11, def = 9,  tough = 10, dex = 20, med = 0, cut = 2,  blunt = 8,  cleave = 1, bleed_res = 1.2, tactic = "swarm" },
      { name = "Fogman",     race = "Fogman", weapon = "Iron Stick", hp = 54,  atk = 10, def = 8,  tough = 10, dex = 19, med = 0, cut = 2,  blunt = 8,  cleave = 1, bleed_res = 1.2, tactic = "swarm" },
    },
  },
  catlon = {
    title = "Mad Cat-Lon & Thralls",
    zone = "Ashlands Dome",
    intro = "\"WREEEEE! I was not the monster!\" (Endgame: 65+ stats)",
    foes = {
      { name = "Mad Cat-Lon", race = "Skeleton", weapon = "Falling Sun (Meitou)", hp = 260, atk = 78, def = 74, tough = 82, dex = 46, med = 0, cut = 26, blunt = 24, cleave = 3, bleed_res = 0.0, armour_mit = 0.34, tactic = "bruiser" },
      { name = "Stormthrall", race = "Skeleton", weapon = "Iron Club",            hp = 160, atk = 40, def = 38, tough = 45, dex = 22, med = 0, cut = 4,  blunt = 16, cleave = 1, bleed_res = 0.0, armour_mit = 0.16, tactic = "balanced" },
      { name = "Stormthrall", race = "Skeleton", weapon = "Iron Club",            hp = 160, atk = 40, def = 38, tough = 45, dex = 22, med = 0, cut = 4,  blunt = 16, cleave = 1, bleed_res = 0.0, armour_mit = 0.16, tactic = "balanced" },
    },
  },
}

local ENCOUNTER_ORDER = { "hungry", "dust", "beak", "holy", "fog", "catlon" }

-- ---------------------------------------------------------------------------
-- Mini ECS unit factory (plain tables: stats, status, ATB, tactics)
-- ---------------------------------------------------------------------------

local function make_unit(tpl, side, tag)
  return {
    name       = tag and (tpl.name .. " " .. tag) or tpl.name,
    short      = tpl.name,
    race       = tpl.race or "Greenlander",
    robot      = tpl.robot or (tpl.bleed_res == 0.0),
    weapon     = tpl.weapon or "Katana",
    style      = tpl.style or "blade",
    side       = side,
    hp         = tpl.hp,
    max_hp     = tpl.max_hp or tpl.hp,
    bleed      = tpl.bleed or 0,
    atk        = tpl.atk,
    def        = tpl.def,
    tough      = tpl.tough,
    dex        = math.max(8, tpl.dex),
    med        = tpl.med or 8,
    cut        = tpl.cut or 10,
    blunt      = tpl.blunt or 6,
    cleave     = tpl.cleave or 1,
    bleed_res  = tpl.bleed_res or 1.0,
    armour_mit = tpl.armour_mit or 0.0,
    ranged     = tpl.ranged or false,
    tactic     = tpl.tactic or "balanced",
    barks      = tpl.barks,
    atb        = math.random(10, 45),
    guard      = false,
    ko         = tpl.ko or false,
    grit_used  = false,
    dmg_done   = 0,
    kos        = 0,
  }
end

local function make_unit_from_snap(f)
  local prof = profile_weapon(f.weapon, f.weapon_level, f.ranged, f.str, f.dex, f.ma)
  local unarmed = (prof.style == "martial")
  local eff_atk = unarmed and math.max(f.atk, f.ma) or f.atk
  local eff_def = unarmed and math.max(f.def, f.dodge) or f.def
  local eff_med = f.robot and math.max(f.med, f.robotics) or f.med
  local armour_mit = clamp((f.armour or 0) * 0.045 + (f.armour_level or 0) * 0.0022, 0, 0.42)
  local wlabel = f.weapon
  local grade = weapon_grade_label(f.weapon_level or 0)
  if not unarmed and grade ~= "" and not string.lower(wlabel):find(string.lower(grade), 1, true) then
    wlabel = wlabel .. " [" .. grade .. "]"
  elseif unarmed then
    wlabel = "Martial Arts"
  end
  local barks = nil
  local low = string.lower(f.name)
  if COMPANIONS[low] then barks = COMPANIONS[low].barks end
  if not barks then
    if f.robot then barks = { "Optimal strike.", "Target disabled.", "Oil pressure nominal." }
    elseif string.lower(f.race):find("shek") then barks = { "Glory in battle!", "Yield or break!" }
    elseif string.lower(f.race):find("hive") then barks = { "For the squad!", "Too fast for you!" }
    else barks = { "Stay down!", "Not today, wasteland.", "Keep moving!" } end
  end
  return make_unit({
    name       = f.name,
    race       = f.race,
    robot      = f.robot,
    weapon     = wlabel,
    style      = prof.style,
    hp         = math.max(1, math.floor((f.hp or 100) + 0.5)),
    max_hp     = math.max(10, math.floor((f.max_hp or 100) + 0.5)),
    bleed      = math.floor((f.bleeding or 0) * 3 + 0.5),
    atk        = math.max(1, f.atk and eff_atk or 1),
    def        = math.max(1, eff_def or 1),
    tough      = math.max(1, f.tough or 1),
    dex        = clamp(10 + (f.dex or 1) * 0.55, 10, 48),
    med        = eff_med or 1,
    cut        = prof.cut,
    blunt      = prof.blunt,
    cleave     = prof.cleave,
    bleed_res  = f.robot and 0.0 or (string.lower(f.race):find("shek") and 0.85 or 1.0),
    armour_mit = armour_mit,
    ranged     = prof.ranged,
    tactic     = eff_med >= 20 and "medic" or prof.tactic,
    barks      = barks,
  }, "ally")
end

local function make_leader(name)
  local key = string.lower(name)
  if COMPANIONS[key] then return make_unit(COMPANIONS[key], "ally") end
  return make_unit({
    name = name, race = "Drifter", weapon = "Katana",
    hp = 105, atk = 21, def = 19, tough = 19, dex = 19, med = 16,
    cut = 14, blunt = 5, cleave = 1, bleed_res = 1.0, armour_mit = 0.10,
    tactic = "balanced",
    barks = { "Hold steady!", "Watch the legs!", "Not today, wasteland." },
  }, "ally")
end

-- Modes: "live" (default: use world.squad() when present, else lore fallback), "lore", or custom name list
_G._battle_mode   = _G._battle_mode or "live"
_G._battle_roster = _G._battle_roster or nil

local function get_live_squad()
  if type(world) ~= "table" or type(world.squad) ~= "function" then return {} end
  local raw = world.squad() or {}
  local living = {}
  for i = 1, #raw do
    if raw[i] and not raw[i].down and (raw[i].hp or 0) > 0 then
      living[#living + 1] = raw[i]
      if #living >= 4 then break end
    end
  end
  return living
end

local function build_party()
  if _G._battle_mode == "custom" and _G._battle_roster and #_G._battle_roster > 0 then
    local p = {}
    for i = 1, math.min(4, #_G._battle_roster) do
      p[#p + 1] = make_leader(_G._battle_roster[i])
    end
    return p, "Custom"
  end
  if _G._battle_mode ~= "lore" then
    local live = get_live_squad()
    if #live > 0 then
      local p = {}
      for i = 1, #live do p[#p + 1] = make_unit_from_snap(live[i]) end
      return p, "Live Squad"
    end
  end
  local lead_name = (type(selected) == "table" and selected.name and selected.name()) or "Drifter"
  local lead_low = string.lower(lead_name)
  local party = { make_leader(lead_name) }
  local defaults = { "ruka", "beep", "sadneil" }
  for _, k in ipairs(defaults) do
    local pick_k = (k == lead_low) and "hobbs" or k
    party[#party + 1] = make_unit(COMPANIONS[pick_k], "ally")
  end
  return party, "Lore Squad"
end

local function build_foes(enc)
  local counts, seen, out = {}, {}, {}
  for _, f in ipairs(enc.foes) do counts[f.name] = (counts[f.name] or 0) + 1 end
  for _, f in ipairs(enc.foes) do
    seen[f.name] = (seen[f.name] or 0) + 1
    local tag = counts[f.name] > 1 and ("#" .. seen[f.name]) or nil
    out[#out + 1] = make_unit(f, "foe", tag)
  end
  return out
end

-- ---------------------------------------------------------------------------
-- Combat systems (ECS-style helpers operating on the units list)
-- ---------------------------------------------------------------------------

local function alive_on(units, side)
  local res = {}
  for _, u in ipairs(units) do
    if u.side == side and not u.ko then res[#res + 1] = u end
  end
  return res
end

local function lowest_hp_ratio(list)
  local best, best_r = nil, 2.0
  for _, u in ipairs(list) do
    local r = u.hp / u.max_hp
    if r < best_r then best, best_r = u, r end
  end
  return best, best_r
end

local function sys_bleed(u, emit)
  if u.ko or u.bleed <= 0 then return end
  local tick = math.max(1, math.floor(u.bleed + 0.5))
  u.hp = u.hp - tick
  u.bleed = math.max(0, u.bleed - 1.2)
  if u.hp <= 0 then
    u.hp = 0
    u.ko = true
    emit(string.format("  ! %s collapses from blood loss!", u.name), 3)
  end
end

local function apply_hit(att, def, mult, emit)
  local block_chance = clamp(0.14 + (def.def - att.atk) * 0.014 + (def.guard and 0.30 or 0), 0.05, 0.72)
  if att.ranged then block_chance = block_chance * 0.35 end
  if math.random() < block_chance then
    return "block", 0, false
  end

  local scale = (0.85 + math.random() * 0.35) * clamp(1 + (att.atk - def.tough * 0.45) * 0.015, 0.45, 2.4) * mult
  if att.tactic == "paladin" and def.robot then scale = scale * 1.30 end
  local cut_d   = math.max(0, math.floor(att.cut * scale + 0.5))
  local blunt_d = math.max(0, math.floor(att.blunt * scale + 0.5))
  local raw     = math.max(2, cut_d + blunt_d)
  local total   = math.max(1, math.floor(raw * (1 - def.armour_mit) + 0.5))
  if def.guard then total = math.max(1, math.floor(total * 0.6)) end

  def.hp = def.hp - total
  att.dmg_done = att.dmg_done + total
  if cut_d >= 6 and def.bleed_res > 0 then
    def.bleed = def.bleed + math.floor(cut_d * 0.25 * def.bleed_res * (1 - def.armour_mit))
  end

  local ko_now = false
  if def.hp <= 0 then
    if not def.grit_used and def.tough >= 22 and math.random() < clamp(def.tough * 0.010, 0.15, 0.55) then
      def.grit_used = true
      def.hp = math.max(4, math.floor(def.max_hp * 0.12))
      emit(string.format("  + %s grits through the pain! (%d HP)", def.name, def.hp), 3)
    else
      def.hp = 0
      def.ko = true
      att.kos = att.kos + 1
      ko_now = true
    end
  end
  return "hit", total, ko_now
end

local function sys_act(u, units, emit)
  u.guard = false
  local enemy_side = (u.side == "ally") and "foe" or "ally"
  local allies = alive_on(units, u.side)
  local foes   = alive_on(units, enemy_side)
  if #foes == 0 then return end

  -- 1. First Aid / Robotics repair tactic
  if u.med >= 12 then
    local patient, ratio = lowest_hp_ratio(allies)
    if patient and (patient.bleed >= 5 or ratio < 0.42) then
      local heal = math.floor(clamp(u.med, 12, 80) * (0.65 + math.random() * 0.45))
      local old_b = patient.bleed
      patient.hp = math.min(patient.max_hp, patient.hp + heal)
      patient.bleed = 0
      local tgt = (patient == u) and "self" or patient.name
      local verb = patient.robot and "repairs" or "bandages"
      local note = old_b >= 5 and " (bleed stopped)" or ""
      emit(string.format("  + %s %s %s +%d HP%s", u.name, verb, tgt, heal, note), 2)
      return
    end
  end

  -- 2. Defensive stance
  if u.tactic ~= "beast" and u.tactic ~= "bruiser" and (u.hp / u.max_hp) < 0.28 and math.random() < 0.45 then
    u.guard = true
    emit(string.format("  [ %s braces with %s!", u.name, u.weapon), 1)
    return
  end

  -- 3. Target selection
  local targets = {}
  if u.cleave > 1 and #foes > 1 then
    local n = math.min(u.cleave, #foes)
    local pool = {}
    for i = 1, #foes do pool[i] = foes[i] end
    for _ = 1, n do
      local idx = math.random(1, #pool)
      targets[#targets + 1] = table.remove(pool, idx)
    end
  elseif u.tactic == "skirmisher" then
    local w = lowest_hp_ratio(foes)
    targets[1] = w or pick(foes)
  else
    targets[1] = pick(foes)
  end

  -- 4. Resolve strike
  if #targets == 1 then
    local t = targets[1]
    local kind, dmg, ko = apply_hit(u, t, 1.0, emit)
    local mark = (u.side == "ally") and ">" or "<"
    local verb = u.ranged and "shoots" or (u.style == "martial" and "kicks" or "hits")
    if kind == "block" then
      local dodge_word = t.style == "martial" and "dodges!" or "parries!"
      emit(string.format("  %s %s swings at %s -> %s %s", mark, u.name, t.name, t.name, dodge_word), 1)
    elseif ko then
      local bark = (u.barks and math.random() < 0.45) and (" \"" .. pick(u.barks) .. "\"") or ""
      emit(string.format("  %s %s KOs %s (-%d)!%s", mark, u.name, t.name, dmg, bark), 4)
    else
      emit(string.format("  %s %s %s %s [%s] -%d (%d/%d)", mark, u.name, verb, t.name, u.weapon, dmg, t.hp, t.max_hp), 2)
    end
  else
    local hits, kos_n, tot = 0, 0, 0
    for _, t in ipairs(targets) do
      local kind, dmg, ko = apply_hit(u, t, 0.82, emit)
      if kind == "hit" then hits = hits + 1; tot = tot + dmg end
      if ko then kos_n = kos_n + 1 end
    end
    local mark = (u.side == "ally") and ">" or "<"
    if hits == 0 then
      emit(string.format("  %s %s sweeps %s -> all blocked!", mark, u.name, u.weapon), 1)
    else
      local ko_str = kos_n > 0 and string.format(" (%d KO!)", kos_n) or ""
      local noun = hits == 1 and "foe" or "foes"
      emit(string.format("  %s %s CLEAVES %d %s (%s) -%d total%s", mark, u.name, hits, noun, u.weapon, tot, ko_str), kos_n > 0 and 4 or 3)
    end
  end
end

-- ---------------------------------------------------------------------------
-- Squad inspector & encounter runner
-- ---------------------------------------------------------------------------

local function show_squad()
  local raw = (type(world) == "table" and type(world.squad) == "function") and (world.squad() or {}) or {}
  local cats = (type(world) == "table" and type(world.cats) == "function") and world.cats() or nil
  if #raw == 0 then
    print("== SQUAD SNAPSHOT (no live characters: using Lore Squad) ==")
    local p = build_party()
    for _, u in ipairs(p) do
      print(string.format("  %-10s %-11s HP %3d/%-3d ATK %2d DEF %2d %s", u.short:sub(1, 10), u.race:sub(1, 11), u.hp, u.max_hp, u.atk, u.def, u.weapon))
    end
    return
  end
  local cstr = cats and string.format(" | %d cats", math.floor(cats)) or ""
  print(string.format("== LIVE SQUAD SNAPSHOT (%d shown%s) ==", #raw, cstr))
  for i = 1, #raw do
    local f = raw[i]
    local st = f.down and "DOWN" or string.format("%3d/%-3d", math.floor(f.hp + 0.5), math.floor(f.max_hp + 0.5))
    local w = f.weapon
    if f.weapon_level and f.weapon_level > 0 then w = w .. "(" .. f.weapon_level .. ")" end
    print(string.format("  %-9s %-10s HP %-7s A%2d D%2d T%2d %s",
      f.name:sub(1, 9), f.race:sub(1, 10), st, math.floor(f.atk + 0.5), math.floor(f.def + 0.5), math.floor(f.tough + 0.5), w:sub(1, 18)))
  end
  print("Mode: " .. _G._battle_mode .. "  (use squad(\"lore\") or squad(\"live\") to switch)")
end

local function run_encounter(key)
  local enc_key = string.lower(key or "hungry")
  if enc_key == "squad" or enc_key == "inspect" then
    show_squad()
    return
  end
  if enc_key == "list" then
    print("== KENSHI AUTO-BATTLER: ENCOUNTERS ==")
    for _, k in ipairs(ENCOUNTER_ORDER) do
      local e = ENCOUNTERS[k]
      print(string.format("  battle(\"%-6s\")  %-22s [%s]", k, e.title, e.zone))
    end
    local p, mode = build_party()
    local names = {}
    for _, u in ipairs(p) do names[#names + 1] = u.short .. "(" .. u.weapon .. ")" end
    print("Party [" .. mode .. "]: " .. table.concat(names, ", "))
    print("Commands: battle(\"squad\") | squad(\"lore\") | squad(\"live\")")
    return
  end

  local enc = ENCOUNTERS[enc_key]
  if not enc then
    print("Unknown encounter '" .. tostring(key) .. "'. Try battle(\"list\").")
    return
  end

  local allies, mode = build_party()
  local foes = build_foes(enc)
  local units = {}
  for _, u in ipairs(allies) do units[#units + 1] = u end
  for _, u in ipairs(foes)   do units[#units + 1] = u end

  -- Transient coroutine: yields { text, prio } beats; discarded cleanly on return
  local co = coroutine.create(function()
    local function emit(text, prio)
      coroutine.yield({ text = text, prio = prio or 1 })
    end

    if enc_key == "holy" then
      for _, u in ipairs(allies) do
        if u.robot or string.lower(u.race):find("hive") or string.lower(u.race):find("shek") then
          emit("  ! Paladin glares at " .. u.name .. ": \"Darkness walks among you!\"", 4)
          break
        end
      end
    end

    local turn, max_turns = 0, 90
    while turn < max_turns do
      if #alive_on(units, "ally") == 0 or #alive_on(units, "foe") == 0 then break end
      local ready = nil
      for _ = 1, 20 do
        for _, u in ipairs(units) do
          if not u.ko then
            u.atb = u.atb + u.dex
            if u.atb >= 100 and (ready == nil or u.atb > ready.atb) then
              ready = u
            end
          end
        end
        if ready then break end
      end
      if not ready then break end

      turn = turn + 1
      ready.atb = ready.atb - 100
      sys_bleed(ready, emit)
      if not ready.ko then
        sys_act(ready, units, emit)
      end
    end
    return turn
  end)

  local beats, turns = {}, 0
  while coroutine.status(co) ~= "dead" do
    local ok, val = coroutine.resume(co)
    if not ok then error(val) end
    if type(val) == "table" then
      beats[#beats + 1] = val
    elseif type(val) == "number" then
      turns = val
    end
  end

  local max_beats = 9
  local shown = {}
  if #beats <= max_beats then
    shown = beats
  else
    local keep = { [1] = true, [#beats] = true }
    local count = 2
    for prio = 4, 1, -1 do
      for i = 2, #beats - 1 do
        if count >= max_beats then break end
        if not keep[i] and beats[i].prio == prio then
          keep[i] = true
          count = count + 1
        end
      end
      if count >= max_beats then break end
    end
    for i = 1, #beats do
      if keep[i] then shown[#shown + 1] = beats[i] end
    end
  end

  local h, d = world.hour(), world.day()
  local stamp = (h and d)
    and string.format("Day %d %02d:%02d", d, math.floor(h), math.floor((h % 1) * 60))
    or "Wasteland Sim"

  print(string.format("== %s | %s (%s) ==", string.upper(enc.title), mode, stamp))
  print(enc.intro)
  for _, b in ipairs(shown) do
    print(b.text)
  end

  local a_up = #alive_on(units, "ally")
  local f_up = #alive_on(units, "foe")
  local verdict = (f_up == 0)
    and string.format("VICTORY in %d turns! (%d/%d standing)", turns, a_up, #allies)
    or (a_up == 0)
      and string.format("WIPED OUT in %d turns! (Toughness +1...)", turns)
      or string.format("STALEMATE after %d turns.", turns)
  print("-- " .. verdict .. " --")

  for i = 1, #allies, 2 do
    local left = allies[i]
    local right = allies[i + 1]
    local ltxt = string.format("%-9s [%s] %3d/%-3d", left.short:sub(1, 9), bar(left.hp, left.max_hp, 6), left.hp, left.max_hp)
    if right then
      local rtxt = string.format("%-9s [%s] %3d/%-3d", right.short:sub(1, 9), bar(right.hp, right.max_hp, 6), right.hp, right.max_hp)
      print("  " .. ltxt .. "  " .. rtxt)
    else
      print("  " .. ltxt)
    end
  end
  print("Next: battle(\"dust\") | battle(\"beak\") | battle(\"squad\") | battle(\"list\")")
end

-- REPL helpers
function battle(preset)
  run_encounter(preset)
end

function squad(...)
  local names = { ... }
  if #names == 0 then
    _G._battle_mode = "live"
    _G._battle_roster = nil
    print("Squad mode: live (reads your in-game squad; falls back to lore squad).")
  elseif #names == 1 and string.lower(tostring(names[1])) == "live" then
    _G._battle_mode = "live"
    _G._battle_roster = nil
    print("Squad mode: live (reads your in-game squad).")
  elseif #names == 1 and string.lower(tostring(names[1])) == "lore" then
    _G._battle_mode = "lore"
    _G._battle_roster = nil
    print("Squad mode: lore (selected + Ruka, Beep, Sadneil).")
  else
    _G._battle_mode = "custom"
    _G._battle_roster = {}
    for i = 1, math.min(4, #names) do
      _G._battle_roster[i] = tostring(names[i])
    end
    print("Squad mode: custom (" .. table.concat(_G._battle_roster, ", ") .. ").")
  end
end

run_encounter("hungry")
