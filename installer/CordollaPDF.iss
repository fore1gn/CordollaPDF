#ifndef AppName
  #define AppName "CordollaPDF"
#endif

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef AppPublisher
  #define AppPublisher "CordollaPDF"
#endif

#ifndef AppExeName
  #define AppExeName "CordollaPDF.exe"
#endif

#ifndef AppAssocKey
  #define AppAssocKey "CordollaPDF.pdf"
#endif

#ifndef PublishDir
  #define PublishDir "..\\artifacts\\publish\\win-x64-single"
#endif

#ifndef OutputDir
  #define OutputDir "..\\artifacts\\installer"
#endif

[Setup]
AppId={{9E19E4D8-6D4E-4333-A4D0-56363A4EFBE9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.txt
OutputDir={#OutputDir}
OutputBaseFilename=CordollaPDF-Setup-{#AppVersion}
SetupIconFile=..\CordollaPDF\Assets\app_icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "pdfassociation"; Description: "Register CordollaPDF for PDF files"; GroupDescription: "File integration:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassociation
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: pdfassociation

Root: HKA; Subkey: "Software\Classes\{#AppAssocKey}"; ValueType: string; ValueData: "{#AppName} PDF"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\Classes\{#AppAssocKey}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\Classes\{#AppAssocKey}\shell\open"; ValueType: string; ValueName: "MuiVerb"; ValueData: "Open with {#AppName}"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\Classes\{#AppAssocKey}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: pdfassociation

Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "{#AppAssocKey}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassociation
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\OpenWithCordollaPDF"; ValueType: string; ValueData: "Open with {#AppName}"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\OpenWithCordollaPDF\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: pdfassociation

Root: HKA; Subkey: "Software\CordollaPDF\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\CordollaPDF\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "CordollaPDF PDF viewer"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\CordollaPDF\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#AppAssocKey}"; Flags: uninsdeletekey; Tasks: pdfassociation
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "CordollaPDF"; ValueData: "Software\CordollaPDF\Capabilities"; Flags: uninsdeletevalue; Tasks: pdfassociation

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
