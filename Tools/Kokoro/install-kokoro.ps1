$ErrorActionPreference = "Stop"

if (-not (Get-Command python.exe -ErrorAction SilentlyContinue)) {
    throw "Python was not found. Install Python 3.11 x64 and enable 'Add Python to PATH'."
}

python.exe -m pip install --upgrade pip
python.exe -m pip install -r "$PSScriptRoot\requirements.txt"

Write-Host ""
Write-Host "Kokoro dependencies installed. Voice Chatbot will start its local Kokoro server when needed."
