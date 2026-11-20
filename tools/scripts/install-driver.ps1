# Rasa Mic driver install (test-signed). Run from an elevated PowerShell.
#
# PREREQUISITE (user action, never automated):
#   bcdedit /set testsigning on
#   ...then reboot. Secure Boot must be off or support testsigning.
#
# Usage: .\install-driver.ps1 -PackageDir <path to driver package with .inf/.sys/.cat>
# Uninstall: pnputil /delete-driver <oem#.inf> /uninstall /force  (find via pnputil /enum-drivers)

param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [string]$DevconPath = "devcon.exe"
)

$ErrorActionPreference = "Stop"

$inf = Get-ChildItem -Path $PackageDir -Filter *.inf | Select-Object -First 1
if (-not $inf) { throw "No .inf found in $PackageDir" }

# 1. Test certificate (created once, reused on reruns).
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=RasaMic Test" } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=RasaMic Test" `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
}

# 2. Trust the cert on this machine (admin).
$store = Get-Item Cert:\LocalMachine\Root
$store.Open("ReadWrite")
$store.Add($cert)
$store.Close()
$pub = Get-Item Cert:\LocalMachine\TrustedPublisher
$pub.Open("ReadWrite")
$pub.Add($cert)
$pub.Close()

# 3. Sign the driver binary and catalog.
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found (install Windows SDK)" }

$sys = Get-ChildItem -Path $PackageDir -Filter *.sys | Select-Object -First 1
& $signtool.FullName sign /fd SHA256 /a /n "RasaMic Test" $sys.FullName
$cat = Get-ChildItem -Path $PackageDir -Filter *.cat | Select-Object -First 1
if ($cat) { & $signtool.FullName sign /fd SHA256 /a /n "RasaMic Test" $cat.FullName }

# 4. Install the driver package and create the root device (one UAC).
pnputil /add-driver $inf.FullName /install
& $DevconPath install $inf.FullName "ROOT\RasaMic"

Write-Host "Done. 'Rasa Mic' should appear in Sound settings as a recording device."
Write-Host "Start the server with: rasamicd --driver"