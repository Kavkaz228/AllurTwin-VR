$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = Join-Path $root 'runtime'
$null = New-Item -ItemType Directory -Path $runtime -Force
$base = 'http://127.0.0.1:11434'
try {
    $tags = Invoke-RestMethod -Uri ($base + '/api/tags') -TimeoutSec 2
    if (@($tags.models.name) -notcontains 'qwen3:4b') { Write-Warning 'Ollama is available, but qwen3:4b is not installed.' }
    Write-Output 'Reusing the local Ollama service.'
    return
} catch {}
if (@(Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue).Count) {
    throw 'Port 11434 is occupied by an unresponsive service. It was not changed.'
}
$exe = $env:ALLUR_OLLAMA_EXE
$models = $env:ALLUR_OLLAMA_MODELS
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $root '../..'))
$settingsPath = Join-Path $sourceRoot 'config.ai.local.json'
if (-not $exe -and (Test-Path -LiteralPath $settingsPath)) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $candidate = [IO.Path]::GetFullPath((Join-Path $sourceRoot $settings.executablePath))
    $modelCandidate = [IO.Path]::GetFullPath((Join-Path $sourceRoot $settings.modelsPath))
    $boundary = $sourceRoot.TrimEnd('\') + '\'
    if ($candidate.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -and $modelCandidate.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        $exe = $candidate
        if (-not $models) { $models = $modelCandidate }
    }
}
if (-not $exe) {
    $installed = Get-Command ollama.exe -ErrorAction SilentlyContinue
    if ($installed) { $exe = $installed.Source }
}
if (-not $models) { $models = Join-Path $env:USERPROFILE '.ollama/models' }
if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { throw 'Installed Ollama was not found. Set ALLUR_OLLAMA_EXE and ALLUR_OLLAMA_MODELS, or start the existing local Ollama service.' }
$exe = [IO.Path]::GetFullPath($exe)
$profile = Join-Path $runtime 'ollama-profile'
$null = New-Item -ItemType Directory -Path $profile -Force
$environment = @{
    OLLAMA_HOST='127.0.0.1:11434'; OLLAMA_MODELS=$models; OLLAMA_NO_CLOUD='true';
    OLLAMA_CONTEXT_LENGTH='4096'; OLLAMA_NUM_PARALLEL='1'; OLLAMA_MAX_LOADED_MODELS='1';
    OLLAMA_KEEP_ALIVE='10m'; USERPROFILE=$profile
}
$previous = @{}
foreach($key in $environment.Keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
try {
    foreach($key in $environment.Keys) { [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process') }
    $process = Start-Process -FilePath $exe -ArgumentList 'serve' -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtime 'ollama.log') -RedirectStandardError (Join-Path $runtime 'ollama-error.log')
} finally {
    foreach($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
$statePath = Join-Path $runtime 'processes.json'
$saved = @{}
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    foreach($property in $state.PSObject.Properties) { $saved[$property.Name] = $property.Value }
}
$identity = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $process.Id)
$saved['ollama'] = @{pid=$process.Id; marker=$exe; executable=$exe; projectOwned=$true; created=$identity.CreationDate.ToUniversalTime().ToString('o')}
$saved | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8
for($attempt=0; $attempt -lt 60; $attempt++) {
    if ($process.HasExited) { throw 'Ollama exited; see runtime/ollama-error.log.' }
    try {
        $tags = Invoke-RestMethod -Uri ($base + '/api/tags') -TimeoutSec 1
        if (@($tags.models.name) -notcontains 'qwen3:4b') { Write-Warning 'Ollama started, but qwen3:4b is not installed in its model directory.' }
        Write-Output ('Local Ollama ready, project-owned PID ' + $process.Id)
        return
    } catch {}
    Start-Sleep -Milliseconds 250
}
throw 'Ollama did not become ready; see runtime/ollama-error.log.'
