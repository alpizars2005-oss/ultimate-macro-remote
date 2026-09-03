[CmdletBinding()]
param(
    [string]$PublicOrigin = "",

    [string]$AgentExe = (Join-Path $PSScriptRoot "..\UltimateRemoteAgent\src\UltimateRemoteAgent\bin\Release\net10.0-windows\win-x64\publish\UltimateRemoteAgent.exe"),

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\dist")
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$agentPath = [IO.Path]::GetFullPath($AgentExe)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if ([string]::IsNullOrWhiteSpace($PublicOrigin)) {
    $envPath = Join-Path $repoRoot ".env"
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        throw "PublicOrigin was not supplied and .env was not found. Pass -PublicOrigin or configure ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN in .env."
    }
    $originLine = Get-Content -LiteralPath $envPath | Where-Object {
        $_ -match '^\s*ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN\s*='
    } | Select-Object -Last 1
    if ($null -eq $originLine) {
        throw "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN is missing from .env."
    }
    $PublicOrigin = (($originLine -split '=', 2)[1]).Trim().Trim('"').Trim("'")
    if ([string]::IsNullOrWhiteSpace($PublicOrigin)) {
        throw "ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN is blank in .env."
    }
    Write-Host "Using ULTIMATE_REMOTE_PUBLIC_HTTPS_ORIGIN from .env."
}

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
$botContractPath = Join-Path $repoRoot "docs\discord-link-code-contract.md"
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
if (-not (Test-Path -LiteralPath $botContractPath -PathType Leaf)) {
    throw "Required bot linking contract is missing: docs\discord-link-code-contract.md"
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
    Copy-Item -LiteralPath $botContractPath -Destination (Join-Path $staging "REMOTE_BOT_LINKING.md")
    $canonicalOrigin = $origin.GetLeftPart([UriPartial]::Authority)
    [IO.File]::WriteAllText(
        (Join-Path $staging "remote_service.url"),
        $canonicalOrigin,
        $utf8NoBom
    )

    # Keep this generated here-string ASCII-only because Windows PowerShell 5.1 can
    # interpret UTF-8 script files without a BOM using the active ANSI code page.
    $readme = @"
Ultimate Macro Remote - Macro-first Link Code Preview

Service origin: $canonicalOrigin

USER FLOW
1. Extract this ZIP to a normal local folder.
2. Run Main_Remote.ahk normally.
3. On first run, review the Remote Terms/Privacy notice and choose Generate Link Code.
4. Ultimate Macro shows a top-most code such as ULT-7KQ3M-P9R2X and the exact command:

   /macro link ULT-7KQ3M-P9R2X

5. Run that command in the official Ultimate Macro Discord server.
6. The popup closes automatically after the bot claims the code and the Agent stores its DPAPI-protected enrollment.
7. If Start with Windows is enabled, only the Remote Agent waits in the background. It does not start Roblox or a strategy by itself.

There is no website/browser step in the default link flow. The client never needs a Discord user ID, bot token, OAuth client secret, Python, .env, or terminal.

BOT DEVELOPER
See REMOTE_BOT_LINKING.md in this ZIP for the exact code formula, claim contract, error mapping, and trust-boundary requirements for the official bot.

Remote is optional. If declined, Ultimate Macro continues normally.

COMPATIBILITY NOTE
This ZIP is a private Remote integration preview. The link-code contract is the bot handoff. Do not label the entire AutoHotkey Remote runtime as an official 1.3.4 build until its separate upstream runtime rebase is verified.
"@
    [IO.File]::WriteAllText(
        (Join-Path $staging "REMOTE_README.txt"),
        $readme,
        $utf8NoBom
    )

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
    $zipPath = Join-Path $outputRoot "Ultimate_Macro_Remote_LinkCode_Preview.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -LiteralPath $staging -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Created: $zipPath"
    Write-Host "Embedded service origin: $canonicalOrigin"
} finally {
    if (Test-Path -LiteralPath $stagingParent) {
        Remove-Item -LiteralPath $stagingParent -Recurse -Force
    }
}
