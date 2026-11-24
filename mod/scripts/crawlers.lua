-- MeitouMoonscript: Cat Crawlers (Swamp Bar Game) playable in the terminal.
-- Author: PZA. Licence: GPLv3 or later.
--
-- Exact same ladder rules as PZAMeitouCatCrawlers\mod\scripts\ladder.lua (CrawlCore.cs):
--   7 crawlers x 4 copies (28-card crawl):
--     1 Wolf Tail < 2 Buxom Red < 3 Chubby Nine < 4 Stoneroller
--     < 5 Hungry Gutter < 6 Arrow in the Neck < 7 Plank
--   Dealer never opens on an end (Wolf Tail or Plank). Ties are a dryout (house cut).
--   Fair-odds payouts: floor(pot * 6 / winning_ranks) -> 120 / 150 / 200 / 300 / 600 on 100.
--   Hot chain: after a lick, take() pockets the pot or ride() lets it ride (up to 6,000 limit).
--
-- Usage in the Moonscript terminal (Shift+Y):
--   /run crawlers      open the table & deal a 100-cat hand (practice cats; never touches real money)
--   fish()             call "Fish!" (next crawler is higher)
--   roll()             call "Roll back!" (next crawler is lower)
--   take()             pocket the pot after a lick
--   ride()             let the pot ride on a hot chain
--   deal(100|200|500)  deal a new hand at 100, 200 or 500 cats
--   ladder()           show the 7 crawlers & how many copies are left in the crawl

local RANKS, COPIES = 7, 4
local SIZE = RANKS * COPIES
local STAKES = { [100] = true, [200] = true, [500] = true }
local HOUSE_LIMIT, MIN_TO_DEAL = 6000, 6
local IDLE, CHOOSE, LICKED, DRY = 0, 1, 2, 3
local FISH, ROLL = 0, 1
local WOLF, PLANK = 1, 7

local NAMES = {
  "Wolf Tail",
  "Buxom Red",
  "Chubby Nine",
  "Stoneroller",
  "Hungry Gutter",
  "Arrow in the Neck",
  "Plank",
}

local function next_pick()
  return math.random(0, 2147483646)
end

-- Keep table state across terminal commands until /reset
_G._crawl = _G._crawl or nil

local function fresh(st, keep_out)
  st.left = {}
  for i = 1, RANKS do st.left[i] = COPIES end
  st.total = SIZE
  if keep_out and keep_out >= 1 and keep_out <= RANKS and st.left[keep_out] > 0 then
    st.left[keep_out] = st.left[keep_out] - 1
    st.total = st.total - 1
  end
end

local function draw(st)
  if st.total <= 0 then return 0 end
  local n = next_pick() % st.total
  for i = 1, RANKS do
    if n < st.left[i] then
      st.left[i] = st.left[i] - 1
      st.total = st.total - 1
      return i
    end
    n = n - st.left[i]
  end
  return 0
end

local function winning_ranks(face, call)
  if face < WOLF or face > PLANK then return 0 end
  if call == FISH then return RANKS - face end
  return face - 1
end

local function pays_of(pot, face, call)
  local k = winning_ranks(face, call)
  if k <= 0 or pot <= 0 then return 0 end
  return math.floor(pot * (RANKS - 1) / k)
end

local function can_ride(st)
  return st.phase == LICKED and st.drawn > WOLF and st.drawn < PLANK and st.pot < HOUSE_LIMIT
end

local function init_table()
  local start_purse = 1000
  if type(world) == "table" and type(world.cats) == "function" then
    local c = world.cats()
    if type(c) == "number" and c >= 100 then start_purse = math.floor(c) end
  end
  local st = {
    phase = IDLE, face = 0, drawn = 0,
    stake = 0, pot = 0, chain = 0,
    purse = start_purse, hands = 0, wins = 0, net = 0,
  }
  fresh(st, nil)
  _G._crawl = st
  return st
end

local function get_table()
  return _G._crawl or init_table()
end

local function show_prompt(st)
  local pf = pays_of(st.pot, st.face, FISH)
  local pr = pays_of(st.pot, st.face, ROLL)
  local kf = winning_ranks(st.face, FISH)
  local kr = winning_ranks(st.face, ROLL)
  print(string.format("  Face: [%d] %-17s | Pot: %d cats (chain %d)", st.face, NAMES[st.face], st.pot, st.chain))
  print(string.format("  fish() -> higher (%d/6, pays %d) | roll() -> lower (%d/6, pays %d)", kf, pf, kr, pr))
end

function ladder()
  local st = get_table()
  print(string.format("== CAT CRAWLERS LADDER | Crawl: %d/28 left | Purse: %d cats ==", st.total, st.purse))
  for i = 1, RANKS do
    local tag = (i == st.face and st.phase == CHOOSE) and " <-- FACE" or ""
    print(string.format("  [%d] %-18s  %d/4 left%s", i, NAMES[i], st.left[i], tag))
  end
  print("Commands: deal(100|200|500) | fish() | roll() | take() | ride()")
end

function deal(stake)
  local st = get_table()
  stake = stake or 100
  if st.phase ~= IDLE and st.phase ~= DRY then
    print("Finish the current hand first (fish(), roll(), take(), or ride()).")
    return
  end
  if not STAKES[stake] then
    print("Stakes are 100, 200, or 500 cats. Example: deal(200)")
    return
  end
  if st.purse < stake then
    st.purse = 1000
    print("Blackshifter dealer tosses you a fresh 1,000 practice-cat tab.")
  end
  local was_fresh, burned = false, 0
  if st.total < MIN_TO_DEAL then fresh(st, nil); was_fresh = true end
  local face
  while true do
    face = draw(st)
    if face == 0 then
      fresh(st, nil); was_fresh = true
    elseif face == WOLF or face == PLANK then
      burned = burned + 1
    else
      break
    end
  end
  st.phase, st.face, st.drawn = CHOOSE, face, 0
  st.stake, st.pot, st.chain = stake, stake, 0
  st.purse = st.purse - stake
  st.net = st.net - stake
  st.hands = st.hands + 1

  local who = (type(selected) == "table" and selected.name and selected.name()) or "Drifter"
  local note = was_fresh and " (fresh crawl)" or ""
  if burned > 0 then note = note .. string.format(" (%d end burned)", burned) end
  print(string.format("== CAT CRAWLERS | %s | Stake %d | Purse %d%s ==", who, stake, st.purse, note))
  show_prompt(st)
end

local function call_move(call)
  local st = get_table()
  if st.phase ~= CHOOSE then
    print("No open call right now. Use deal(100), take(), or ride().")
    return
  end
  local was_fresh = false
  if st.total == 0 then fresh(st, st.face); was_fresh = true end
  local win_pot = pays_of(st.pot, st.face, call)
  local long_shot = winning_ranks(st.face, call) == 1
  local card = draw(st)
  st.drawn = card
  local call_name = (call == FISH) and "Fish!" or "Roll back!"
  local win = (call == FISH) and (card > st.face) or (card < st.face)
  local fresh_note = was_fresh and " [fresh crawl]" or ""

  if win then
    st.pot, st.chain, st.phase = win_pot, st.chain + 1, LICKED
    local banner = long_shot and "CLOSE STROKE! GETS THE GIRL!" or "LICK!"
    print(string.format("  > \"%s\" -> [%d] %s! %s Pot: %d cats (chain %d)%s",
      call_name, card, NAMES[card], banner, st.pot, st.chain, fresh_note))
    if can_ride(st) then
      print("  Next: take() to pocket " .. st.pot .. " cats, or ride() for a hot chain!")
    else
      print("  End crawler / house limit reached: call take() to collect " .. st.pot .. " cats!")
    end
  else
    local kind = (card == st.face) and "TIE -> DRYOUT! (house cut)" or "DRYOUT!"
    st.pot, st.chain, st.phase = 0, 0, DRY
    print(string.format("  > \"%s\" -> [%d] %s. %s (Purse: %d, net %+d)%s",
      call_name, card, NAMES[card], kind, st.purse, st.net, fresh_note))
    print("  Next: deal(100) | deal(200) | deal(500) | ladder()")
  end
end

function fish() call_move(FISH) end
function roll() call_move(ROLL) end

function take()
  local st = get_table()
  if st.phase ~= LICKED then
    print("Nothing to collect right now. Use deal(100) or fish() / roll().")
    return
  end
  local cats = st.pot
  st.purse = st.purse + cats
  st.net = st.net + cats
  st.wins = st.wins + 1
  st.pot, st.phase = 0, IDLE
  print(string.format("  + Pocketed %d cats! Purse: %d (net %+d, %d/%d won)", cats, st.purse, st.net, st.wins, st.hands))
  print("  Next: deal(100) | deal(200) | deal(500) | ladder()")
end

function ride()
  local st = get_table()
  if not can_ride(st) then
    print(st.phase == LICKED and "Can't ride on an end crawler or past the 6,000 limit: call take()!" or "No pot to ride right now.")
    return
  end
  st.face, st.drawn, st.phase = st.drawn, 0, CHOOSE
  print(string.format("  ! HOT CHAIN x%d! Letting %d cats ride...", st.chain + 1, st.pot))
  show_prompt(st)
end

-- Auto-deal a fresh 100-cat hand when `/run crawlers` is called
local cur = get_table()
if cur.phase == IDLE or cur.phase == DRY then
  deal(100)
else
  print(string.format("== CAT CRAWLERS (hand in progress, Purse %d) ==", cur.purse))
  if cur.phase == CHOOSE then show_prompt(cur)
  else print(string.format("  Pot: %d cats ready -> take() or ride()", cur.pot)) end
end
