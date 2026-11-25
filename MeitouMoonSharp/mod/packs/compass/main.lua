-- Simple Compass: starter Lua pack demonstrating `camera.read` + `ui.hud`.
-- Author: PZA. Licence: GPLv3 or later.

local dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }
local enabled = false

local function render()
  if not enabled then return end
  local yaw = camera.yaw()
  if yaw == nil then
    hud.set_text("compass", 0.38, 0.012, 0.24, 0.032, "[  COMPASS: NO WORLD  ]", "#8A9099")
    return
  end
  local idx = math.floor(((yaw + 22.5) % 360) / 45) + 1
  local left = dirs[((idx - 2) % 8) + 1]
  local mid = dirs[idx]
  local right = dirs[(idx % 8) + 1]
  local deg = math.floor(yaw + 0.5) % 360
  local line = string.format("[  %s   ·   %s (%03d°)   ·   %s  ]", left, mid, deg, right)
  hud.set_text("compass", 0.36, 0.012, 0.28, 0.032, line, "#F0E6D2")
end

command("compass", function(arg)
  if arg == "on" then enabled = true
  elseif arg == "off" then enabled = false
  else enabled = not enabled end
  if enabled then
    render()
    print("Compass HUD: ON (F7 hides with HUD; /compass off to disable)")
  else
    hud.clear()
    print("Compass HUD: OFF")
  end
end)

every(0.01, render)
