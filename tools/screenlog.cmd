@echo off
rem Double-click this to record what happens to your screens while you are away.
rem The log lands on your Desktop as dimly-screenlog.txt.
rem
rem Records for 15 minutes: long enough for a 3 minute screen timeout, ten minutes away,
rem and the wake afterwards. Close the window early if you want to stop sooner.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0screenlog.ps1" -Minutes 15
