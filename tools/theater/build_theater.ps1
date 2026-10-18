# build_theater.ps1 - the theater's own game file: our lui\ffotdmp.lua (the Reluduali theater layer, plain text) inside a
# bytecode stub of ours (hksstub.py: it compiles the text with the game's own loadstring and runs it), laid out where
# the game takes it from: data\ui_mp\t6\ffotdmp.lua, Redacted's loose folder (probe 3, sandbox 0531: it works there;
# main\ and the zone slots hang the game). Nothing from the game goes in. The output folder is laid out like the game's,
# ready for make_installer.ps1 (its -TheaterLayer, by default this output).
#   powershell -ExecutionPolicy Bypass -File tools\theater\build_theater.ps1 [-Out <folder>]
# Needs Python 3 as python on the PATH.
param([string]$Out = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $here)                        # tools\theater\ -> the repository
if (-not $Out) { $Out = Join-Path $root 'release\test\theater-layer' }

# only our own earlier output goes: a folder given by mistake (the game's own, say) is never emptied (the code review's C-9)
$dest = Join-Path $Out 'data\ui_mp\t6\ffotdmp.lua'
if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }
New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
& python -I (Join-Path $here 'hksstub.py') (Join-Path $here 'lui\ffotdmp.lua') 'theater' $dest
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dest)) { throw 'hksstub.py failed' }
# read it back: the stub's calls, and the text whole inside it
$check = & python -I (Join-Path $here 'hksdis.py') $dest
if ($LASTEXITCODE -ne 0 -or -not ($check -match "LOADK +r2 = 'theater'") -or -not ($check -match 'Reluduali theater, version')) { throw "$dest does not read back as the stub with the theater layer in it" }
"data\ui_mp\t6\ffotdmp.lua  {0} bytes (the stub, with {1} bytes of Lua)" -f (Get-Item $dest).Length, (Get-Item (Join-Path $here 'lui\ffotdmp.lua')).Length
"built: $Out"
