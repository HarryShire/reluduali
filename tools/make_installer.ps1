# make_installer.ps1 - ReludualiSetup.exe, the one installer of The Reluduali Designer.
# Builds the Designer (Release), stages the payload, embeds it in ReludualiSetup, builds that, and writes a MANIFEST with
# every file's SHA-256 beside it. Output: release\installer\ReludualiSetup-<version>-<stamp>.exe (not in git).
#   powershell -ExecutionPolicy Bypass -File tools\make_installer.ps1 -Panel <the Reluduali panel's zip> -PanelSha <its SHA-256>
# The installer writes the Redacted folder to %LOCALAPPDATA%\Reluduali\Console\redacted-folder.txt, where the Designer
# reads it (Where.Installed).
#
# The theater launch: the Designer carries its greeting (theater\splash.wav, theater\reluduali.svg) and the theater layer
# (theater\ffotdmp.lua, made by build_theater.ps1 into release\test\theater-layer). The Designer puts the layer into the
# game only for its own theater launches and takes it out again when that game exits; nothing of it goes into the game
# at install. -TheaterLayer <folder> takes it from elsewhere. -Out <folder> builds somewhere else. -Draft packs even while
# the documents still have a [[CONFIRM ...]] in them (never for a release).
param(
    [string]$Bridge = '',                 # default: RipperBridge\RipperBridge.Red32n (the newest build)
    [string]$Panel = '',                  # the Reluduali panel for Lumit (its zip) and its SHA-256
    [string]$PanelSha = '',
    [string]$Font = '',                   # a font pack for RipperBridge, if one is to go in (none in the public installer)
    [string]$TheaterLayer = '',
    [string]$Out = '',
    [switch]$NoBuild,
    [switch]$Draft,
    # the Test Build (2026-10-18): build.txt "test" beside the Designer - unfinished features greyed (Unfinished.cs), the
    # update notifier asks for test builds; the installer's name says "TestBuild"
    [switch]$TestBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot                  # tools\ -> the repository
$tools = Join-Path $root 'tools'
$docs = $root                                             # LICENSE and THIRD-PARTY-NOTICES.txt
$outDefault = Join-Path $root 'release\installer'
if (-not $Panel -or -not $PanelSha) { throw 'the Reluduali panel for Lumit: -Panel <its zip> -PanelSha <its SHA-256>' }
if (-not $Bridge) { $Bridge = Join-Path $root 'RipperBridge\RipperBridge.Red32n' }
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
function Build([string]$proj) {
    $p = Start-Process -FilePath $msbuild -ArgumentList "`"$proj`"", '/p:Configuration=Release', '/v:minimal', '/nologo', '/clp:ErrorsOnly', '/nodeReuse:false' -NoNewWindow -PassThru
    $null = $p.Handle      # without the handle taken now, PowerShell 5.1 reports no exit code
    try { $p.PriorityClass = 'BelowNormal' } catch {}
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "the build failed: $proj (exit $($p.ExitCode))" }
}

if (-not $NoBuild) { Build (Join-Path $root 'ReludualiConsole\ReludualiConsole.csproj') }
$bin = Join-Path $root 'ReludualiConsole\bin\Release'
$ver = (Get-Item (Join-Path $bin 'ReludualiConsole.exe')).VersionInfo.FileVersion
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$out = if ($Out) { $Out } else { $outDefault }
$stage = Join-Path $out "payload-$stamp"
New-Item -ItemType Directory -Force (Join-Path $stage 'console\Presets'), (Join-Path $stage 'console\theater'), (Join-Path $stage 'plugin'), (Join-Path $stage 'lumit') | Out-Null

# the Designer, its README, its licence and the notices of what it carries
foreach ($f in 'ReludualiConsole.exe', 'ReludualiConsole.exe.config', 'BatchCore.dll') { Copy-Item (Join-Path $bin $f) (Join-Path $stage 'console') }
# Microsoft's WebView2 SDK (2026-10-18, the Camo Editor page; ReludualiConsole\lib\webview2\PIN.md): its three DLLs beside the
# Designer, its notice in THIRD-PARTY-NOTICES.txt. The runtime is Windows' own: the Designer checks for it and offers it.
foreach ($f in 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $bin $f))) { throw "no $f beside the built Designer ($bin)" }
    Copy-Item (Join-Path $bin $f) (Join-Path $stage 'console')
}
Copy-Item (Join-Path $root 'ReludualiConsole\lib\webview2\1.0.4258.31\NOTICE.txt') (Join-Path $stage 'console\WebView2-NOTICE.txt')
if ($TestBuild) { Set-Content (Join-Path $stage 'console\build.txt') 'test' -Encoding ASCII -NoNewline; "the Test Build: build.txt beside the Designer" }
$shipped = Join-Path $tools 'presets'                     # presets that ship (fog looks for editors)
if (Test-Path $shipped) { Copy-Item (Join-Path $shipped '*') (Join-Path $stage 'console\Presets') -Recurse }
Copy-Item (Join-Path $tools 'README-RELUDUALI.txt') (Join-Path $stage 'console\README.txt')
Copy-Item (Join-Path $docs 'LICENSE') (Join-Path $stage 'console\LICENSE.txt')
Copy-Item (Join-Path $docs 'THIRD-PARTY-NOTICES.txt') (Join-Path $stage 'console\THIRD-PARTY-NOTICES.txt')
# the theater launch's greeting: the sample and RELUDUALI in Reluduali's lettering, read from theater\ beside the Designer.
# The sample is Mycelium's own (2026-10-17: "That was a sample I made, it was supposed to be for the Reluduali BO2 intro").
Copy-Item (Join-Path $tools 'theater\splash.wav') (Join-Path $stage 'console\theater\splash.wav')
Copy-Item (Join-Path $tools 'theater\reluduali.svg') (Join-Path $stage 'console\theater\reluduali.svg')

# the theater layer (see above): beside the Designer
if (-not $TheaterLayer) { $TheaterLayer = Join-Path $root 'release\test\theater-layer' }
$layer = Join-Path $TheaterLayer 'data\ui_mp\t6\ffotdmp.lua'
if (-not (Test-Path -LiteralPath $layer)) { throw "no theater layer at $layer (build_theater.ps1 builds it)" }
$head = [IO.File]::ReadAllBytes($layer)[0..4]
if ($head[0] -ne 0x1B -or [Text.Encoding]::ASCII.GetString($head, 1, 4) -ne 'LuaQ') { throw "$layer is not our bytecode stub (text would hang the game)" }
Copy-Item -LiteralPath $layer (Join-Path $stage 'console\theater\ffotdmp.lua')
"the theater layer: " + (Get-Item -LiteralPath $layer).Length + " bytes, beside the Designer"

# the game's plugin (and a font pack, only if one was given)
if (-not (Test-Path -LiteralPath $Bridge)) { throw "no RipperBridge at $Bridge" }
Copy-Item -LiteralPath $Bridge (Join-Path $stage 'plugin\RipperBridge.Red32n')
# and a copy beside the Designer, for Setup's "Install RipperBridge into the game" (a game reinstalled or moved, or the
# box unticked at install): the installer puts console\ files into the Designer's folder and records them
New-Item -ItemType Directory -Force (Join-Path $stage 'console\plugin') | Out-Null
Copy-Item -LiteralPath $Bridge (Join-Path $stage 'console\plugin\RipperBridge.Red32n')
if ($Font) { if (Test-Path -LiteralPath $Font) { Copy-Item -LiteralPath $Font (Join-Path $stage 'plugin\RipperBridge.font') } else { "no font pack at ${Font}: the installer goes without it" } }

# the Reluduali panel for Lumit: pinned by its SHA-256, copied as it is
if (-not (Test-Path -LiteralPath $Panel)) { throw "no Lumit panel at $Panel" }
$got = (Get-FileHash -LiteralPath $Panel -Algorithm SHA256).Hash
if ($got -ne $PanelSha) { throw "the Lumit panel is not the pinned one: $got (want $PanelSha)" }
Copy-Item -LiteralPath $Panel (Join-Path $stage 'lumit\reluduali-lumit.zip')
Set-Content (Join-Path $stage 'lumit\sha256.txt') $got.ToLower() -Encoding ASCII -NoNewline

# a document still waiting for a confirmation never ships
foreach ($d in 'console\README.txt', 'console\LICENSE.txt', 'console\THIRD-PARTY-NOTICES.txt') {
    if ((Get-Content -Raw (Join-Path $stage $d)) -match '\[\[CONFIRM') {
        if ($Draft) { "DRAFT: $d still has a [[CONFIRM ...]]" } else { throw "$d still has a [[CONFIRM ...]]: confirm it first (or -Draft for a test installer)" }
    }
}

# the payload into the installer
$zip = Join-Path $root 'ReludualiSetup\payload.zip'
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
# the entries must use '/': .NET Framework 4.7.2's CreateFromDirectory already does
Build (Join-Path $root 'ReludualiSetup\ReludualiSetup.csproj')
$exe = Join-Path $out ("ReludualiSetup-$ver-" + $(if ($TestBuild) { 'TestBuild-' } else { '' }) + "$stamp.exe")
Copy-Item (Join-Path $root 'ReludualiSetup\bin\Release\ReludualiSetup.exe') $exe

$manifest = @("ReludualiSetup $ver, $stamp") + (Get-ChildItem $stage -Recurse -File | ForEach-Object { '{0}  {1,10}  {2}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Length, $_.FullName.Substring($stage.Length + 1) }) +
            ('{0}  {1,10}  {2}' -f (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower(), (Get-Item $exe).Length, (Split-Path $exe -Leaf))
Set-Content (Join-Path $out "MANIFEST-$stamp.txt") $manifest -Encoding ASCII
"installer: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
