# uninstall.ps1 - Removes the Yagura Fluent Bit forwarder service and config.
#
# By default only the service registration and the deployed config are
# removed. Pass -RemoveFluentBit to also uninstall the Fluent Bit MSI.
#
# Exit codes: 0 = success, 1 = failure.

[CmdletBinding()]
param(
    [string]$ServiceName = "fluent-bit",
    [switch]$RemoveFluentBit
)

$ErrorActionPreference = "Stop"
$configDir = "C:\ProgramData\fluent-bit-yagura"

function Log([string]$msg) {
    Write-Host ("[fluent-bit-yagura] {0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg)
}

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Log "ERROR: Administrator privileges are required."
    exit 1
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $svc) {
    if ($svc.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
    }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { Log "ERROR: sc.exe delete failed with exit code $LASTEXITCODE"; exit 1 }
    Log ("Service '" + $ServiceName + "' removed.")
} else {
    Log ("Service '" + $ServiceName + "' not found; skipping.")
}

if (Test-Path $configDir) {
    Remove-Item -Path $configDir -Recurse -Force
    Log ("Removed " + $configDir)
}

if ($RemoveFluentBit) {
    $app = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match "^fluent[- ]?bit" } | Select-Object -First 1
    if ($null -ne $app -and $app.PSChildName) {
        Log ("Uninstalling MSI: " + $app.DisplayName)
        $proc = Start-Process -FilePath "msiexec.exe" `
            -ArgumentList @("/x", $app.PSChildName, "/quiet", "/norestart") -Wait -PassThru
        if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 3010) {
            Log ("ERROR: msiexec /x failed with exit code " + $proc.ExitCode)
            exit 1
        }
        Log "Fluent Bit MSI uninstalled."
    } else {
        Log "Fluent Bit MSI entry not found; skipping."
    }
}

Log "UNINSTALL_SUCCESS"
exit 0
