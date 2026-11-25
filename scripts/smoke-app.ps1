# Launches the real Sysacta.exe (tray only) on a clean profile and checks that it starts the MCP server,
# enforces authentication, and keeps a single instance. Used by CI; safe to run locally.
param([string]$Exe = "src/Sysacta.App/bin/Release/net10.0-windows/Sysacta.exe")
$ErrorActionPreference = "Stop"

$data = Join-Path $env:LOCALAPPDATA "Sysacta"
if (Test-Path $data) {
    if ($env:GITHUB_ACTIONS -ne "true") { throw "Refusing to run: $data already exists (would reuse real settings)." }
    # Ephemeral CI runner: show what was there (for diagnosis) and start from a clean profile.
    Get-ChildItem -Recurse $data | ForEach-Object { Write-Host "pre-existing: $($_.FullName)" }
    Remove-Item -Recurse -Force $data
}

$app = Start-Process -FilePath $Exe -ArgumentList "--minimized" -PassThru
try {
    $deadline = (Get-Date).AddSeconds(30)
    $status = $null
    while ((Get-Date) -lt $deadline -and $null -eq $status) {
        Start-Sleep -Milliseconds 500
        if ($app.HasExited) { throw "Sysacta exited early with code $($app.ExitCode)." }
        try {
            # PowerShell 7: -SkipHttpErrorCheck returns 4xx responses instead of throwing; connection refused still throws.
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:8765/mcp" -Method Post -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' -ContentType "application/json" -SkipHttpErrorCheck
            $status = [int]$r.StatusCode
        } catch {
            # Not listening yet.
        }
    }
    if ($status -ne 401) { throw "Expected 401 from /mcp without a token, got '$status'." }
    Write-Host "MCP server is listening and requires authentication (401)."

    if (-not (Test-Path (Join-Path $data "sysacta.db"))) { throw "Database was not created." }
    Write-Host "Database created in $data."

    $second = Start-Process -FilePath $Exe -ArgumentList "--minimized" -PassThru
    if (-not $second.WaitForExit(15000)) { $second.Kill(); throw "Second instance did not exit." }
    if ($app.HasExited) { throw "First instance exited when the second one started." }
    Write-Host "Second instance exited; first instance still running (single instance)."
}
finally {
    if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit(10000) | Out-Null }
}
