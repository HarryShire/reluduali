The Reluduali Designer 0.7.0 - moviemaking for Black Ops II (Redacted)
=====================================================================

Written with an External Cortex, courtesy of Anthropic.

The guide, tool by tool:  https://github.com/HarryShire/reluduali/blob/main/docs/GUIDE.md
Inside the Designer, F1 opens a quick version of it.

Start: the "The Reluduali Designer" shortcut. Press Start Redacted at the top, or start the game yourself: the Designer
finds it either way, and in the Workspace layout the game sits inside the Designer (Ctrl+Alt+D brings the Designer back
while the game holds the mouse). Go to the theater by the game's own menus and start one film there, once after every
game start: films the Designer loads afterwards keep the game's jump points.

The theater launch (if you chose "Theater-only launch"): the "Reluduali Theater" shortcut greets you, starts Redacted
inside the Designer and opens the game's own theater lobby in Reluduali's look; films started there keep the game's own
jump ticks. For
that launch only, the Designer puts one file into the game (data\ui_mp\t6\ffotdmp.lua, with a note of it,
reluduali-theater-placed.txt) and takes both out again when the game closes; one of yours there is set aside in
<Redacted>\reluduali-backup and put back. Redacted started on its own stays as it always was.

Recording needs ffmpeg (https://ffmpeg.org/download.html), which is not included: tell the Batch where ffmpeg.exe is.

What the installer put where:
- The Designer: in the folder you chose (by default %LOCALAPPDATA%\Programs\Reluduali). Its settings are beside it and in
  %LOCALAPPDATA%\Reluduali\Console.
- RipperBridge (the Designer's game plugin): in Redacted's Plugins folder, and a copy in plugin\ here for Setup's
  "Install RipperBridge into the game". What was there before is kept in <Redacted>\reluduali-backup.
- The Reluduali panel for Lumit (Lumit: https://github.com/luminalmvm/Lumit), if you did not choose "Not now":
  - "beside the Designer": the "Lumit panel" folder here. In Lumit: Settings > Addons > Extensions > Install from a folder,
    and choose that folder. Lumit shows its own list of what the panel can do before it installs it.
  - "straight into Lumit": Lumit lists it the next time it starts (it does not show its own list of what the panel can
    do first).
  Needs Lumit 0.8.0 or newer (or a nightly from v0.7.1-nightly.20261015.30 on).
- "Lumit can open the Designer" (unticked to start with): a reluduali-console: link for your Windows user, for a Reluduali
  panel with an Open button (the panel in this installer has none yet). Lumit asks you before it opens it, every time.
  Setup in the Designer turns it off and on; the uninstall takes it out.

Recording into Lumit: pick the same folder in the panel (Record > Watch a folder) and in the Designer (Contest > Lumit
folder). Lumit must be open to bring a take in.

Takes go to the Batch's Output folder (Documents\Reluduali\Recordings to start with), named by their frag: the name of
the folder that holds the demo when that folder holds only this demo, else the demo's file name. A frag's POV and its
cinematics go into one folder, <Output>\<frag>\, named POV_Stnd-Ballst-360nswb_t0001 (Short) or
"<frag> - POV - take0001" (Full): the Cam Queue's File names. A take never replaces an earlier one.

Starting Redacted with your own flags: Redacted ignores every second +command on its command line. Put +wait between
them: +set com_maxfps 250 +wait +set cg_fov 80. The Designer's own starts already do this.

Uninstall: Windows Settings > Apps > Installed apps > The Reluduali Designer, or double-click "Uninstall Reluduali.exe" in
the Designer's folder. It takes out only what the installer put down and puts back what it set aside. Your presets and
recordings stay, and so do the files the Designer made in its folder (its settings and its log).

If something breaks, the Designer keeps running and writes ReludualiConsole.log beside itself: send that file.

Licences: The Reluduali Designer is MIT licensed, Copyright (c) 2026 Mycelium (LICENSE.txt beside the Designer). The
Reluduali panel for Lumit, the RELUDUALI lettering and the Reluduali mark are Reluduali's (THIRD-PARTY-NOTICES.txt).
The colours follow the Ableton Live theme "Eye-pleasing" by dede.zak97. Lumit is luminalmvm's, under the GPL-3.0, and is
not included. Black Ops II is Treyarch's and Activision's; Redacted is the Redacted project's; this project is
affiliated with neither, and no game files are included.
Source: https://github.com/HarryShire/reluduali
