param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot "VoiceChatbot.csproj"
$publishDir = Join-Path $projectRoot "bin\Release\publish\win-x64"
$artifactsDir = Join-Path $projectRoot "artifacts"
$installerScript = Join-Path $projectRoot "installer\VoiceChatbotMini.iss"

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
Get-ChildItem $artifactsDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

$dotnetCandidates = @(
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

function Get-GitHubHeaders {
    $headers = @{ "User-Agent" = "VoiceChatbotMini-Build" }
    if ($env:GITHUB_TOKEN) {
        $headers["Authorization"] = "Bearer $($env:GITHUB_TOKEN)"
        $headers["X-GitHub-Api-Version"] = "2022-11-28"
    }
    return $headers
}

function Get-GitHubReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][scriptblock]$AssetFilter
    )

    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repository/releases/latest" `
        -Headers (Get-GitHubHeaders)
    $asset = $release.assets | Where-Object $AssetFilter | Select-Object -First 1
    if (-not $asset) {
        throw "No matching release asset was found for $Repository."
    }
    return $asset
}

# The newest release that already has a matching asset (a brand-new release may still be uploading its files).
# Pre-releases count: llama.cpp publishes its Windows builds as nightly pre-releases (b11429, ...), while its
# "Latest" versioned releases (v0.6.0, ...) carry no binaries.
function Get-RecentReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][scriptblock]$AssetFilter
    )

    $releases = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repository/releases?per_page=30" `
        -Headers (Get-GitHubHeaders)
    foreach ($release in $releases) {
        if ($release.draft) {
            continue
        }
        $asset = $release.assets | Where-Object $AssetFilter | Select-Object -First 1
        if ($asset) {
            return [pscustomobject]@{ Asset = $asset; Tag = $release.tag_name }
        }
    }
    $newest = $releases | Select-Object -First 1
    $names = ($newest.assets | ForEach-Object { $_.name }) -join ", "
    throw "No recent release of $Repository has a matching asset. Newest release $($newest.tag_name) has: $names"
}

# curl.exe is much faster than Invoke-WebRequest for multi-GB files.
function Save-Download {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$OutFile
    )

    Write-Host "Downloading $Url"
    & curl.exe -L --fail --silent --show-error --retry 5 --retry-delay 5 -o $OutFile $Url
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed (curl exit code $LASTEXITCODE): $Url"
    }
}

$ProgressPreference = "SilentlyContinue"

$mediaDir = Join-Path $publishDir "Tools\Media"
New-Item -ItemType Directory -Force -Path $mediaDir | Out-Null

$ytDlpAsset = Get-GitHubReleaseAsset "yt-dlp/yt-dlp" { $_.name -eq "yt-dlp.exe" }
Invoke-WebRequest $ytDlpAsset.browser_download_url -OutFile (Join-Path $mediaDir "yt-dlp.exe")

$denoAsset = Get-GitHubReleaseAsset "denoland/deno" { $_.name -eq "deno-x86_64-pc-windows-msvc.zip" }
$denoZip = Join-Path $env:TEMP $denoAsset.name
$denoExtract = Join-Path $env:TEMP "voicechatbotmini-deno-$Version"
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
$ffmpegExtract = Join-Path $env:TEMP "voicechatbotmini-ffmpeg-$Version"
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

# ==================== Built-in AI: llama.cpp, Gemma 4 E4B and Kokoro ====================

# llama.cpp's Vulkan build runs on NVIDIA, AMD and Intel graphics cards, and on the processor without one.
$llama = Get-RecentReleaseAsset "ggml-org/llama.cpp" { $_.name -match '^llama-.+-bin-win-vulkan-x64\.zip$' }
$llamaZip = Join-Path $env:TEMP $llama.Asset.name
$llamaExtract = Join-Path $env:TEMP "voicechatbotmini-llama-$Version"
Save-Download $llama.Asset.browser_download_url $llamaZip
if (Test-Path $llamaExtract) {
    Remove-Item -LiteralPath $llamaExtract -Recurse -Force
}
Expand-Archive -LiteralPath $llamaZip -DestinationPath $llamaExtract -Force
$serverExe = Get-ChildItem $llamaExtract -Recurse -Filter llama-server.exe | Select-Object -First 1
if (-not $serverExe) {
    throw "llama-server.exe was not found in $($llama.Asset.name)."
}
$llamaDir = Join-Path $publishDir "llama"
New-Item -ItemType Directory -Force -Path $llamaDir | Out-Null
Copy-Item $serverExe.FullName $llamaDir -Force
Get-ChildItem $serverExe.DirectoryName -Filter *.dll | Copy-Item -Destination $llamaDir -Force
Get-ChildItem $serverExe.DirectoryName -Filter LICENSE* -ErrorAction SilentlyContinue | Copy-Item -Destination $llamaDir -Force
# llama.cpp is built with Visual C++ (and OpenMP): ship that runtime next to it, for PCs without the
# Visual C++ redistributable. Windows looks in the exe's own folder first.
foreach ($dll in @("msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll")) {
    $runtimeTarget = Join-Path $llamaDir $dll
    $runtimeSource = Join-Path $env:WINDIR "System32\$dll"
    if (-not (Test-Path $runtimeTarget) -and (Test-Path $runtimeSource)) {
        Copy-Item $runtimeSource $runtimeTarget
    }
}
Set-Content (Join-Path $llamaDir "VERSION.txt") "llama.cpp $($llama.Tag) ($($llama.Asset.name))" -Encoding utf8
Write-Host "Bundled llama.cpp $($llama.Tag)."

# Gemma 4 E4B, Q4_K_M: the model that works right after installing. The app's model chooser
# (Core/LocalModelCatalog.cs) looks for it under this exact name.
$gemmaRepos = @(
    "ggml-org/gemma-4-E4B-it-GGUF",
    "unsloth/gemma-4-E4B-it-GGUF",
    "lmstudio-community/gemma-4-E4B-it-GGUF",
    "bartowski/google_gemma-4-E4B-it-GGUF"
)
$gemma = $null
foreach ($repo in $gemmaRepos) {
    try {
        $tree = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$repo/tree/main?recursive=true" -Headers @{ "User-Agent" = "VoiceChatbotMini-Build" }
    }
    catch {
        Write-Host "  $($repo): $($_.Exception.Message)"
        continue
    }
    $file = $tree |
        Where-Object { $_.type -eq "file" -and $_.path -match '(^|[-_./])Q4_K_M\.gguf$' -and $_.path -notmatch 'mmproj' -and $_.path -notmatch '-\d{5}-of-\d{5}\.gguf$' } |
        Sort-Object @{ Expression = { if ($_.path -match 'UD[-_]Q4') { 1 } else { 0 } } }, @{ Expression = { $_.path.Length } } |
        Select-Object -First 1
    if ($file) {
        # The listing is kept: the picture support file below must come from this same repository.
        $gemma = [pscustomobject]@{ Repo = $repo; File = $file; Tree = $tree }
        break
    }
    Write-Host "  $($repo): no Q4_K_M file."
}
if (-not $gemma) {
    throw "No Gemma 4 E4B Q4_K_M GGUF was found on Hugging Face."
}
$modelsDir = Join-Path $publishDir "models"
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
$gemmaFile = Join-Path $modelsDir "gemma-4-E4B-it-Q4_K_M.gguf"
$gemmaUrl = "https://huggingface.co/$($gemma.Repo)/resolve/main/$($gemma.File.path)"
Save-Download $gemmaUrl $gemmaFile
$expectedSha = "$($gemma.File.lfs.oid)" -replace '^sha256:', ''
if ($expectedSha -match '^[0-9a-fA-F]{64}$') {
    $actualSha = (Get-FileHash $gemmaFile -Algorithm SHA256).Hash
    if ($actualSha -ne $expectedSha) {
        throw "The Gemma 4 E4B download is damaged (SHA-256 $actualSha, expected $expectedSha)."
    }
}
Write-Host "Bundled $($gemma.Repo)/$($gemma.File.path) ($([math]::Round((Get-Item $gemmaFile).Length / 1GB, 2)) GB)."

# Gemma 4 E4B's picture support (vision projector, "mmproj"), from the same repository as the model: f16
# preferred, then bf16, q8_0, f32. The app looks for it under this exact name (LocalModelInfo.ProjectorFileName).
# Without one the app still works, text only.
function Get-ProjectorRank {
    param([string]$Path)
    $name = ($Path -split '/')[-1]
    # Whole words only: "bf16" must not count as "f16".
    if ($name -match '(?<![a-z0-9])fp?16(?![a-z0-9])') { return 0 }
    if ($name -match '(?<![a-z0-9])bf16(?![a-z0-9])') { return 1 }
    if ($name -match '(?<![a-z0-9])q8_0(?![a-z0-9])') { return 2 }
    if ($name -match '(?<![a-z0-9])fp?32(?![a-z0-9])') { return 3 }
    return 4
}
$projectorFile = Join-Path $modelsDir "gemma-4-E4B-it-mmproj.gguf"
if (Test-Path $projectorFile) {
    Remove-Item -LiteralPath $projectorFile -Force
}
$projector = $gemma.Tree |
    Where-Object { $_.type -eq "file" -and $_.path -match '(^|/)[^/]*mmproj[^/]*\.gguf$' -and $_.path -notmatch '-\d{5}-of-\d{5}\.gguf$' } |
    Sort-Object @{ Expression = { Get-ProjectorRank $_.path } }, @{ Expression = { ($_.path -split '/').Count } }, @{ Expression = { $_.path.Length } } |
    Select-Object -First 1
$projectorNotice = ""
if ($projector) {
    $projectorUrl = "https://huggingface.co/$($gemma.Repo)/resolve/main/$($projector.path)"
    Save-Download $projectorUrl $projectorFile
    $expectedProjectorSha = "$($projector.lfs.oid)" -replace '^sha256:', ''
    if ($expectedProjectorSha -match '^[0-9a-fA-F]{64}$') {
        $actualProjectorSha = (Get-FileHash $projectorFile -Algorithm SHA256).Hash
        if ($actualProjectorSha -ne $expectedProjectorSha) {
            throw "The Gemma 4 E4B picture support download is damaged (SHA-256 $actualProjectorSha, expected $expectedProjectorSha)."
        }
    }
    Write-Host "Bundled picture support $($gemma.Repo)/$($projector.path) ($([math]::Round((Get-Item $projectorFile).Length / 1MB)) MB)."
    $projectorNotice = @"

Gemma 4 E4B picture support (vision projector, $($projector.path))
Source: https://huggingface.co/$($gemma.Repo)
License: see the model card (Gemma 4 is released by Google DeepMind)

"@
}
else {
    Write-Warning "$($gemma.Repo) has no picture support (mmproj) file: the included Gemma 4 E4B will read text only until picture support is added in the app."
}

# Kokoro 82M for the built-in voice (KokoroSharp loads it from kokoro\kokoro.onnx; voices come with the build).
$kokoroDir = Join-Path $publishDir "kokoro"
New-Item -ItemType Directory -Force -Path $kokoroDir | Out-Null
$kokoroUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx"
$kokoroFile = Join-Path $kokoroDir "kokoro.onnx"
Save-Download $kokoroUrl $kokoroFile
if ((Get-Item $kokoroFile).Length -lt 100MB) {
    throw "The Kokoro model download is too small; it is probably an error page."
}
if (-not (Test-Path (Join-Path $kokoroDir "voices\af_heart.npy"))) {
    throw "The Kokoro voices are missing from the published app (kokoro\voices)."
}

$aiNotices = @"
Built-in AI components
======================

llama.cpp ($($llama.Tag))
Source: https://github.com/ggml-org/llama.cpp
Release: $($llama.Asset.browser_download_url)
License: MIT

Gemma 4 E4B (Q4_K_M GGUF)
Source: https://huggingface.co/$($gemma.Repo)
License: see the model card (Gemma 4 is released by Google DeepMind)
$projectorNotice
Kokoro 82M text-to-speech model and voices
Source: https://huggingface.co/hexgrad/Kokoro-82M (ONNX export: https://github.com/Lyrcaxis/KokoroSharpBinaries)
License: Apache 2.0

KokoroSharp
Source: https://github.com/Lyrcaxis/KokoroSharp
License: MIT
"@
Set-Content (Join-Path $publishDir "THIRD-PARTY-NOTICES.txt") $aiNotices -Encoding utf8

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

# No portable zip: with the multi-GB model inside it would double the download. The installer is
# Setup.exe plus its Setup-N.bin parts (keep them together in one folder).

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
