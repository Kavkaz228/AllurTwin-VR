param([switch]$VR, [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = Join-Path $root 'runtime'
$local = Join-Path $root '.local'
$null = New-Item -ItemType Directory -Path $runtime, $local -Force
$credentialsPath = Join-Path $local 'access.json'
function New-LocalSecret {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}
if (-not (Test-Path -LiteralPath $credentialsPath)) {
    @{username='admin'; password=(New-LocalSecret); vrToken=(New-LocalSecret)} |
        ConvertTo-Json | Set-Content -LiteralPath $credentialsPath -Encoding UTF8
}
$credentials = Get-Content -LiteralPath $credentialsPath -Raw | ConvertFrom-Json
$bootstrap = Join-Path $local 'bootstrap-password'
if (-not (Test-Path -LiteralPath $bootstrap)) {
    [IO.File]::WriteAllText($bootstrap, $credentials.password)
}
$python = Join-Path $root '.venv/Scripts/python.exe'
$node = (Get-Command node -ErrorAction Stop).Source
$vite = Join-Path $root 'allur/frontend/node_modules/vite/bin/vite.js'
if (-not (Test-Path -LiteralPath $python) -or -not (Test-Path -LiteralPath $vite) -or -not (Test-Path -LiteralPath (Join-Path $root 'allur/frontend/dist/index.html'))) {
    throw 'Run Setup-Symbiosis.ps1 first.'
}
try { & (Join-Path $root 'Ensure-LocalOllama.ps1') }
catch { Write-Warning ('Local AI unavailable; the project will use explicitly labelled reference answers. ' + $_.Exception.Message) }
$statePath = Join-Path $runtime 'processes.json'
$saved = @{}
if (Test-Path -LiteralPath $statePath) {
    $old = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    foreach($property in $old.PSObject.Properties) { $saved[$property.Name] = $property.Value }
}
function Save-Processes { $saved | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8 }
function Ensure-Service([string]$name, [string]$exe, [string[]]$arguments, [string]$cwd, [string]$health, [hashtable]$environment) {
    if ($saved.ContainsKey($name)) {
        $record = $saved[$name]
        $existing = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$record.pid)
        if ($existing -and $existing.CommandLine.Contains([string]$record.marker)) {
            try { $null = Invoke-WebRequest -UseBasicParsing -Uri $health -TimeoutSec 2; return }
            catch { throw ($name + ' is running but unhealthy. Read runtime logs or run Stop-Symbiosis.ps1.') }
        }
    }
    try { $null = Invoke-WebRequest -UseBasicParsing -Uri $health -TimeoutSec 1; throw ('PORT_IN_USE: ' + $health) }
    catch { if ($_.Exception.Message.StartsWith('PORT_IN_USE:')) { throw } }
    $previous = @{}
    foreach($key in $environment.Keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
    try {
        foreach($key in $environment.Keys) { [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process') }
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $cwd -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtime ($name+'.log')) -RedirectStandardError (Join-Path $runtime ($name+'-error.log'))
    } finally {
        foreach($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    }
    $marker = if ($name -eq 'api') { $python } elseif ($name -eq 'web') { $vite } else { Join-Path $root 'workshop/server/index.js' }
    $saved[$name] = @{pid=$process.Id; marker=$marker; executable=$exe}
    Save-Processes
    for($attempt=0; $attempt -lt 80; $attempt++) {
        if ($process.HasExited) { throw ($name+' exited. Read runtime/'+$name+'-error.log.') }
        try { $null=Invoke-WebRequest -UseBasicParsing -Uri $health -TimeoutSec 1; return } catch {}
        Start-Sleep -Milliseconds 250
    }
    throw ($name+' did not become ready. Read runtime logs.')
}
$brief = Join-Path $runtime 'allur-brief.json'
Ensure-Service 'workshop' $node @(('"'+(Join-Path $root 'workshop/server/index.js')+'"')) (Join-Path $root 'workshop') 'http://127.0.0.1:4182/health' @{
    HOST='127.0.0.1'; PORT='4182'; VR_ACCESS_TOKEN=$credentials.vrToken; CONFIG_FILE=$null;
    TLS_CERT_FILE=$null; TLS_KEY_FILE=$null; DATA_DIR=(Join-Path $root 'workshop/data');
    OLLAMA_URL='http://127.0.0.1:11434'; OLLAMA_MODEL='qwen3:4b'; OLLAMA_TIMEOUT_MS='90000'; ALLUR_BRIEF_FILE=$brief
}
$database = 'sqlite:///' + (Join-Path $runtime 'allur-vr.db').Replace('\','/')
Ensure-Service 'api' $python @('-m','uvicorn','app.main:app','--host','127.0.0.1','--port','8182') (Join-Path $root 'allur/backend') 'http://127.0.0.1:8182/api/health' @{
    DATABASE_URL=$database; DATABASE_PASSWORD_FILE=$null; BOOTSTRAP_PASSWORD_FILE=$bootstrap;
    COOKIE_SECURE='false'; PUBLIC_ORIGIN='http://127.0.0.1:8089'; RUNNER_ENABLED='true';
    MONITORING_AI_ENABLED='false'; PHYSICAL_CONTROL_ENABLED='false'; AI_MODEL='qwen3:4b';
    OLLAMA_BASE_URL='http://127.0.0.1:11434'; VR_ACCESS_TOKEN=$credentials.vrToken;
    VR_WORKSHOP_URL='http://127.0.0.1:4182'; ALLUR_BRIEF_FILE=$brief; FUSION_ENABLED='true'
}
Ensure-Service 'web' $node @(('"'+$vite+'"'),'preview','--host','127.0.0.1','--port','8089','--strictPort') (Join-Path $root 'allur/frontend') 'http://127.0.0.1:8089' @{}
Write-Output 'Allur Twin x VR Workshop: http://127.0.0.1:8089'
Write-Output ('Local account: admin. Password file: '+$credentialsPath)
if ($VR) {
    & (Join-Path $root 'Launch-VR.ps1')
}
if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:8089' }

