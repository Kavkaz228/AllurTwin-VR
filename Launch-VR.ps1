# Launch only this project's fixed player. Used by the desktop launcher and the local web button.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = if ($env:ALLUR_RUNTIME_DIR) { $env:ALLUR_RUNTIME_DIR } else { Join-Path $root 'runtime' }
$accessPath = if ($env:ALLUR_ACCESS_FILE) { $env:ALLUR_ACCESS_FILE } else { Join-Path $root '.local/access.json' }
$playerDir = if ($env:ALLUR_PLAYER_DIR) { $env:ALLUR_PLAYER_DIR } else { Join-Path $root 'workshop/unity/Builds/Windows' }
$player = [IO.Path]::GetFullPath((Join-Path $playerDir 'AllurTwinVR.exe'))
if (-not (Test-Path -LiteralPath $player -PathType Leaf)) { throw 'The AllurTwinVR player build is missing.' }
$required = @('UnityPlayer.dll','MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll','AllurTwinVR_Data/Managed/mscorlib.dll','AllurTwinVR_Data/Managed/Assembly-CSharp.dll','AllurTwinVR_Data/globalgamemanagers','AllurTwinVR_Data/Plugins/x86_64/UnityOpenXR.dll')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $playerDir $name) -PathType Leaf)) { throw ('VR component missing: ' + $name + '. Restart AllurTwin to repair the runtime.') }
}
$manifestPath = Join-Path $playerDir 'player-files.json'
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.PSObject.Properties) {
        $file = [IO.Path]::GetFullPath((Join-Path $playerDir $entry.Name))
        if (-not $file.StartsWith(([IO.Path]::GetFullPath($playerDir).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid VR manifest path.' }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw ('VR component missing: ' + $entry.Name + '. Restart AllurTwin to repair the runtime.') }
        $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
        $stream = [IO.File]::OpenRead($file)
        try { $actualHash = ([BitConverter]::ToString($hashAlgorithm.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose(); $hashAlgorithm.Dispose() }
        if ($actualHash -ne $entry.Value.sha256) { throw ('VR component damaged: ' + $entry.Name + '. Restart AllurTwin to repair the runtime.') }
    }
}
$log = Join-Path $runtime 'vr-player.log'
$sha = [Security.Cryptography.SHA256]::Create()
try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant())))).Replace('-', '').Substring(0, 20) }
finally { $sha.Dispose() }
$mutex = New-Object Threading.Mutex($false, ('Local\AllurTwinVR-' + $key))
$locked = $false
try {
    try { $locked = $mutex.WaitOne(1000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another VR launch is still in progress.' }
    $running = @(Get-Process -Name AllurTwinVR -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $player })
    $alreadyRunning = $running.Count -gt 0
    if ($alreadyRunning) { $process = $running[0] }
    else {
        $credentials = Get-Content -LiteralPath $accessPath -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($credentials.vrToken)) { throw 'VR access token is missing.' }
        $oldUrl = [Environment]::GetEnvironmentVariable('VR_WORKSHOP_CONNECT_URL', 'Process')
        $oldToken = [Environment]::GetEnvironmentVariable('VR_WORKSHOP_ACCESS_TOKEN', 'Process')
        try {
            $env:VR_WORKSHOP_CONNECT_URL = if ($oldUrl) { $oldUrl } else { 'ws://127.0.0.1:4182/ws' }
            $env:VR_WORKSHOP_ACCESS_TOKEN = $credentials.vrToken
            if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
            $process = Start-Process -FilePath $player -ArgumentList @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',('"'+$log+'"')) -WorkingDirectory (Split-Path -Parent $player) -WindowStyle Normal -PassThru
        } finally {
            [Environment]::SetEnvironmentVariable('VR_WORKSHOP_CONNECT_URL', $oldUrl, 'Process')
            [Environment]::SetEnvironmentVariable('VR_WORKSHOP_ACCESS_TOKEN', $oldToken, 'Process')
        }
    }
    $statePath = Join-Path $runtime 'processes.json'
    $saved = @{}
    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        foreach ($property in $state.PSObject.Properties) { $saved[$property.Name] = $property.Value }
    }
    $saved['vr'] = @{pid=$process.Id; marker=$player; executable=$player}
    $saved | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8
    # A native fatal-error dialog also keeps a PID alive. Require managed Unity startup.
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw 'Unity exited during startup. See vr-player.log.' }
        $text = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue } else { '' }
        if ($text -match 'Unable to load mono|Failed to load mono') { throw 'Unity could not load Mono. Restart AllurTwin to repair the runtime.' }
        $ready = $text -match '\[Workshop teleport\] initialized'
        if (-not $ready) { Start-Sleep -Milliseconds 200 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    # OpenXR may wait for the headset. Report initialization, never a false success.
    if (-not $ready -and $text -notmatch 'XRGeneral Settings awakening') { throw 'Unity startup is not confirmed yet. Check vr-player.log and try again.' }
    try { $null = $process.WaitForInputIdle(1500) } catch {}
    $activated = $false
    try { $shell = New-Object -ComObject WScript.Shell; $activated = $shell.AppActivate([int]$process.Id) } catch {}
    @{pid=$process.Id; alreadyRunning=$alreadyRunning; activated=$activated; starting=(-not $ready); headsetError=($text -match 'XR_ERROR_RUNTIME_FAILURE|Unrecoverable runtime error')} | ConvertTo-Json -Compress
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
