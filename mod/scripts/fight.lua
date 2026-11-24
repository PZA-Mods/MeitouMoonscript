-- MeitouMoonscript: a turn-based battle. Author: PZA. Licence: GPLv3 or later.
-- The battle window (Shift+Y terminal: /battle, or F10 > Mods) draws it; this file plays the rules only. Claims B1-B6 in
-- tools/euler/CLAIMS.md. Seeded math.random only; nothing in the world changes. All state lives in `state.fight`.
--
-- The window calls:
--   fight_new(key)        start encounter `key` with the live squad (world.squad()), or the lore squad without a world
--   fight_view()          a plain table to draw: status, round, turn, party, foes, log, bandages, encounter
--   fight_act(a, t)       the player's choice for the unit whose turn it is: "attack" t, "heavy" t, "guard", "bandage" t, "flee"
--                         -> { ok = true } or { ok = false, why = "..." }; after a good choice the foes play until the next choice

state = state or {}

local MAX_ROUNDS, BANDAGES, LOG_KEEP = 30, 3, 40

local ENCOUNTERS = {
  -- tuned by the Euler sweep (fight.sweep, 2026-11-23): attack x1.15, health x1.2 -> a careful player wins about 81% (claim B6)
  hungry = { title = "5 Hungry Bandits", place = "the Border Zone", pic = "hungry", foes = {
    { name = "Leader", hp = 74, atk = 13, def = 5, spd = 11 },
    { name = "Bandit A", hp = 58, atk = 10, def = 3, spd = 12 },
    { name = "Bandit B", hp = 58, atk = 10, def = 3, spd = 10 },
    { name = "Bandit C", hp = 53, atk = 9,  def = 3, spd = 13 },
    { name = "Bandit D", hp = 53, atk = 9,  def = 3, spd = 9 } } },
  dust = { title = "Dust Bandits", place = "the Great Desert", pic = "dust", foes = {
    { name = "Dust Boss", pic = "dustboss", hp = 90, atk = 15, def = 8, spd = 11 },
    { name = "Bandit A", hp = 60, atk = 11, def = 5, spd = 12 },
    { name = "Bandit B", hp = 60, atk = 11, def = 5, spd = 10 },
    { name = "Bandit C", hp = 56, atk = 10, def = 5, spd = 11 } } },
  beak = { title = "A Beak Thing", place = "the Bonefields", pic = "beak", foes = {
    { name = "Beak Thing", hp = 240, atk = 22, def = 10, spd = 14 } } },
}

local LORE = {
  { name = "Beep",    race = "Hiver",    max = 80, atk = 10, def = 4, spd = 12 },
  { name = "Ruka",    race = "Shek",     max = 95, atk = 13, def = 6, spd = 11 },
  { name = "Sadneil", race = "Skeleton", max = 85, atk = 12, def = 5, spd = 13 },
}

local function clamp(v, lo, hi) if v < lo then return lo elseif v > hi then return hi end return v end

local function log(text)
  local f = state.fight
  f.log[#f.log + 1] = text
  while #f.log > LOG_KEEP do table.remove(f.log, 1) end
end

local function emit_event(ev)
  local f = state.fight
  if not f.events then f.events = {} end
  f.events[#f.events + 1] = ev
end

-- the live squad: Kenshi's 0-100 skills scaled to this game's numbers (a starting character about Beep's line above)
local function from_snap(s)
  local max = math.floor(clamp(50 + (s.tough or 10) * 0.8, 40, 150))
  local frac = (s.max_hp or 0) > 0 and clamp((s.hp or 0) / s.max_hp, 0.1, 1) or 1
  return {
    name = s.name or "?",
    race = s.robot and "Skeleton" or (s.race or "Greenlander"),
    max = max,
    hp = math.max(1, math.floor(max * frac)),
    atk = math.floor(clamp(7 + (s.atk or 10) * 0.2 + (s.str or 10) * 0.08 + (s.weapon_level or 0) * 1.5, 5, 40)),
    def = math.floor(clamp(3 + (s.def or 10) * 0.12 + (s.armour_level or 0) * 1.2, 1, 25)),
    spd = math.floor(clamp(9 + (s.dex or 10) * 0.12 + (s.dodge or 0) * 0.04, 5, 30)),
  }
end

local function party_from_world()
  local list = {}
  if type(world) == "table" and type(world.squad) == "function" then
    for _, s in ipairs(world.squad() or {}) do
      if not s.down and #list < 4 then list[#list + 1] = from_snap(s) end
    end
  end
  -- fewer than three who can fight (a new game's lone wanderer): the lore companions join to make three
  for _, t in ipairs(LORE) do
    if #list >= 3 then break end
    list[#list + 1] = { name = t.name, race = t.race, max = t.max, hp = t.max, atk = t.atk, def = t.def, spd = t.spd, lore = true }
  end
  return list
end

local function unit(t, side, slot, pic)
  local max = t.max or t.hp
  return { name = t.name, race = t.race or "", pic = pic, side = side, slot = slot, max = max, hp = t.hp or max, atk = t.atk, def = t.def, spd = t.spd,
           ko = false, guard = false, tired = 0 }
end

-- ---------------------------------------------------------------- order and turns

local function all_units()
  local f, list = state.fight, {}
  for _, u in ipairs(f.party) do list[#list + 1] = u end
  for _, u in ipairs(f.foes) do list[#list + 1] = u end
  return list
end

local function new_round()
  local f = state.fight
  f.round = f.round + 1
  local order = {}
  for _, u in ipairs(all_units()) do if not u.ko then order[#order + 1] = u end end
  table.sort(order, function(a, b)
    if a.spd ~= b.spd then return a.spd > b.spd end
    if a.side ~= b.side then return a.side == "party" end
    return a.slot < b.slot
  end)
  f.order = {}
  for i, u in ipairs(order) do f.order[i] = { side = u.side, slot = u.slot } end
  f.at = 0
end

local function side_down(list) for _, u in ipairs(list) do if not u.ko then return false end end return true end

local function finish_check()
  local f = state.fight
  if side_down(f.foes) then f.status = "won"; log("The fight is won."); return true end
  if side_down(f.party) then f.status = "lost"; log("Your squad is down. The bandits take what they can carry."); return true end
  return false
end

local function get(ref) local f = state.fight; return (ref.side == "party" and f.party or f.foes)[ref.slot] end

-- ---------------------------------------------------------------- actions

local function hit(att, def, bonus, mult, verb)
  local chance = clamp(0.80 + (att.spd - def.spd) * 0.01 + bonus, 0.35, 0.95)
  if math.random() >= chance then
    local msg = att.name .. " swings at " .. def.name .. " and misses."
    log(msg)
    emit_event({ kind = "miss", att_side = att.side, att_slot = att.slot, tgt_side = def.side, tgt_slot = def.slot, dmg = 0, crit = false, ko = false, text = msg })
    return
  end
  local dmg = att.atk * (0.85 + math.random() * 0.3) * mult - def.def * 0.5
  local crit = math.random() < 0.08
  if crit then dmg = dmg * 1.5 end
  if def.guard then dmg = dmg * 0.5 end
  dmg = math.max(1, math.floor(dmg + 0.5))
  def.hp = math.max(0, def.hp - dmg)
  local msg = att.name .. " " .. verb .. " " .. def.name .. " for " .. dmg .. (crit and ", a heavy blow" or "") .. (def.guard and " (guarded)" or "") .. "."
  log(msg)
  local ko_now = false
  if def.hp == 0 then def.ko = true; def.guard = false; ko_now = true; log(def.name .. " goes down.") end
  emit_event({ kind = "hit", att_side = att.side, att_slot = att.slot, tgt_side = def.side, tgt_slot = def.slot, dmg = dmg, crit = crit, ko = ko_now, text = msg })
end

local function foe_turn(u)
  local f = state.fight
  -- the hungry go for whoever looks weakest; one time in four they swing at random
  local targets = {}
  for _, p in ipairs(f.party) do if not p.ko then targets[#targets + 1] = p end end
  if #targets == 0 then return end
  local t = targets[math.random(1, #targets)]
  if math.random() < 0.75 then
    for _, p in ipairs(targets) do if p.hp / p.max < t.hp / t.max then t = p end end
  end
  hit(u, t, 0, 1, "hits")
end

local ACTIONS = { attack = true, heavy = true, guard = true, bandage = true, flee = true }

local function player_turn(u, action, target)
  local f = state.fight
  if action == "attack" or action == "heavy" then
    local t = f.foes[target]
    if action == "heavy" then
      if u.tired > 0 then return false, u.name .. " is still recovering from a heavy blow" end
      hit(u, t, -0.15, 1.5, "brings a heavy blow down on"); u.tired = 2
    else
      hit(u, t, 0, 1, "hits")
    end
  elseif action == "guard" then
    u.guard = true
    local msg = u.name .. " raises a guard."
    log(msg)
    emit_event({ kind = "guard", att_side = u.side, att_slot = u.slot, tgt_side = u.side, tgt_slot = u.slot, dmg = 0, crit = false, ko = false, text = msg })
  elseif action == "bandage" then
    local t = f.party[target]
    local heal = math.floor(t.max / 4) + 5
    local before = t.hp
    t.hp = math.min(t.max, t.hp + heal)
    f.bandages = f.bandages - 1
    local gained = t.hp - before
    local msg = u.name .. " bandages " .. (t == u and "their own wounds" or t.name) .. " (+" .. gained .. ")."
    log(msg)
    emit_event({ kind = "heal", att_side = u.side, att_slot = u.slot, tgt_side = t.side, tgt_slot = t.slot, dmg = gained, crit = false, ko = false, text = msg })
  elseif action == "flee" then
    local ps, fs, pn, fn = 0, 0, 0, 0
    for _, p in ipairs(f.party) do if not p.ko then ps = ps + p.spd; pn = pn + 1 end end
    for _, e in ipairs(f.foes) do if not e.ko then fs = fs + e.spd; fn = fn + 1 end end
    local chance = clamp(0.5 + (ps / math.max(1, pn) - fs / math.max(1, fn)) * 0.03, 0.2, 0.9)
    if math.random() < chance then f.status = "fled"; log("The squad breaks away and runs."); return true end
    log("The squad tries to run, but the bandits cut them off.")
  end
  return true
end

-- why a choice is refused, or nil when it is allowed (B5: checked before anything changes)
local function refuse(u, action, target)
  local f = state.fight
  if f.status ~= "choose" then return "the fight is over" end
  if not ACTIONS[action] then return "unknown action " .. tostring(action) end
  if action == "attack" or action == "heavy" then
    local t = type(target) == "number" and f.foes[target] or nil
    if not t then return "choose a foe to hit" end
    if t.ko then return t.name .. " is already down" end
    if action == "heavy" and u.tired > 0 then return u.name .. " is still recovering from a heavy blow" end
  elseif action == "bandage" then
    if f.bandages <= 0 then return "no bandages left" end
    local t = type(target) == "number" and f.party[target] or nil
    if not t then return "choose who to bandage" end
    if t.ko then return t.name .. " is down: bandages can't help now" end
    if t.hp >= t.max then return t.name .. " is not hurt" end
  end
  return nil
end

-- ---------------------------------------------------------------- the turn loop

-- advance until a squad unit must choose, or the fight ends
local function advance()
  local f = state.fight
  while f.status == "playing" or f.status == "choose" do
    f.status = "playing"
    f.at = f.at + 1
    if f.at > #f.order then
      if f.round >= MAX_ROUNDS then f.status = "draw"; log("After a long brawl both sides lose interest."); return end
      new_round()
      log("Round " .. f.round .. ".")
    else
      local u = get(f.order[f.at])
      if u and not u.ko then
        u.guard = false   -- a guard lasts until the unit's own next turn
        if u.tired > 0 then u.tired = u.tired - 1 end
        if u.side == "party" then f.status = "choose"; f.turn = u.slot; return end
        foe_turn(u)
        if finish_check() then return end
      end
    end
  end
end

function fight_new(key)
  local enc = ENCOUNTERS[key or "hungry"] or ENCOUNTERS.hungry
  local f = { key = key or "hungry", title = enc.title, place = enc.place, party = {}, foes = {}, log = {}, events = {}, round = 0, at = 0,
              order = {}, status = "playing", turn = 0, bandages = BANDAGES, acts = 0 }
  state.fight = f
  for i, t in ipairs(party_from_world()) do f.party[i] = unit(t, "party", i) end
  for i, t in ipairs(enc.foes) do f.foes[i] = unit(t, "foe", i, t.pic or enc.pic) end
  log(enc.title .. " in " .. enc.place .. ". Choose each move; KO, not death.")
  advance()
  return fight_view()
end

function fight_act(action, target)
  local f = state.fight
  if not f then return { ok = false, why = "no fight: start one" } end
  local u = f.status == "choose" and f.party[f.turn] or nil
  -- not `u and refuse(...) or ...`: refuse returns nil for an allowed move, and the `or` would then refuse it (the offline check found it)
  local why
  if u then why = refuse(u, action, target)
  else why = f.status ~= "choose" and "the fight is over" or "not your turn" end
  if why then return { ok = false, why = why } end
  f.events = {}
  f.acts = f.acts + 1
  player_turn(u, action, target)
  if f.status == "fled" or finish_check() then return { ok = true } end
  advance()
  return { ok = true }
end

local function copy_unit(u) return { name = u.name, race = u.race, pic = u.pic, hp = u.hp, max = u.max, ko = u.ko, guard = u.guard, tired = u.tired, atk = u.atk, def = u.def, spd = u.spd } end

function fight_view()
  local f = state.fight
  if not f then return { status = "none" } end
  local v = { status = f.status, round = f.round, turn = f.turn, bandages = f.bandages, title = f.title, place = f.place,
              party = {}, foes = {}, log = {}, events = f.events or {} }
  for i, u in ipairs(f.party) do v.party[i] = copy_unit(u) end
  for i, u in ipairs(f.foes) do v.foes[i] = copy_unit(u) end
  for i = math.max(1, #f.log - 5), #f.log do v.log[#v.log + 1] = f.log[i] end
  return v
end

function fight_keys() local k = {} for name in pairs(ENCOUNTERS) do k[#k + 1] = name end table.sort(k) return k end
