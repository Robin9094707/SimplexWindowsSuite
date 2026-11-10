; Inno-Setup-Skript für Simplex Suite
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\SimplexSuite"
#endif

[Setup]
AppId={{6F1E5D2A-7B6C-4C1D-9A3E-5B8F2C7D1E90}
AppName=Simplex Suite
AppVersion={#AppVersion}
AppVerName=Simplex Suite {#AppVersion}
AppPublisher=Simplex
DefaultDirName={autopf}\Simplex Suite
DefaultGroupName=Simplex Suite
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=SimplexSuite-Setup-{#AppVersion}-x64
SetupIconFile=..\src\SimplexSuite\Assets\AppIcon.ico
UninstallDisplayIcon={app}\SimplexSuite.exe
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Simplex Suite"; Filename: "{app}\SimplexSuite.exe"
Name: "{group}\{cm:UninstallProgram,Simplex Suite}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Simplex Suite"; Filename: "{app}\SimplexSuite.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SimplexSuite.exe"; Description: "{cm:LaunchProgram,Simplex Suite}"; Flags: nowait postinstall skipifsilent
