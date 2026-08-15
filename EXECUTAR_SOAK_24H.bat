@echo off
REM SPDX-License-Identifier: MIT
setlocal
cd /d "%~dp0"
dotnet run --project ".\tests\AircraftDataEnhanced.Tests\AircraftDataEnhanced.Tests.csproj" -c Release -p:Platform=x86 -- --soak --duration 1.00:00:00 --output ".\artifacts\soak\release-24h"
if errorlevel 1 (echo [ERRO] Soak P2 falhou.& pause& exit /b 1)
echo [OK] Soak P2 de 24 horas aprovado.
pause
