# Install ClaudeBridge Add-In for RobotStudio 2025
# Run with: powershell -ExecutionPolicy Bypass -File install-addin.ps1

$source = "$PSScriptRoot\addin\bin"
$dest = "$env:LOCALAPPDATA\ABB\RobotStudio\Addins\ClaudeBridge"

# Create target directory
New-Item -ItemType Directory -Path $dest -Force | Out-Null

# Copy files
Copy-Item "$source\ClaudeBridge.dll" $dest -Force
Copy-Item "$PSScriptRoot\addin\ClaudeBridge.rsaddin" $dest -Force

Write-Host "ClaudeBridge Add-In installed to: $dest"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Restart RobotStudio"
Write-Host "2. Open your GoHolo_Simulation station"
Write-Host "3. The Add-In starts an HTTP server on localhost:58080"
Write-Host "4. Restart Claude Code - the MCP tools will be available"
Write-Host ""
Write-Host "Test: curl http://localhost:58080/ping"
