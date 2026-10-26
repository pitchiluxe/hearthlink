#define AppVersion "0.3.1"
[Setup]
AppId={{8910C137-DC6A-4A87-BE94-601B77D80391}
AppName=HearthLink
AppVersion={#AppVersion}
DefaultDirName={autopf}\HearthLink
DefaultGroupName=HearthLink
OutputDir=..\artifacts
OutputBaseFilename=HearthLink-0.3.1-win-x64-setup
SetupIconFile=..\assets\HearthLink.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\HearthLink.exe
CloseApplications=yes
[Tasks]
Name: firewall; Description: "Also allow direct connections on Private networks (TCP 45831, local subnet only) — not needed for normal use"; Flags: unchecked
[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\HearthLink"; Filename: "{app}\HearthLink.exe"
Name: "{group}\Uninstall HearthLink"; Filename: "{uninstallexe}"
[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""HearthLink Host TCP"" dir=in action=allow program=""{app}\HearthLink.exe"" protocol=TCP localport=45831 remoteip=localsubnet profile=private"; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{app}\HearthLink.exe"; Description: "Launch HearthLink"; Flags: nowait postinstall skipifsilent runasoriginaluser
[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""HearthLink Host TCP"" program=""{app}\HearthLink.exe"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHearthLinkFirewall"
