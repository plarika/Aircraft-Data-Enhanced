@echo off
REM SPDX-License-Identifier: MIT
setlocal
cd /d "%~dp0"
chcp 65001 >nul

echo ==============================================================
echo Aircraft Data Enhanced - VDL2 Payload + AVLC Offline
echo ==============================================================

where py >nul 2>&1
if errorlevel 1 (
  echo [ERRO] Python Launcher nao encontrado.
  pause
  exit /b 1
)

py -3 -c "import numpy" >nul 2>&1
if errorlevel 1 (
  echo A instalar numpy...
  py -3 -m pip install numpy
  if errorlevel 1 (
    echo [ERRO] Nao foi possivel instalar numpy.
    pause
    exit /b 1
  )
)

echo.
echo Selecione um ficheiro IQ .iqf32.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.OpenFileDialog; $d.Filter='IQ float32 (*.iqf32)|*.iqf32|Todos (*.*)|*.*'; if($d.ShowDialog() -eq 'OK'){[Console]::Write($d.FileName)}" > "%TEMP%\ade_payload_iq.txt"

set /p IQFILE=<"%TEMP%\ade_payload_iq.txt"
del "%TEMP%\ade_payload_iq.txt" >nul 2>&1

if not defined IQFILE (
  echo Operacao cancelada.
  exit /b 0
)

py -3 "%~dp0tools\decode_vdl2_payload.py" "%IQFILE%" --sample-rate 37500

echo.
pause
