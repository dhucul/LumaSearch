#define MyAppName "LumaSearch"
#define MyAppVersion "1.4.4"
#ifndef ReleaseDirectory
  #define ReleaseDirectory AddBackslash(SourcePath) + "..\artifacts\publish\Release\win-x64"
#endif

#ifnexist ReleaseDirectory + "\LumaSearch.exe"
  #error "Publish the Release build with build.ps1 before compiling the installer."
#endif
#if GetFileVersion(ReleaseDirectory + "\LumaSearch.exe") != MyAppVersion + ".0"
  #error "The published application version does not match the installer version."
#endif

#if Exec("dotnet.exe", AddQuotes(AddBackslash(ReleaseDirectory) + "LumaSearch.dll") + " --verify-release", ReleaseDirectory, 1, SW_HIDE) != 0
  #error "The installer payload failed Release verification. Run build.ps1."
#endif

[Setup]
AppId={{88E32521-7C34-4598-89E7-46FEC39F0B96}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=LumaSearch
DefaultDirName={autopf}\LumaSearch
UsePreviousAppDir=no
DisableDirPage=no
DefaultGroupName=LumaSearch
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=.
OutputBaseFilename=LumaSearch-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\Assets\LumaSearch.ico
UninstallDisplayIcon={app}\LumaSearch.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#ReleaseDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LumaSearch"; Filename: "{app}\LumaSearch.exe"
Name: "{autodesktop}\LumaSearch"; Filename: "{app}\LumaSearch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LumaSearch.exe"; Description: "Launch LumaSearch"; Flags: nowait postinstall skipifsilent runascurrentuser
