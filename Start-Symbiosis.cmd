@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Symbiosis.ps1" -VR
if errorlevel 1 pause

