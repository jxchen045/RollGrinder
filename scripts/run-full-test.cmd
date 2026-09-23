@echo off
rem Double-click to run the full test and produce a zip to upload.
rem Extra arguments are passed through, e.g.:  run-full-test.cmd -SkipBuild -UiPasses sim
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-full-test.ps1" %*
echo.
pause
