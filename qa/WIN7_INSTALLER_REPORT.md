# 验收报告 · Win7/8.1 .NET Framework 4.8 安装包改造

- **验收对象**：`setup.nsi` 新增强量 + `README.md` / `RELEASE.md` 的 Win7/8.1 运行环境说明
- **验收人**：QA 工程师（Edward）
- **根因背景**：程序基于 .NET Framework 4.8，空 Win7 缺运行时会弹「需要 .NET Framework v4.0.30319」错误。采用方案 B（安装包自动检测 / 安装 4.8）。
- **验收日期**：2026-08-18
- **环境限制**：沙箱无 NSIS 编译器（无法实际 `makensis` 编译 / 真机安装），故采用**静态逐行审查 + Python 等价测试**方式验收。

---

## Summary（验收结论）

| 维度 | 结论 |
|------|------|
| `setup.nsi` 静态审查 | ✅ 通过（语法合法、检测逻辑正确、分支完整、文件/快捷方式/卸载完整、命名一致） |
| 文档准确性（README / RELEASE） | ✅ 通过（Win7/8.1 章节、v4.0.30319 误区澄清、两种解决方式、便携版/安装包区分均准确一致） |
| 等价测试 | ✅ 通过（4 个测试方法 / 15 项断言，含 12 行真值表，全部 OK） |
| **Routing Decision** | **NoOne**（源码 / 脚本未发现 Bug；测试代码为 QA 自写且全部通过，无需自修） |
| 整体判定 | **PASS** —— 可接受合入 |

---

## 1. 文件核对

| 文件 / 路径 | 是否存在 | 说明 |
|------|------|------|
| `setup.nsi` | ✅ | 本次新增的 NSIS 脚本 |
| `bin\Release\XiaoMiaoWinUpdate.exe` | ✅（349184 字节） | `File` 指令释放的主程序，路径存在 |
| `icon.ico` | ✅（3252 字节） | `File` / `MUI_ICON` / `MUI_UNICON` 引用，路径存在 |
| `ndp48-x86-x64-allos-enu.exe` | ⚠️ 不存在（可选） | 离线包为**可选**分发件，脚本已对缺失做降级处理（`ExecShell` 打开下载页 + `MessageBox MB_ICONSTOP` + `Abort`），符合预期 |
| `README.md` | ✅ | 含第 11 行起的 Win7/8.1 必读章节、第 9 节安装包构建 |
| `RELEASE.md` | ✅ | 含「便携版 exe / 安装包 Setup」双形态说明 |

---

## 2. setup.nsi 静态审查结论

### 2.1 语法合法性 ✅
- `Unicode true`（L56）+ `!include "MUI2.nsh"`（L58）、`!include "LogicLib.nsh"`（L59）均在顶部、`${If}` 宏依赖 LogicLib 已正确包含。
- 安装页成对：`MUI_PAGE_WELCOME` / `DIRECTORY` / `INSTFILES` / `FINISH`（L83-86）；卸载页：`MUI_UNPAGE_WELCOME` / `CONFIRM` / `INSTFILES` / `FINISH`（L88-91）。（`LICENSE` 非强制，设计合理。）
- `!insertmacro MUI_LANGUAGE "SimpChinese"`（L94）位于所有页面宏之后，顺序正确。
- `Function .onInit ... FunctionEnd`（L102-134）、`Section "MainSection" SEC_INSTALL ... SectionEnd`（L139-176）、`Section "Uninstall" ... SectionEnd`（L181-196）均正确闭合。
- 标签跳转目标全部存在且拼写一致：
  - `Goto dotNetReady`（L115）→ 标签 `dotNetReady:`（L133）✅
  - `IfFileExists ... runOfflineInstaller dotNetNeedManual`（L119）→ 标签 `runOfflineInstaller:`（L121）、`dotNetNeedManual:`（L127）✅
  - `Goto dotNetReady`（L125）→ ✅
  - 无悬空 / 拼写不一致的标签。

### 2.2 .NET 4.8 检测逻辑 ✅
- 读取 `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` 的 `Release` DWORD（L110）。
- 阈值 `>= ${DOTNET48_RELEASE}`（L97 定义为 `528040`，L113 比较）——`528040` 为微软官方 .NET Framework 4.8 Release 常量，正确。
- `SetRegView 64`（L109）在读取前切换 64 位视图以规避 Wow6432Node 偏差；读取后 `SetRegView 32`（L111）复位。32 位系统上 `SetRegView 64` 等效原生视图，安全。✅
- 初始 `StrCpy $R0 "0"`（L104），键缺失时 `ReadRegDWORD` 失败保持 0 → 判定「未装」，逻辑自洽。✅

### 2.3 离线包分支 ✅
- 命中：`IfFileExists "$EXEDIR\ndp48-x86-x64-allos-enu.exe"`（L119）→ `ExecWait '"$EXEDIR\ndp48-x86-x64-allos-enu.exe" /q /norestart'`（L124）→ `Goto dotNetReady`。
- 未命中：`ExecShell open` 官方下载页（L128）+ `MessageBox MB_OK|MB_ICONSTOP`（L129-130）+ `Abort`（L131）。
- 分支完整、无死分支、无遗漏路径。✅

### 2.4 管理员权限 ✅
- `RequestExecutionLevel admin`（L65）位于脚本顶部基本信息区，先于 `.onInit`，符合安装包需改系统服务 / 注册表的权限要求。卸载程序由安装程序生成并继承提权。✅

### 2.5 文件释放 / 快捷方式 / 卸载 ✅
- 释放：`File "bin\Release\XiaoMiaoWinUpdate.exe"`（L143）、`File "icon.ico"`（L144），两文件路径均确认存在。
- 桌面快捷方式：`$DESKTOP\小喵 Windows 更新助手.lnk`（L147-148）。
- 开始菜单：`CreateDirectory "$SMPROGRAMS\XiaoMiaoWinUpdate"`（L151）+ 同名 `.lnk`（L152-153）。
- 卸载程序：`WriteUninstaller "$INSTDIR\Uninstall.exe"`（L156）。
- 卸载注册表：写入 `DisplayName` / `UninstallString` / `DisplayIcon` / `Publisher` / `InstallLocation` / `NoModify` / `NoRepair`（L159-172）；`Uninstall` 段删除快捷方式、程序文件、`Uninstall.exe`、`RMDir`，并清理两个注册表键（L181-196），闭环完整。

### 2.6 命名一致性 ✅
`XiaoMiaoWinUpdate_Setup.exe`、`ndp48-x86-x64-allos-enu.exe`、`setup.nsi`、`XiaoMiaoWinUpdate.exe`、`icon.ico` 在脚本与 README/RELEASE 中拼写完全一致（已用 grep 交叉核对）。

---

## 3. 文档审查结论

### 3.1 README.md ✅
- 第 11 行起有专门章节「⚠️ Windows 7 / 8.1 用户必读（运行环境要求）」。
- 第 18 行正确澄清：`v4.0.30319` 只是 .NET 4.x 系列 CLR 版本号，并非「缺 4.0」，缺失的是 **.NET Framework 4.8 运行时**。
- 第 20-34 行给出两种解决方式：① 手动安装官方离线包；② 使用安装包 `XiaoMiaoWinUpdate_Setup.exe` 自动检测并安装 4.8。
- 第 9 节（L201-218）给出 `setup.nsi` 构建步骤，文件名 `makensis setup.nsi`、`XiaoMiaoWinUpdate_Setup.exe` 与脚本一致。

### 3.2 RELEASE.md ✅
- 第 17-21 行「便携版 exe（XiaoMiaoWinUpdate.exe）」：注明需已装 4.8 运行时，适用 Win10/11 或已装 4.8 的 Win7/8.1。
- 第 22-28 行「安装包 Setup（XiaoMiaoWinUpdate_Setup.exe）」：说明自动检测并安装 4.8（附离线包则静默装；否则引导下载页），并明确「**尤其推荐 Windows 7 / 8.1 用户使用本安装包**」。
- 第 30 行再次澄清 v4.0.30319 误区，与 README 一致。
- 第 34 行离线包文件名与 `setup.nsi` 一致。

### 3.3 文档与脚本一致性 ✅
README/RELEASE 中指向安装包的文件名（`setup.nsi`、`XiaoMiaoWinUpdate_Setup.exe`、`ndp48-x86-x64-allos-enu.exe`）与 `setup.nsi` 脚本内完全一致。

---

## 4. 等价测试结果

- **测试文件**：`qa/test_dotnet48_detect_equiv.py`
- **方法**：用纯 Python 复刻 `release >= 528040` 判定，以微软官方典型 Release 值构建真值表，验证阈值边界。
- **运行命令**：`python3 -m unittest qa.test_dotnet48_detect_equiv -v`
- **结果**：`Ran 4 tests ... OK`（4 个测试方法，共 15 项断言：12 行真值表 subTest + 边界单调性 + 官方常量 + 缺键回归），**全部通过**。

覆盖值（部分）：`0`(未装)→F；`378389`(4.5)→F；`394802`(4.6.1)→F；`460798`(4.7)→F；`461808`(4.7.1)→F；`528039`→F；`528040`(4.8 阈值)→T；`528041`→T；`528049`(Win10 1903+)→T；`528372`(Win10 2004/11)→T；`533320`(4.8.1)→T。

> 说明：本等价测试替换脚本中 `${If} $R0 >= ${DOTNET48_RELEASE}` 的数值比较语义；NSIS LogicLib 的 `>=` 为带符号数值比较，`release >= 528040` 的 Python 语义与之一致（Release 值均为正且 < 2³¹），阈值边界正确。

---

## 5. Routing Decision

**NoOne** —— 本次改动（脚本 + 文档）经静态审查与等价测试，未发现源码 / 脚本级 Bug，测试代码为 QA 自写且全部通过，无需反馈工程师修复，亦无需 QA 自修。

判定依据：
- 若断言期望正确行为但实现错误 → 路由 Engineer；本次实现与官方常量 / 设计一致，未触发。
- 若测试断言本身有误 → 自行修复；本次测试实现正确，全部通过，未触发。

> **Round 2 路由补充**：验收报告 Known Limitations #1（离线安装未二次校验）已反馈 software-engineer，对方已合入修复（见第 6 节）。QA 对修复做了回归复核（见第 7 节），结论为 **NoOne**（修复后逻辑正确、无新 Bug，等价测试仍覆盖其阈值语义）。

---

## 6. Known Limitations（已知限制 / 建议，非阻断）

1. ~~**离线安装结果未二次校验（健壮性建议，非 Bug）**~~ —— **已于 Round 2 由 software-engineer 合入修复并复核通过**：`runOfflineInstaller` 在 `ExecWait` 离线包后重新 `SetRegView 64` 读取 `Release`，`>= 528040` 则继续安装，仍 `< 528040` 则 `MessageBox MB_ICONSTOP` + `Abort`。原建议已闭环，从限制清单移除。
2. **卸载注册表落在 Wow6432Node**：因 32 位 NSIS 默认 `SetRegView 32`，卸载项写入 `HKLM\Software\WOW6432Node\...\Uninstall\XiaoMiaoWinUpdate`。这是 32 位安装器标准行为，仍正确显示在「程序和功能」中，非缺陷。
3. **`MUI_PAGE_FINISH` 未自动运行主程序**：未定义 `MUI_FINISHPAGE_RUN`，安装完成后不自动启动。属可选增强，不影响功能。
4. **未实际编译 / 真机安装**：沙箱无 NSIS 编译器，验收为静态审查 + 等价测试。建议在 Windows + NSIS 3.x 真机执行 `makensis setup.nsi`，并在 Win7 虚拟机验证「无 4.8 → 自动装 / 引导下载」与「有离线包 → 静默安装」两条路径，以及桌面 / 开始菜单快捷方式与卸载清理。

---

## 7. Round 2 回归复核（针对 Known Limitations #1 修复）

- **修复位置**：`setup.nsi` `Function .onInit` 的 `runOfflineInstaller:` 标签（L121-135）。
- **复核要点**：
  1. `ExecWait '"$EXEDIR\ndp48-x86-x64-allos-enu.exe" /q /norestart'`（L124）后不再无条件 `Goto dotNetReady`；
  2. 重新 `SetRegView 64`（L127）→ `ReadRegDWORD $R0 ...\v4\Full "Release"`（L128）→ `SetRegView 32`（L129）读取真实安装结果；
  3. `${If} $R0 >= ${DOTNET48_RELEASE}`（L130）→ 成功则 `Goto dotNetReady`（L131）；
  4. 失败（仍 `< 528040`，如用户取消 / 安装失败返回非零）则 `MessageBox MB_OK|MB_ICONSTOP`（L133-134）+ `Abort`（L135），阻止主程序在缺 4.8 运行时下启动失败。
- **结论**：
  - 控制流完整：成功 → 进入安装；失败 → 中止，无死分支。
  - 阈值与 `SetRegView 64` 行为与原检测一致，未引入偏差。
  - `Function .onInit ... FunctionEnd`（L102 / L144）仍正确闭合；`dotNetReady:` 标签（L143）与 `Abort` 路径均有效。
  - 等价测试 `qa/test_dotnet48_detect_equiv.py` 的阈值语义（`>= 528040`）覆盖本修复的判断条件，原 4 测试 / 15 断言仍全部成立，无需改动。
- **最终 Routing Decision：NoOne（修复验证通过）**。
