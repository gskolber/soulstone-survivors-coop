@echo off
rem Double-click: build the mod, install it and open two connected game windows (tools\test-local.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-local.ps1" %*
pause
