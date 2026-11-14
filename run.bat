@echo off
chcp 65001 >nul
setlocal

rem .NET 8 SDK 装在用户目录时，把它临时加进 PATH
set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"

cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo   没找到 dotnet。先装一个 .NET 8 SDK 再来。
  echo   https://dotnet.microsoft.com/download/dotnet/8.0
  echo.
  pause
  exit /b 1
)

echo.
echo   数字炸弹启动中……
echo   电脑： http://localhost
echo   手机： http://局域网IP   （同一 WiFi，启动完控制台会打出具体地址）
echo   不需要装证书，打开就能玩。
echo.
dotnet run --project "%~dp0NumberBomb.csproj" -c Release

pause
