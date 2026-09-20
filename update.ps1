<#
    GorillaNextBots updater.

    Fetches the newest release from GitHub and drops the mods into your Gorilla Tag folder.
    Run it before you play; it takes a couple of seconds and does nothing if you are already
    up to date.

        Right-click update.ps1 > Run with PowerShell        (or double-click update.bat)
        .\update.ps1 -GameDir "D:\Games\Gorilla Tag"        (if it cannot find the game)
        .\update.ps1 -Force                                 (reinstall the current version)

    It only ever writes the three mod DLLs. Your settings, bot images, sounds and death log
    style are left alone.

    Author: stridefr
#>

param(
    [string]$GameDir,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$Repo = "stridefr/GorillaNextBots"

function Find-GameDir {
    # Steam keeps its library list in libraryfolders.vdf; the game sits under steamapps\common.
    $roots = @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")
    foreach ($d in [char[]]([char]'C'..[char]'H')) {
        $roots += "${d}:\SteamLibrary", "${d}:\Steam", "${d}:\Games\Steam"
    }
    foreach ($root in $roots) {
        # Plain strings, not Join-Path: it throws on a drive letter that does not exist.
        $vdf = "$root\steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $roots += $m.Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    foreach ($root in ($roots | Select-Object -Unique)) {
        $game = "$root\steamapps\common\Gorilla Tag"
        if (Test-Path "$game\Gorilla Tag.exe") { return $game }
    }
    return $null
}

Write-Host "GorillaNextBots updater" -ForegroundColor Cyan

if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir -or -not (Test-Path "$GameDir\Gorilla Tag.exe")) {
    Write-Host "Could not find Gorilla Tag. Run it again with the folder, like:" -ForegroundColor Red
    Write-Host '    .\update.ps1 -GameDir "D:\Games\Gorilla Tag"'
    exit 1
}

$plugins = "$GameDir\BepInEx\plugins"
if (-not (Test-Path $plugins)) {
    Write-Host "No BepInEx\plugins folder in $GameDir - install BepInEx 5 first:" -ForegroundColor Red
    Write-Host "    https://github.com/BepInEx/BepInEx/releases"
    exit 1
}
Write-Host "  game     $GameDir"

if (Get-Process -Name "Gorilla Tag" -ErrorAction SilentlyContinue) {
    Write-Host "Gorilla Tag is running. Close it and run this again." -ForegroundColor Red
    exit 1
}

# What is installed, as written by the last run of this script.
$stamp = "$plugins\GorillaNextBots.version"
$have = if (Test-Path $stamp) { (Get-Content $stamp -Raw).Trim() } else { "" }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# A private repo needs a sign-in. GITHUB_TOKEN if it is set, otherwise the token from the
# GitHub CLI if that is installed and signed in. A public repo needs neither.
$token = $env:GITHUB_TOKEN
if (-not $token -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $token = (& gh auth token 2>$null | Select-Object -First 1)
}
$headers = @{ "User-Agent" = "GorillaNextBots-updater"; "Accept" = "application/vnd.github+json" }
if ($token) { $headers["Authorization"] = "Bearer $token" }

try {
    $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}
catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 404) {
        Write-Host "GitHub says that repo or release does not exist." -ForegroundColor Red
        Write-Host "  If it is private, sign in first: install the GitHub CLI and run 'gh auth login',"
        Write-Host "  or set a token:  $env:GITHUB_TOKEN = 'ghp_...'"
        exit 1
    }
    throw
}
$tag = $release.tag_name
Write-Host "  latest   $tag$(if ($have) { "   (you have $have)" })"

if ($tag -eq $have -and -not $Force) {
    Write-Host "Already up to date." -ForegroundColor Green
    exit 0
}

$asset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
if (-not $asset) {
    Write-Host "That release has no zip attached." -ForegroundColor Red
    exit 1
}

$temp = "$env:TEMP\GorillaNextBots-$tag"
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
New-Item -ItemType Directory -Path $temp | Out-Null
$zip = "$temp\$($asset.name)"

Write-Host "  download $($asset.name) ..."
if ($token) {
    # The public download link is not public on a private repo; the API asset URL is.
    $dl = $headers.Clone()
    $dl["Accept"] = "application/octet-stream"
    Invoke-WebRequest $asset.url -Headers $dl -OutFile $zip -UseBasicParsing
}
else {
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
}
Expand-Archive $zip -DestinationPath $temp -Force

# Only the DLLs: everything else in the game's plugin folders is the player's own.
$dlls = Get-ChildItem "$temp\BepInEx\plugins" -Recurse -Filter *.dll
if (-not $dlls) {
    Write-Host "No mod DLLs inside the zip - nothing changed." -ForegroundColor Red
    exit 1
}
foreach ($dll in $dlls) {
    $folder = "$plugins\$($dll.Directory.Name)"
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item $dll.FullName "$folder\$($dll.Name)" -Force
    Write-Host "  installed $($dll.Directory.Name)\$($dll.Name)" -ForegroundColor Green
}

Set-Content -Path $stamp -Value $tag -Encoding ascii
Remove-Item $temp -Recurse -Force

Write-Host "Updated to $tag. Start Gorilla Tag." -ForegroundColor Green
if ($release.body) {
    Write-Host ""
    Write-Host ($release.body -split "`n" | Select-Object -First 6 | Out-String).Trim() -ForegroundColor DarkGray
}
