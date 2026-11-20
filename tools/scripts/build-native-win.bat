@echo off
call "D:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
"M:\Android\Sdks\cmake\3.22.1\bin\cmake.exe" -G Ninja -S "M:\Agentic\Projects\WoMic-Foss\native\rasamic-native" -B "M:\Agentic\Projects\WoMic-Foss\native\rasamic-native\build-win-x64" -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
"M:\Android\Sdks\cmake\3.22.1\bin\cmake.exe" --build "M:\Agentic\Projects\WoMic-Foss\native\rasamic-native\build-win-x64"