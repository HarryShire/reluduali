# The Reluduali Designer: the guide

Every tool, what it does, and how to use it. Version 0.7.8. The short version is the [README](../README.md); inside
the Designer, **F1** opens a quick version of this guide.

- [Before you start](#before-you-start)
- [Getting around](#getting-around)
- [1 Pre-Production](#1-pre-production): [Setup](#setup)
- [2 Principal Vegaography](#2-principal-vegaography): [Demos](#demos), [Theater](#theater), [Look](#look),
  [Fog](#fog), [Sun](#sun), [SSAO](#ssao), [DOF](#dof), [Sky](#sky), [CFG](#cfg), [Custom](#custom),
  [Cam Queue](#cam-queue), [Batch](#batch)
- [Live sliders, presets and moves](#live-sliders-presets-and-moves)
- [Contest](#contest)
- [The game inside the Designer](#the-game-inside-the-designer)
- [The Reluduali Theater launch](#the-reluduali-theater-launch)
- [If something goes wrong](#if-something-goes-wrong)

---

## Before you start

1. **Start Redacted.** Press **Start Redacted** at the top of the Designer, or start it the way you always do: the
   Designer finds it either way.
2. **Go to the theater by the game's own menus and start one film there (**Select Film**, **Start Film**)**, after every game start. Films the
   Designer loads after that keep the game's own jump points, and seeking needs them. A film loaded before that shows
   *Downloading: 0.00 %* and has none. The [Reluduali Theater launch](#the-reluduali-theater-launch) takes you to the
   theater lobby by itself: start that first film there.
3. **Play windowed or fullscreen windowed.** Exclusive fullscreen minimises the game when you click the Designer.

## Getting around

- **The start screen** is the three stages of making a film: **Pre-Production** (this PC), **Principal
  Vegaography** (the game: films, looks, cameras, recording) and **Post-Production** (3D in Blender, in a later
  version). Click a stage to open it.
- **The rail** on the left lists the open stage's tools in working order: Film, Look, Control, Shoot.
- **The pills** at the top show the tether to the game: **Redacted** and **RipperBridge**. Green is connected.
  **Re-attach** makes the tether again after the game restarts.
- **Help** is **F1** from anywhere. **The logo** (or **Ctrl+Home**) goes back to the start screen. **Ctrl+Alt+D**
  brings the Designer to the front from anywhere, also while the game holds the mouse.
- **The coloured button** on a page is its main action (maroon by day, lilac by night); **raspberry** is stop.

---

## 1 Pre-Production

### Setup

Where the game and your folders are on this PC, and what this PC is missing.

- It finds Redacted by itself (the folder with `t6mpv43.exe`); **Choose...** picks another.
- **Install RipperBridge into the game**: most tools need this plugin in Redacted's `Plugins` folder. Close the game
  first. One already there is set aside, never deleted.
- **Start Redacted** with the arguments you type here (for example `+set com_maxfps 250`). The Designer puts a
  `+wait` between your `+commands` by itself, because Redacted ignores every second one.
- **Lumit can open the Designer**: turns the `reluduali-console:` link off and on (for a Reluduali panel with an
  Open button; the panel in this installer has none yet).
- **3D view maps**: the Cam Queue's 3D views draw the map around your camera path. No map shapes come with the
  Designer: it makes each one from your own game. Play a film on the map, then press **Make this map's shape**. With
  **Each map by itself** on (it is, to start with), the Designer makes a map's shape by itself when a 3D view needs it
  and the game has that map loaded. The shapes are kept in `Documents\Reluduali\maps`.
- **Appearance**: **Dark**, **Light** or **Dynamic**. Dark and Light stay as they are: Dark is the velvet, Light is
  reluduali.com's card screen by day (the sky, glass and printed cards). **Dynamic** (the start) changes with the day:
  light while the Sun is up, its colours turning to the site's evening beige and orange as the Sun goes down, and dark
  once the Sun is 6 degrees below the horizon. The sky is your PC's: its time zone gives the place, nothing is asked or
  sent. **Contrast** (Soft, Normal, High) for the letters, the lines and the texture. Both show at once.

---

## 2 Principal Vegaography

### Demos

The films in the game's theater folder, or any folder you choose.

- **Search** and **Map** narrow the list.
- **Load in theater** loads the film you picked, safely: never two loads within 10 seconds, because that crashes the
  game. If the game is not running yet, the Designer starts it and asks you to open the theater once by its menus
  first.
- **Playback:** **Play / pause**, **End playback**, **Go to** a time (`1:57`, as the theater's clock shows it) or a
  tick.
- **Bookmarks:** **Mark here** names the moment you are at. Double-click a bookmark to go back to it. Bookmarks belong
  to the film they were made in.

### Theater

Live passes for the edit, while the film plays:

- **No HUD** (the kill feed goes too).
- **No gun** (first person).
- **FOV**, **blur**, **far clip**.
- **Sky off**: the background colour shows instead, for a green sky. Props that are models go too.

### Look

A look for each map: the FOV, exposure and colour, the light grid. Turn on a group (**Exposure tweak**, **Colour
control**, **Light grid tweaks**) to make its sliders count, then save it for the map (**Map look**: kept in the
`Looks` folder beside the Designer, sent before every recording on that map).

### Fog

The game's fog, live: distance fog, height fog and the sun's haze.

**Blend modes** (at the bottom): **Fog with a blend mode** mixes in a fog of its own by distance, in a mode the
map's fog never had: normal, add, screen, multiply, overlay, soft light, colour dodge or colour burn (the **Blend**
buttons pick one). It has its colour, opacity, start and end (or an exponential curve by density), and how much
covers the sky.

- You see it at once. **Recordings leave it out** until you tick **Lock it in place: record it**.
- Locked, it goes into the takes and the world pass, never the players pass.
- **Over the finished frame** puts it after the game's bloom and colour grade.
- **Reset** takes it away with the rest.

### Sun

The sun's light, colour (the game keeps only its hue; the brightness is **Sun light**), direction, flare
(**Remove the sun flare**), the sky's temperature, brightness, rotation and transition.

### SSAO

Ambient occlusion: the soft dark in corners and under things. Turn on **SSAO on**. It shows most on close walls and
on the gun.

### DOF

Depth of field, near and far. Turn on **Depth of field with these values**: without it, the game ignores them.

### Sky

Install a set of sky images into the game, or go back to the maps' own skies. Each set is a folder in the sky store
(`Stores\Skies` beside the Designer to start with; Setup > **Sky sets** changes it, **Open store** opens it). Restart
the game after a change.
Nothing is deleted: the files it replaces go into the sky store's `removed` folder, from where you can copy them back.

### Camo Editor

Paint a gun's camo or a sky and send it to the game (Dress > **Camo Editor**; Guns > **Paint a camo** and Sky >
**Paint a sky...** open it too).

- **Pick a gun:** its picture with the UV lines, and the gun in 3D beside it. Paint on either: brushes, spray, fill,
  gradient, text, tape, layers, history. Your edit is kept as you go and offered back the next time.
- **Save DDS + IWI** writes the game's two formats; **Send to the game** puts it on the gun in the running game, and
  Guns > **Back to the game's** takes it off.
- **Skies** open as six squares in a cross. Save, then Sky > **New set from my painted skies**, **Install set**, and
  restart the game.
- **The first time:** **Make the gun models** downloads OpenAssetTools from its authors' release (after asking; it is
  checked before use) and makes the guns' 3D models from your own game's files, in about half a minute. It needs
  Microsoft's Visual C++ runtime: when it is missing the Designer offers Microsoft's installer. The page runs on
  Microsoft WebView2 (part of Windows 11); when that is missing the Designer offers Microsoft's installer and opens the
  editor in your browser meanwhile.

### CFG

Type any setting or command: the box completes over every setting and command the game has, and shows its type,
default and range.

- **Enter** sends. **Up** and **Down** go through what you sent. **Read** shows what the game holds now.
- **Timescale** buttons.
- **The CFG box** sends many lines at once.
- **The library** on the right lists your `.cfg` files: add folders, double-click a file to open it.

### Custom

Your own sliders on any number setting.

- **New slider**: name the setting. The slider takes the game's own range.
- **Keys**: two keys nudge it while the game is in front (the game still gets the keys).
- **Learn MIDI**: press it, then move a fader or knob on your MIDI device (open the device in **MIDI in** first).

### Cam Queue

Your camera paths and POV takes, queued for the night, and the 3D views.

- **Camera paths:** in the film, switch to the free camera, right-click for the dolly (press **Enter** on its
  notice), right-click again for **Edit Camera**, and place markers (**F** inserts one). Then press **SAVE MY
  MARKERS**: they become a **CINE** row, the same path the game's own dolly flies. The game's markers are never
  changed. A film in the game's own theater folder is named "FILM_MP": give the row a frag name of your own.
- **POV takes:** **POV in** and **POV out** mark a stretch of a player's own view; **Add POV take** adds it as a row.
- **Row defaults:** each kind, POV and CINE, has its defaults: its CFG, frame rate, time before and after, HUD and
  film mode. A new queue gives CINE rows 1000 fps, half a second either side and no HUD; POV rows the Batch page's
  values. **Own CFG** gives a row a CFG of its own; its other values change only in `queue.csv`.
- **Jump:** type a marker's number in **Marker** (1 is the first) and the film goes there by the game's own jump.
  **Shift+click** keeps the camera where it is.
- **Check** finds every demo, path and CFG now rather than at 3 am. **Rehearse** runs the night without recording.
  **RUN QUEUE** asks first, records every row with the Batch page's settings, records a failed row once more at the
  end, then, as ticked under it, quits the game cleanly and can shut the PC down two minutes later (`shutdown /a`
  cancels). **Stop** ends the night there: the game stays open and the PC stays on.
- **File names:** **Short** (`POV_Stnd-Ballst-360nswb_t0001`), **Full** (`<frag> - POV - take0001`) or **Old** (a folder
  per row). A frag's POV and its cinematics go into one folder in the Batch's Output folder
  (`Documents\Reluduali\Recordings\<frag>\` to start with); later ones are numbered (`POV02_`, `CINE03_`). The frag is
  the name of the folder that holds the demo when that folder holds only this demo, else the demo's file name.

**The 3D views.** The path loops in every view as soon as two markers are placed, at the speed you set.

- **+ View** adds one: **Through the camera** (the loop, 1920x808, with thirds), **Top**, **Side**, **Front**,
  **Back** (or the other side), or **Free**. **Back to the default layout** puts the usual set back.
- **The map look** of each view: colour, see-through, wireframe (from a fixed side, a slice of the map around the
  path; in perspective, the whole map fading with distance) or **No map**. The Designer makes a map's shape from your
  own game (Setup > **3D view maps**); until a map has one, its views show the path without the map. With **Each map
  by itself** on, it is made as soon as a view needs it while the game has that map loaded (once a session per map).
- **The person button** is for the players' shapes, which this version does not make yet.
- **The pin** keeps a view every time the Designer opens. **Double-click** a view's header to make it the big one;
  the **pop-out** button puts it in a window of its own.
- **Blender's numpad** (or the number row) in a view you clicked: **1** front, **3** right, **7** top (**Ctrl**: the
  other side), **9** the opposite side, **5** orthographic and back, **0** through the camera, **2 4 6 8** turn it,
  **+** and **-** zoom, **.** or **Home** frames the path, **Esc** lets go of the picked camera.
- **The mouse:** in the free view, drag to turn it and right-drag to move it; on a fixed side, drag to move it. The
  wheel zooms, a double-click frames the path, a right-click adds a camera there. **Drag** a camera marker to move it,
  **Alt+drag** to aim it, **Ctrl+Z** to undo. On a queued row, an edit is saved into that row's files at once (the
  first original is kept as `<id>.orig.*`).

### Batch

Records a list of takes by itself. Its take list is a CSV with `folder`, `clock_in` and `clock_out` columns, loaded
with **Load** (the Cam Queue writes its own). With **Load each demo** ticked it loads each take's film, goes to the
player, seeks, records and moves on; with it off, it records from the film already in the theater (hide the theater bar
and the HUD yourself then).

- **The recorder** steps the film one frame at a time, in whole milliseconds, so nothing is dropped at rates that
  divide 1000 (1000, 500, 250, 200, 125, 100 ...), and sends each frame to **ffmpeg**, which you install yourself
  ([ffmpeg.org](https://ffmpeg.org/download.html)).
- **Formats:** H.264 all-intra CRF 12 (`.mp4`), DNxHR HQ (`.mov`), ProRes 422 HQ (`.mov`), ProRes 4444 (`.mov`),
  Ut Video RGB lossless (`.avi`), HEVC on the GPU (`.mp4`).
- **Passes:**
  - **Depth**: from the take's own frames, a 16-bit PNG sequence in `<take> - depth\` (near is white, black at the
    Depth distance, 2000 game units to start with, and beyond; the sky is black).
  - **Clean plate**: from the take's own frames, `<take> - plate.<ext>`: the scene before bloom, the colour grade and
    the HUD.
  - **No players**: the same ticks recorded again after the take, `<take> - world`: the players, their guns and their
    gear gone.
  - **Players only**: the same ticks again, `<take> - players`: the players with their guns, on black.
- **Where takes go:** the **Output folder** (`Documents\Reluduali\Recordings` to start with; it is made the first time).
  A take never replaces an earlier recording: a second one gets " (2)".
- **Run all**, **Run selected take**, **Stop**. **Dry run** (ticked to start with) goes through everything except the
  recording: it sends the CFG, not the HUD line or the look, and does not check ffmpeg, the output folder or the free
  space.
- **The Lumit folder:** each take's videos (its passes too) can be copied to the folder Lumit watches (see
  [Contest](#contest)).
- **Tools for when something is stuck:** read the state, send a command, press Enter in the game, read the roster,
  go to a player's view, set CODCAST.

---

## Live sliders, presets and moves

Fog, Sun, SSAO, DOF, Look, Theater and Custom work the same way.

- **Every slider acts while you drag it.** Click anywhere on a slider to jump there. Type a number in the box beside
  it for an exact value (**Enter**).
- **The toolbar:** presets first (pick, **Load**, **Save as**), then files (**Open file** takes a `.cfg`; **Copy as
  CFG**), then the game (**Read from game** fills the sliders with what it holds; **Reset** puts the game's own values
  back).
- **A coloured square** opens the colour wheel.
- **Global look** (the bar above the sliders): one look across Look, Fog, Sun, SSAO, DOF, Sky, Theater and Custom.
  **Load everywhere** sends it to every page, **Save this page to it** puts this page's sliders in, **New global look...**
  makes one from every page you have opened.
- **Fog** also has **Haze = fog colour**, **No fog** and **Random fog**.
- **A film or map load puts the map's own fog back:** send your look again after a load.

**Moves.** **Record moves**, play the film and move sliders: each move is stamped with the film's own clock.
**Play moves**: the sliders follow those stamps whenever the film passes them again, also while the Batch records.
Both on: a second pass, recording over the sliders you touch while the others play.

- **Timeline** shows the moves as curves: click to add a key, drag to move one, right-click to remove, the wheel to
  zoom, **Shift+wheel** to scroll, double-click to send the game there.
- **Save moves** and **Load moves** keep them.
- **In recordings the moves are exact:** the Batch writes them for every frame (a `.params.txt` beside the video) and
  RipperBridge applies each one on its own frame.

---

## Contest

For [reluduali.com](https://reluduali.com)'s contests, with the Reluduali panel inside
[Lumit](https://github.com/luminalmvm/Lumit).

- **Contest time:** in the panel, press **Record > Link the Console**, then type the code here (**Link...**). From
  then on the Designer reports in-game work and queued recording at the contest's rates, about once a minute.
  Editing and rendering are timed by the panel in Lumit. **What the Designer sends** lists every timer message.
- **Lumit folder:** choose the same folder you chose in the panel's **Watch a folder**. Each finished take's videos
  are copied there whole. Lumit must be open to bring them in.
- **Account: signing in to Reluduali.** Press **Sign in with the browser...**: the Designer shows a short code and
  your browser opens reluduali.com's sign-in page with it. Sign in there with your Reluduali account (the Designer
  never sees your password), check that the code matches and press **Allow**. The Designer is signed in for 30 days,
  then asks again. Signed in, it shows your friends and requests, the contests and your entry, the same as on the
  site; **Hand in** sends your entry's links. **Sign out** signs you out here and on the site.

**The Reluduali panel for Lumit** comes with the installer. If you chose *beside the Designer*, add it in Lumit:
Settings > Addons > Extensions > Install from a folder, then pick the **Lumit panel** folder in the Designer's
folder. It needs Lumit 0.8.0 or newer (or a nightly from v0.7.1-nightly.20261015.30 on).

---

## The game inside the Designer

At the top: **Classic**, **Workspace**, **Overlay**.

- **Classic**: the game in its own window.
- **Workspace**: the game in a slot beside the page you use; **Page below** puts the page under it instead.
- **Overlay**: the game takes the whole area and the page floats over it; its eye slider makes it see-through.
- **Into the game** (or a click on the game) gives the game the mouse and keys. Click the Designer to come back;
  while the game holds the mouse (the free camera, first person) a click cannot leave it, so press **Ctrl+Alt+D**.
  The click that brings the game to the front does nothing else in the game.
- **The timeline** under the docked game can be dragged: the film follows while you hold it and goes to the exact
  point where you let go.
- The recording size never changes with the slot's size: the picture is only scaled.

## The Reluduali Theater launch

The **Reluduali Theater** shortcut (if you ticked **Theater-only launch** at install):

1. A greeting fills the screen. Its sky follows the time of day: blue by day, warm at sunrise and sunset, the
   website's warm night colours after dark.
2. Redacted starts behind it, docked in the Designer, with Demos open.
3. The game goes by itself to its own theater lobby, **RELUDUALI THEATER**. The greeting fades once the lobby is
   ready.
4. **Select Film**, then **Start Film**: films start the game's own way and keep their jump points, and the
   Designer's own loads (Demos > **Load in theater**) keep them too from then on. **Back** asks before it quits.

For that launch only, the Designer puts one file into the game (`data\ui_mp\t6\ffotdmp.lua`), with a note of what it
placed (`reluduali-theater-placed.txt` in the game's folder), and takes both out again when the game closes, within a
few seconds. A file of yours already there is set aside in `<Redacted>\reluduali-backup` and put back. If the PC
crashed meanwhile, the next Designer start tidies up. Redacted started any other way stays exactly as it always was.

## If something goes wrong

- The Designer catches its own errors and keeps running. **ReludualiConsole.log** beside it says what happened:
  send that file.
- **The game restarted:** press **Re-attach**.
- **A seek or Go to does not land:** go to the theater by the game's menus and start one film there, then load the
  film again.
- **A click cannot reach the Designer** (the game holds the mouse): press **Ctrl+Alt+D**.
- **Your own Redacted shortcut ignores some flags:** put `+wait` between the `+commands` (Redacted ignores every
  second one).
