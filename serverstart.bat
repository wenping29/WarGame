
@echo off
echo Path:%cd%


cd /d "%~dp0"
echo Path:%cd%

cd server/hexfront.api/
echo Path:%cd%

dotnet run --project hexfront.api.csproj

pause
