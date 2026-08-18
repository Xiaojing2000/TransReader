#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #error SourceDir must point to the published application directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
AppId={{8A73C614-FA7F-4E56-8EC8-55350FB9B55E}
AppName=TransReader
AppVersion={#MyAppVersion}
AppVerName=TransReader {#MyAppVersion}
AppPublisher=TransReader contributors
DefaultDirName={localappdata}\Programs\TransReader
DefaultGroupName=TransReader
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir={#OutputDir}
OutputBaseFilename=TransReader-v{#MyAppVersion}-win-x64-setup
SetupIconFile={#SourceDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\TransReader.App.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.19041
LicenseFile={#SourceDir}\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Files shipped by 0.3.0 through the full Windows App SDK metapackage or the
; development runtime. Delete them during an in-place upgrade so slimming the
; new payload also reduces an existing installation.
[InstallDelete]
Type: files; Name: "{app}\CommunityToolkit.*"
Type: files; Name: "{app}\Microsoft.Windows.AI.*"
Type: files; Name: "{app}\Microsoft.Windows.Widgets.*"
Type: files; Name: "{app}\Microsoft.Graphics.Imaging*"
Type: files; Name: "{app}\Microsoft.Graphics.Internal.Imaging*"
Type: files; Name: "{app}\Microsoft.Windows.Workloads*"
Type: files; Name: "{app}\Microsoft.Windows.Private.Workloads.*"
Type: files; Name: "{app}\Microsoft.Windows.PrivateCommon.winmd"
Type: files; Name: "{app}\Microsoft.Windows.*Vision*.winmd"
Type: files; Name: "{app}\Microsoft.Windows.SemanticSearch.winmd"
Type: files; Name: "{app}\Microsoft.WindowsAppRuntime.Insights.Resource.dll"
Type: files; Name: "{app}\WindowsAppSdk.AppxDeploymentExtensions.*"
Type: files; Name: "{app}\WindowsAppRuntime.DeploymentExtensions.*"
Type: files; Name: "{app}\SessionHandleIPCProxyStub.dll"
Type: files; Name: "{app}\DWriteCore.dll"
Type: files; Name: "{app}\WindowsAppRuntime.png"
Type: files; Name: "{app}\Microsoft.ML.OnnxRuntime.dll"
Type: files; Name: "{app}\DirectML.dll"
Type: files; Name: "{app}\onnxruntime*.dll"
Type: files; Name: "{app}\System.Numerics.Tensors.dll"
Type: files; Name: "{app}\workloads*.json"
Type: filesandordirs; Name: "{app}\NpuDetect"
Type: files; Name: "{app}\TransReader.*.pdb"
Type: files; Name: "{app}\createdump.exe"
Type: files; Name: "{app}\Microsoft.DiaSymReader.Native.amd64.dll"
Type: files; Name: "{app}\mscordaccore*.dll"
Type: files; Name: "{app}\mscordbi.dll"
Type: files; Name: "{app}\Assets\AppIcon-16.png"
Type: files; Name: "{app}\Assets\AppIcon-20.png"
Type: files; Name: "{app}\Assets\AppIcon-24.png"
Type: files; Name: "{app}\Assets\AppIcon-32.png"
Type: files; Name: "{app}\Assets\AppIcon-48.png"
Type: files; Name: "{app}\Assets\AppIcon-64.png"
Type: files; Name: "{app}\Assets\AppIcon-256.png"
Type: files; Name: "{app}\Assets\AppIcon-512.png"
Type: filesandordirs; Name: "{app}\TransReader.App.exe.WebView2"

[Icons]
Name: "{group}\TransReader"; Filename: "{app}\TransReader.App.exe"
Name: "{autodesktop}\TransReader"; Filename: "{app}\TransReader.App.exe"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\Applications\TransReader.App.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "TransReader"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\TransReader.App.exe\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\TransReader.App.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\TransReader.App.exe"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{app}\TransReader.App.exe"; Description: "{cm:LaunchProgram,TransReader}"; Flags: nowait postinstall skipifsilent
