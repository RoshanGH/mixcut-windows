; MixCut Windows Inno Setup script (v0.4.0+)
; 用法（在 Windows 构建机）：
;   "C:\Users\mlamp\AppData\Local\Programs\Inno Setup 6\iscc.exe" installer\MixCut.iss
; 输出：installer\out\MixCut-Setup-vX.Y.Z-win-x64.exe

#define MyAppName "MixCut"
#define MyAppVersion "0.7.2"
#define MyAppPublisher "MixCut"
#define MyAppURL "https://github.com/RoshanGH/mixcut-windows"
#define MyAppExeName "MixCut.exe"

[Setup]
AppId={{B7C8F2E0-5B3A-4D1A-9E4F-3C2A1B0E5D6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; per-user 安装：不需要管理员权限
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 仅 x64 Windows 10/11
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; 兜底 UI
DisableProgramGroupPage=yes
DisableDirPage=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; 输出
OutputDir=out
OutputBaseFilename=MixCut-Setup-v{#MyAppVersion}-win-x64

; 分卷：单卷 < 90 MB（Gitee Release 单文件 100MB 上限，留余量）
; 输出会变成 stub setup.exe + 多个 .bin 文件，用户从 Gitee 下全部文件放同目录双击 setup.exe
; v0.6.1 起回退 hover 到 MediaElement 并清掉 LibVLC（省 ~60MB），lzma2/max 压缩后
; 整包约 100-130MB，会分成 2-3 个 .bin 分卷
DiskSpanning=yes
DiskSliceSize=94371840
SlicesPerDisk=1

; 语言
ShowLanguageDialog=no

; 关闭杀软可能误报的特性
SetupLogging=yes

; 默认使用 English；后续 v0.4.x 可补 ChineseSimplified.isl
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其它任务:"; Flags: unchecked

[Files]
; self-contained publish 全部内容 → {app}
; 假设构建脚本在跑 iscc 前已经把 publish/ 目录刷新好。
; recursesubdirs 会递归包含所有子目录：
;   bin/         FFmpeg / ffprobe / whisper-cli / 6 个 VC Runtime DLL / vcomp140 / concrt140
;   Resources/   AI prompt 模板
;   *.dll *.exe  .NET 运行时（self-contained）+ MixCut.exe
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清掉用户数据可能有争议，这里不删 %APPDATA%\MixCut
; 让用户手动决定是否清理（README 会有说明）

[Code]
const
  PF_XMMI64_INSTRUCTIONS_AVAILABLE = 10;  // SSE2
  PF_SSE3_INSTRUCTIONS_AVAILABLE = 13;
  PF_XSAVE_ENABLED = 17;
  PF_AVX_INSTRUCTIONS_AVAILABLE = 17;       // 注意：Win API 用 17 表示 AVX
  PF_AVX2_INSTRUCTIONS_AVAILABLE = 40;

function IsProcessorFeaturePresent(Feature: DWORD): BOOL;
  external 'IsProcessorFeaturePresent@kernel32.dll stdcall';

function InitializeSetup(): Boolean;
var
  HasAvx2: Boolean;
  Resp: Integer;
begin
  Result := True;

  // 检测 AVX2 —— 内置 whisper-cli 强依赖
  HasAvx2 := IsProcessorFeaturePresent(PF_AVX2_INSTRUCTIONS_AVAILABLE);
  if not HasAvx2 then
  begin
    Resp := MsgBox(
      '检测到当前 CPU 不支持 AVX2 指令集。' + #13#10#13#10 +
      'MixCut 的语音识别（Whisper）功能需要 AVX2，否则会立即崩溃。' + #13#10 +
      '其它功能（导入视频 / AI 切分 / 方案生成 / 导出）仍可正常使用。' + #13#10#13#10 +
      '是否继续安装？',
      mbConfirmation, MB_YESNO);
    if Resp = IDNO then
    begin
      Result := False;
      exit;
    end;
  end;
end;
