; Installer per utente singolo: niente permessi di amministratore (l'app deve girare senza privilegi elevati,
; altrimenti Esplora file non le consegna i drop). Si compila con installer\build.ps1.

#define AppVersion GetVersionNumbersString("..\.build\publish\Clementine.App.exe")

[Setup]
AppId={{6F1C2A4E-3B7D-4E8A-9C51-2D7F0B8E4A13}
AppName=Clementine
AppVersion={#AppVersion}
AppPublisher=Clementine
DefaultDirName={localappdata}\Programs\Clementine
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\.build\installer
OutputBaseFilename=Clementine-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\Clementine.App.exe
UninstallDisplayName=Clementine
; chiude Clementine se è aperta, sia per aggiornare sia per disinstallare
CloseApplications=force

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "autostart"; Description: "Avvia Clementine all'accensione del computer"

[Files]
Source: "..\.build\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\Clementine"; Filename: "{app}\Clementine.App.exe"

[Registry]
; stesso valore che scrive la finestra Impostazioni (Autostart.cs)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Clementine"; \
    ValueData: """{app}\Clementine.App.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Clementine.App.exe"; Description: "Avvia Clementine"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; chiude Clementine, toglie le voci del clic destro e l'avvio automatico; impostazioni e log in %APPDATA%\Clementine restano
Filename: "{app}\Clementine.App.exe"; Parameters: "--unregister"; RunOnceId: "Unregister"; Flags: runhidden waituntilterminated

[Code]
// aggiornamento: la Clementine aperta va chiusa prima di sostituirne i file
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\Clementine.App.exe')) then
    Exec(ExpandConstant('{app}\Clementine.App.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
