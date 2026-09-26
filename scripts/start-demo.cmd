@echo off
rem Start the HMI against the simulated machine, for demos and operator tests.
rem Double-click it, or run it from a console with one optional argument:
rem   start-demo.cmd            simulated machine, 5x time speed
rem   start-demo.cmd 20         simulated machine, 20x time speed (1-100)
rem   start-demo.cmd offline    offline programming mode (no machine)
rem   start-demo.cmd reset      wipe the demo data first (accounts, programs, profiles, records)
rem Demo data lives in the "demo" folder next to "src" and never touches real data.
rem The solution is built (incrementally) every time, so the demo always runs the current source.
setlocal
set "ROOT=%~dp0.."
set "EXE=%ROOT%\src\RollGrinder.App\bin\Release\net8.0-windows\RollGrinder.App.exe"
set "DEMO=%ROOT%\demo"
set "MODE=--gateway sim --sim-speed 5"

if /i "%~1"=="offline" set "MODE=--offline"
echo %~1| findstr /r "^[0-9][0-9]*$" >nul && set "MODE=--gateway sim --sim-speed %~1"
if /i "%~1"=="reset" (
    echo Wiping demo data in "%DEMO%" ...
    if exist "%DEMO%" rmdir /s /q "%DEMO%"
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK not found. Install Visual Studio or the .NET 8 SDK or later, then try again.
    pause
    exit /b 1
)

echo Building (first time about a minute, later a few seconds) ...
dotnet build "%ROOT%\RollGrinder.sln" -c Release -nologo -v quiet
if errorlevel 1 (
    echo.
    echo Build failed. If the HMI is still open, close it and try again; otherwise see the errors above.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo Cannot find "%EXE%".
    pause
    exit /b 1
)

echo Starting: RollGrinder.App.exe %MODE%
echo Demo data: "%DEMO%"
start "" "%EXE%" %MODE% --config "%DEMO%\config" --data "%DEMO%\data"
