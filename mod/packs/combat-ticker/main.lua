-- Combat Ticker: starter Lua pack demonstrating narrow game events (`on(...)`), `lib.fsm`, and `command()`.
-- Author: PZA. Licence: GPLv3 or later.

state = state or {
  days = 0,
  hits = 0,
  downs = 0,
  totalDamage = 0,
  biggestHit = 0,
  biggestAttacker = "",
  biggestTarget = "",
  status = lib.fsm.new("quiet"),
}

on("day", function(ev)
  state.days = ev.day or (state.days + 1)
  if state.status.state ~= "quiet" then
    lib.fsm.go(state.status, "quiet")
  end
end)

on("hit", function(ev)
  local dmg = ev.damage or 0
  state.hits = state.hits + 1
  state.totalDamage = state.totalDamage + dmg
  if dmg > state.biggestHit then
    state.biggestHit = dmg
    state.biggestAttacker = ev.attacker or "Unknown"
    state.biggestTarget = ev.target or "Unknown"
  end
  if state.hits >= 10 then
    lib.fsm.go(state.status, "battle")
  elseif state.hits >= 1 then
    lib.fsm.go(state.status, "skirmish")
  end
end)

on("character_down", function(ev)
  state.downs = state.downs + 1
end)

command("ticker", function(args)
  if args == "reset" then
    state.hits = 0
    state.downs = 0
    state.totalDamage = 0
    state.biggestHit = 0
    state.biggestAttacker = ""
    state.biggestTarget = ""
    lib.fsm.go(state.status, "quiet")
    print("Combat Ticker reset.")
    return
  end
  local avg = state.hits > 0 and math.floor((state.totalDamage / state.hits) * 10 + 0.5) / 10 or 0
  print(string.format("Combat Ticker [%s]  hits: %d  downs: %d  avg dmg: %.1f", state.status.state, state.hits, state.downs, avg))
  if state.biggestHit > 0 then
    print(string.format("  Biggest hit: %.1f (%s -> %s)", state.biggestHit, state.biggestAttacker, state.biggestTarget))
  end
end)
