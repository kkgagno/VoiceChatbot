param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

function Get-ReleaseAsset {
    param(
        [string]$Repository,
        [scriptblock]$Filter
    )

    $headers = @{ "User-Agent" = "VoiceChatbot-Build" }
    if ($env:GITHUB_TOKEN) {
        $headers["Authorization"] = "Bearer $($env:GITHUB_TOKEN)"
        $headers["X-GitHub-Api-Version"] = "2022-11-28"
    }

    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repository/releases/latest" `
        -Headers $headers
    $asset = $release.assets | Where-Object $Filter | Select-Object -First 1
    if (-not $asset) {
        throw "No matching release asset was found for $Repository."
    }
    return $asset
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$ytDlp = Get-ReleaseAsset "yt-dlp/yt-dlp" { $_.name -eq "yt-dlp.exe" }
Invoke-WebRequest $ytDlp.browser_download_url -OutFile (Join-Path $OutputDirectory "yt-dlp.exe")

$deno = Get-ReleaseAsset "denoland/deno" { $_.name -eq "deno-x86_64-pc-windows-msvc.zip" }
$denoZip = Join-Path $env:TEMP $deno.name
$denoExtract = Join-Path $env:TEMP "voicechatbot-deno-$([guid]::NewGuid().ToString('N'))"
Invoke-WebRequest $deno.browser_download_url -OutFile $denoZip
Expand-Archive -LiteralPath $denoZip -DestinationPath $denoExtract -Force
Copy-Item `
    (Get-ChildItem $denoExtract -Recurse -Filter deno.exe | Select-Object -First 1 -ExpandProperty FullName) `
    (Join-Path $OutputDirectory "deno.exe") -Force

$ffmpeg = Get-ReleaseAsset "yt-dlp/FFmpeg-Builds" {
    $_.name -match "win64-gpl\.zip$" -and $_.name -notmatch "shared"
}
$ffmpegZip = Join-Path $env:TEMP $ffmpeg.name
$ffmpegExtract = Join-Path $env:TEMP "voicechatbot-ffmpeg-$([guid]::NewGuid().ToString('N'))"
Invoke-WebRequest $ffmpeg.browser_download_url -OutFile $ffmpegZip
Expand-Archive -LiteralPath $ffmpegZip -DestinationPath $ffmpegExtract -Force
Copy-Item `
    (Get-ChildItem $ffmpegExtract -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName) `
    (Join-Path $OutputDirectory "ffmpeg.exe") -Force

@"
Bundled media tools
===================

yt-dlp: https://github.com/yt-dlp/yt-dlp (The Unlicense)
Deno: https://github.com/denoland/deno (MIT)
FFmpeg build: https://github.com/yt-dlp/FFmpeg-Builds (GPL build)
"@ | Set-Content (Join-Path $OutputDirectory "THIRD-PARTY-NOTICES.txt") -Encoding utf8

& (Join-Path $OutputDirectory "yt-dlp.exe") --version
& (Join-Path $OutputDirectory "deno.exe") --version
& (Join-Path $OutputDirectory "ffmpeg.exe") -version
