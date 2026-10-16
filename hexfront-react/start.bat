@echo off
echo ========================================
echo  HEXFRONT-REACT 开发服务器
echo ========================================
echo.
echo 访问地址: http://localhost:5173
echo.
echo 按 Ctrl+C 停止服务器
echo.

cd /d "%~dp0"
npm run dev
