param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://github\.com/alpizars2005-oss/ultimate-macro-remote/archive/[0-9a-f]{40}\.zip$')]
    [string]$DownloadUrl,

    [Parameter(Mandatory = $true)]
    [string]$MacroDir,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$CommitSha,

    [Parameter(Mandatory = $true)]
    [string]$MarkerPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Main.ahk', 'Main_Remote.ahk')]
    [string]$EntryPoint
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$macroPath = [System.IO.Path]::GetFullPath($MacroDir)
$marker = [System.IO.Path]::GetFullPath($MarkerPath)
$tempRoot = Join-Path $env:TEMP ("ultimate-macro-update-" + [Guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tempRoot 'update.zip'
$extractPath = Join-Path $tempRoot 'extract'
$backupPath = $macroPath + '.update-backup-' + [Guid]::NewGuid().ToString('N')
$activated = $false

function Write-UpdateLog {
    param([string]$Message)
    try {
        $logDir = Split-Path -Parent $marker
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $line = "{0:u} {1}" -f (Get-Date), $Message
        Add-Content -LiteralPath (Join-Path $logDir 'update.log') -Value $line -Encoding UTF8
    } catch {
        # Update logging must never make rollback fail.
    }
}

function Copy-PreservedFile {
    param([string]$RelativePath, [string]$StageRoot)
    $source = Join-Path $macroPath $RelativePath
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        return
    }
    $destination = Join-Path $StageRoot $RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

try {
    if (-not (Test-Path -LiteralPath $macroPath -PathType Container)) {
        throw "Macro folder not found: $macroPath"
    }

    # Give AutoHotkey time to finish exiting before the live directory is renamed.
    Start-Sleep -Milliseconds 1200
    New-Item -ItemType Directory -Force -Path $extractPath | Out-Null

    Invoke-WebRequest -UseBasicParsing -Uri $DownloadUrl -OutFile $zipPath
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw 'The update archive was not downloaded.'
    }

    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractPath -Force
    $roots = @(Get-ChildItem -LiteralPath $extractPath -Directory)
    if ($roots.Count -ne 1) {
        throw "Expected one staged repository root, found $($roots.Count)."
    }
    $stageRoot = $roots[0].FullName

    foreach ($required in @('Main.ahk', 'Main_Remote.ahk', 'lib', 'submacros', 'Resources')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageRoot $required))) {
            throw "Staged update is incomplete; missing $required."
        }
    }

    # Preserve machine-local connection data. These are never trusted as update inputs.
    Copy-PreservedFile -RelativePath '.env' -StageRoot $stageRoot
    Copy-PreservedFile -RelativePath 'remote_service.url' -StageRoot $stageRoot

    # Preserve user-created strategies that do not collide with maintained strategies.
    $oldStrats = Join-Path $macroPath 'Resources\Strats'
    $newStrats = Join-Path $stageRoot 'Resources\Strats'
    if (Test-Path -LiteralPath $oldStrats -PathType Container) {
        New-Item -ItemType Directory -Force -Path $newStrats | Out-Null
        Get-ChildItem -LiteralPath $oldStrats -Filter '*.strat' -File -Recurse | ForEach-Object {
            $relative = $_.FullName.Substring($oldStrats.Length).TrimStart('\', '/')
            $destination = Join-Path $newStrats $relative
            if (-not (Test-Path -LiteralPath $destination)) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $destination
            }
        }
    }

    # Activate only after the replacement tree is complete. The old installation
    # remains a sibling backup until the new tree is in place and the marker is written.
    Move-Item -LiteralPath $macroPath -Destination $backupPath
    try {
        Move-Item -LiteralPath $stageRoot -Destination $macroPath
        $activated = $true
    } catch {
        if (-not (Test-Path -LiteralPath $macroPath) -and (Test-Path -LiteralPath $backupPath)) {
            Move-Item -LiteralPath $backupPath -Destination $macroPath
        }
        throw
    }

    $markerDir = Split-Path -Parent $marker
    New-Item -ItemType Directory -Force -Path $markerDir | Out-Null
    Set-Content -LiteralPath $marker -Value $CommitSha -Encoding ASCII -NoNewline

    $target = Join-Path $macroPath $EntryPoint
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Updated entry point is missing: $EntryPoint"
    }

    Write-UpdateLog "Activated commit $CommitSha and restarting $EntryPoint"
    Start-Process -FilePath $target -WorkingDirectory $macroPath

    if (Test-Path -LiteralPath $backupPath) {
        Remove-Item -LiteralPath $backupPath -Recurse -Force
    }
} catch {
    Write-UpdateLog ("Update failed: " + $_.Exception.Message)

    if ($activated -and (Test-Path -LiteralPath $backupPath)) {
        try {
            if (Test-Path -LiteralPath $macroPath) {
                Remove-Item -LiteralPath $macroPath -Recurse -Force
            }
            Move-Item -LiteralPath $backupPath -Destination $macroPath
            Write-UpdateLog 'Rollback restored the previous installation.'
            $fallback = Join-Path $macroPath $EntryPoint
            if (Test-Path -LiteralPath $fallback -PathType Leaf) {
                Start-Process -FilePath $fallback -WorkingDirectory $macroPath
            }
        } catch {
            Write-UpdateLog ("Rollback failed: " + $_.Exception.Message)
        }
    }
    exit 1
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit 0
