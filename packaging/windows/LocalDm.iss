; LocalDm per-user installer (Inno Setup 6)。
; 数据保留策略：卸载不删除 %LOCALAPPDATA%\LocalDm 下的剧本包与存档，除非用户显式勾选。
; SourceDir / OutputDir 由 Publish.ps1 显式传入。

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

[Setup]
AppId={{6B1B7B2E-8E4F-4A43-9E9C-2A1E7E1B7C11}}
AppName=Dungeon Master
AppVersion=0.1.0
AppPublisher=LocalDm
DefaultDirName={autopf}\LocalDm
DefaultGroupName=Dungeon Master
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=LocalDm-Setup-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\LocalDm.exe
SetupIconFile=..\..\src\LocalDm.App\Assets\app.ico
; 安装目录只放程序，不放用户数据
UsePreviousAppDir=yes
; 改名后不沿用旧的「本局主持」开始菜单文件夹
UsePreviousGroup=no

[InstallDelete]
; 改名前留下的快捷方式（开始菜单旧文件夹与桌面）
Type: filesandordirs; Name: "{userprograms}\本局主持"
Type: files; Name: "{autodesktop}\本局主持.lnk"

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Dungeon Master"; Filename: "{app}\LocalDm.exe"
Name: "{group}\卸载 Dungeon Master"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Dungeon Master"; Filename: "{app}\LocalDm.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LocalDm.exe"; Description: "启动 Dungeon Master"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理安装目录自身的空目录；用户数据目录绝不触碰
Type: dirifempty; Name: "{app}"

[Code]
function InitializeUninstall(): Boolean;
var
  Msg: String;
begin
  { 静默卸载（/VERYSILENT）时不能弹模态框，否则会卡住自动化与无人值守卸载 }
  if UninstallSilent() then
  begin
    Result := True;
    exit;
  end;

  Msg := '卸载程序不会删除你的运行数据：' + #13#10 +
         ExpandConstant('{localappdata}') + '\LocalDm（剧本包、存档、设置）' + #13#10#13#10 +
         '需要彻底清理时请手动删除该目录。继续卸载？';
  Result := MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES;
end;
