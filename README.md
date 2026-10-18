# The Reluduali Designer

Written with an External Cortex, courtesy of Anthropic.

Moviemaking for Black Ops II on Redacted: your films, their look, your cameras and the recording, in one window,
with the game sitting inside it.

**Version 0.7.2.** New since 0.7.1: the Batch finds ffmpeg by itself (an empty ffmpeg box no longer fails every take).
Since 0.7.0: the Reluduali Theater launch no longer stops on an "[EXE_UI_ERROR]" box; a second Theater launch while
the Designer is open works; a **Sign in** button with an **Account** page; **Share to reluduali.com** for your looks,
presets, CFGs and camos; an update notifier.

- [What it does](#what-it-does)
- [Download](#download)
- [Requirements](#requirements)
- [Install](#install)
- [Quick start](#quick-start)
- [The three stages](#the-three-stages)
- [The game inside the Designer](#the-game-inside-the-designer)
- [The Reluduali Theater launch](#the-reluduali-theater-launch)
- [Recording](#recording)
- [Lumit and reluduali.com](#lumit-and-reludualicom)
- [Good to know](#good-to-know)
- [Troubleshooting](#troubleshooting)
- [What it changes on your PC](#what-it-changes-on-your-pc)
- [Uninstall](#uninstall)
- [Credits and licences](#credits-and-licences)
- [Building from source](#building-from-source)
- [What's in this repository](#whats-in-this-repository)

The full guide, tool by tool, is in **[docs/GUIDE.md](docs/GUIDE.md)**.

---

## What it does

- **Films:** finds your films, loads them into the game's own theater safely, plays, pauses, jumps to a time, keeps
  bookmarks.
- **Looks, live:** fog (with blend modes the game never had), sun, ambient occlusion, depth of field, per-map looks,
  skies. Every slider acts while you drag it.
- **Cameras:** the Cam Queue keeps the camera paths you place in the theater and your POV takes, and shows the paths
  in 3D views you can turn, pin and pop out.
- **Recording:** the Batch records a whole list of takes by itself, at any frame rate, through RipperBridge's own
  recorder, frame for frame. Optional passes: depth, a clean plate, the world without the players, the players alone.
- **Moves:** move sliders while the film plays and the moves are kept against the film's own clock; in recordings they
  land on the exact frame.
- **The game inside the Designer:** BO2 docked beside your tools (Workspace), or the tools floating over the game
  (Overlay).
- **The Reluduali Theater launch:** a greeting, then the game straight into its theater, in Reluduali's look.
- **Lumit:** the Reluduali panel for [Lumit](https://github.com/luminalmvm/Lumit) times reluduali.com's contests, and
  finished takes can go straight into a Lumit folder.

## Download

Get **ReludualiSetup-0.7.2.exe** (or **ReludualiSetup-0.7.2-maps.exe**, with the 31 maps' 3D shapes included) from the
[latest release](https://github.com/HarryShire/reluduali/releases/latest), or from
[reluduali.com](https://reluduali.com). One file: the Designer, its game plugin and the Reluduali panel for Lumit.

## Requirements

| | |
|---|---|
| Windows | Windows 10 or 11. |
| .NET | .NET Framework 4.7.2 or later (part of Windows 10 1803 and later). |
| Game | Black Ops II for PC, played through **Redacted** (`t6mpv43.exe`), offline. The Designer and its plugin work only with Redacted. They never touch retail Steam Black Ops II. |
| Display mode | **Windowed** or **fullscreen windowed**. Exclusive fullscreen minimises the game when you click the Designer. |
| Recording | **ffmpeg** ([ffmpeg.org](https://ffmpeg.org/download.html)): the Batch sends the frames to it. It is not included: install it (in PowerShell: `winget install Gyan.FFmpeg`) and the Batch finds it by itself, or point the Batch's ffmpeg box at `ffmpeg.exe`. |
| Lumit (optional) | [Lumit](https://github.com/luminalmvm/Lumit) 0.8.0 or newer (or a nightly from v0.7.1-nightly.20261015.30 on), for the Reluduali panel. |

## Install

1. **Close the game.**
2. Run **ReludualiSetup-0.7.2.exe** (or the -maps one). It installs for your Windows user only: nothing needs an administrator.
3. Check the choices:
   - **Redacted's folder**: the one with `t6mpv43.exe`. It is filled in when Redacted is in Steam's usual folder;
     otherwise type it or press **Choose...**.
   - **Install the Designer to**: by default `%LOCALAPPDATA%\Programs\Reluduali`.
   - **RipperBridge, the game plugin**: goes into Redacted's `Plugins` folder. The Designer talks to the game through
     it. One already there is set aside, never deleted.
   - **Lumit can open the Designer**: a `reluduali-console:` link for your Windows user, for a Reluduali panel with an
     Open button (the panel in this installer has none yet, so it starts unticked). Lumit asks you first, every time.
   - **A shortcut on the desktop** (one in the Start menu is always added).
   - **Theater-only launch**: adds a **Reluduali Theater** shortcut, in the Start menu and on the desktop (see
     [The Reluduali Theater launch](#the-reluduali-theater-launch)).
   - **The Reluduali panel for Lumit**: **beside the Designer** (you add it in Lumit yourself, and Lumit shows what it
     can do first), **straight into Lumit**, or **Not now**.
4. Press **Install**, then **Open the Designer**.

## Quick start

1. **Start Redacted**, or press **Start Redacted** at the top of the Designer. The Designer finds the game either way.
   The pills at the top turn green: Redacted, RipperBridge.
2. **Pre-Production > Setup** shows where it found the game and what this PC is missing. If RipperBridge is missing,
   Setup installs it (close the game first).
3. **Go to the theater by the game's own menus and start one film there (**Select Film**, **Start Film**)**, once after every game
   start. Films the Designer loads afterwards keep the game's own jump points, and seeking needs them. The Reluduali
   Theater launch takes you to the theater lobby: start that first film there.
4. **Demos**: pick a film and press **Load in theater**.
5. **Look**: Fog, Sun, SSAO, DOF, Look. Drag the sliders while the film plays.
6. **Cam Queue**: in the film, switch to the free camera, right-click for the dolly (press **Enter** on its notice),
   right-click again for **Edit Camera**, and place markers (**F** inserts one). Then press **SAVE MY MARKERS**: your
   path appears as a row and in the 3D views.
7. **Batch** (or **RUN QUEUE** on the Cam Queue): records the takes by itself.

**F1** opens the Help page from anywhere. The logo (or **Ctrl+Home**) goes back to the start screen.

## The three stages

The start screen is the three stages of making a film. The rail on the left lists the open stage's tools in working
order.

| Stage | Tools |
|---|---|
| **1 Pre-Production** (this PC) | **Setup**: the game, the plugin, the folders. |
| **2 Principal Vegaography** (the game) | **Film**: Demos, Theater. **Look**: Look, Fog, Sun, SSAO, DOF, Sky. **Control**: CFG, Custom. **Shoot**: Cam Queue, Batch. |
| **3 Post-Production** (Blender) | Comes in a later version. |

Beside them: **Help** and **Contest**. Every tool is described in [docs/GUIDE.md](docs/GUIDE.md).

## The game inside the Designer

At the top: **Classic**, **Workspace**, **Overlay**.

- **Classic**: the game in its own window, the Designer in its own.
- **Workspace**: the game sits in a slot beside (or below) the page you are using.
- **Overlay**: the game takes the whole area and the page floats over it, see-through by its eye slider.

Click the game to play it, and click the Designer to come back. While the game holds the mouse (the free camera,
first person), a click cannot leave it: press **Ctrl+Alt+D**, which brings the Designer back from anywhere. The click
that brings the game to the front does nothing else in the game. The recording size never changes with the slot's size: the picture is only scaled. If the
game restarts, press **Re-attach**.

## The Reluduali Theater launch

With **Theater-only launch** ticked at install, the **Reluduali Theater** shortcut opens a greeting (the sky follows
the time of day: blue by day, warm at sunrise and sunset, the website's warm night colours after dark), starts
Redacted docked in the Designer, and opens the game's own theater lobby in Reluduali's look. The greeting fades once
the lobby is ready. Films started from the lobby keep the game's own jump points: start your first film there, and the
Designer's own loads keep them too from then on.

For that launch only, the Designer puts one file into the game, `data\ui_mp\t6\ffotdmp.lua`, with a note of what it
placed (`reluduali-theater-placed.txt` in the game's folder), and takes both out again when the game closes. A file of
yours already there is set aside in `<Redacted>\reluduali-backup` and put back. **Redacted started on its own stays
exactly as it always was.**

## Recording

The **Batch** records a list of takes by itself. Its take list is a CSV with `folder`, `clock_in` and `clock_out`
columns, loaded with **Load** (the Cam Queue writes its own). With **Load each demo** ticked it loads each take's film,
goes to the player, seeks, records and moves on; with it off, it records from the film already in the theater (hide
the theater bar and the HUD yourself then). The **Cam Queue's RUN QUEUE** records your camera paths and POV takes the
same way (Load each demo is always on for it), records a failed row once more at the end, then, as ticked under it,
quits the game and can shut the PC down two minutes later (`shutdown /a` cancels). **Stop** ends the night there: the
game stays open and the PC stays on.

- **The recorder** is RipperBridge's own: it steps the film one frame at a time, in whole milliseconds, so nothing is
  dropped at rates that divide 1000 (1000, 500, 250, 200, 125, 100 ...), and sends each frame to ffmpeg.
- **Formats:** H.264 all-intra, DNxHR HQ, ProRes 422 HQ, ProRes 4444, Ut Video lossless, or HEVC on the GPU.
- **Passes:**
  - **Depth**: from the take's own frames, a 16-bit PNG sequence in `<take> - depth\` (near is white, black at the
    Depth distance, 2000 game units to start with, and beyond; the sky is black).
  - **Clean plate**: from the take's own frames, `<take> - plate.<ext>`: the scene before bloom, the grade and the HUD.
  - **No players**: the same ticks recorded again after the take, `<take> - world`: the players, their guns and their
    gear taken out.
  - **Players only**: the same ticks again, `<take> - players`: the players with their guns, on black.
- **Fog's blend modes** show at once, but recordings leave them out until you tick **Lock it in place**. Locked, the
  fog goes into the takes and the world pass, never the players pass.
- **Dry run** (ticked to start with) goes through everything except the recording. It sends the CFG, not the HUD line
  or the look, and does not check ffmpeg, the output folder or the free space.
- **Where takes go:** the Batch's **Output folder** (`Documents\Reluduali\Recordings` to start with). A take is named by
  its frag: the name of the folder that holds the demo when that folder holds only this demo, else the demo's file
  name (a film in the game's own theater folder is named "FILM_MP": give it a frag name of your own on the Cam Queue's
  row). A take never replaces an earlier recording: a second one gets " (2)".

## Lumit and reluduali.com

[**Lumit**](https://github.com/luminalmvm/Lumit) is an editor by luminalmvm ([lumitlab.com](https://lumitlab.com)).
The **Reluduali panel for Lumit** (made by Reluduali, included in the installer; its source is to be published with
the Reluduali Timer) puts reluduali.com's contest clock inside Lumit.

- **Sign in to Reluduali:** **Sign in** at the top right (or **Contest > Account**), then **Sign in with the
  browser...** opens reluduali.com's sign-in page in your browser with a short code; sign in there and press **Allow**.
  Your friends, contests and hand-ins then show in the Designer.
- **Share to reluduali.com:** on the **Account** page, pick a global look, a page preset, a CFG, a map look, your
  custom sliders or a Camo Editor package, name it and press **Share**. It goes on your page on reluduali.com; **Your
  shares** lists them, each with **Remove**. Sharing needs a sign-in that allowed it: if the card says yours does not,
  sign out and in again.
- **Updates:** the Designer asks reluduali.com about new versions and notices when it starts and every few hours, and
  shows them under the header (**Download**, or close it). Only its version and edition are sent. **SETUP > Updates >
  Check now** asks at once.
- **Install the panel:** if you chose *beside the Designer*, open Lumit, go to Settings > Addons > Extensions >
  Install from a folder, and pick the **Lumit panel** folder in the Designer's folder.
- **Link the Designer:** in the panel, press **Record > Link the Console**, then type the code into the Designer's
  **Contest** page (**Link...**). From then on the Designer reports your in-game work and queued recording at the
  contest's rates, about once a minute. The Contest page lists every timer message it sends.
- **Takes into Lumit:** pick the same folder in the panel (**Record > Watch a folder**) and in the Designer
  (**Contest > Lumit folder**). Each finished take's videos are copied there whole (its passes too). Lumit must be
  open to bring a take in.

## Good to know

- **Load films with the Designer, not twice in a row:** two loads within 10 seconds crash the game; Load in theater
  waits for you.
- **A film or map load puts the map's own fog back:** send your look again after a load.
- **Starting Redacted with your own flags:** Redacted ignores every second `+command` on its command line. Put
  `+wait` between them: `+set com_maxfps 250 +wait +set cg_fov 80`. The Designer's own starts already do this.
- **Two Designers at once:** a second one only brings the first to the front.

## Troubleshooting

| What you see | What to do |
|---|---|
| A seek or **Go to** does not land | Go to the theater by the game's menus and start one film there, then load the film again. |
| The game has the mouse and a click cannot reach the Designer | Press **Ctrl+Alt+D**. |
| The pills stay grey after the game restarted | Press **Re-attach**. |
| The game minimises when you click the Designer | Use windowed or fullscreen windowed, not exclusive fullscreen. |
| The Batch says it cannot find ffmpeg | Install it (`winget install Gyan.FFmpeg`), or point the Batch's ffmpeg box at your `ffmpeg.exe`. |
| Something else | The Designer keeps running and writes **ReludualiConsole.log** beside itself: send that file. |

## What it changes on your PC

- **The Designer** lives in the folder you chose, with its settings beside it and in
  `%LOCALAPPDATA%\Reluduali\Console`; its shortcuts in the Start menu (and on the desktop, if you ticked it).
- **RipperBridge** goes into Redacted's `Plugins` folder (and a copy beside the Designer, for Setup's **Install
  RipperBridge into the game**). What was there before is kept in `<Redacted>\reluduali-backup`.
- **Your work** goes to `Documents\Reluduali`: `Recordings` (the Batch), `queues` (the Cam Queue), `maps` (the 3D views'
  map shapes, made from your own game).
- **Sky** never deletes anything: the files it replaces are moved into the sky store's `removed` folder, from where you
  can copy them back.
- **The theater file** is in the game only while a Reluduali Theater launch runs (above).
- **Your sign-in** is kept, encrypted for your Windows user, in `%LOCALAPPDATA%\Reluduali\Console\account.bin`; the
  notices you closed in `updates-seen.txt` beside it.
- **Lumit can open the Designer** adds a `reluduali-console:` link for your Windows user. Setup turns it off and on;
  the uninstall takes it out.
- **`C:\ProgramData\Reluduali`** is used only when your Windows temp folder's name has letters the game plugin cannot
  be sent: the map shapes are made there for a moment.

## Uninstall

Windows Settings > Apps > Installed apps > **The Reluduali Designer**, or double-click **Uninstall Reluduali.exe** in
the Designer's folder. It takes out only what the installer put down and puts back what it set aside. Your presets and
recordings stay, and so do the files the Designer made in its folder while you used it (its settings and its log): delete
that folder yourself if you want it all gone.

## Credits and licences

**The Reluduali Designer** (the Designer, RipperBridge, the batch recorder, the theater launch and the installer) is
**MIT licensed**: see [LICENSE](LICENSE). Copyright (c) 2026 Mycelium.

- **[Lumit](https://github.com/luminalmvm/Lumit)** is luminalmvm's, under the GPL-3.0. It is not part of this
  release; the Reluduali panel runs inside it.
- **The Reluduali panel for Lumit** is Reluduali's (reluduali.com), MIT licensed; its source is to be published with
  the Reluduali Timer. **The RELUDUALI lettering** on the greeting and **the Reluduali mark** (the Designer's icon
  and logo) are Reluduali's own. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
- **The greeting's sound** is Mycelium's own sample, made for the Reluduali BO2 intro.
- **The colours** follow the Ableton Live theme "Eye-pleasing" by dede.zak97.
- **Black Ops II** is Treyarch's and Activision's; **Redacted** is the Redacted project's. This project is not
  affiliated with either. No game files are included: the Designer works with the game you already have.

## Building from source

Visual Studio 2026 (18) with .NET desktop development and Desktop development with C++.

- **The Designer and BatchCore:** open `ReludualiConsole\ReludualiConsole.csproj` and build **Release**, or
  `MSBuild ReludualiConsole\ReludualiConsole.csproj /p:Configuration=Release`.
- **RipperBridge:** in the `RipperBridge` folder run `build.cmd` (`.\build.cmd` in PowerShell; it sets up the 32-bit
  compiler itself). It makes `RipperBridge.Red32n`.
- **The theater layer:** `tools\theater\build_theater.ps1` puts `tools\theater\lui\ffotdmp.lua` into the bytecode stub
  the game loads.
- **The installer:** build RipperBridge and the theater layer first; then `tools\make_installer.ps1 -Panel <the
  Reluduali panel's zip> -PanelSha <its SHA-256>` builds the Designer and packs `ReludualiSetup-<version>-<stamp>.exe`
  in `release\installer`.

More in [docs/BUILDING.md](docs/BUILDING.md).

## What's in this repository

| Folder | What it is |
|---|---|
| `ReludualiConsole\` | The Designer (WPF, .NET Framework 4.7.2). |
| `BatchRecorder\BatchCore\` | The Batch: takes, the recorder's controls, the game's clock. |
| `RipperBridge\` | The game plugin (C++, 32-bit): the bridge, the recorder, the passes, the live looks. |
| `ReludualiSetup\` | The installer and uninstaller. |
| `tools\theater\` | The Reluduali Theater launch's layer and how it is built. |
| `docs\` | The guide and the build notes. |
