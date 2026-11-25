-- MeitouMoonscript starter pack: Wasteland Dice (author PZA, GPLv3 or later).
-- Demonstrates MTA:SA-style Lua packs: command("roll", fn) + lib.fsm + seeded math.random.

local streak = lib.fsm.new("cold")

command("roll", function(arg)
  local s = (arg and #arg > 0) and arg or "2d6"
  local n_str, sides_str = string.match(s, "^(%d+)[dD](%d+)$")
  local n = tonumber(n_str) or 2
  local sides = tonumber(sides_str) or 6
  if n < 1 or n > 20 or sides < 2 or sides > 100 then
    print("Usage: /roll [1..20]d[2..100]  (example: /roll 3d6)")
    return
  end
  local rolls, sum = {}, 0
  for i = 1, n do
    local r = math.random(1, sides)
    rolls[i] = tostring(r)
    sum = sum + r
  end
  local avg = n * (sides + 1) * 0.5
  lib.fsm.go(streak, sum >= avg and "hot" or "cold")
  local who = (type(selected) == "table" and selected.name and selected.name()) or "Drifter"
  print(string.format("%s rolls %dd%d: [%s] = %d (%s hand)", who, n, sides, table.concat(rolls, ", "), sum, streak.state))
end)
