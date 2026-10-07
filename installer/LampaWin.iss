#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{C7A5C33F-3531-44A0-AB47-622913A015D9}
AppName=LampaWin
AppVersion=1.0.0
AppPublisher=LampaWin contributors
DefaultDirName={localappdata}\Programs\LampaWin
MinVersion=10.0.17763
DefaultGroupName=LampaWin
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=LampaWin-Setup-win-x64
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\LampaWin.Desktop\Assets\LampaWin.ico
WizardImageFile=..\src\LampaWin.Desktop\Assets\Installer-Wizard.bmp
WizardSmallImageFile=..\src\LampaWin.Desktop\Assets\Installer-Small.bmp
InfoBeforeFile=webview2-terms.txt
UninstallDisplayIcon={app}\LampaWin.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные значки:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\components\webview2\MicrosoftEdgeWebView2Setup.exe"; Flags: dontcopy; Check: not IsWebView2Installed

[Icons]
Name: "{autoprograms}\LampaWin"; Filename: "{app}\LampaWin.exe"
Name: "{autodesktop}\LampaWin"; Filename: "{app}\LampaWin.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LampaWin.exe"; Description: "Запустить LampaWin"; Flags: postinstall nowait skipifsilent

[Code]
function IsValidWebView2Version(const Version: string): Boolean;
begin
  Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function IsWebView2Installed: Boolean;
var
  Version: string;
begin
  Result := RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and IsValidWebView2Version(Version);
  if not Result then
    Result := RegQueryStringValue(HKLM, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and IsValidWebView2Version(Version);
  if not Result then
    Result := RegQueryStringValue(HKLM, 'Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and IsValidWebView2Version(Version);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if IsWebView2Installed then
    Exit;

  ExtractTemporaryFile('MicrosoftEdgeWebView2Setup.exe');

  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := 'Не удалось запустить установщик Microsoft Edge WebView2 Runtime. Проверьте подключение к Интернету и запустите установку повторно.'
  else if (ExitCode <> 0) or not IsWebView2Installed then
    Result := 'Microsoft Edge WebView2 Runtime не установлена. Для этого шага требуется подключение к Интернету. Установите Runtime и повторите установку LampaWin.';
end;
