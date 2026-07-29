@echo off
REM SPDX-License-Identifier: MIT
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

set "PROJECT=%CD%\AircraftDataEnhanced.csproj"
set "LOG=%CD%\BUILD_INSTALL.log"

echo ==============================================================
echo Aircraft Data Enhanced v0.19.0-beta - C# BUILD + INSTALL
echo ==============================================================
echo.
echo SQLite esta embutido. Nao precisa de instalar servidor nem base de dados.
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERRO] .NET SDK nao encontrado.
  echo Instale o .NET 9 SDK e volte a executar.
  pause
  exit /b 1
)

for /f "delims=" %%V in ('dotnet --version') do set "DOTNET=%%V"
echo [OK] .NET SDK: %DOTNET%

if not exist "lib\SDRSharp.Common.dll" goto :missing_sdrsharp_sdk
if not exist "lib\SDRSharp.Radio.dll" goto :missing_sdrsharp_sdk

echo Build - %DATE% %TIME% > "%LOG%"

where py >nul 2>&1
if not errorlevel 1 (
  py -3 "%~dp0tools\validate_csharp_strings.py" "%~dp0src"
  if errorlevel 1 goto 
:missing_sdrsharp_sdk
echo.
echo [ERRO] Faltam as referencias oficiais do SDR#:
echo   lib\SDRSharp.Common.dll
echo   lib\SDRSharp.Radio.dll
echo.
echo Obtenha "SDR# SDK for Plugin Developers" em:
echo   https://airspy.com/download/
echo.
echo Copie as DLL para lib. Pode executar GET_SDRSHARP_SDK.bat.
pause
exit /b 1

:validation_failed
  py -3 "%~dp0tools\validate_d8psk_analyzer.py" "%~dp0src\D8pskSymbolAnalyzer.cs"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_csharp_structure.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_capture_calls.py" "%~dp0src\IqCaptureManager.cs"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_vdl2_core.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_vdl2_payload_avlc.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_live_pipeline.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_aircraft_online_lookup.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_aircraft_dashboard.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_acars_intelligence.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_acars_parser_types.py" "%~dp0src\AcarsMessageParser.cs"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_professional_ui.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_waterfall_layout.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_verified_aircraft_only.py" "%~dp0src"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\test_verified_aircraft_only.py"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_active_aircraft_sessions.py" "%~dp0src" --project "%~dp0AircraftDataEnhanced.csproj"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\test_active_aircraft_sessions.py"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_windows_root_path.py" "%~dp0."
  if errorlevel 1 goto :validation_failed

  py -3 "%~dp0tools\validate_unsafe_async_context.py" "%~dp0src\AircraftDataPanel.cs"
  if errorlevel 1 goto :validation_failed

  py -3 "%~dp0tools\validate_local_history_database.py" "%~dp0."
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\test_local_history_database.py"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_air_operations_terminal.py" "%~dp0."
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\test_ui_preferences.py"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\test_vdl2_header_decoder.py"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\test_vdl2_payload_decoder.py"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\test_acars_envelope_parser.py"
  if errorlevel 1 goto :validation_failed
  py -3 "%~dp0tools\validate_build_output_path.py" "%~dp0."
  if errorlevel 1 goto :validation_failed
)

echo.
echo [INFO] A primeira compilacao pode descarregar Microsoft.Data.Sqlite 9.0.18 do NuGet.
echo.

dotnet restore "%PROJECT%" -r win-x86 >> "%LOG%" 2>&1
if errorlevel 1 goto :failed

dotnet build "%PROJECT%" -c Release -r win-x86 -p:Platform=x86 --no-restore >> "%LOG%" 2>&1
if errorlevel 1 goto :failed

set "OUTDIR=%CD%\bin\x86\Release\net9.0-windows\win-x86"
set "DLL=%OUTDIR%\SDRSharp.Plugin.AircraftDataEnhanced.dll"

if not exist "%DLL%" (
  echo [ERRO] A DLL compilada nao foi encontrada no output x86 esperado:
  echo %DLL%
  echo.
  echo [INFO] DLLs do plugin encontradas em bin:
  dir /s /b "%CD%\bin\SDRSharp.Plugin.AircraftDataEnhanced.dll" 2>nul
  goto :failed
)

echo [OK] Plugin compilado:
echo %DLL%
echo [OK] Pasta de output:
echo %OUTDIR%

if not exist "%OUTDIR%\Microsoft.Data.Sqlite.dll" (
  echo [ERRO] Microsoft.Data.Sqlite.dll nao foi encontrada no output x86:
  echo %OUTDIR%
  echo.
  echo [INFO] DLLs Microsoft.Data.Sqlite encontradas em bin:
  dir /s /b "%CD%\bin\Microsoft.Data.Sqlite.dll" 2>nul
  goto :failed
)

set "SQLITE_NATIVE="
if exist "%OUTDIR%\e_sqlite3.dll" set "SQLITE_NATIVE=%OUTDIR%\e_sqlite3.dll"
if exist "%OUTDIR%\runtimes\win-x86\native\e_sqlite3.dll" set "SQLITE_NATIVE=%OUTDIR%\runtimes\win-x86\native\e_sqlite3.dll"
if not defined SQLITE_NATIVE (
  echo [ERRO] e_sqlite3.dll x86 nao foi encontrada no output:
  echo %OUTDIR%
  echo.
  echo [INFO] Bibliotecas e_sqlite3 encontradas em bin:
  dir /s /b "%CD%\bin\e_sqlite3.dll" 2>nul
  goto :failed
)

echo [OK] SQLite gerida: %OUTDIR%\Microsoft.Data.Sqlite.dll
echo [OK] SQLite nativa: %SQLITE_NATIVE%
echo.
set /p "SDRDIR=Indique a pasta principal do SDRSharp: "

if not exist "%SDRDIR%\SDRSharp.dotnet9.exe" (
  echo [ERRO] SDRSharp.dotnet9.exe nao encontrado nessa pasta.
  pause
  exit /b 1
)

tasklist /FI "IMAGENAME eq SDRSharp.dotnet9.exe" 2>nul | find /I "SDRSharp.dotnet9.exe" >nul
if not errorlevel 1 (
  echo [ERRO] O SDRSharp esta aberto.
  echo Feche completamente o SDRSharp antes de instalar o plugin.
  pause
  exit /b 1
)

set "DEST=%SDRDIR%\Plugins\AircraftDataEnhanced"
if exist "%DEST%" rmdir /s /q "%DEST%"
mkdir "%DEST%"
if errorlevel 1 goto :failed

robocopy "%OUTDIR%" "%DEST%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :failed

if not exist "%DEST%\SDRSharp.Plugin.AircraftDataEnhanced.dll" (
  echo [ERRO] A DLL principal nao ficou instalada.
  goto :failed
)
if not exist "%DEST%\Microsoft.Data.Sqlite.dll" (
  echo [ERRO] Microsoft.Data.Sqlite.dll nao ficou instalada.
  goto :failed
)
if not exist "%DEST%\e_sqlite3.dll" (
  if not exist "%DEST%\runtimes\win-x86\native\e_sqlite3.dll" (
    echo [ERRO] A biblioteca SQLite x86 nao ficou instalada.
    goto :failed
  )
)

echo.
echo [OK] Instalado em:
echo %DEST%
echo.
echo [OK] SQLite embutido: nao precisa de instalar base de dados.
echo [OK] Historico persistente:
echo %%LOCALAPPDATA%%\AircraftDataEnhanced\aircraft-history.sqlite3
echo.
pause
exit /b 0

:validation_failed
echo.
echo [ERRO] Uma validacao preventiva falhou.
pause
exit /b 1

:failed
echo.
echo [ERRO] Build ou instalacao interrompida. Ultimas linhas:
powershell -NoProfile -Command "Get-Content -LiteralPath '%LOG%' -Tail 100"
pause
exit /b 1
