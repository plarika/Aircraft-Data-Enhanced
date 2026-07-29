@echo off
REM SPDX-License-Identifier: MIT
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

echo ==============================================================
echo Aircraft Data Enhanced - Offline VDL2 IQ Laboratory
echo ==============================================================

set "PY=py -3.11"
%PY% -c "import sys; print(sys.version)" >nul 2>&1
if errorlevel 1 (
  echo [ERRO] Python 3.11 nao foi encontrado.
  echo Instale Python 3.11 x64 e execute novamente.
  pause
  exit /b 1
)

if not exist ".venv\Scripts\python.exe" (
  echo [1/4] A criar ambiente virtual...
  %PY% -m venv .venv
  if errorlevel 1 goto :failed
)

echo [2/4] A instalar dependencias...
".venv\Scripts\python.exe" -m pip install --disable-pip-version-check -r "tools\requirements.txt"
if errorlevel 1 goto :failed

echo.
set /p "IQWAV=Indique o caminho completo do WAV IQ: "
if not exist "%IQWAV%" (
  echo [ERRO] Ficheiro nao encontrado: %IQWAV%
  pause
  exit /b 1
)

set /p "CENTER=Frequencia central em MHz [137.100]: "
if "%CENTER%"=="" set "CENTER=137.100"

echo [3/4] A analisar IQ...
".venv\Scripts\python.exe" "tools\offline_vdl2_analyzer.py" ^
  --input "%IQWAV%" ^
  --center-mhz %CENTER% ^
  --channels 136.725,136.775,136.875,136.975 ^
  --output "reports\latest" ^
  --progress
if errorlevel 1 goto :failed

echo [4/4] Analise concluida.
echo Relatorios:
echo   reports\latest\summary.json
echo   reports\latest\channel_summary.csv
echo   reports\latest\bursts.csv
start "" "reports\latest"
pause
exit /b 0

:failed
echo.
echo [ERRO] A analise falhou.
pause
exit /b 1
