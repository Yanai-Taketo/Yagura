# install.ps1 - Silent installer for the Yagura Fluent Bit forwarder kit.
#
# Installs Fluent Bit from an MSI placed next to this script (or -MsiPath),
# deploys the pre-configured forwarding config + Lua filter, registers
# Fluent Bit as a Windows service (auto start) and starts it.
#
# Designed for unattended push (Intune / SCCM / GPO startup script) and
# manual admin execution. ASCII-only on purpose: avoids PowerShell 5.1
# BOM/encoding traps across distribution channels.
#
# Usage:
#   powershell -NoProfile -File install.ps1 -YaguraHost 192.0.2.10
#   powershell -NoProfile -File install.ps1 -YaguraHost 192.0.2.10 -YaguraPort 514 -Channels "System,Application"
#
# Exit codes: 0 = success, 1 = failure, 3010 = success but reboot required (from msiexec).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9\.\-:]+$')]
    [string]$YaguraHost,

    [ValidateRange(1, 65535)]
    [int]$YaguraPort = 514,

    [ValidatePattern('^[A-Za-z0-9,\- ]+$')]
    [string]$Channels = "System,Application",

    [string]$MsiPath = "",

    [string]$ServiceName = "fluent-bit"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configDir = "C:\ProgramData\fluent-bit-yagura"
$fluentBitExe = "C:\Program Files\fluent-bit\bin\fluent-bit.exe"
$confTemplate = Join-Path $scriptDir "fluent-bit-yagura.conf"
$luaTemplate = Join-Path $scriptDir "winevt-severity.lua"
$confTarget = Join-Path $configDir "fluent-bit-yagura.conf"

function Log([string]$msg) {
    Write-Host ("[fluent-bit-yagura] {0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg)
}

function Fail([string]$msg) {
    Log ("ERROR: " + $msg)
    exit 1
}

# --- 0. Preconditions -------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "Administrator privileges are required."
}
foreach ($f in @($confTemplate, $luaTemplate)) {
    if (-not (Test-Path $f)) { Fail ("Kit file not found: " + $f) }
}

$rebootRequired = $false

# --- 1. Install Fluent Bit MSI (skipped when already installed) -------------
if (Test-Path $fluentBitExe) {
    Log ("Fluent Bit already installed: " + $fluentBitExe)
} else {
    if ([string]::IsNullOrEmpty($MsiPath)) {
        $msi = Get-ChildItem -Path $scriptDir -Filter "fluent-bit-*-win64.msi" |
            Sort-Object Name | Select-Object -Last 1
        if ($null -eq $msi) {
            Fail "No fluent-bit-*-win64.msi found next to the script. Place the MSI there or pass -MsiPath."
        }
        $MsiPath = $msi.FullName
    }
    if (-not (Test-Path $MsiPath)) { Fail ("MSI not found: " + $MsiPath) }

    Log ("Installing MSI silently: " + $MsiPath)
    $proc = Start-Process -FilePath "msiexec.exe" `
        -ArgumentList @("/i", ('"{0}"' -f $MsiPath), "/quiet", "/norestart") `
        -Wait -PassThru
    if ($proc.ExitCode -eq 3010) {
        $rebootRequired = $true
        Log "msiexec exit 3010 (success, reboot required)."
    } elseif ($proc.ExitCode -ne 0) {
        Fail ("msiexec failed with exit code " + $proc.ExitCode)
    }
    if (-not (Test-Path $fluentBitExe)) {
        Fail ("Fluent Bit executable not found after install: " + $fluentBitExe)
    }
    Log "MSI install completed."
}

# --- 2. Deploy config + Lua filter ------------------------------------------
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}

$conf = [IO.File]::ReadAllText($confTemplate)
$conf = $conf.Replace("@@YAGURA_HOST@@", $YaguraHost)
$conf = $conf.Replace("@@YAGURA_PORT@@", [string]$YaguraPort)
$conf = $conf.Replace("@@CHANNELS@@", $Channels)
# UTF-8 without BOM: Fluent Bit's config parser does not expect a BOM.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($confTarget, $conf, $utf8NoBom)
Copy-Item -Path $luaTemplate -Destination (Join-Path $configDir "winevt-severity.lua") -Force
Log ("Config deployed to " + $configDir + " (target: " + $YaguraHost + ":" + $YaguraPort + " udp, channels: " + $Channels + ")")

# --- 3. Validate config before touching the service -------------------------
$dryRun = Start-Process -FilePath $fluentBitExe `
    -ArgumentList @("-c", ('"{0}"' -f $confTarget), "--dry-run") `
    -Wait -PassThru -NoNewWindow
if ($dryRun.ExitCode -ne 0) {
    Fail ("Config validation failed (fluent-bit --dry-run exit " + $dryRun.ExitCode + "). See " + $confTarget)
}
Log "Config validated (--dry-run ok)."

# --- 4. Register Windows service (delayed auto start) -----------------------
# The Fluent Bit MSI (verified with 4.0.14) registers a 'fluent-bit' service
# pointing at the stock config. Passing a quoted binPath through sc.exe from
# PowerShell 5.1 is unreliable (quote mangling), so instead of editing the
# existing service we delete and recreate it via New-Service, which takes the
# quoted binPath as a plain .NET string.
$binPath = ('"{0}" -c "{1}"' -f $fluentBitExe, $confTarget)
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Log ("Service '" + $ServiceName + "' already exists; recreating with the kit configuration.")
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
    }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail ("sc.exe delete failed with exit code " + $LASTEXITCODE) }
    $deadline = (Get-Date).AddSeconds(15)
    while ($null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) { Fail ("Service '" + $ServiceName + "' is still present after delete (delete pending?).") }
        Start-Sleep -Seconds 1
    }
}
New-Service -Name $ServiceName `
    -BinaryPathName $binPath `
    -DisplayName "Fluent Bit (Yagura forwarder)" `
    -Description "Forwards Windows event logs to the Yagura syslog server via Fluent Bit." `
    -StartupType Automatic | Out-Null
# Delayed auto start (same as the MSI default): no quoting involved, safe via sc.exe.
& sc.exe config $ServiceName start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { Log "WARN: sc.exe config start= delayed-auto returned $LASTEXITCODE; service stays on plain auto start." }
Log ("Service '" + $ServiceName + "' registered (delayed auto start).")

# Restart automatically on failure (60s delay, reset counter after 1 day).
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { Log "WARN: sc.exe failure (recovery options) returned $LASTEXITCODE; continuing." }

# --- 5. Start and verify ----------------------------------------------------
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus("Running", (New-TimeSpan -Seconds 30))
Log ("Service '" + $ServiceName + "' is Running.")
Log "INSTALL_SUCCESS"

if ($rebootRequired) { exit 3010 }
exit 0
