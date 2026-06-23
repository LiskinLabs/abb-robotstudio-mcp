# Install ClaudeBridge Add-In for RobotStudio 2025
# Run with: powershell -ExecutionPolicy Bypass -File install-addin.ps1
# Or with custom version: powershell -ExecutionPolicy Bypass -File install-addin.ps1 -RobotStudioVersion "2024"

param(
    [string]$RobotStudioVersion = "2025"
)

$rsBase = "${env:ProgramFiles(x86)}\ABB\RobotStudio $RobotStudioVersion"
$addinName = "ClaudeBridge"
$source = "$PSScriptRoot\addin\bin"
$dest = "$rsBase\Bin\Addins\$addinName"

# Verify RobotStudio installation
if (-not (Test-Path "$rsBase\Bin\ABB.Robotics.RobotStudio.dll")) {
    Write-Error "RobotStudio $RobotStudioVersion not found at: $rsBase"
    Write-Output "Try specifying a different version: -RobotStudioVersion '2024'"
    exit 1
}

# Create target directory (may require admin rights)
try {
    New-Item -ItemType Directory -Path $dest -Force -ErrorAction Stop | Out-Null
} catch {
    Write-Error "Cannot create directory: $dest"
    Write-Output "Run PowerShell as Administrator to install to Program Files."
    exit 1
}

# Copy files
try {
    Copy-Item "$source\ClaudeBridge.dll" $dest -Force -ErrorAction Stop
    Copy-Item "$PSScriptRoot\addin\ClaudeBridge.rsaddin" $dest -Force -ErrorAction Stop
} catch {
    Write-Error "Cannot copy files to: $dest"
    Write-Output "Run PowerShell as Administrator."
    exit 1
}

# Clean up old LocalAppData install (no longer used)
$oldPath = "$env:LOCALAPPDATA\ABB\RobotStudio\Addins\$addinName"
if (Test-Path $oldPath) {
    try {
        Remove-Item $oldPath -Recurse -Force
        Write-Output "Cleaned up old install: $oldPath"
    } catch {
        Write-Output "Note: old install at $oldPath could not be cleaned (may need manual removal)"
    }
}

Write-Host ""
Write-Host "=== ClaudeBridge Add-In Installed ==="
Write-Host "  Source:  $source"
Write-Host "  Target:  $dest"
Write-Host "  Version: 1.1.0 (TcpListener)"
Write-Host "  Port:    58080"
Write-Host "  AutoLoad: true"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Restart RobotStudio (Add-In loads automatically with AutoLoad)"
Write-Host "2. Open your station with a Virtual Controller"
Write-Host "3. Verify: curl http://localhost:58080/ping"
Write-Host "4. No admin rights needed (TcpListener, no netsh urlacl)"
Write-Host ""
