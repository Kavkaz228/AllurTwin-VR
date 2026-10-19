$ErrorActionPreference='Stop'
$statePath=Join-Path $PSScriptRoot 'runtime/processes.json'
if(-not (Test-Path -LiteralPath $statePath)){Write-Output 'No project process registry.'; exit 0}
$state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
foreach($name in @('vr','web','api','workshop','ollama')){
    $record=$state.$name
    if($null -eq $record){continue}
    $process=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$record.pid)
    if($null -eq $process){continue}
    if($name -eq 'ollama'){
        if(-not $record.projectOwned -or $process.ExecutablePath -ne [string]$record.executable -or
            $process.CreationDate.ToUniversalTime().ToString('o') -ne [string]$record.created -or
            $process.CommandLine -notmatch '\bserve\b') {
            Write-Warning 'Ollama no longer matches the project-owned process; left running.'
            continue
        }
    }
    if(-not $process.CommandLine.Contains([string]$record.marker)) {Write-Warning ('PID no longer belongs to '+$name);continue}
    if($name -eq 'api'){
        # Windows venv python.exe may launch the real interpreter as a child.
        # Only stop children of the already verified project process that run
        # this exact API entry point and isolated port.
        $children=Get-CimInstance Win32_Process -Filter ('ParentProcessId='+[int]$record.pid)
        foreach($child in $children){
            if($child.CommandLine.Contains('-m uvicorn app.main:app') -and $child.CommandLine -match '--port\s+8182(?:\s|$)'){
                Stop-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            }
        }
    }
    if($name -eq 'vr'){
        $player=Get-Process -Id $record.pid
        $null=$player.CloseMainWindow()
        if($player.WaitForExit(5000)){continue}
    }
    Stop-Process -Id $record.pid -ErrorAction SilentlyContinue
    Write-Output ('Stopped '+$name)
}
Write-Output 'The SQLite database and saved vehicle passports remain in runtime/.'
