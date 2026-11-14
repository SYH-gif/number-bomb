@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "CF=%~dp0tools\cloudflared.exe"
if not exist "%CF%" (
  echo.
  echo   隧道工具不见了。重新下一个放到 tools\ 里：
  echo   https://github.com/cloudflare/cloudflared/releases
  echo.
  pause
  exit /b 1
)

echo.
echo   正在开门 —— 网站 + 公网隧道，大概二十秒。
echo.

rem 网站没开就开一个
netstat -ano | findstr ":5080 " | findstr LISTENING >nul
if errorlevel 1 (
  start "数字炸弹" cmd /c "%~dp0run.bat"
  timeout /t 6 >nul
) else (
  echo   网站已经开着了。
)

rem 隧道已经在跑就沿用它的地址。重开隧道 = 换新地址，能避免就避免。
tasklist /fi "imagename eq cloudflared.exe" 2>nul | findstr /i /c:"cloudflared.exe" >nul
if not errorlevel 1 (
  echo   隧道已经开着，地址不变。
) else (
  rem 新开隧道才需要清日志，免得读到上一次的旧域名
  if exist "%~dp0tunnel.log" del /q "%~dp0tunnel.log" >nul 2>nul
  start "数字炸弹·隧道" /min "%CF%" tunnel --url http://localhost:5080 --no-autoupdate --logfile "%~dp0tunnel.log"
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-url.ps1"

echo   这个窗口可以关了。网站和隧道各自在自己的窗口里跑，关掉就断。
echo.
pause
