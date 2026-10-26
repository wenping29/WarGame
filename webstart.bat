
@echo off
echo Path:%cd%


cd /d "%~dp0"
echo Path:%cd%

cd webfront
echo Path:%cd%

npm run dev

pause
