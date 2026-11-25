// MeitouMoonscript: vetted, pure-Lua 5.2 standard helpers (`lib.tween`, `lib.fsm`). Author: PZA. Licence: GPLv3 or later.
//
// Compiled once per LuaBox by the host (before any script runs, outside user budgets; scripts still have no `load` or `require`, claim C4).
// Pure Lua only -> zero new C# functions on the C4 reach list, 100% deterministic (C6), and `lib.fsm` stores plain data tables that
// round-trip through Save/Restore (C7) without closures or metatables.
using MoonSharp.Interpreter;

namespace PZA.Moonscript.Core;

public static class StdLib
{
    public const string Source = """
local pi = math.pi
local sin, cos, pow, abs, max, min = math.sin, math.cos, math.pow, math.abs, math.max, math.min

local function outBounce(t)
  if t < 1 / 2.75 then return 7.5625 * t * t
  elseif t < 2 / 2.75 then t = t - 1.5 / 2.75; return 7.5625 * t * t + 0.75
  elseif t < 2.5 / 2.75 then t = t - 2.25 / 2.75; return 7.5625 * t * t + 0.9375
  else t = t - 2.625 / 2.75; return 7.5625 * t * t + 0.984375 end
end

local easing = {
  linear      = function(t) return t end,
  inQuad      = function(t) return t * t end,
  outQuad     = function(t) return t * (2 - t) end,
  inOutQuad   = function(t) return t < 0.5 and 2 * t * t or -1 + (4 - 2 * t) * t end,
  inCubic     = function(t) return t * t * t end,
  outCubic    = function(t) t = t - 1; return t * t * t + 1 end,
  inOutCubic  = function(t) return t < 0.5 and 4 * t * t * t or (t - 1) * (2 * t - 2) * (2 * t - 2) + 1 end,
  inQuart     = function(t) return t * t * t * t end,
  outQuart    = function(t) t = t - 1; return 1 - t * t * t * t end,
  inOutQuart  = function(t) if t < 0.5 then return 8 * t * t * t * t end t = t - 1; return 1 - 8 * t * t * t * t end,
  inSine      = function(t) return 1 - cos(t * pi * 0.5) end,
  outSine     = function(t) return sin(t * pi * 0.5) end,
  inOutSine   = function(t) return 0.5 * (1 - cos(pi * t)) end,
  inExpo      = function(t) return t == 0 and 0 or pow(2, 10 * (t - 1)) end,
  outExpo     = function(t) return t == 1 and 1 or 1 - pow(2, -10 * t) end,
  inOutExpo   = function(t) if t == 0 or t == 1 then return t end if t < 0.5 then return 0.5 * pow(2, 20 * t - 10) end return 1 - 0.5 * pow(2, -20 * t + 10) end,
  inBack      = function(t) local s = 1.70158; return t * t * ((s + 1) * t - s) end,
  outBack     = function(t) local s = 1.70158; t = t - 1; return t * t * ((s + 1) * t + s) + 1 end,
  inOutBack   = function(t) local s = 2.5949095; t = t * 2; if t < 1 then return 0.5 * (t * t * ((s + 1) * t - s)) end t = t - 2; return 0.5 * (t * t * ((s + 1) * t + s) + 2) end,
  outBounce   = outBounce,
  inBounce    = function(t) return 1 - outBounce(1 - t) end,
  inOutBounce = function(t) return t < 0.5 and 0.5 * (1 - outBounce(1 - 2 * t)) or 0.5 * outBounce(2 * t - 1) + 0.5 end,
  outElastic  = function(t) if t == 0 or t == 1 then return t end return pow(2, -10 * t) * sin((t - 0.075) * (2 * pi) / 0.3) + 1 end,
}

local function clamp01(t) if t <= 0 then return 0 elseif t >= 1 then return 1 else return t end end

local Tween = {}
Tween.__index = Tween

function Tween:set(clock)
  self.clock = max(0, min(self.duration, clock))
  local p = self.duration <= 0 and 1 or clamp01(self.clock / self.duration)
  local e = self.ease(p)
  for k, b in pairs(self.initial) do
    self.subject[k] = b + (self.target[k] - b) * e
  end
  return self.clock >= self.duration
end

function Tween:update(dt)
  return self:set(self.clock + (dt or 0))
end

function Tween:reset()
  return self:set(0)
end

function Tween:progress()
  return self.duration <= 0 and 1 or clamp01(self.clock / self.duration)
end

local tween = { easing = easing }

function tween.ease(name, t)
  local fn = type(name) == "function" and name or easing[name] or easing.linear
  return fn(clamp01(t or 0))
end

function tween.lerp(a, b, t, ease_name)
  return a + (b - a) * (ease_name and tween.ease(ease_name, t) or clamp01(t or 0))
end

function tween.new(duration, subject, target, ease_name)
  assert(type(duration) == "number" and duration >= 0, "tween.new: duration must be >= 0")
  assert(type(subject) == "table" and type(target) == "table", "tween.new: subject and target must be tables")
  local fn = type(ease_name) == "function" and ease_name or easing[ease_name or "linear"]
  assert(fn ~= nil, "tween.new: unknown easing '" .. tostring(ease_name) .. "'")
  local init = {}
  for k, v in pairs(target) do
    assert(type(v) == "number" and type(subject[k]) == "number", "tween.new: key '" .. tostring(k) .. "' must be a number in both subject and target")
    init[k] = subject[k]
  end
  return setmetatable({ duration = duration, subject = subject, target = target, initial = init, ease = fn, clock = 0 }, Tween)
end

-- Plain-data Finite State Machine (save/restore C7-safe: store the returned table directly inside `state`)
local fsm = {}

function fsm.new(initial)
  return { state = initial or "idle", prev = nil, time = 0, step = 0 }
end

function fsm.is(m, name)
  return m ~= nil and m.state == name
end

function fsm.go(m, next_state)
  if m == nil or next_state == nil or m.state == next_state then return false end
  m.prev = m.state
  m.state = next_state
  m.time = 0
  m.step = 0
  return true
end

function fsm.tick(m, dt, handlers)
  if m == nil then return nil end
  m.time = (m.time or 0) + (dt or 0)
  m.step = (m.step or 0) + 1
  if handlers and type(handlers[m.state]) == "function" then
    local nxt = handlers[m.state](m, dt or 0)
    if type(nxt) == "string" and nxt ~= m.state then
      fsm.go(m, nxt)
    end
  end
  return m.state
end

-- Compact 2D AABB collision world (`lib.bump`, pure Lua 5.2, swept Minkowski-difference raycast).
-- Supports responses: "slide" (default), "touch", "cross", or nil/false (ignore).
local bump = {}
local BWorld = {}
BWorld.__index = BWorld

local function rectOverlap(ax, ay, aw, ah, bx, by, bw, bh)
  return ax < bx + bw and bx < ax + aw and ay < by + bh and by < ay + ah
end

local function sweepAABB(x1, y1, w1, h1, gx, gy, x2, y2, w2, h2)
  local dx, dy = gx - x1, gy - y1
  local rx, ry, rw, rh = x2 - (x1 + w1), y2 - (y1 + h1), w1 + w2, h1 + h2
  local tx0, tx1, ty0, ty1
  if dx == 0 then
    if rx >= 0 or rx + rw <= 0 then return nil end
    tx0, tx1 = -1e9, 1e9
  elseif dx > 0 then
    tx0, tx1 = rx / dx, (rx + rw) / dx
  else
    tx0, tx1 = (rx + rw) / dx, rx / dx
  end
  if dy == 0 then
    if ry >= 0 or ry + rh <= 0 then return nil end
    ty0, ty1 = -1e9, 1e9
  elseif dy > 0 then
    ty0, ty1 = ry / dy, (ry + rh) / dy
  else
    ty0, ty1 = (ry + rh) / dy, ry / dy
  end
  local t0, t1 = max(tx0, ty0), min(tx1, ty1)
  if t0 > t1 or t1 < 0 or t0 > 1 then return nil end
  if t0 < 0 then
    -- already overlapping: push along smallest axis
    local ox1, ox2 = rx, rx + rw
    local oy1, oy2 = ry, ry + rh
    local sx = abs(ox1) < abs(ox2) and ox1 or ox2
    local sy = abs(oy1) < abs(oy2) and oy1 or oy2
    if abs(sx) < abs(sy) then
      return 0, sx < 0 and 1 or -1, 0, x1, y1
    else
      return 0, 0, sy < 0 and 1 or -1, x1, y1
    end
  end
  local nx, ny = 0, 0
  if tx0 > ty0 then nx = dx > 0 and -1 or 1 else ny = dy > 0 and -1 or 1 end
  return t0, nx, ny, x1 + dx * t0, y1 + dy * t0
end

function bump.newWorld()
  return setmetatable({ rects = {}, order = {} }, BWorld)
end

function BWorld:add(item, x, y, w, h)
  assert(item ~= nil, "bump:add item cannot be nil")
  assert(self.rects[item] == nil, "bump:add item already exists")
  self.rects[item] = { x = x, y = y, w = w, h = h }
  self.order[#self.order + 1] = item
  return item
end

function BWorld:remove(item)
  if self.rects[item] == nil then return false end
  self.rects[item] = nil
  for i = 1, #self.order do
    if self.order[i] == item then table.remove(self.order, i); break end
  end
  return true
end

function BWorld:hasItem(item) return self.rects[item] ~= nil end

function BWorld:getRect(item)
  local r = self.rects[item]
  if not r then return nil end
  return r.x, r.y, r.w, r.h
end

function BWorld:update(item, x, y, w, h)
  local r = assert(self.rects[item], "bump:update unknown item")
  r.x, r.y = x, y
  if w ~= nil then r.w = w end
  if h ~= nil then r.h = h end
end

function BWorld:queryRect(x, y, w, h, filter)
  local out, n = {}, 0
  for i = 1, #self.order do
    local it = self.order[i]
    local r = self.rects[it]
    if rectOverlap(x, y, w, h, r.x, r.y, r.w, r.h) and (not filter or filter(it)) then
      n = n + 1; out[n] = it
    end
  end
  return out, n
end

function BWorld:move(item, goalX, goalY, filter)
  local r = assert(self.rects[item], "bump:move unknown item")
  local x, y, w, h = r.x, r.y, r.w, r.h
  local gx, gy = goalX, goalY
  local cols, visited = {}, {}
  for _ = 1, 4 do
    local bestTi, bestNx, bestNy, bestTx, bestTy, bestOther, bestKind = 2, 0, 0, gx, gy, nil, nil
    for i = 1, #self.order do
      local other = self.order[i]
      if other ~= item and not visited[other] then
        local kind = filter and filter(item, other) or "slide"
        if kind then
          local o = self.rects[other]
          local ti, nx, ny, tx, ty = sweepAABB(x, y, w, h, gx, gy, o.x, o.y, o.w, o.h)
          if ti ~= nil and ti < bestTi then
            bestTi, bestNx, bestNy, bestTx, bestTy, bestOther, bestKind = ti, nx, ny, tx, ty, other, kind
          end
        end
      end
    end
    if not bestOther then x, y = gx, gy; break end
    visited[bestOther] = true
    cols[#cols + 1] = { item = item, other = bestOther, type = bestKind, ti = bestTi, touch = { x = bestTx, y = bestTy }, normal = { x = bestNx, y = bestNy } }
    if bestKind == "cross" then
      -- pass through without stopping
    elseif bestKind == "touch" then
      x, y = bestTx, bestTy; break
    else
      -- slide along the hit face
      x, y = bestTx, bestTy
      if bestNx ~= 0 then gx = bestTx end
      if bestNy ~= 0 then gy = bestTy end
    end
  end
  r.x, r.y = x, y
  return x, y, cols, #cols
end

lib = { tween = tween, fsm = fsm, bump = bump }
""";

    /// <summary>Installs `lib.tween`, `lib.fsm`, and `lib.bump` into a freshly built sandboxed Script (called by LuaBox before attaching the debugger).</summary>
    public static void Install(Script s) => s.DoString(Source, null, "lib");
}
