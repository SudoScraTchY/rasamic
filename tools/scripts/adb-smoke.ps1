# Rasa Android client smoke test. Requires: phone with USB debugging enabled
# (user action), adb on PATH, and a built APK (dotnet build src/RasaMic.Client
# -f net11.0-android). Start rasamicd on the PC first, then run this script.
#
# Usage: .\adb-smoke.ps1 [-Apk <path>] [-ServerIp <ip>] [-SoakMinutes N] [-Usb]

param(
    [string]$Apk = "$PSScriptRoot\..\..\src\RasaMic.Client\bin\Debug\net11.0-android\dev.rasamic.client-Signed.apk",
    [string]$ServerIp = "",
    [int]$SoakMinutes = 0,
    [switch]$Usb
)

$ErrorActionPreference = "Stop"
$pkg = "dev.rasamic.client"

$devices = adb devices | Select-String "device$"
if (-not $devices) { throw "No adb device. Enable Developer Options + USB debugging on the phone (user action)." }

Write-Host "Installing $Apk"
adb install -r $Apk

adb shell pm grant $pkg android.permission.RECORD_AUDIO
adb shell pm grant $pkg android.permission.POST_NOTIFICATIONS 2>$null

adb logcat -c
adb shell monkey -p $pkg -c android.intent.category.LAUNCHER 1 | Out-Null
if ($Usb) {
    adb reverse tcp:47300 tcp:47300
    Write-Host "USB mode: enable 'USB (adb reverse)' in the app and use server IP 127.0.0.1."
} else {
    Write-Host "App launched. Enter the server IP $(if ($ServerIp) { "($ServerIp)" } else { '' }) and tap Connect."
}
Write-Host "Watching logcat for Rasa/pipeline activity (Ctrl+C to stop)..."

if ($SoakMinutes -gt 0) {
    $deadline = (Get-Date).AddMinutes($SoakMinutes)
    adb logcat -v time | ForEach-Object {
        if ($_ -match "rasa|RasaMic|AudioPipeline|AndroidRuntime|FATAL") { Write-Host $_ }
        if ((Get-Date) -gt $deadline) { adb shell am force-stop $pkg; break }
    }
    Write-Host "Soak complete. Check for FATAL/ANR lines above; stats should show steady packet counts."
} else {
    adb logcat -v time | Select-String -Pattern "rasa|RasaMic|AudioPipeline|AndroidRuntime|FATAL"
}