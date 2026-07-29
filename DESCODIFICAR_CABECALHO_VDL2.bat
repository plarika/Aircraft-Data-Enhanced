@echo off
REM SPDX-License-Identifier: MIT
setlocal
cd /d "%~dp0"
echo ============================================================== 
echo Aircraft Data Enhanced - Offline VDL2 Header Decoder
echo ============================================================== 
where py >nul 2>&1
if errorlevel 1 (echo [ERRO] Python Launcher nao encontrado.& pause & exit /b 1)
py -3 -c "import numpy" >nul 2>&1
if errorlevel 1 py -3 -m pip install numpy
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.OpenFileDialog; $d.Filter='IQ float32 (*.iqf32)|*.iqf32'; if($d.ShowDialog() -eq 'OK'){[Console]::Write($d.FileName)}" > "%TEMP%\ade_iq.txt"
set /p IQFILE=<"%TEMP%\ade_iq.txt"
del "%TEMP%\ade_iq.txt" >nul 2>&1
if not defined IQFILE exit /b 0
py -3 "%~dp0tools\decode_vdl2_header.py" "%IQFILE%" --sample-rate 37500
pause
