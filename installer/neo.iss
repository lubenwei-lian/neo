
; ============================================================================
; neo 安装包脚本（Inno Setup 6）
;
;   禁止直接拿半成品 dist\publish 打包。正确顺序只有一条：
;     1) tools\publish.ps1        —— 生成完整 dist\publish（launcher + app\ +
;                                      app\runtime\ 全量资产 + 说明 + data）并做断言
;     2) iscc installer\neo.iss   —— 只允许打包上一步的产物
;   本文件在 [Code] 前用 ISPP FileExists 做编译期闸门：dist\publish\neo.exe 或
;   dist\publish\app\runtime\xray.exe 缺一个就 #error，iscc 直接失败；
;   tools\publish.ps1 末尾还有一份运行时断言。两处都不允许绕过。
;
; 版本号唯一来源：installer\version.iss（本文件不再写 #define MyAppVersion）。
; 升版本时改 version.iss + 两个 csproj + launcher rc + 说明.txt，
; publish.ps1 会逐项断言一致，不一致直接 throw。
; ============================================================================

#define MyAppName "neo"
#include "version.iss"
#define MyAppPublisher "neo"
#define MyAppExeName "neo.exe"

[Setup]
AppId={{8F5A1C2E-6D3B-4A97-9E44-7B2C5D8F0A13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={code:GetDefaultDir}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist\installer
OutputBaseFilename=neo-setup-{#MyAppVersion}
SetupIconFile=..\src\Neo.App\Assets\neo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex=neo-single-instance-4f8a2c
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=neo 安装程序

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: checkedonce
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "快捷方式："; Flags: checkedonce

[Files]
; 保留 ignoreversion：它保证新版文件永远覆盖（不因版本号比较被跳过），
; 这是发布正确性的底线。**残留堆积不是它造成的，而是没有 [InstallDelete]**。
; 所以「无条件覆盖 + 主动清旧」两件一起做，才既不漏新版也不留旧货。
; SHA256SUMS.txt 只是发布校验清单（publish.ps1 生成），不属于程序文件，不装进用户机器。
; 用户已更新到的 xray 26.10.17 覆盖回了 26.9.30。内核升级统一走应用内更新链路（SHA-256 校验/回滚），
; 安装包只在全新安装或文件缺失时补种。
; 2026-10-23：分流数据（geoip/geosite/Country.mmdb/rule-sets）也属于「应用内可更新」的资产，
; 安装包更新时**不得覆盖**用户已经更新过的版本（否则每次升级都会把数据回退到发布时那一天）。
Source: "..\dist\publish\*"; DestDir: "{app}"; Excludes: "SHA256SUMS.txt,app\runtime\xray.exe,app\runtime\sing-box.exe,app\runtime\mihomo.exe,app\runtime\geoip.dat,app\runtime\geosite.dat,app\runtime\Country.mmdb,app\runtime\rule-sets\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\publish\app\runtime\xray.exe"; DestDir: "{app}\app\runtime"; Flags: onlyifdoesntexist
Source: "..\dist\publish\app\runtime\sing-box.exe"; DestDir: "{app}\app\runtime"; Flags: onlyifdoesntexist
; mihomo 是「按需下载」的可选内核：发布目录通常不带它；若这次带了，同样只在缺失时补种，
; 绝不用安装包覆盖用户已经升级过的版本（xray/sing-box 同理，见上面两行）。
#if FileExists("..\dist\publish\app\runtime\mihomo.exe")
Source: "..\dist\publish\app\runtime\mihomo.exe"; DestDir: "{app}\app\runtime"; Flags: onlyifdoesntexist
#endif
; 分流数据：同样只在缺失时补种（全新安装/文件被删才会写），绝不用安装包覆盖用户更新过的数据。
#if FileExists("..\dist\publish\app\runtime\geoip.dat")
Source: "..\dist\publish\app\runtime\geoip.dat"; DestDir: "{app}\app\runtime"; Flags: onlyifdoesntexist
#endif
#if FileExists("..\dist\publish\app\runtime\geosite.dat")
Source: "..\dist\publish\app\runtime\geosite.dat"; DestDir: "{app}\app\runtime"; Flags: onlyifdoesntexist
#endif
#if FileExists("..\dist\publish\app\runtime\Country.mmdb")
Source: "..\dist\publish\app\runtime\Country.mmdb"; DestDir: "{app}\app\runtime"; Flags: onlyifdoesntexist
#endif
#if FileExists("..\dist\publish\app\runtime\rule-sets\geoip-cn.srs")
Source: "..\dist\publish\app\runtime\rule-sets\*"; DestDir: "{app}\app\runtime\rule-sets"; Flags: onlyifdoesntexist recursesubdirs
#endif

;
; 该目录继承的是 Program Files 的 ACL：BUILTIN\Users 只有 (I)(RX)，即**只读**。
; 唯一的可写 ACE 是 Users / CREATOR OWNER 的 (I)(OI)(CI)(IO) 项 —— InheritOnly，
; 只对「用户自己创建的文件」生效，而用户在该目录里**连创建文件的权限都没有**，
; 所以它等于不存在。
;
; 后果：日常启动是非提权的（[Run] 用 runasoriginaluser，launcher 也无提权 manifest），
; AppState.Save() 写 state.json 时抛 UnauthorizedAccessException —— 于是主题、代理模式、
; 多节点、窗口位置、DNS、**尤其是 UseTun** 全都只改了内存，磁盘上仍是旧值，重启读回旧值。
;
; ↑ 这段与下面 [InstallDelete] 那段「绝不触碰 data\」**不是一回事，不要混读**：
;   · 那条讲的是**不删除用户数据**（Do Not Delete）—— 怕重装把订阅/设置/日志清掉；
;   · 本条讲的是**调整目录权限**（ACL Modify）—— 让普通用户能写，好让设置真的存下来。
;   只放开「写权限」不会删任何既有文件；而只承诺「不删」也不等于用户能写。
;   两者方向不冲突，改这里时不要因为看见「绝不触碰 data」就把这个 [Dirs] 条目删掉。
;
; 权限范围刻意收得很窄（对齐 Inno 官方文档的告诫）：
;   · 只放开 {app}\data 这一个**子目录**，绝不碰 {app} 本身 —— {app} 装着 neo.exe 与 app\*.dll，
;     对它 everyone-modify 等于让普通用户篡改程序文件，是提权漏洞；
;     官方原话是「在 {app} 的子目录上改权限是安全的，例如 {app}\data」。
;   · users-modify = Modify（读/执行/创建/修改/删除，含子目录与文件）。
;     用它而不用 everyone-modify：后者除 Users 组外还含 Guests / Anonymous，
;     而 Users 已覆盖所有交互登录的普通用户，够用且更小。
;   · 这里会写入一个**显式**的 ACE，所以它不受上面那些「继承且 InheritOnly」项的干扰。
;
;   它们自身的 ACL 是继承来的 BUILTIN\Users:(I)(RX)，**没有写位**。
;   疑虑是「只给目录授 users-modify，这些**已存在**的文件会不会仍然写不进去」。
;     · 在目录上授 users:(OI)(CI)M 后，Windows 会把这条 ACE **下发到既有子对象**，
;       既有 state.json 的 ACL 立刻变成 BUILTIN\Users:(I)(M)；
;     · 非提权令牌下 追加写 / 新建文件 / tmp+Move 替换 / 新建子目录 **四项全部成功**；
;     · 同一目录在改动前，上述四项**全部「拒绝访问」**（即当前已装版本的故障现场）。
;   所以 users-modify 足够，不需要更大的 users-full。权限给到能写成的最小值。
;
; 【重要 · 前提条件，不要删】上面那条「ACE 会下发到既有子对象」依赖一个条件：
;   data 目录**自身**没有 `(OI)(CI)(IO)`（InheritOnly）形态的 Users 项。
;   `BUILTIN\Users:(I)(RX)` + `BUILTIN\Users:(I)(OI)(CI)(IO)(GR,GE)` ——
;   后者带 IO 标志，**不参与「只对本对象」的访问判定**，所以本对象判定的仍是 (RX)，
;   反例（别照抄）：如果某个目录的 Users 项是不带 IO 的 `(OI)(CI)(RX)`，
;   那它本身就参与本对象判定，授予 M 只会**按 (M, RX) 取并集**——对既有子文件仍然是只读，
;   「用 tmp 新建再 Move 替换」能过，但「原地覆盖既有文件」会失败。
[Dirs]
Name: "{app}\data"; Permissions: users-modify; Flags: uninsneveruninstall

[InstallDelete]
; 只清「本程序自己安装 / 自己产生」的产物类型。
; 绝不触碰 data\（用户订阅 / 设置 / 日志）与用户自己的文件。
; ※ 注意：这里的「绝不触碰」指**不删除数据**，与上面 [Dirs] 授予的写权限是两回事：
;   放开 ACL 不等于删除，二者不要互相套用（详见 [Dirs] 段的长注释）。
;
; {app}\app\（程序本体）+ {app}\app\runtime\（内核资产）。旧布局把本体直接铺在
; {app}\ 根、runtime 也在 {app}\runtime\，不显式清就会永远留在盘上：
;   · is-*.tmp       ：Inno 覆盖占用文件时产生的临时文件，根 / app\ / app\runtime\ 三处都有
;   · app\*.bak*     ：程序自更新留下的备份（如 xray.exe.bak-prev、neo.exe.bak-0.1.1-preupdate）
; 这些都不是用户数据；data\ 与用户手工放在 {app}\ 下的其它文件不在清理范围。
Type: files; Name: "{app}\app\*.dll"
Type: files; Name: "{app}\app\*.pdb"
Type: files; Name: "{app}\neo.exe"
Type: files; Name: "{app}\*.pdb"
Type: filesandordirs; Name: "{app}\app-sc"
Type: filesandordirs; Name: "{app}\runtime"
Type: files; Name: "{app}\is-*.tmp"
Type: files; Name: "{app}\app\is-*.tmp"
Type: files; Name: "{app}\app\runtime\is-*.tmp"
Type: files; Name: "{app}\app\*.bak*"
Type: files; Name: "{app}\app\runtime\*.bak*"

[Registry]
; HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 "neo" 值。
; ValueType: none 表示安装时不写值，只登记「卸载时删除该值」。
; dontcreatekey 表示安装时不主动创建 Run 键 —— 没开自启的用户不该多出一个空键。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "neo"; Flags: dontcreatekey uninsdeletevalue

[Icons]
Name: "{autodesktop}\neo"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autoprograms}\neo"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon

[Run]
Filename: "{app}\neo.exe"; Description: "立即启动 neo"; Flags: nowait postinstall runasoriginaluser

; ============================================================================
; dist\publish 缺关键产物时让 iscc 直接失败，而不是打出一个残废安装包。
; ISPP 的相对路径按本 .iss 所在目录（installer\）解析。
; 这里只查「是否存在」；体积、版本一致性、SHA256 由 tools\publish.ps1 末尾断言。
; ============================================================================
#if !FileExists("..\dist\publish\neo.exe")
  #error "dist\publish\neo.exe 不存在：这是半成品，禁止打包。先完整运行 tools\publish.ps1，再 iscc。"
#endif
#if !FileExists("..\dist\publish\app\runtime\xray.exe")
  #error "dist\publish\app\runtime\xray.exe 不存在：内核资产没进发布目录，禁止打包。先完整运行 tools\publish.ps1，再 iscc。"
#endif

[Code]
const
  SHIFT_PX = 30;
  DRIVE_FIXED = 3;

var
  DriveRoots: array of String;
  DriveLabels: array of String;
  DriveFreeGB: array of Double;
  DefaultDriveIndex: Integer;
  DriveCombo: TNewComboBox;

function GetDriveTypeW(lpRootPathName: String): UINT;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function GetDiskFreeSpaceExW(lpDirectoryName: String;
  var lpFreeBytesAvailableToCaller, lpTotalNumberOfBytes, lpTotalNumberOfFreeBytes: Int64): BOOL;
  external 'GetDiskFreeSpaceExW@kernel32.dll stdcall';

function HumanSize(GB: Double): String;
begin
  if GB >= 1024 then
    Result := Format('%.1f TB', [GB / 1024.0])
  else
    Result := Format('%.0f GB', [GB]);
end;

procedure AddDrive(const Root: String);
var
  freeAvail, totalBytes, freeBytes: Int64;
  n: Integer;
begin
  if not GetDiskFreeSpaceExW(Root, freeAvail, totalBytes, freeBytes) then
    Exit;
  n := GetArrayLength(DriveRoots);
  SetArrayLength(DriveRoots, n + 1);
  SetArrayLength(DriveLabels, n + 1);
  SetArrayLength(DriveFreeGB, n + 1);
  DriveRoots[n] := Root;
  DriveFreeGB[n] := freeAvail / 1073741824.0;
  DriveLabels[n] := Format('%s  空闲 %s / 共 %s', [Copy(Root, 1, 2),
    HumanSize(DriveFreeGB[n]), HumanSize(totalBytes / 1073741824.0)]);
end;

procedure ScanDrives();
var
  i, j: Integer;
  c: Char;
  root: String;
  noneFound: Boolean;
  tmpS: String;
  tmpD: Double;
begin
  SetArrayLength(DriveRoots, 0);
  SetArrayLength(DriveLabels, 0);
  SetArrayLength(DriveFreeGB, 0);
  noneFound := True;

  for i := Ord('D') to Ord('Z') do
  begin
    c := Chr(i);
    root := c + ':\';
    if GetDriveTypeW(root) = DRIVE_FIXED then
    begin
      AddDrive(root);
      noneFound := False;
    end;
  end;

  if noneFound then
    AddDrive('C:\');

  for i := 0 to GetArrayLength(DriveRoots) - 2 do
    for j := 0 to GetArrayLength(DriveRoots) - 2 - i do
      if DriveFreeGB[j] < DriveFreeGB[j + 1] then
      begin
        tmpD := DriveFreeGB[j]; DriveFreeGB[j] := DriveFreeGB[j + 1]; DriveFreeGB[j + 1] := tmpD;
        tmpS := DriveRoots[j]; DriveRoots[j] := DriveRoots[j + 1]; DriveRoots[j + 1] := tmpS;
        tmpS := DriveLabels[j]; DriveLabels[j] := DriveLabels[j + 1]; DriveLabels[j + 1] := tmpS;
      end;

  DefaultDriveIndex := 0;
  for i := 0 to GetArrayLength(DriveRoots) - 1 do
    if UpperCase(Copy(DriveRoots[i], 1, 1)) <> 'C' then
    begin
      DefaultDriveIndex := i;
      Break;
    end;
end;

function GetDefaultDir(Param: String): String;
begin
  if GetArrayLength(DriveRoots) = 0 then
    ScanDrives();
  Result := DriveRoots[DefaultDriveIndex] + 'Program Files\neo';
end;

procedure DirComboChanged(Sender: TObject);
begin
  if (DriveCombo <> nil) and (DriveCombo.ItemIndex >= 0) then
    WizardForm.DirEdit.Text := DriveRoots[DriveCombo.ItemIndex] + 'Program Files\neo';
end;

procedure ShiftPageControls(Dy: Integer);
begin
  WizardForm.SelectDirLabel.Top := WizardForm.SelectDirLabel.Top + Dy;
  WizardForm.DirEdit.Top := WizardForm.DirEdit.Top + Dy;
  WizardForm.SelectDirBrowseLabel.Top := WizardForm.SelectDirBrowseLabel.Top + Dy;
  WizardForm.DirBrowseButton.Top := WizardForm.DirBrowseButton.Top + Dy;
  WizardForm.DiskSpaceLabel.Top := WizardForm.DiskSpaceLabel.Top + Dy;
end;

function InitializeSetup(): Boolean;
begin
  ScanDrives();
  Result := True;
end;

procedure InitializeWizard();
var
  Surface: TWinControl;
  Lbl: TNewStaticText;
  i: Integer;
begin
  Surface := WizardForm.DirEdit.Parent;

  Lbl := TNewStaticText.Create(WizardForm);
  Lbl.Parent := Surface;
  Lbl.Caption := '选择安装盘：';
  Lbl.AutoSize := True;
  Lbl.Left := 0;
  Lbl.Top := WizardForm.SelectDirLabel.Top + 2;

  DriveCombo := TNewComboBox.Create(WizardForm);
  DriveCombo.Parent := Surface;
  DriveCombo.Style := csDropDownList;
  DriveCombo.Left := Lbl.Left + Lbl.Width + 10;
  DriveCombo.Top := WizardForm.SelectDirLabel.Top - 2;
  DriveCombo.Width := 300;
  for i := 0 to GetArrayLength(DriveLabels) - 1 do
    DriveCombo.Items.Add(DriveLabels[i]);
  if DriveCombo.Items.Count > 0 then
    DriveCombo.ItemIndex := DefaultDriveIndex;
  DriveCombo.OnChange := @DirComboChanged;

  ShiftPageControls(SHIFT_PX);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if UninstallSilent then
      Exit; // 静默卸载默认保留用户数据
    if MsgBox('是否同时删除用户数据（订阅配置、设置、日志）？' + #13#10 +
              '选择「否」将保留数据，方便日后重装继续使用。',
              mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{app}\data'), True, True, True);
  end;
end;
