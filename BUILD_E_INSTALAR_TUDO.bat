@echo off
REM SPDX-License-Identifier: MIT
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
set "SOLUTION=%CD%\AircraftDataEnhanced.sln"
set "TESTS=%CD%\tests\AircraftDataEnhanced.Tests\AircraftDataEnhanced.Tests.csproj"
set "LOG=%CD%\BUILD_INSTALL.log"
echo ==============================================================
echo Aircraft Data Enhanced v1.0.0 STABLE - BUILD + INSTALL
echo ==============================================================
echo Official SDR# SDK for Plugin Developers:
echo https://airspy.com/download/
echo.
where dotnet >nul 2>&1
if errorlevel 1 goto :missing_dotnet
for /f "delims=" %%V in ('dotnet --version') do set "DOTNET=%%V"
if not "%DOTNET%"=="9.0.316" goto :wrong_dotnet
if not exist "lib\SDRSharp.Common.dll" goto :missing_sdk
if not exist "lib\SDRSharp.Radio.dll" goto :missing_sdk
powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\Verify-SdrSharpSdk.ps1" -LibDirectory ".\lib" -ApprovedPath ".\sdk\approved-sdks.json"
if errorlevel 1 goto :sdk_unapproved
set "PY="
where py >nul 2>&1
if not errorlevel 1 goto :use_py
where python >nul 2>&1
if not errorlevel 1 goto :use_python
goto :missing_python
:use_py
set "PY=py -3"
goto :python_ready
:use_python
set "PY=python"
:python_ready
%PY% -c "import sys; raise SystemExit(0 if sys.version_info[:2] >= (3,10) else 1)"
if errorlevel 1 goto :validation_failed
%PY% tools\verify_release_manifest.py .
if errorlevel 1 goto :validation_failed
%PY% tools\audit_public_repository.py .
if errorlevel 1 goto :validation_failed
%PY% tools\validate_p2_stable.py .
if errorlevel 1 goto :validation_failed
%PY% tools\test_vdl2_header_decoder.py
if errorlevel 1 goto :validation_failed
%PY% tools\test_vdl2_payload_decoder.py
if errorlevel 1 goto :validation_failed
%PY% tools\test_acars_envelope_parser.py
if errorlevel 1 goto :validation_failed
%PY% tools\validate_csharp_strings.py src
if errorlevel 1 goto :validation_failed
%PY% tools\test_aircraft_dashboard_lookup.py .
if errorlevel 1 goto :validation_failed
%PY% tools\test_final_visual_interface.py .
if errorlevel 1 goto :validation_failed
%PY% tools\test_native_dashboard_visual.py
if errorlevel 1 goto :validation_failed
%PY% tools\test_lower_workspace_visual.py
if errorlevel 1 goto :validation_failed
%PY% tools\test_workspace_reorganization.py .
if errorlevel 1 goto :validation_failed
%PY% tools\test_mouse_cursor_scope.py
if errorlevel 1 goto :validation_failed
%PY% tools\test_settings_export_navigation.py
if errorlevel 1 goto :validation_failed
echo Build - %DATE% %TIME% > "%LOG%"
dotnet restore "%SOLUTION%" -p:Platform=x86 >> "%LOG%" 2>&1
if errorlevel 1 goto :failed
%PY% tools\generate_release_manifest.py .
if errorlevel 1 goto :failed
dotnet build "%SOLUTION%" -c Release -p:Platform=x86 --no-restore >> "%LOG%" 2>&1
if errorlevel 1 goto :failed
dotnet run --project "%TESTS%" -c Release -p:Platform=x86 --no-build >> "%LOG%" 2>&1
if errorlevel 1 goto :failed
set "OUTDIR=%CD%\src\AircraftDataEnhanced.SdrSharpAdapter\bin\x86\Release\net9.0-windows\win-x86"
if not exist "%OUTDIR%\SDRSharp.Plugin.AircraftDataEnhanced.dll" goto :failed
powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\New-BinaryManifest.ps1" -OutputDirectory "%OUTDIR%"
if errorlevel 1 goto :failed
echo [OK] Build, tests and binary manifest passed.
set /p "SDRDIR=Indique a pasta principal do SDRSharp: "
if not exist "%SDRDIR%\SDRSharp.dotnet9.exe" goto :bad_host
tasklist /FI "IMAGENAME eq SDRSharp.dotnet9.exe" 2>nul | find /I "SDRSharp.dotnet9.exe" >nul
if not errorlevel 1 goto :host_open
set "DEST=%SDRDIR%\Plugins\AircraftDataEnhanced"
if exist "%DEST%" rmdir /s /q "%DEST%"
mkdir "%DEST%"
if errorlevel 1 goto :failed
robocopy "%OUTDIR%" "%DEST%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :failed
echo [OK] Instalado em: %DEST%
echo [INFO] Soak de release: EXECUTAR_SOAK_24H.bat
pause
exit /b 0
:missing_dotnet
echo [ERRO] .NET SDK nao encontrado.
pause
exit /b 1
:wrong_dotnet
echo [ERRO] SDK exato obrigatorio: 9.0.316. Encontrado: %DOTNET%
pause
exit /b 1
:missing_sdk
echo [ERRO] Copie SDRSharp.Common.dll e SDRSharp.Radio.dll para lib.
pause
exit /b 1
:sdk_unapproved
echo [ERRO] Execute PREPARAR_SDK_ESTAVEL.ps1 para aprovar os hashes exatos.
pause
exit /b 1
:missing_python
echo [ERRO] Python 3.10 ou superior nao encontrado.
pause
exit /b 1
:validation_failed
echo [ERRO] Uma validacao preventiva falhou.
pause
exit /b 1
:bad_host
echo [ERRO] SDRSharp.dotnet9.exe nao encontrado.
pause
exit /b 1
:host_open
echo [ERRO] Feche o SDRSharp antes de instalar.
pause
exit /b 1
:failed
echo [ERRO] Build/teste/instalacao falhou. Ultimas linhas:
powershell -NoProfile -Command "Get-Content -LiteralPath '%LOG%' -Tail 140"
pause
exit /b 1
