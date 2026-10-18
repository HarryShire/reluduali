# Building The Reluduali Designer

Everything builds on Windows with **Visual Studio 2026 (18) Community**, installed where it goes by default, with two
workloads: **.NET desktop development** and **Desktop development with C++**. The scripts look for it there; with
another edition or folder, change the path at the top of `RipperBridge\build.cmd` and `tools\make_installer.ps1`.

| Piece | Language | Output |
|---|---|---|
| The Designer (`ReludualiConsole\`) | C#, WPF, .NET Framework 4.7.2 | `ReludualiConsole.exe` |
| BatchCore (`BatchRecorder\BatchCore\`) | C#, .NET Framework 4.7.2 | `BatchCore.dll`, built with the Designer |
| RipperBridge (`RipperBridge\`) | C++, 32-bit | `RipperBridge.Red32n` (a Redacted plugin) |
| The theater layer (`tools\theater\`) | Lua as text, inside a small bytecode stub | `ffotdmp.lua` |
| The installer (`ReludualiSetup\`) | C#, WPF, .NET Framework 4.7.2 | `ReludualiSetup-<version>-<stamp>.exe` in `release\installer` |

## The Designer and BatchCore

```
MSBuild ReludualiConsole\ReludualiConsole.csproj /p:Configuration=Release
```

or open `ReludualiConsole\ReludualiConsole.csproj` in Visual Studio and build **Release**. BatchCore is a project
reference and builds with it. The output is in `ReludualiConsole\bin\Release\`.

The Designer checks itself without a game: `ReludualiConsole.exe --theater-test <folder>` runs the theater launch's
checks (the greeting's frames for day, dusk and night, the command-line rule, placing and taking back the theater
file in a pretend game folder) and writes `theater-test.txt` there.

## RipperBridge

```
cd RipperBridge
.\build.cmd
```

(`.\build.cmd` in PowerShell; `build.cmd` in a command prompt.)

`build.cmd` sets up Visual Studio's 32-bit compiler and makes `RipperBridge.Red32n`. Redacted loads it from its
`Plugins` folder. It is 32-bit because the game is.

## The theater layer

```
powershell -ExecutionPolicy Bypass -File tools\theater\build_theater.ps1
```

The game only loads compiled Lua there, so `build_theater.ps1` wraps our text Lua (`tools\theater\lui\ffotdmp.lua`) in
a small hand-assembled stub (`hksstub.py`, Python 3) that hands the text to the game's own `loadstring`. Plain text in
that place stops the game at start.

## The installer

```
powershell -ExecutionPolicy Bypass -File tools\make_installer.ps1 -Panel <the Reluduali panel's zip> -PanelSha <its SHA-256>
```

Build RipperBridge and the theater layer first: it takes them from where those builds put them. It builds the
Designer, stages everything the installer carries (the Designer, RipperBridge, the theater files and the Reluduali
panel for Lumit), embeds that in `ReludualiSetup.exe`, and writes `release\installer\ReludualiSetup-<version>-<stamp>.exe`
with a `MANIFEST` of every file's SHA-256. `tools\installer_test.ps1 -Setup <that exe>` installs and uninstalls it in a
scratch folder and checks each step.

The Reluduali panel for Lumit is not in this repository (its source is to be published with the Reluduali Timer): the
zip comes from reluduali.com, and `-PanelSha` pins the exact one. The `MANIFEST` of a published installer lists its
SHA-256.
