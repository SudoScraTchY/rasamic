@echo off
setlocal
set ANDROID_SDK=M:\Android\Sdks
set NDK=%ANDROID_SDK%\ndk\26.1.10909125
if not exist "%NDK%" for /d %%D in ("%ANDROID_SDK%\ndk\*") do set NDK=%%D
set CMAKE=%ANDROID_SDK%\cmake\3.22.1\bin\cmake.exe
set ABI=%1
if "%ABI%"=="" set ABI=arm64-v8a
set OUT=build-android-%ABI%
"%CMAKE%" -S M:\Agentic\Projects\WoMic-Foss\native\rasamic-native -B M:\Agentic\Projects\WoMic-Foss\native\rasamic-native\%OUT% -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_TOOLCHAIN_FILE=%NDK%\build\cmake\android.toolchain.cmake -DANDROID_ABI=%ABI% -DANDROID_PLATFORM=android-26 -DCMAKE_MAKE_PROGRAM=%ANDROID_SDK%\cmake\3.22.1\bin\ninja.exe
if errorlevel 1 exit /b 1
"%CMAKE%" --build M:\Agentic\Projects\WoMic-Foss\native\rasamic-native\%OUT%
endlocal