#requires -Version 5.1
param(
    [switch]$Build,
    [switch]$RepairImports,
    [string]$UnityEditor = ''
)

$ErrorActionPreference = 'Stop'
if ($Build -and $RepairImports) { throw 'Use -Build or -RepairImports separately.' }

$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'workshop\unity')).TrimEnd('\')
$versionFile = Join-Path $project 'ProjectSettings\ProjectVersion.txt'
if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) { throw 'Unity project is missing.' }
if ([string]::IsNullOrWhiteSpace($UnityEditor)) {
    $versionLine = Get-Content -LiteralPath $versionFile | Where-Object { $_ -match '^m_EditorVersion:\s+' } | Select-Object -First 1
    $version = ($versionLine -replace '^m_EditorVersion:\s+', '').Trim()
    $UnityEditor = Join-Path ${env:ProgramFiles} ('Unity\Hub\Editor\' + $version + '\Editor\Unity.exe')
}
if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw ('Unity Editor not found: ' + $UnityEditor) }

# A junction shortens the path seen by Unity's legacy Mono file APIs. The source
# stays in this separate project; no packages or Library folders are removed.
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$hash = [Security.Cryptography.SHA256]::Create()
try { $suffix = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($project.ToLowerInvariant())))).Replace('-', '').Substring(0, 12).ToLowerInvariant() }
finally { $hash.Dispose() }
$alias = [IO.Path]::GetFullPath((Join-Path $tempRoot ('AllurTwinVR-Unity-' + $suffix)))
if (-not $alias.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unity alias must stay inside the temporary directory.' }
$existing = Get-Item -LiteralPath $alias -Force -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    $targets = @($existing.Target)
    if ($existing.LinkType -ne 'Junction' -or $targets.Count -ne 1) { throw ('Path is not the expected junction: ' + $alias) }
    $target = [IO.Path]::GetFullPath([string]$targets[0]).TrimEnd('\')
    if (-not [string]::Equals($target, $project, [StringComparison]::OrdinalIgnoreCase)) { throw ('Junction belongs to another project: ' + $alias) }
} else {
    $null = New-Item -ItemType Junction -Path $alias -Target $project
    $created = Get-Item -LiteralPath $alias -Force
    $targets = @($created.Target)
    if ($created.LinkType -ne 'Junction' -or $targets.Count -ne 1 -or -not [string]::Equals([IO.Path]::GetFullPath([string]$targets[0]).TrimEnd('\'), $project, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unable to verify the new Unity junction.'
    }
}

$arguments = @('-projectPath', ('"' + $alias + '"'))
if ($Build -or $RepairImports) {
    $logs = Join-Path $project 'Logs'
    $null = New-Item -ItemType Directory -Path $logs -Force
    if ($RepairImports) {
        if (-not (Test-Path -LiteralPath (Join-Path $project 'Assets\VRWorkshop\Editor\WorkshopShortPathVerification.cs'))) {
            throw 'WorkshopShortPathVerification.cs must be installed in the copied project before repairing imports.'
        }
        $method = 'VRWorkshop.Editor.WorkshopShortPathVerification.Reimport'
        $log = Join-Path $logs 'repair-short-path.log'
    } else {
        $method = 'VRWorkshop.Editor.WorkshopBuild.RebuildWindowsScene'
        $log = Join-Path $logs 'build-short-path.log'
    }
    $arguments += @('-batchmode', '-quit', '-executeMethod', $method, '-logFile', ('"' + $log + '"'))
    Write-Output ('Unity project alias: ' + $alias)
    Write-Output ('Log: ' + $log)
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw ('Unity exited with code ' + $process.ExitCode + '. See ' + $log) }
    Write-Output 'Unity operation completed successfully.'
} else {
    # This is the interactive Unity editor requested by the user, not a service.
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Normal -PassThru
    Write-Output ('Unity Editor opened through ' + $alias + ' (PID ' + $process.Id + ').')
}
