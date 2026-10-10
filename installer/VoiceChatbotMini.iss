; Voice Chatbot Mini: its own AppId, install folder, exe and shortcuts, so it installs next to the full
; Voice Chatbot app without replacing or uninstalling it. Its data is in %APPDATA%\VoiceChatbotMini.
#define MyAppName "Voice Chatbot Mini"
#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Keith Gagnon"
#define MyAppExeName "VoiceChatbotMini.exe"

[Setup]
AppId={{49724379-390E-4C03-8B27-D47955CAC8E7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\VoiceChatbotMini
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=VoiceChatbotMini-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\Resources\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
; The bundled Gemma 4 model makes the setup several GB: Setup.exe plus Setup-N.bin parts (under 2 GB each).
DiskSpanning=yes
DiskSliceSize=max
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Windows voice AI client, mini edition

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\bin\Release\publish\win-x64\*"; Excludes: "\models\*,\kokoro\kokoro.onnx"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The AI models do not compress: store them as they are (much faster to build and install).
Source: "..\bin\Release\publish\win-x64\models\*"; DestDir: "{app}\models"; Flags: ignoreversion nocompression skipifsourcedoesntexist
Source: "..\bin\Release\publish\win-x64\kokoro\kokoro.onnx"; DestDir: "{app}\kokoro"; Flags: ignoreversion nocompression skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} Setup Guide"; Filename: "{app}\README.md"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
