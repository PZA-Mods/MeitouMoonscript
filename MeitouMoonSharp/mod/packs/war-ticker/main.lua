-- War Ticker: starter pack for the org bridge (dustlife.read, composer.read, twr.read). Reads only; changes nothing in the world.
-- Author: PZA. Licence: GPLv3 or later.
-- /warticker            on / off
-- /warticker news       the last few war reports in the terminal
-- /warticker status     what the bridge can see

local enabled = false
local seen, gen = 0, nil        -- the last Seq shown, and the world it belongs to
local lines = {}                -- newest first, at most 3
local MIN_IMPORTANCE = 2

local function push(text)
  table.insert(lines, 1, text)
  while #lines > 3 do table.remove(lines) end
end

local function header()
  local parts = {}
  if composer.available() and composer.active() then
    local p = composer.phase()
    if p ~= "" then parts[#parts + 1] = "story: " .. p end
  end
  if twr.available() and twr.ready() then
    local hot, top = nil, 0
    for _, t in ipairs(twr.towns()) do
      if (t.Heat or 0) > top then hot, top = t.Town, t.Heat end
    end
    if hot then parts[#parts + 1] = string.format("heat: %s (%s)", hot, twr.tier(hot)) end
  end
  return table.concat(parts, "   ")
end

local function render()
  if not enabled then return end
  local y = 0.06
  hud.set_text("wt.head", 0.70, y, 0.29, 0.026, "[ WAR ]  " .. header(), "#D9B26A")
  for i = 1, 3 do
    hud.set_text("wt." .. i, 0.70, y + 0.028 * i, 0.29, 0.026, lines[i] or "", i == 1 and "#F0E6D2" or "#A89F8E")
  end
end

local function poll()
  if not dustlife.available() then return end
  local g = dustlife.generation()
  if g ~= gen then gen, seen, lines = g, 0, {} end
  for _, row in ipairs(dustlife.news(seen, 32)) do
    seen = math.max(seen, row.Seq or seen)
    if (row.Importance or 0) >= MIN_IMPORTANCE and row.Text then push(row.Text) end
  end
  render()
end

command("warticker", function(arg)
  if arg == "news" then
    if not dustlife.available() then print("War Ticker: Dustlife is not running.") return end
    local rows = dustlife.news(0, 64)
    for i = math.max(1, #rows - 7), #rows do
      local r = rows[i]
      print(string.format("  [%s] %s", r.Key or "?", r.Text or ""))
    end
    if #rows == 0 then print("War Ticker: no war news yet.") end
    return
  end
  if arg == "status" then
    print(string.format("War Ticker: dustlife %s, composer %s, twr %s",
      dustlife.available() and (dustlife.active() and "on" or "idle") or "absent",
      composer.available() and (composer.active() and composer.drama() or "idle") or "absent",
      twr.available() and (twr.ready() and "on" or "idle") or "absent"))
    return
  end
  if arg == "on" then enabled = true elseif arg == "off" then enabled = false else enabled = not enabled end
  if enabled then poll() print("War Ticker HUD: ON (F7 hides it with the HUD; /warticker off)")
  else hud.clear() print("War Ticker HUD: OFF") end
end)

every(0.25, poll)
