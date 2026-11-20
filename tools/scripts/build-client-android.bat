@echo off
rem Build the Rasa MAUI Android client. Pins JDK 17 (PATH default may be JDK 11,
rem which the Android SDK tooling rejects). Usage: build-client-android.bat [Debug|Release]
setlocal
set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug
if exist "C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot" set "JAVA_HOME=C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot"
cd /d "%~dp0..\.."
dotnet build src\RasaMic.Client -f net11.0-android -c %CONFIG%
endlocal