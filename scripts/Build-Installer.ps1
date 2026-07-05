param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot "VoiceChatbot.csproj"
$publishDir = Join-Path $projectRoot "bin\Release\publish\win-x64"
$artifactsDir = Join-Path $projectRoot "artifacts"
$installerScript = Join-Path $projectRoot "installer\VoiceChatbot.iss"

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
Get-ChildItem $artifactsDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

$dotnetCandidates = @(
    "$env:USERPROFILE\.dotnet-sdk-codex\dotnet.exe",
    "$env:USERPROFILE\.dotnet\dotnet.exe",
    "$env:ProgramFiles\dotnet\dotnet.exe",
    (Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
) | Where-Object { $_ -and (Test-Path $_) }

$dotnet = $dotnetCandidates | Where-Object {
    $sdkList = & $_ --list-sdks 2>$null
    $LASTEXITCODE -eq 0 -and $sdkList -match '^8\.'
} | Select-Object -First 1
if (-not $dotnet) {
    throw ".NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

& $dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishProfile=win-x64 `
    -p:PublishDir="$publishDir\"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

function Get-GitHubReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][scriptblock]$AssetFilter
    )

    $headers = @{ "User-Agent" = "VoiceChatbot-Build" }
    if ($env:GITHUB_TOKEN) {
        $headers["Authorization"] = "Bearer $($env:GITHUB_TOKEN)"
        $headers["X-GitHub-Api-Version"] = "2022-11-28"
    }

    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repository/releases/latest" `
        -Headers $headers
    $asset = $release.assets | Where-Object $AssetFilter | Select-Object -First 1
    if (-not $asset) {
        throw "No matching release asset was found for $Repository."
    }
    return $asset
}

$mediaDir = Join-Path $publishDir "Tools\Media"
New-Item -ItemType Directory -Force -Path $mediaDir | Out-Null

$ytDlpAsset = Get-GitHubReleaseAsset "yt-dlp/yt-dlp" { $_.name -eq "yt-dlp.exe" }
Invoke-WebRequest $ytDlpAsset.browser_download_url -OutFile (Join-Path $mediaDir "yt-dlp.exe")

$denoAsset = Get-GitHubReleaseAsset "denoland/deno" { $_.name -eq "deno-x86_64-pc-windows-msvc.zip" }
$denoZip = Join-Path $env:TEMP $denoAsset.name
$denoExtract = Join-Path $env:TEMP "voicechatbot-deno-$Version"
Invoke-WebRequest $denoAsset.browser_download_url -OutFile $denoZip
if (Test-Path $denoExtract) {
    Remove-Item -LiteralPath $denoExtract -Recurse -Force
}
Expand-Archive -LiteralPath $denoZip -DestinationPath $denoExtract -Force
Copy-Item (Get-ChildItem $denoExtract -Recurse -Filter deno.exe | Select-Object -First 1 -ExpandProperty FullName) `
    (Join-Path $mediaDir "deno.exe") -Force

$ffmpegAsset = Get-GitHubReleaseAsset "yt-dlp/FFmpeg-Builds" {
    $_.name -match "win64-gpl\.zip$" -and $_.name -notmatch "shared"
}
$ffmpegZip = Join-Path $env:TEMP $ffmpegAsset.name
$ffmpegExtract = Join-Path $env:TEMP "voicechatbot-ffmpeg-$Version"
Invoke-WebRequest $ffmpegAsset.browser_download_url -OutFile $ffmpegZip
if (Test-Path $ffmpegExtract) {
    Remove-Item -LiteralPath $ffmpegExtract -Recurse -Force
}
Expand-Archive -LiteralPath $ffmpegZip -DestinationPath $ffmpegExtract -Force
Copy-Item (Get-ChildItem $ffmpegExtract -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName) `
    (Join-Path $mediaDir "ffmpeg.exe") -Force

$thirdParty = @"
Bundled media tools
===================

yt-dlp
Source: https://github.com/yt-dlp/yt-dlp
Release: $($ytDlpAsset.browser_download_url)
License: The Unlicense

Deno
Source: https://github.com/denoland/deno
Release: $($denoAsset.browser_download_url)
License: MIT

FFmpeg build for yt-dlp
Source: https://github.com/yt-dlp/FFmpeg-Builds
Release: $($ffmpegAsset.browser_download_url)
License: GPL build; see https://ffmpeg.org/legal.html
"@
Set-Content (Join-Path $mediaDir "THIRD-PARTY-NOTICES.txt") $thirdParty -Encoding utf8

$isccCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e"
}

& $iscc "/DMyAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$zipPath = Join-Path $artifactsDir "VoiceChatbot-$Version-win-x64-portable.zip"
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$checksumsPath = Join-Path $artifactsDir "SHA256SUMS.txt"
Get-ChildItem $artifactsDir -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } |
    Set-Content -Path $checksumsPath -Encoding ascii

Write-Host ""
Write-Host "Artifacts:"
Get-ChildItem $artifactsDir -File | Select-Object Name, Length, LastWriteTime
