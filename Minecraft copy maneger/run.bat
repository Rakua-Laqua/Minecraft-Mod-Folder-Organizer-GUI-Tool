@echo off
chcp 65001 > nul
cd /d "%~dp0"
echo Minecraft Copy Manager を起動しています...
python main.py
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo アプリケーションがエラーで終了しました。
    pause
)
