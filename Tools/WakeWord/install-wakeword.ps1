$ErrorActionPreference = "Stop"

# Use the same Python that Voice Chatbot runs: VOICECHATBOT_PYTHON, then the python.org install
# folders it checks, then python.exe on PATH.
$candidates = @(
    $env:VOICECHATBOT_PYTHON,
    "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe",
    "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe",
    "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe",
    "$env:ProgramFiles\Python313\python.exe",
    "$env:ProgramFiles\Python312\python.exe",
    "$env:ProgramFiles\Python311\python.exe"
)
$python = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $python) {
    if (-not (Get-Command python.exe -ErrorAction SilentlyContinue)) {
        throw "Python was not found. Install Python 3.11 x64 and enable 'Add Python to PATH'."
    }
    $python = "python.exe"
}
Write-Host "Using $python"

& $python -m pip install --upgrade pip
& $python -m pip install -r "$PSScriptRoot\requirements.txt"
if ($LASTEXITCODE -ne 0) { throw "pip could not install the wake word packages." }

# Download the wake word models now so the first start in the app does not have to.
& $python "$PSScriptRoot\wakeword_server.py" --download-only
if ($LASTEXITCODE -ne 0) { throw "The wake word models could not be downloaded. Check the internet connection and run this script again." }

Write-Host ""
Write-Host "Wake word detector installed. In Voice Chatbot, open Voice Input and turn on 'Listen for a wake word'."
