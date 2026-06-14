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
