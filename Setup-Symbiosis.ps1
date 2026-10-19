param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$venv = Join-Path $root '.venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $venv)) {
    & $Python -m venv (Join-Path $root '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Python 3.12+ is required. Pass -Python with its full path.' }
}
& $venv -m pip install -r (Join-Path $root 'allur/backend/requirements-test.txt')
if ($LASTEXITCODE -ne 0) { throw 'Python dependency installation failed.' }
foreach ($folder in @('workshop', 'allur/frontend')) {
    Push-Location (Join-Path $root $folder)
    try {
        & npm.cmd ci
        if ($LASTEXITCODE -ne 0) { throw ('npm ci failed: ' + $folder) }
        if ($folder -eq 'allur/frontend') {
            & npm.cmd run build
            if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
        }
    } finally { Pop-Location }
}
Write-Output 'Dependencies ready. Run Start-Symbiosis.ps1, or build workshop/unity in Unity first for VR.'

