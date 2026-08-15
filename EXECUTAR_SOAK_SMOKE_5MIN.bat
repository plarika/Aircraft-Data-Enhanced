@echo off
REM SPDX-License-Identifier: MIT
setlocal
cd /d "%~dp0"
dotnet run --project ".\tests\AircraftDataEnhanced.Tests\AircraftDataEnhanced.Tests.csproj" -c Release -p:Platform=x86 -- --soak --duration 00:05:00 --output ".\artifacts\soak\smoke-5min"
pause
