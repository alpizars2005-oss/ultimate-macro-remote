[CmdletBinding()]
param(
    [string]$AgentExe = (Join-Path $PSScriptRoot "..\UltimateRemoteAgent\src\UltimateRemoteAgent\bin\Release\net10.0-windows\win-x64\publish\UltimateRemoteAgent.exe"),
    [switch]$ResetRemoteChoice
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$envPath = Join-Path $repoRoot ".env"
$agentPath = [IO.Path]::GetFullPath($AgentExe)
$targetAgent = Join-Path $repoRoot "UltimateRemoteAgent.exe"
$targetOrigin = Join-Path $repoRoot "remote_service.url"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
    throw ".env was not found at the repository root."
}
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf)) {
    throw "Published UltimateRemoteAgent.exe was not found. Run dotnet publish first: $agentPath"
}
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "Main_Remote.ahk") -PathType Leaf)) {
    throw "Main_Remote.ahk was not found at the repository root."
}

$originLine = Get-Content -LiteralPath $envPath | Where-Object {
    $_ -match '^\s*ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN\s*='
} | Select-Object -Last 1
if ($null -eq $originLine) {
    throw "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN is missing from .env."
}
$originText = (($originLine -split '=', 2)[1]).Trim().Trim('"').Trim("'")
if ([string]::IsNullOrWhiteSpace($originText)) {
    throw "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN is blank in .env."
}

try {
    $origin = [Uri]$originText
} catch {
    throw "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN must be an absolute HTTPS origin."
}
if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne "https" -or
    -not [string]::IsNullOrEmpty($origin.UserInfo) -or
    -not [string]::IsNullOrEmpty($origin.Query) -or
    -not [string]::IsNullOrEmpty($origin.Fragment) -or
    ($origin.AbsolutePath -ne "/" -and $origin.AbsolutePath -ne "")) {
    throw "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN must be an HTTPS origin without path, credentials, query, or fragment."
}

$canonicalOrigin = $origin.GetLeftPart([UriPartial]::Authority)
Copy-Item -LiteralPath $agentPath -Destination $targetAgent -Force
[IO.File]::WriteAllText($targetOrigin, $canonicalOrigin, $utf8NoBom)

if ($ResetRemoteChoice) {
    $preferencesPath = Join-Path $env:LOCALAPPDATA "UltimateRemoteAgent\preferences.v1.json"
    if (Test-Path -LiteralPath $preferencesPath -PathType Leaf) {
        Remove-Item -LiteralPath $preferencesPath -Force
        Write-Host "Reset the local Remote consent choice. Existing encrypted device enrollment was left intact."
    } else {
        Write-Host "No saved Remote consent choice was present."
    }
}

Write-Host "Local development Remote client is ready."
Write-Host "Agent: $targetAgent"
Write-Host "Service origin: $canonicalOrigin"
Write-Host "Next: run Main_Remote.ahk from this repository root."
Write-Host "If this Windows user previously chose 'Not now', rerun with -ResetRemoteChoice."
