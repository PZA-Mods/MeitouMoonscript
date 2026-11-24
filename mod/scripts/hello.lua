-- MeitouMoonscript: sample Lua script. Author: PZA. Licence: GPLv3 or later.
-- /run hello in the terminal. Edit this file while the game runs and /run it again: it is read every time.
local h = world.hour()
if h == nil then
  print("no world yet: start or load a game")
  return
end
local who = selected.name() or "nobody"
local part = h < 6 and "night" or h < 12 and "morning" or h < 18 and "afternoon" or "evening"
print(string.format("Day %d, %02d:%02d, %s. Selected: %s.", world.day(), math.floor(h), math.floor((h % 1) * 60), part, who))
local rolls = {}
for i = 1, 5 do rolls[i] = math.random(1, 6) end
print("Five dice (the seeded random): " .. table.concat(rolls, " "))
