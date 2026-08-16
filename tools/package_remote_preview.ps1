[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublicOrigin,

    [string]$AgentExe = (Join-Path $PSScriptRoot "..\UltimateRemoteAgent\src\UltimateRemoteAgent\bin\Release\net10.0-windows\win-x64\publish\UltimateRemoteAgent.exe"),

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\dist")
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$agentPath = [IO.Path]::GetFullPath($AgentExe)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)

try {
    $origin = [Uri]$PublicOrigin
} catch {
    throw "PublicOrigin must be an absolute HTTPS origin."
}
if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne "https" -or
    -not [string]::IsNullOrEmpty($origin.UserInfo) -or
    -not [string]::IsNullOrEmpty($origin.Query) -or
    -not [string]::IsNullOrEmpty($origin.Fragment) -or
    ($origin.AbsolutePath -ne "/" -and $origin.AbsolutePath -ne "")) {
    throw "PublicOrigin must be an HTTPS origin without path, credentials, query, or fragment."
}
if (-not (Test-Path -LiteralPath $agentPath -PathType Leaf)) {
    throw "Published UltimateRemoteAgent.exe was not found: $agentPath"
}

$requiredFiles = @(
    "Main_Remote.ahk",
    "LICENSE",
    "run_remote_macro.bat"
)
$requiredDirectories = @(
    "Resources",
    "lib",
    "submacros"
)
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relative) -PathType Leaf)) {
        throw "Required client file is missing: $relative"
    }
}
foreach ($relative in $requiredDirectories) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relative) -PathType Container)) {
        throw "Required client directory is missing: $relative"
    }
}

$stagingParent = Join-Path ([IO.Path]::GetTempPath()) ("UltimateMacroRemote." + [Guid]::NewGuid().ToString("N"))
$staging = Join-Path $stagingParent "Ultimate_Macro_Remote"
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    foreach ($relative in $requiredFiles) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $relative) -Destination (Join-Path $staging $relative)
    }
    foreach ($relative in $requiredDirectories) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $relative) -Destination $staging -Recurse
    }

    Copy-Item -LiteralPath $agentPath -Destination (Join-Path $staging "UltimateRemoteAgent.exe")
    $canonicalOrigin = $origin.GetLeftPart([UriPartial]::Authority)
    [IO.File]::WriteAllText(
        (Join-Path $staging "remote_service.url"),
        $canonicalOrigin,
        [Text.UTF8Encoding]::new($false)
    )

    @"
Ultimate Macro Remote — Development Preview

1. Extract this ZIP to a normal local folder.
2. Run Main_Remote.ahk normally.
3. On first run only, review the Remote Terms/Privacy notice and choose Connect Discord.
4. Authorize Discord in the browser. No Discord ID, ticket, Python, .env, bot token, or terminal is required on the client PC.
5. If Start with Windows is enabled, only the Remote Agent waits in the background. It does not start Roblox or a strategy by itself.

Remote is optional. If declined, Ultimate Macro continues normally.
"@ | Set-Content -LiteralPath (Join-Path $staging "REMOTE_README.txt") -Encoding utf8NoBOM

    # Packaging must never leak central/server secrets even if the developer has an
    # untracked .env in the repository working tree.
    $forbiddenNames = @(
        ".env", ".env.local", "bot.py", "requirements.txt"
    )
    foreach ($name in $forbiddenNames) {
        if (Test-Path -LiteralPath (Join-Path $staging $name)) {
            throw "Forbidden server-side file entered the client package: $name"
        }
    }
    if (Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object {
        $_.Name -match '(?i)(secret|credential|token).*\.(json|txt|env)$'
    }) {
        throw "A suspicious secret-like file entered the client package."
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $zipPath = Join-Path $outputRoot "Ultimate_Macro_Remote_Preview.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -LiteralPath $staging -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Created: $zipPath"
} finally {
    if (Test-Path -LiteralPath $stagingParent) {
        Remove-Item -LiteralPath $stagingParent -Recurse -Force
    }
}
