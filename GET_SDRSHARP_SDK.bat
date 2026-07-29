@echo off
REM SPDX-License-Identifier: MIT
setlocal
chcp 65001 >nul
echo Official Airspy downloads page:
echo https://airspy.com/download/
echo.
echo Locate: SDR# SDK for Plugin Developers
echo Copy SDRSharp.Common.dll and SDRSharp.Radio.dll into: %~dp0lib
echo.
set /p "OPEN_PAGE=Open the official page now? [Y/N]: "
if /I "%OPEN_PAGE%"=="Y" start "" "https://airspy.com/download/"
pause
