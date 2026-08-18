;============================================================
; 小喵 Windows 更新助手 · NSIS 安装脚本 (setup.nsi)
;============================================================
;
; 本脚本用于生成 Windows 安装包 XiaoMiaoWinUpdate_Setup.exe，
; 让用户（尤其是 Windows 7 / 8.1 用户）双击安装包即可：
;   1) 自动以管理员权限运行（主程序需修改系统服务 / 注册表）；
;   2) 自动检测并安装 .NET Framework 4.8 运行库；
;   3) 释放主程序 XiaoMiaoWinUpdate.exe 与图标 icon.ico；
;   4) 创建桌面与开始菜单快捷方式；
;   5) 提供卸载程序。
;
;------------------------------------------------------------
; 一、为什么需要这个安装包？
;
; 本程序基于 .NET Framework 4.8 开发。.NET Framework 应用
; 无法像 .NET (Core) 那样 self-contained 打包，必须系统预装
; 对应运行时。Windows 10 / 11 通常已自带或已安装 4.8；而全新
; 的 Windows 7 / 8.1 往往没有 4.8 运行库，直接双击
; XiaoMiaoWinUpdate.exe 会弹出「需要 .NET Framework
; v4.0.30319」的错误。注意：v4.0.30319 只是 .NET 4.x 系列的
; CLR 版本号，实际缺失的是 .NET Framework 4.8 运行库。
;
;------------------------------------------------------------
; 二、如何准备 .NET 4.8 离线安装包（可选但推荐）
;
; 若希望安装包能在「无网络 / 受限环境」一键装好 4.8，可把
; 官方离线安装包放到与本脚本同目录：
;
;   文件名：ndp48-x86-x64-allos-enu.exe
;   下载页：https://dotnet.microsoft.com/download/dotnet-framework/net48
;
; 编译后，把该 exe 与生成的 XiaoMiaoWinUpdate_Setup.exe 放在
; 同一文件夹分发即可。运行时安装包会自动静默执行它
; （/q /norestart）。若同目录没有该文件，安装包会改为打开官方
; 下载页，提示用户手动安装 4.8 后重试。
;
;------------------------------------------------------------
; 三、如何编译
;
;   1. 安装 NSIS 3.x（https://nsis.sourceforge.io/）。
;   2. 确保本脚本同目录存在：
;        - bin\Release\XiaoMiaoWinUpdate.exe  （Release 编译产物）
;        - icon.ico
;        - （可选）ndp48-x86-x64-allos-enu.exe
;   3. 在「命令提示符」中进入项目根目录，执行：
;          makensis setup.nsi
;   4. 生成的安装包为：XiaoMiaoWinUpdate_Setup.exe
;
; 说明：本脚本顶部声明了 Unicode true，请用 NSIS 3.x 的
;       makensis（默认即为 Unicode 构建）编译，.nsi 文件请以
;       UTF-8（含 BOM）保存，以确保简体中文界面正常显示。
;
;============================================================

Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"

;----- 基本信息 -----
Name "小喵 Windows 更新助手"
OutFile "XiaoMiaoWinUpdate_Setup.exe"
InstallDir "$PROGRAMFILES\XiaoMiaoWinUpdate"
RequestExecutionLevel admin

; 记住上次安装路径，便于升级
InstallDirRegKey HKLM "Software\XiaoMiaoWinUpdate" "InstallDir"

;----- 版本信息（安装包属性）-----
VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "小喵 Windows 更新助手 安装程序"
VIAddVersionKey "CompanyName" "小喵软件 (XiaoMiao Software)"
VIAddVersionKey "FileVersion" "1.0.0.0"
VIAddVersionKey "FileDescription" "小喵 Windows 更新助手 安装包"
VIAddVersionKey "LegalCopyright" "Copyright (C) 小喵软件"

;----- MUI2 界面设置 -----
!define MUI_ABORTWARNING
!define MUI_ICON "icon.ico"
!define MUI_UNICON "icon.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; 简体中文安装界面
!insertmacro MUI_LANGUAGE "SimpChinese"

; .NET Framework 4.8 的 Release 注册表阈值（>= 528040 即 4.8）
!define DOTNET48_RELEASE 528040

;============================================================
; .onInit：安装前检测并（必要时）安装 .NET Framework 4.8
;============================================================
Function .onInit
  ; 初始化检测结果为 0（ReadRegDWORD 失败时保持 0）
  StrCpy $R0 "0"

  ; .NET Framework 4.x 的 Release 值写在 64 位注册表视图；
  ; 32 位安装程序默认读 Wow6432Node，这里显式切到 64 位视图
  ; （在 32 位系统上 SetRegView 64 等效于原生视图，安全）。
  SetRegView 64
  ReadRegDWORD $R0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  SetRegView 32

  ${If} $R0 >= ${DOTNET48_RELEASE}
    ; 已安装 .NET Framework 4.8，直接进入安装流程
    Goto dotNetReady
  ${EndIf}

  ; 未安装：优先使用安装包同目录的离线安装包
  IfFileExists "$EXEDIR\ndp48-x86-x64-allos-enu.exe" runOfflineInstaller dotNetNeedManual

runOfflineInstaller:
  MessageBox MB_OK|MB_ICONINFORMATION \
    "未检测到 .NET Framework 4.8 运行库。$\n$\n安装程序将自动为你安装（请稍候，可能需要几分钟，期间请勿关闭窗口）。"
  ExecWait '"$EXEDIR\ndp48-x86-x64-allos-enu.exe" /q /norestart'
  ; 安装完成后重新检测 .NET 4.8 是否真正就绪
  ; （用户可能取消安装 / 安装失败返回非零退出码，此时不应继续）
  SetRegView 64
  ReadRegDWORD $R0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  SetRegView 32
  ${If} $R0 >= ${DOTNET48_RELEASE}
    Goto dotNetReady
  ${EndIf}
  MessageBox MB_OK|MB_ICONSTOP \
    "未能成功安装 .NET Framework 4.8 运行库（可能被取消或安装失败）。本程序无法在缺少 4.8 运行库的系统上运行。$\n$\n请手动安装 .NET Framework 4.8 后重试。$\n官方下载页：https://dotnet.microsoft.com/download/dotnet-framework/net48"
  Abort

dotNetNeedManual:
  ExecShell open "https://dotnet.microsoft.com/download/dotnet-framework/net48"
  MessageBox MB_OK|MB_ICONSTOP \
    "本程序需要 .NET Framework 4.8 运行库，但安装包同目录未找到离线安装文件 ndp48-x86-x64-allos-enu.exe。$\n$\n已为你打开官方下载页面，请先手动安装 .NET Framework 4.8，安装完成后再重新运行本安装程序。"
  Abort

dotNetReady:
FunctionEnd

;============================================================
; 安装段
;============================================================
Section "MainSection" SEC_INSTALL
  SetOutPath "$INSTDIR"

  ; 释放主程序与图标
  File "bin\Release\XiaoMiaoWinUpdate.exe"
  File "icon.ico"

  ; 桌面快捷方式（使用 icon.ico 作为图标）
  CreateShortCut "$DESKTOP\小喵 Windows 更新助手.lnk" \
    "$INSTDIR\XiaoMiaoWinUpdate.exe" "" "$INSTDIR\icon.ico" 0

  ; 开始菜单快捷方式
  CreateDirectory "$SMPROGRAMS\XiaoMiaoWinUpdate"
  CreateShortCut "$SMPROGRAMS\XiaoMiaoWinUpdate\小喵 Windows 更新助手.lnk" \
    "$INSTDIR\XiaoMiaoWinUpdate.exe" "" "$INSTDIR\icon.ico" 0

  ; 生成卸载程序
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 写入卸载信息（便于在「程序和功能」中显示与卸载）
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate" \
    "DisplayName" "小喵 Windows 更新助手"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate" \
    "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate" \
    "DisplayIcon" "$INSTDIR\icon.ico"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate" \
    "Publisher" "小喵软件 (XiaoMiao Software)"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate" \
    "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate" \
    "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate" \
    "NoRepair" 1

  ; 记录安装路径，便于下次升级
  WriteRegStr HKLM "Software\XiaoMiaoWinUpdate" "InstallDir" "$INSTDIR"
SectionEnd

;============================================================
; 卸载段
;============================================================
Section "Uninstall"
  ; 删除快捷方式
  Delete "$DESKTOP\小喵 Windows 更新助手.lnk"
  Delete "$SMPROGRAMS\XiaoMiaoWinUpdate\小喵 Windows 更新助手.lnk"
  RMDir "$SMPROGRAMS\XiaoMiaoWinUpdate"

  ; 删除程序文件与目录
  Delete "$INSTDIR\XiaoMiaoWinUpdate.exe"
  Delete "$INSTDIR\icon.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  ; 清理注册表
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaoMiaoWinUpdate"
  DeleteRegKey HKLM "Software\XiaoMiaoWinUpdate"
SectionEnd
