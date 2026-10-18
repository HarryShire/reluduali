# installer_test.ps1 - ReludualiSetup install -> check -> uninstall -> check, in a scratch folder (2026-10-16). Nothing real is
# touched: a fake Redacted folder (an empty t6mpv43.exe, an old RipperBridge and font in Plugins), the install folder, the
# Lumit extensions folder, the Console's config and the shortcuts all under %TEMP%\reluduali-setup-test, and the registry
# under HKCU\Software\ReludualiSetupTest (the installer's /test-root). Prints PASS/FAIL per check.
#   powershell -ExecutionPolicy Bypass -File installer_test.ps1 -Setup <ReludualiSetup.exe> [-Lumit into|beside|none]
# Whether the installer carries a font pack is read from its MANIFEST beside it (the public one carries none).
#
# -Theater (2026-10-17): the theater-only launch as well. A first install without it (no theater shortcut), then a second one
# over it with /theater - the installer takes the first out before it puts the second down, so the old RipperBridge is
# still the one kept aside, once - with the person's own "Reluduali Theater" shortcut already on the desktop: it must be
# left as it is, and the Start menu one must run the Console with --theater. The theater layer ships beside the Console
# (theater\ffotdmp.lua) and nothing of it goes into the game: the Console puts it in per theater launch.
param([Parameter(Mandatory = $true)][string]$Setup, [string]$Lumit = 'into', [switch]$Theater)
$ErrorActionPreference = 'Stop'
$s = Join-Path $env:TEMP 'reluduali-setup-test'
if (Test-Path $s) { [IO.Directory]::Delete($s, $true) }
$red = Join-Path $s 'redacted'; $inst = Join-Path $s 'install'; $outside = Join-Path $s 'outside'
New-Item -ItemType Directory -Force (Join-Path $red 'Plugins') | Out-Null
Set-Content (Join-Path $red 't6mpv43.exe') 'fake'
Set-Content (Join-Path $red 'Plugins\RipperBridge.Red32n') 'the old bridge'
Set-Content (Join-Path $red 'Plugins\RipperBridge.font') 'the old font'
$stampOf = [regex]::Match((Split-Path $Setup -Leaf), '-(\d{8}-\d{4})\.exe$').Groups[1].Value
$manifest = Join-Path (Split-Path -Parent $Setup) "MANIFEST-$stampOf.txt"
$hasFont = (Test-Path $manifest) -and [bool](Select-String -Path $manifest -SimpleMatch 'plugin\RipperBridge.font' -Quiet)
# and whether it carries map shapes (the public one carries none: the Designer makes them from the person's own game)
$hasMaps = (Test-Path $manifest) -and [bool](Select-String -Path $manifest -SimpleMatch 'console\maps\' -Quiet)
$theaterLnk = Join-Path $outside 'start-menu\Reluduali Theater.lnk'
$personsLnk = Join-Path $outside 'desktop\Reluduali Theater.lnk'
$layer = Join-Path $red 'data\ui_mp\t6\ffotdmp.lua'
New-Item -ItemType Directory -Force (Join-Path $red 'data\images') | Out-Null     # as in the game: data\ is there, data\ui_mp is not
if ($Theater) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $personsLnk) | Out-Null
    Set-Content $personsLnk 'the person''s own theater shortcut'
}
$log = Join-Path $env:TEMP 'reluduali-setup.log'; if (Test-Path $log) { Remove-Item $log }
$fails = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:fails++ } }
function Install([string[]]$extra) {
    $a = @('/silent', '/redacted', "`"$red`"", '/to', "`"$inst`"", '/lumit', $Lumit, '/test-root', "`"$outside`"") + $extra
    $p = Start-Process $Setup -ArgumentList $a -PassThru -Wait
    Check "install $($extra -join ' ') exits 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
}
function Shortcut([string]$lnk) { $w = New-Object -ComObject WScript.Shell; $l = $w.CreateShortcut($lnk); @{ Target = $l.TargetPath; Args = $l.Arguments } }

Install @()
if ($Theater) {
    Check 'no theater shortcut without /theater' (-not (Test-Path $theaterLnk))
    Check "the person's own desktop shortcut untouched" ((Get-Content $personsLnk) -eq 'the person''s own theater shortcut')
    Install @('/theater')                         # the second install, over the first
}
Check 'the Console is there' (Test-Path (Join-Path $inst 'ReludualiConsole.exe'))
if ($hasMaps) { Check '31 maps' ((Get-ChildItem (Join-Path $inst 'maps') -Filter *.rvmesh.gz).Count -eq 31) }
else { Check 'no map shapes shipped' (-not (Test-Path (Join-Path $inst 'maps'))) }
Check 'RipperBridge beside the Designer too (Setup''s Install button)' (Test-Path (Join-Path $inst 'plugin\RipperBridge.Red32n'))
Check 'the splash sample is beside the Console' ((Get-Item (Join-Path $inst 'theater\splash.wav') -ErrorAction SilentlyContinue).Length -gt 1000000)
Check 'the splash lettering is beside the Console' ((Get-Item (Join-Path $inst 'theater\reluduali.svg') -ErrorAction SilentlyContinue).Length -gt 10000)
Check 'the theater layer is beside the Console (our bytecode)' ((Test-Path (Join-Path $inst 'theater\ffotdmp.lua')) -and [IO.File]::ReadAllBytes((Join-Path $inst 'theater\ffotdmp.lua'))[0] -eq 0x1B)
Check 'nothing of the theater in the game (the Console puts it in per launch)' (-not (Test-Path $layer))
Check 'the uninstaller is there' (Test-Path (Join-Path $inst 'Uninstall Reluduali.exe'))
Check 'RipperBridge replaced (a real DLL now)' ((Get-Item (Join-Path $red 'Plugins\RipperBridge.Red32n')).Length -gt 100000)
$kept = @(Get-ChildItem (Join-Path $red 'reluduali-backup') -Filter 'RipperBridge.Red32n.*')
Check "the old RipperBridge kept aside, once ($($kept.Count))" ($kept.Count -eq 1 -and (Get-Content $kept[0].FullName) -eq 'the old bridge')
if ($hasFont) { Check 'the font pack replaced' ((Get-Item (Join-Path $red 'Plugins\RipperBridge.font')).Length -gt 100000) }
else { Check 'the font pack left alone (none in this installer)' ((Get-Content (Join-Path $red 'Plugins\RipperBridge.font')) -eq 'the old font') }
$panel = if ($Lumit -eq 'into') { Join-Path $outside 'lumit-extensions\reluduali' } else { Join-Path $inst 'Lumit panel' }
if ($Lumit -ne 'none') { Check "the panel's extension.json ($Lumit)" (Test-Path (Join-Path $panel 'extension.json')) }
Check 'redacted-folder.txt names the folder' ((Get-Content (Join-Path $outside 'console-config\redacted-folder.txt')) -eq $red)
$cmd = (Get-ItemProperty 'HKCU:\Software\ReludualiSetupTest\Classes\reluduali-console\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
Check "the link runs the exe itself ($cmd)" ($cmd -eq "`"$inst\ReludualiConsole.exe`" `"%1`"")
Check 'the Installed apps entry' (Test-Path 'HKCU:\Software\ReludualiSetupTest\Microsoft\Windows\CurrentVersion\Uninstall\Reluduali')
Check 'two shortcuts' ((Test-Path (Join-Path $outside 'start-menu\The Reluduali Designer.lnk')) -and (Test-Path (Join-Path $outside 'desktop\The Reluduali Designer.lnk')))
if ($Theater) {
    $t = if (Test-Path $theaterLnk) { Shortcut $theaterLnk } else { @{ Target = ''; Args = '' } }
    Check "the Start menu's Reluduali Theater runs the Console with --theater ($($t.Target) $($t.Args))" ($t.Target -eq (Join-Path $inst 'ReludualiConsole.exe') -and $t.Args -eq '--theater')
    Check "the person's own desktop Reluduali Theater left as it is" ((Get-Content $personsLnk) -eq 'the person''s own theater shortcut')
    Check 'the log says so' (@(Get-Content $log | Where-Object { $_ -match 'not this installer''s is already in .*desktop: left as it is' }).Count -ge 1)
}
New-Item -ItemType Directory -Force (Join-Path $inst 'Presets\GLOBAL') | Out-Null
Set-Content (Join-Path $inst 'Presets\GLOBAL\mine.cfg') 'a look of the person''s own'

$p = Start-Process (Join-Path $inst 'Uninstall Reluduali.exe') -ArgumentList '/uninstall', '/silent' -PassThru -Wait
Start-Sleep 4
Check "uninstall exits 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
Check 'the old RipperBridge back' ((Get-Content (Join-Path $red 'Plugins\RipperBridge.Red32n')) -eq 'the old bridge')
Check 'the old font back' ((Get-Content (Join-Path $red 'Plugins\RipperBridge.font')) -eq 'the old font')
Check 'the backup folder gone (empty)' (-not (Test-Path (Join-Path $red 'reluduali-backup')))
if ($Lumit -ne 'none') { Check 'the panel gone' (-not (Test-Path $panel)) }
Check 'the link gone' (-not (Test-Path 'HKCU:\Software\ReludualiSetupTest\Classes\reluduali-console'))
Check 'the Installed apps entry gone' (-not (Test-Path 'HKCU:\Software\ReludualiSetupTest\Microsoft\Windows\CurrentVersion\Uninstall\Reluduali'))
Check 'the shortcuts gone' (-not (Test-Path (Join-Path $outside 'desktop\The Reluduali Designer.lnk')))
Check 'the Console gone' (-not (Test-Path (Join-Path $inst 'ReludualiConsole.exe')))
Check "the person's own preset kept" (Test-Path (Join-Path $inst 'Presets\GLOBAL\mine.cfg'))
if ($Theater) {
    Check 'the Start menu theater shortcut gone' (-not (Test-Path $theaterLnk))
    Check "the person's own desktop Reluduali Theater still there" ((Get-Content $personsLnk) -eq 'the person''s own theater shortcut')
}
$left = @(Get-ChildItem $inst -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($inst.Length + 1) })
"left in the install folder: " + ($left -join ', ')
Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Last 4
# the test's own registry key goes too
if (Test-Path 'HKCU:\Software\ReludualiSetupTest') { Remove-Item 'HKCU:\Software\ReludualiSetupTest' -Recurse }
if ($fails) { "$fails FAILED" } else { 'ALL PASS' }
