# Launches the real Sysacta.exe on a clean profile and checks: MCP server start, authentication, window close keeps
# the app running in the tray, single instance re-activates the window, and an independent MCP client (TypeScript SDK)
# can connect with a token.
# CI only: it uses and finally deletes the real %LOCALAPPDATA%\Sysacta, which on a workstation could be created by a
# normal Sysacta instance while the test runs. It refuses to run outside GitHub Actions or if that folder exists.
param([string]$Exe = "src/Sysacta.App/bin/Release/net10.0-windows10.0.19041.0/Sysacta.exe")
$ErrorActionPreference = "Stop"
if ($env:GITHUB_ACTIONS -ne "true") { throw "smoke-app.ps1 runs only on disposable CI runners (it deletes %LOCALAPPDATA%\Sysacta)." }
$url = "http://127.0.0.1:8765/mcp"

$data = Join-Path $env:LOCALAPPDATA "Sysacta"
# Must not exist: locally it would mean reusing real settings; in CI it would mean a test touched the real profile.
if (Test-Path $data) {
    Get-ChildItem -Recurse $data | ForEach-Object { Write-Host "pre-existing: $($_.FullName)" }
    throw "Refusing to run: $data already exists (real settings, or a test wrote to the real profile)."
}

function Wait-Until([scriptblock]$Condition, [string]$What, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for: $What" }
        Start-Sleep -Milliseconds 300
    }
}

function Get-Status {
    try {
        $r = Invoke-WebRequest -Uri $url -Method Post -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' -ContentType "application/json" -SkipHttpErrorCheck
        return [int]$r.StatusCode
    } catch { return $null } # not listening
}

function Get-MainWindow([System.Diagnostics.Process]$p) { $p.Refresh(); return $p.MainWindowHandle }

$app = Start-Process -FilePath $Exe -PassThru
try {
    Wait-Until { if ($app.HasExited) { throw "Sysacta exited early ($($app.ExitCode))." }; (Get-Status) -eq 401 } "MCP server (401 without token)"
    Write-Host "MCP server is listening and requires authentication (401)."
    if (-not (Test-Path (Join-Path $data "sysacta.db"))) { throw "Database was not created." }
    Write-Host "Database created in $data."

    Wait-Until { (Get-MainWindow $app) -ne 0 } "main window"
    Write-Host "Main window shown: '$($app.MainWindowTitle)'."
    [void]$app.CloseMainWindow()
    Wait-Until { (Get-MainWindow $app) -eq 0 } "window hidden after close" 10
    Start-Sleep -Seconds 1
    if ($app.HasExited) { throw "Closing the window exited the app." }
    if ((Get-Status) -ne 401) { throw "Server stopped after closing the window." }
    Write-Host "Window closed: process and server keep running (tray)."

    $second = Start-Process -FilePath $Exe -PassThru
    if (-not $second.WaitForExit(15000)) { $second.Kill(); throw "Second instance did not exit." }
    if ($app.HasExited) { throw "First instance exited when the second one started." }
    Wait-Until { (Get-MainWindow $app) -ne 0 } "window re-shown by second launch" 15
    Write-Host "Second instance exited and re-activated the first one's window (single instance)."

    # A client issued as the console would do it: random token, only its SHA-256 stored, audio_get_state granted.
    $bytes = [byte[]]::new(32); [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $token = "sya_" + [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $hash = -join ([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString("x2") })
    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")
    $seed = "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute('insert into clients (id,name,token_hash,created_at) values (?,?,?,?)',('ci','ci',sys.argv[2],sys.argv[3])); c.execute('insert into client_permissions (client_id,tool_name,allowed) values (?,?,1)',('ci','audio_get_state')); c.commit()"
    python -c $seed (Join-Path $data "sysacta.db") $hash $now
    if ($LASTEXITCODE -ne 0) { throw "Could not seed the test client." }

    $clientDir = Join-Path ([IO.Path]::GetTempPath()) "sysacta-mcp-client"
    New-Item -ItemType Directory -Force $clientDir | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "ci/mcp-client.mjs") $clientDir
    Push-Location $clientDir
    try {
        npm install --no-save --silent "@modelcontextprotocol/sdk@1.32.2" | Out-Null
        node mcp-client.mjs $url $token
        if ($LASTEXITCODE -ne 0) { throw "TypeScript MCP client check failed." }
    } finally { Pop-Location }
    Write-Host "Independent MCP client (TypeScript SDK) connected with a Bearer token."

    # Regression: a saved Light/Dark theme used to abort startup before the tray and the server were created.
    $app.Kill(); $app.WaitForExit(10000) | Out-Null
    Wait-Until { (Get-Status) -eq $null } "port released after the first instance ended" 15
    python -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute('insert into settings (key,value,updated_at) values (?,?,?) on conflict(key) do update set value=excluded.value',('theme','Dark','x')); c.commit()" (Join-Path $data "sysacta.db")
    if ($LASTEXITCODE -ne 0) { throw "Could not save the theme setting." }
    $app = Start-Process -FilePath $Exe -ArgumentList "--minimized" -PassThru
    Wait-Until { if ($app.HasExited) { throw "Sysacta exited early ($($app.ExitCode))." }; (Get-Status) -eq 401 } "server after restart with a saved Dark theme"
    Write-Host "Restart with a saved Dark theme completes startup (server running)."
}
finally {
    if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit(10000) | Out-Null }
    # The profile did not exist before this script, so everything in it was created here.
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
