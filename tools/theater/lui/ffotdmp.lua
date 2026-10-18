-- Reluduali theater, version 5 (2026-10-17; docs\theater-only.md). The text of our ui_mp/t6/ffotdmp.lua: the game runs that
-- file once its menus are loaded (the fix-of-the-day hook), and takes ours from Redacted's loose folder,
-- <game>\data\ui_mp\t6\ffotdmp.lua (sandbox 0531). The game's loader reads only bytecode, so the file there is a small stub
-- of ours (hksstub.py) that compiles this text with the game's own loadstring and runs it. It does what the game's own file
-- does on a PC, and nothing more - unless the game was started with "+set reluduali_theater 1" (the Reluduali Theater
-- shortcut, through the Console).
-- Then:
--   * the first two screens are passed through under a cover ("Reluduali Black Ops 2" fading in), by the game's own events:
--     the main menu's open_main_lobby_requested, then the main lobby's open_theater_lobby - the way a player clicks, so the
--     theater lobby is the game's own and a film loaded from it keeps the game's own jump ticks (a film loaded the other
--     way shows "Downloading: 0.00 %" in the theater and has none);
--   * the theater lobby is dressed in Reluduali's colours, keeps the spinning globe, says reluduali_theater_ready 1 (the
--     Console's splash waits for it), shows the film the Console named (reluduali_theater_film), and its Back asks to quit.
-- Everything the theater layer does runs inside pcall: a mistake here leaves the game's own menus as they are, and says
-- what went wrong in the dvar reluduali_theater_error.
-- Read from the game's own scripts (hksdis.py listings of main, mainmenu, mainlobby, switchlobbies, theaterlobby, luitimer).

-- 1. what the game's own ffotdmp.lua does on a PC
if CoD.isXbox then
	CoD.Popup.CONNECTINGDW_MAX_WAIT_TIME = 60000
end
EnableGlobals()
MOTD_NA_Only = function(controller) return false end           -- PS3 only
MOTD_ROW_Only = function(controller) return false end          -- PS3 only
MOTD_SeasonPassholder = function(controller)
	if Engine.IsContentAvailableByPakName("seasonpass") then return true end
	return false
end
MOTD_NA_SeasonPassholder = function(controller) return MOTD_SeasonPassholder(controller) and MOTD_NA_Only(controller) end
MOTD_ROW_SeasonPassholder = function(controller) return MOTD_SeasonPassholder(controller) and MOTD_ROW_Only(controller) end
DisableGlobals()

-- 2. the theater layer, only when asked for
local function dvarInt(name)
	local ok, v = pcall(UIExpression.DvarInt, nil, name)
	if ok then return v end
	return 0
end
local function dvarString(name)
	local ok, v = pcall(UIExpression.DvarString, nil, name)
	if ok and v then return v end
	return ""
end
local function say(name, value)
	pcall(Engine.SetDvar, name, value)
end

-- the theater layer runs only when asked for: "+set reluduali_theater 1" (the theater) or "+set reluduali_lua_dev 1" (the
-- live-Lua loop and the menu register, for finding things out in any start: nothing is opened or changed by it alone)
local theater = dvarInt("reluduali_theater") == 1
local dev = dvarInt("reluduali_lua_dev") == 1
if not theater and not dev then
	return
end
-- the game runs this file again whenever its menus start again, in a film too (sandbox 0542: our step changed while a
-- film played): in game the theater layer leaves everything alone, the dvars the Console reads included
-- (sandbox 0554: in a film our step was set by a run of this file that did not count as in game, while another said
-- "in game": a film playing counts too, by the game's own UIExpression.IsDemoPlaying(controller))
local inGame = false
pcall(function() inGame = UIExpression.IsInGame() == 1 end)
pcall(function() if UIExpression.IsDemoPlaying(0) == 1 then inGame = true end end)
if inGame then theater = false end
if theater then
	say("reluduali_theater_error", "")
	say("reluduali_theater_ready", 0)
	say("reluduali_theater_layer", 1)
end

-- the website's colours, as the Console's splash has them (reluduali.com's Animated Launch Screen Brief, 2026-10-17):
-- the cover takes the splash's ground at its foot, so the hand-off from the Console's greeting to the game's does not
-- flash; day or night as the Console chose it (+set reluduali_theater_night)
-- The sky follows the Sun over the PC (the Console's Sky: the site's formulae): reluduali_theater_night is how much it is
-- night, reluduali_theater_warm the warm tint near sunrise and sunset, each as a whole percent (the Console's
-- SplashPainter.Palette blends the same colours the same way)
local night = math.max(0, math.min(100, dvarInt("reluduali_theater_night"))) / 100
local warm = math.max(0, math.min(100, dvarInt("reluduali_theater_warm"))) / 100
local function mix(a, b, t) return { a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t, a[3] + (b[3] - a[3]) * t } end
local Look = {
	-- the ground's foot: #eaf6ff by day, #e7d3b8 by night, toward gold #ffd9a0 near the horizon
	back = mix(mix({ 0.918, 0.965, 1.000 }, { 0.906, 0.827, 0.722 }, night), { 1.000, 0.851, 0.627 }, 0.45 * warm),
	ink = mix({ 0.043, 0.165, 0.290 }, { 0.227, 0.027, 0.086 }, night),      -- #0b2a4a / #3a0716
	maroon = { 0.361, 0.059, 0.180 },                                        -- #5c0f2e
	seed = mix({ 1.000, 0.310, 0.682 }, { 0.886, 0.529, 0.235 }, night),     -- #ff4fae pink / #e2873c orange: it reads on the game's dark too
	text = { 0.910, 0.886, 0.941 },                                          -- light words on the game's dark lobby
}
local state = { mainMenuSeen = false, lobbyOpened = false, attempts = 0, theaterOpened = false, theaterAsks = 0 }

local function guard(where, f, ...)
	local ok, err = pcall(f, ...)
	if not ok then say("reluduali_theater_error", where .. ": " .. tostring(err)) end
	return ok
end

-- the cover over a screen we pass through: the splash's ground, "Reluduali" in its ink and "BLACK OPS 2" in maroon under
-- it, fading in over the same 1.8 s
local function words(menu, text, font, size, top, colour)
	local t = LUI.UIText.new()
	t:setLeftRight(true, true, 0, 0)
	t:setTopBottom(false, false, top, top + size)
	t:setFont(font)
	t:setAlignment(LUI.Alignment.Center)
	t:setRGB(colour[1], colour[2], colour[3])
	t:setText(text)
	t:setAlpha(0)
	menu:addElement(t)
	t:beginAnimation("reluduali_fade_in", 1800)
	t:setAlpha(1)
end
local function cover(menu)
	local back = LUI.UIImage.new()
	back:setLeftRight(true, true, 0, 0)
	back:setTopBottom(true, true, 0, 0)
	back:setRGB(Look.back[1], Look.back[2], Look.back[3])
	back:setAlpha(1)
	menu:addElement(back)
	words(menu, "Reluduali", CoD.fonts.Big, CoD.textSize.Big, -CoD.textSize.Big, Look.ink)
	words(menu, "BLACK OPS 2", CoD.fonts.Condensed, CoD.textSize.Condensed, 6, Look.maroon)
end

-- What the sandbox showed (0533-0536): asking the main menu for the main lobby ourselves ended on an EXE_UI_ERROR 23292
-- box both times, while one real press of ONLINE reaches the lobby in about 6 s; our pcall caught nothing, and the step
-- never got past "main lobby", so the error came later, outside our calls - and every timer fired at once at the start
-- (V2's 1.5 s and 31.5 s asks came 2 s apart: the first menu frame after the long load moves the menu clock in one jump).
-- So V3: waits are counted in frames (a 100 ms ticker that fires at most once a frame), there is ONE ask and no second,
-- the game's own makers run inside pcall so the error's own words land in reluduali_theater_error (and the error goes
-- on as before), and "+set reluduali_lua_dev 1" alone gives the live loop and a register of the open menus
-- (CoD.Reluduali.menus) in any start, so the asks can be tried by hand.
CoD.Reluduali = { menus = {}, state = state }

-- after `frames` ticks of a 100 ms ticker on `menu`, run `f` once
local function later(menu, frames, label, f)
	local count = 0
	local done = false
	local event = "reluduali_tick_" .. label
	menu:registerEventHandler(event, function(self, e)
		if done then return end
		count = count + 1
		if count >= frames then
			done = true
			guard(label, f, self)
		end
	end)
	menu:addElement(LUI.UITimer.new(100, event, false, menu))
end

-- the main menu: on to the main lobby, as its ONLINE button does, ONCE (a press carries its controller, 0 on a PC; a maker
-- may be given none, so 0 stands in)
local function askMainLobby(self, controller, why)
	if state.lobbyOpened or state.attempts >= 1 then return end
	state.attempts = state.attempts + 1
	say("reluduali_theater_step", "main lobby (" .. why .. ")")
	self:processEvent({ name = "open_main_lobby_requested", controller = controller or 0 })
end
-- V6 (2026-10-18, the main test PC: "[EXE_UI_ERROR] 23292", 2 of 7 starts): the game opens its own "Connecting to Online
-- Service" popup at the start. Asked for the main lobby while it is up, the ONLINE action opens a second one; when the
-- online service is ready both close themselves, and the second finds itself gone ("LUI Error: Element has no parent!" in
-- is_demonware_fetching_done, caught by the trace below). So the ask waits until the online service is ready - the popup's
-- own test - and then a little longer, so the game's popup has closed (it looks every 400 ms); at most 20 s, then it asks
-- as before.
local function onlineReady(controller)
	local ok, v = pcall(UIExpression.IsDemonwareFetchingDone, controller or 0)
	if ok and v ~= nil then return v == 1 or v == true end
	ok, v = pcall(Engine.IsDemonwareFetchingDone, controller or 0)
	if ok and v ~= nil then return v == 1 or v == true end
	return true          -- neither can be asked: as before V6
end
local function whenOnline(menu, controller, label, f, least)
	local ticks, readyAt, done = 0, nil, false
	local event = "reluduali_online_" .. label
	menu:registerEventHandler(event, function(self, e)
		if done then return end
		ticks = ticks + 1
		if readyAt == nil and onlineReady(controller) then readyAt = ticks end
		-- 1.5 s after the main menu at the least (V3's wait was 3 s), 1 s after the online service is ready, 20 s at most
		if (readyAt ~= nil and ticks >= (least or 15) and ticks - readyAt >= 10) or ticks >= 200 then
			done = true
			say("reluduali_theater_online", readyAt ~= nil and ("ready at " .. readyAt / 10 .. " s") or "not ready after 20 s")
			guard(label, f, self)
		end
	end)
	menu:addElement(LUI.UITimer.new(100, event, false, menu))
end
local function onMainMenu(menu, controller)
	state.mainMenuSeen = true
	if not theater then return end
	cover(menu)
	whenOnline(menu, controller, "main_lobby", function(self) askMainLobby(self, controller, "the main menu") end)
end

-- the main lobby: on to the theater lobby, as its Theater button does, ONCE, about 2.5 s after it is up
local function onMainLobby(menu, controller)
	state.lobbyOpened = true
	if not theater then return end
	say("reluduali_theater_step", "main lobby open")
	cover(menu)
	later(menu, 25, "theater_lobby", function(self)
		if state.theaterOpened then return end
		say("reluduali_theater_step", "theater lobby (asked)")
		self:processEvent({ name = "open_theater_lobby", controller = controller or 0 })
	end)
end

-- the theater lobby: the game's own, in Reluduali's colours, with the globe; Back asks to quit (the lobbies before it are gone)
local function onTheaterLobby(menu, controller)
	state.theaterOpened = true
	if not theater then return end
	-- each part on its own: one that fails is written down and the rest still happen, the ready signal last of all
	-- the title: the menu's own setTitle (titleText is the words, titleElement the text on screen; sandbox 0537: V3
	-- called setText on the words), in the seed colour (the lobby keeps the game's own dark: the brief's maroon would
	-- not read on it)
	guard("the title", function()
		menu:setTitle("RELUDUALI THEATER")
		menu.titleElement:setRGB(Look.seed[1], Look.seed[2], Look.seed[3])
		local panels = menu.panelManager and menu.panelManager.panels
		if panels and panels.buttonPane then panels.buttonPane.titleText = "RELUDUALI THEATER" end
	end)
	guard("the mark", function()
		local mark = LUI.UIText.new()
		mark:setLeftRight(false, true, -480, -48)
		mark:setTopBottom(true, false, 32, 32 + CoD.textSize.Condensed)
		mark:setFont(CoD.fonts.Condensed)
		mark:setAlignment(LUI.Alignment.Right)
		mark:setRGB(Look.seed[1], Look.seed[2], Look.seed[3])
		mark:setText("Reluduali Black Ops 2")
		menu:addElement(mark)
	end)
	guard("the film hint", function()
		local film = dvarString("reluduali_theater_film")
		if film == "" then return end
		local hint = LUI.UIText.new()
		hint:setLeftRight(false, true, -480, -48)
		hint:setTopBottom(true, false, 32 + CoD.textSize.Condensed + 4, 32 + CoD.textSize.Condensed + 4 + CoD.textSize.ExtraSmall)
		hint:setFont(CoD.fonts.ExtraSmall)
		hint:setAlignment(LUI.Alignment.Right)
		hint:setRGB(Look.text[1], Look.text[2], Look.text[3])
		hint:setText("From the Console: " .. film)
		menu:addElement(hint)
	end)
	guard("the globe", ShowGlobe)
	guard("the back button", function()
		menu:registerEventHandler("button_prompt_back", function(self, event)
			guard("the quit popup", function() self:openPopup("QuitPopup", event.controller or controller or 0) end)
		end)
	end)
	say("reluduali_theater_step", "theater lobby open")
	say("reluduali_theater_ready", 1)
end

-- the live-Lua loop ("+set reluduali_lua_dev 1"): code put into the dvar reluduali_lua runs, its answer goes to
-- reluduali_lua_result. Over RipperBridge's pipe: cmd set reluduali_lua "return 6 * 7", then dvar reluduali_lua_result
-- ("ok: 42"; the bridge's own setdvar writes numbers only - sandbox 0532)
-- The poller lives on the root and on every menu we make: in theater mode the root's own went quiet once the lobbies
-- changed (sandbox 0537, 0552: the code was taken in, no answer came), the menus' ones are there wherever the game is.
-- Two pollers in the same frame cannot run the code twice: the first empties the dvar before the second reads it.
-- The switch is read at every tick, not only at the start: "+set reluduali_lua_dev 1" on the command line never became a
-- dvar in the sandbox (0557: the other flags did), while "cmd set reluduali_lua_dev 1" at any time turns the loop on at
-- once, with no restart (0558). So in theater mode the pollers are always there and wait for that switch.
local function poller(element)
	if element == nil or element.reludualiDev then return end
	element.reludualiDev = true
	element:registerEventHandler("reluduali_lua_poll", function(self, event)
		if dvarInt("reluduali_lua_dev") ~= 1 then return end
		local code = dvarString("reluduali_lua")
		if code == "" then return end
		say("reluduali_lua", "")
		local f, err = loadstring(code)
		if f == nil then say("reluduali_lua_result", "compile: " .. tostring(err)) return end
		local ok, res = pcall(f)
		say("reluduali_lua_result", (ok and "ok: " or "error: ") .. tostring(res))
	end)
	element:addElement(LUI.UITimer.new(250, "reluduali_lua_poll", false, element))
end
local function devLoop(menu)
	if not (dev or theater) or loadstring == nil then return end
	if LUI.roots ~= nil then poller(LUI.roots.UIRootFull) end
	poller(menu)
end

-- each screen's maker, wrapped: the game's own first (inside pcall: its error's words are kept, then it goes on as
-- before), registered for the live loop, then ours on top of it
local after = { MainMenu = onMainMenu, MainLobby = onMainLobby, TheaterLobby = onTheaterLobby }
local function wrap(name, stock)
	return function(controller, userData)
		local ok, menu = pcall(stock, controller, userData)
		if not ok then
			say("reluduali_theater_error", name .. " (the game's own maker): " .. tostring(menu))
			error(menu, 0)
		end
		say("reluduali_theater_made", name)
		CoD.Reluduali.menus[name] = menu
		guard("the dev loop", devLoop, menu)
		guard(name, after[name], menu, controller)
		return menu
	end
end
local waiting = {}
for name, _ in pairs(after) do
	local stock = rawget(LUI.createMenu, name)
	if type(stock) == "function" then
		LUI.createMenu[name] = wrap(name, stock)
	else
		waiting[name] = true
	end
end
-- a maker defined after this file ran is wrapped as it arrives
if next(waiting) ~= nil then
	guard("the late makers", function()
		local mt = getmetatable(LUI.createMenu) or {}
		local before = mt.__newindex
		mt.__newindex = function(t, k, v)
			if waiting[k] and type(v) == "function" then
				waiting[k] = nil
				v = wrap(k, v)
			end
			if before then before(t, k, v) else rawset(t, k, v) end
		end
		setmetatable(LUI.createMenu, mt)
	end)
end

-- The game runs this file with its menus loaded and the root up (sandbox 0531), so the makers above are wrapped at once.
-- If the main menu was open before this ran (its wrapper then never fires), the root asks for the main lobby itself,
-- once, about 4 s in: the event reaches the open main menu's own handler, as a click would.
guard("the dev loop", devLoop)
-- The trace (2026-10-18, on the main test PC: "[EXE_UI_ERROR] 23292" came 1-2 s after the main lobby opened, before our ask for the
-- theater lobby, in one start of three): any Lua error inside an event, ours or the game's, has its words and the event's
-- name kept in reluduali_theater_error (the first one only), then goes on exactly as before. The bridge can read it while
-- the error box is up.
local function traced(f, where)
	return function(self, event, ...)
		local r = { pcall(f, self, event, ...) }
		if not r[1] then
			local name = tostring(type(event) == "table" and event.name or event)
			-- the game's own connecting popup, looking again after it has been closed (V6 above): nothing is left for it to
			-- do, so its error is kept and not passed on - passed on, it ends the game with "[EXE_UI_ERROR]"
			if name == "is_demonware_fetching_done" and tostring(r[2]):find("no parent", 1, true) then
				say("reluduali_theater_caught", name .. ": " .. tostring(r[2]):sub(1, 120))
				return
			end
			if dvarString("reluduali_theater_error") == "" then
				say("reluduali_theater_error", where .. " " .. name .. ": " .. tostring(r[2]))
			end
			error(r[2], 0)
		end
		return unpack(r, 2)
	end
end
if theater then
	guard("the trace", function()
		LUI.UIElement.processEvent = traced(LUI.UIElement.processEvent, "element")
		for _, name in ipairs({ "UIRootFull", "UIRoot0", "UIRoot1" }) do
			local root = LUI.roots and LUI.roots[name]
			if root and root.processEvent then root.processEvent = traced(root.processEvent, name) end
		end
	end)
end
if theater then
	guard("the start", function()
		whenOnline(LUI.roots.UIRootFull, 0, "start", function(self)
			-- only when no menu of ours has come up since this file ran (after a film the game comes back to the theater
			-- lobby itself, and nothing must be asked for then)
			if not state.mainMenuSeen and not state.lobbyOpened and not state.theaterOpened then askMainLobby(self, 0, "the root") end
		end, 40)
	end)
	say("reluduali_theater_step", "loaded")
end
say("reluduali_lua_where", inGame and "in game" or "front end")     -- for the live loop: which of the two this is
