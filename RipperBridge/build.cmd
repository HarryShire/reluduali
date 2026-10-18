@echo off
rem Builds RipperBridge.Red32n (32-bit). Run from this folder.
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars32.bat" >nul 2>nul
cl /nologo /O2 /MT /EHa /W3 /LD RipperBridge.cpp /Fe:RipperBridge.dll /link /NOLOGO user32.lib d3d11.lib dxgi.lib
if errorlevel 1 exit /b 1
copy /y RipperBridge.dll RipperBridge.Red32n >nul
del /q RipperBridge.obj RipperBridge.exp RipperBridge.lib 2>nul
