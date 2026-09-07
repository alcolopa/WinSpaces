; Inno Setup script for Windows Spaces.
; Built by .github/workflows/release.yml via ISCC.exe, which passes:
;   /DAppVersion=<version>  /DPublishDir=<path to dotnet publish output>
;
; To build locally:
;   dotnet publish src\WindowsSpaces.App\WindowsSpaces.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o publish\App
;   iscc installer\WindowsSpaces.iss /DAppVersion=0.0.0-dev /DPublishDir=..\publish\App

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\App"
#endif

#define AppName "Windows Spaces"
#define AppExeName "WindowsSpaces.App.exe"
#define AppPublisher "Emilio El Murr"
#define AppUrl "https://github.com/alcolopa/WinSpaces"

[Setup]
AppId={{9E9F6A9E-7A3D-4E6C-9E7F-6C6A6B9C7D9A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={autopf}\WindowsSpaces
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=WindowsSpacesSetup-{#AppVersion}
SetupIconFile=..\src\WindowsSpaces.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startupicon"; Description: "Start Windows Spaces automatically when Windows starts"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; Config lives in %APPDATA%\WindowsSpaces (see AppHost.cs); left in place on
; uninstall by default so re-installing keeps the user's settings. Uncomment
; to wipe it instead:
; [UninstallDelete]
; Type: filesandordirs; Name: "{userappdata}\WindowsSpaces"
