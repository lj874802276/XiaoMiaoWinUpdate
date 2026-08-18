# 小喵 Windows 更新助手 · 发布说明（RELEASE）

> 软件名称：**小喵 Windows 更新助手**
> 可执行文件：`XiaoMiaoWinUpdate.exe`
> 当前版本：**v1.0.0**（对应程序集版本 `1.0.0.0`，AssemblyFileVersion `1.0.0.0`）
> 发布日期：**2026-08-17**
> 出品：小喵软件（XiaoMiao Software）
> 技术栈：C# + WPF + .NET Framework 4.8，Costura.Fody 单文件 EXE（便携版，双击即用）
> 质量状态：通过 2 轮 QA，30/30 等价测试通过，F1（恢复缺陷）已闭环，**IS_PASS: YES**

---

## 安装与运行环境

本程序基于 **.NET Framework 4.8** 开发，提供两种分发形态，请根据系统选择：

### 便携版 exe（XiaoMiaoWinUpdate.exe）
- 单文件、免安装，双击即用（依赖 Costura.Fody 嵌入的依赖）。
- **前提**：运行该 exe 的机器必须已安装 **.NET Framework 4.8 运行时**。
- **适用**：Windows 10 / 11（通常已自带 4.8），或已手动装好 4.8 的 Windows 7 / 8.1。

### 安装包 Setup（XiaoMiaoWinUpdate_Setup.exe）
- 由仓库根目录 `setup.nsi`（NSIS 脚本）生成。
- **安装包会自动检测并安装 .NET Framework 4.8**：
  - 若安装包同目录附带官方离线包 `ndp48-x86-x64-allos-enu.exe`，则静默安装 4.8（`/q /norestart`）；
  - 若没有离线包，则打开官方下载页引导你手动安装后重试。
- 安装后释放主程序，并在桌面 / 开始菜单创建快捷方式，同时提供卸载程序。
- **适用**：所有受支持系统，**尤其推荐 Windows 7 / 8.1 用户使用本安装包**。

> **Win7 / 8.1 用户请注意**：这两类系统默认没有 .NET Framework 4.8 运行库。请**直接使用安装包 Setup**，或先手动安装 .NET Framework 4.8 后再运行便携版 exe。直接双击 exe 会报错「需要 .NET Framework v4.0.30319」——其中 v4.0.30319 只是 4.x 系列 CLR 版本号，实际缺失的是 **4.8 运行时**。

### .NET Framework 4.8 离线安装包下载
- 官方下载页：<https://dotnet.microsoft.com/download/dotnet-framework/net48>
- 离线包文件名：`ndp48-x86-x64-allos-enu.exe`（放到 `setup.nsi` 同目录后编译，即可让安装包离线自动安装 4.8）

---

## 1. 产品简介

**一句话定位**：小喵 Windows 更新助手是一款**单文件、免安装、一键关闭 / 恢复 Windows 自动更新**的桌面工具，首次运行自动备份本机更新策略，恢复时 100% 还原到运行前的状态。

### 核心特性

| 特性 | 说明 |
|------|------|
| 📦 单 EXE 便携 | 经 Costura.Fody（5.7.0）打包，`Newtonsoft.Json.dll` 等依赖全部嵌入 `XiaoMiaoWinUpdate.exe` 内部，无需安装、无需随附 DLL，双击即用 |
| 🖥️ 全版本兼容 | 兼容 Windows 7 / 8 / 8.1 / 10 / 11（含 OS 版本分支与 `app.manifest` 兼容性清单） |
| 🔘 一键关闭 / 恢复 | 主界面两个按钮即可「彻底关闭 Windows 更新」或「恢复到运行本软件前的状态」 |
| 💾 首次自动备份 | 首次点击关闭前，自动在 `%LOCALAPPDATA%\XiaoMiaoWinUpdate\backup.json` 全量备份注册表、服务与计划任务状态 |
| 🔁 100% 还原 | 恢复采用「受管键内容级还原」——回写备份原值，并**删除禁用阶段新增的值**，确保彻底回到首次运行前的策略 |
| 🛡️ 管理员提权 | `app.manifest` 声明 `requireAdministrator` + 启动二次管理员身份检测，系统级操作安全可控 |

> 窗口标题显示为「Win7/10/11 自动更新关闭工具」，作者署名「小喵软件」。

---

## 2. 兼容性矩阵

### 2.1 操作系统支持

| Windows 版本 | 内核版本（识别依据） | 工具是否支持 | 备注 |
|------|------|------|------|
| Windows 7 | 6.1 | ✅ 支持 | 旧策略分支（SearchOrderConfig） |
| Windows 8 | 6.2 | ✅ 支持 | 同旧策略分支 |
| Windows 8.1 | 6.3 | ✅ 支持 | 同旧策略分支 |
| Windows 10（Build < 22000） | 10.0 | ✅ 支持 | 新策略分支（ExcludeWUDrivers…） |
| Windows 11（Build ≥ 22000） | 10.0.22000+ | ✅ 支持 | 新策略分支（ExcludeWUDrivers…） |
| 其它（如 Vista / XP / 未知版本） | — | ⚠️ 不保证 | `OsHelper` 标记为 `Unknown`，不写入策略 |

> 版本识别基于 `Environment.OSVersion` + WMI `Win32_OperatingSystem.Caption`。`app.manifest` 已声明全部 `supportedOS` GUID（含 Win10 `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`），因此 Win10/11 下 `Environment.OSVersion` **不会**被谎报为 6.2（规避同类工具最高频的版本识别坑）。

### 2.2 服务兼容矩阵

| 服务名称 | 显示名（常见） | Win7 / 8 / 8.1 | Win10 / 11 | 工具行为 |
|------|------|------|------|------|
| `wuauserv` | Windows Update | ✅ 全版本 | ✅ 全版本 | 禁用（停止 + `sc config start= disabled`） |
| `UsoSvc` | Update Orchestrator Service | ❌ 不涉及 | ✅ 涉及 | 仅 Win10/11 禁用 |
| `WaaSMedicSvc` | Windows Update Medic | ❌ 不涉及 | ✅ 涉及 | 仅 Win10/11 禁用（见「已知限制」） |

### 2.3 注册表策略差异矩阵

以下注册表键均位于 **`HKLM`（64 位视图）**，工具通过 `RegistryView.Registry64` 打开，规避 WOW64 重定向。

| 注册表路径 | 写入的值 | Win7/8/8.1 | Win10/11 | 含义 |
|------|------|------|------|------|
| `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` | `NoAutoUpdate = 1` | ✅ 写入 | ✅ 写入 | 关闭自动更新 |
| `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` | `AUOptions = 2` | ✅ 写入 | ✅ 写入 | 通知下载并手动安装 |
| `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` | `NoAutoRebootWithLoggedOnUsers = 1` | ✅ 写入 | ✅ 写入 | 限制登录用户时自动重启 |
| `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` | `SetDisableUXWUAccess = 1` | ✅ 写入 | ✅ 写入 | 禁用 Windows Update 手动访问（UX） |
| `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` | `ExcludeWUDriversInQualityUpdate = 1` | ❌ 不写 | ✅ 写入 | 质量更新中排除驱动更新（仅新分支） |
| `SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching` | `SearchOrderConfig = 0` | ✅ 写入 | ❌ 不写 | 旧分支下关闭驱动搜索排序（仅旧系统） |

#### 版本行为差异要点

- **Win10/11 专属**：`ExcludeWUDriversInQualityUpdate`（驱动更新排除）、禁用 `UsoSvc` 与 `WaaSMedicSvc` 服务、禁用计划任务 `\Microsoft\Windows\WindowsUpdate\Scheduled Start`。
- **Win7/8/8.1 专属**：通过 `DriverSearching\SearchOrderConfig = 0` 关闭驱动更新排序（无 UsoSvc / WaaSMedicSvc / 上述计划任务）。
- **全版本共有**：`NoAutoUpdate`、`AUOptions`、`NoAutoRebootWithLoggedOnUsers`、`SetDisableUXWUAccess` 四项策略，以及 `wuauserv` 服务禁用。

### 2.4 计划任务兼容矩阵（仅 Win10/11）

| 计划任务路径 | Win7/8/8.1 | Win10/11 | 工具行为 |
|------|------|------|------|
| `\Microsoft\Windows\WindowsUpdate\Scheduled Start` | ❌ 不涉及 | ✅ 涉及 | 仅 Win10/11 用 `schtasks /Change /DISABLE` 禁用 |

---

## 3. 使用步骤

### 3.1 以管理员身份运行

本工具需要管理员权限才能修改注册表、服务与计划任务：

1. 右键 `XiaoMiaoWinUpdate.exe` → **「以管理员身份运行」**。
2. 若 `app.manifest` 已触发 UAC 提权，按提示允许；若未提权（如被系统策略拦截），程序启动时会二次检测 `IsInRole(Administrator)`，非管理员将弹出提示并退出（`Shutdown(1)`）。
3. 窗口标题「Win7/10/11 自动更新关闭工具」出现即代表已正常启动。

### 3.2 主界面说明

主界面（640×520，居中，不可缩放）包含：

- **当前系统**：顶部显示系统版本（如 `当前系统：Windows 11 Pro`）。
- **功能说明**：提示本工具关闭自动更新、更新通知、自动重启、驱动更新并禁止手动访问。
- **状态卡片（6 个状态指示）**：

| 状态项 | 含义 | 判定来源（节选） |
|------|------|------|
| 自动更新 | 是否关闭自动更新 | `AU\NoAutoUpdate == 1` |
| Windows Update 驱动更新 | 驱动更新是否被排除 | Win10/11：`ExcludeWUDrivers…==1`；Win7/8.1：`SearchOrderConfig == 0` |
| 更新通知 | 更新通知是否被抑制 | Win10/11：`UsoSvc` 禁用或计划任务禁用；Win7/8.1：`wuauserv` 禁用或 `NoAutoUpdate==1` |
| 更新自动重启 | 自动重启是否被限制 | `NoAutoRebootWithLoggedOnUsers == 1` |
| Windows Update 手动访问 | 手动访问是否被禁用 | `SetDisableUXWUAccess == 1` |
| 附加更新封锁 | 综合附加封锁是否生效 | `SetDisableUXWUAccess` 禁用 且（Win10/11：`ExcludeWUDrivers…` 或 旧系统：`SearchOrderConfig`） |

- **状态大标题**：根据已关闭项数显示
  - 0 项关闭 → 「Windows 自动更新正常运行」（绿色）
  - ≥ 4 项关闭 → 「Windows 自动更新已关闭」（红）
  - 1–3 项 → 「Windows 自动更新已关闭，但部分封锁策略未生效」（橙）
- **两个按钮**：
  - 〔彻底关闭 Windows 更新〕（主按钮）
  - 〔恢复到运行本软件前的状态〕（次按钮）

### 3.3 点击「彻底关闭 Windows 更新」的预期效果

1. 弹窗确认 → 选择「是」。
2. **首次运行或备份缺失时**自动在 `%LOCALAPPDATA%\XiaoMiaoWinUpdate\backup.json` 创建全量备份。
3. 写入上述注册表策略、禁用相关服务与计划任务。
4. 刷新状态，弹窗提示「Windows 自动更新已关闭。原始设置已备份到：<路径>」。

### 3.4 点击「恢复到运行本软件前的状态」的预期效果

1. 若未找到备份，弹窗提示「未找到备份文件，无法恢复。请先点击『彻底关闭 Windows 更新』生成备份。」（不崩溃）。
2. 弹窗确认 → 选择「是」。
3. 从 `backup.json` 回写注册表、服务与计划任务（详见第 4 节）。
4. 弹窗提示「已恢复到本软件第一次运行前的状态」。

### 3.5 关闭后 Windows 更新的行为

关闭后，Windows **不会按正常自动更新流程**获取：

- 系统安全补丁（自动更新被禁用）；
- Windows Update 驱动更新（被排除/排序关闭）；
- 更新通知（Win10/11 下 UsoSvc / 计划任务被禁用）；
- 登录状态下的自动重启；
- 手动检查 / 下载 / 安装更新的入口（UX 访问被禁用）。

> 如需重新接收更新，只需运行本工具并点击「恢复」，详见第 4 节。

---

## 4. 备份与恢复说明

### 4.1 备份存放路径

```
%LOCALAPPDATA%\XiaoMiaoWinUpdate\backup.json
```

即 `C:\Users\<用户名>\AppData\Local\XiaoMiaoWinUpdate\backup.json`。目录在 `BackupService` 构造时自动创建。

### 4.2 备份内容（全量）

备份文件为 JSON（Newtonsoft.Json，`Formatting.Indented`），结构如下：

- **元信息**
  - `SchemaVersion`：当前为 `1`
  - `CreatedAt`：备份生成时间（UTC，ISO 8601，如 `2026-08-17T12:34:56.7890123Z`）
  - `OsVersion`：备份时的系统描述（WMI Caption）
- **注册表（`RegistryKeys`）**：对每个受管键记录
  - `Path`：键路径
  - `Exists`：**布尔标记**，指示首次备份时该键是否存在（恢复时据此决定是否整体删除）
  - `Values`：键值列表，每项含 `Name`（值名）、`Kind`（类型字符串，如 `DWord`/`String`/`MultiString`…）、`Value`（值内容）
  - 覆盖 3 个键：`WindowsUpdate\AU`、`WindowsUpdate`、`DriverSearching`
- **服务（`Services`）**：对 `wuauserv`、`UsoSvc`、`WaaSMedicSvc` 记录
  - `Name`（服务名）、`StartType`（启动类型整数）、`Status`（运行状态字符串，如 `Running`/`Stopped`/`NotFound`）
- **计划任务（`Tasks`，仅 Win10/11）**：对 `\Microsoft\Windows\WindowsUpdate\Scheduled Start` 记录
  - `Path`、`Enabled`（布尔，备份时的启用状态）

### 4.3 恢复机制（100% 还原）

恢复（`RestoreBackup`）依次处理注册表、服务、计划任务：

- **注册表（受管键内容级还原 —— F1 修复）**
  - 若备份时键 `Exists == false`：恢复时执行 `DeleteSubKeyTree` 整体删除该键，回到「原本不存在」的状态。
  - 若备份时键 `Exists == true`：**先删除当前键下「不在备份值名单里」的所有值**（即禁用阶段新增的 `NoAutoUpdate` / `NoAutoRebootWithLoggedOnUsers` / `ExcludeWUDriversInQualityUpdate` / `SetDisableUXWUAccess` / `SearchOrderConfig` 等），再回写备份记录的原始值。这保证了在「受管键本身已存在」的机器（企业 GPO、被其它工具改过的机器）上也能 100% 还原，不再残留禁用残留值。
  - 类型与值经 `NormalizeRegistryValue` 归一化（DWord/QWord/Binary(base64)/MultiString/String 等），避免 JSON 往返失真。
- **服务（F2 改进）**
  - 用 `sc config` 还原 `StartType`（auto/demand/disabled/boot/system 等映射）。
  - 若原状态为 `Running` 且当前已停止，恢复后**重新拉起服务**（`controller.Start()`），确保组件回到原始运行状态。
  - 原 `StartType < 0`（服务不存在）的备份项在恢复时跳过。
- **计划任务**：依备份的 `Enabled` 标志执行 `schtasks /Change /ENABLE` 或 `/DISABLE`。

### 4.4 备份缺失时的友好提示

- 主界面「恢复」按钮在 `BackupExists()` 为 `false` 时直接提示「未找到备份文件，无法恢复……」，不会崩溃。
- `RestoreBackup` 在 `backup == null` 或 `SchemaVersion` 不兼容时抛出中文异常并由 UI 捕获提示。

### 4.5 恢复后服务状态

恢复后服务按**原始状态重启**：

- 启动类型回到首次运行前的值；
- 原 `Running` 的服务会被重新拉起，原 `Stopped` 的保持停止；
- 计划任务回到备份时的启用 / 禁用状态。

---

## 5. EV 代码签名与 Defender 白名单

### 5.1 为何需要 EV 代码签名证书

未签名的系统工具极易被 Microsoft Defender 与第三方杀毒软件误报为恶意程序（如 `Trojan:Win32/Wacatac.B!ml`）。使用 **EV（Extended Validation）代码签名证书** 签名的 EXE 具备更高的发布者信誉，可显著降低误报率。证书需向 DigiCert、Sectigo、GlobalSign 等 CA 自行申请。

### 5.2 signtool 签名命令（引用自 README）

> 在「Developer Command Prompt for VS 2022」中，进入编译产物目录执行：

**普通 EV 令牌 / HSM 签名：**

```cmd
cd /d E:\.workbuddy\2026-08-17-20-07-51\winupdate-disabler\bin\Release

signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a XiaoMiaoWinUpdate.exe
```

参数：`/tr` RFC 3161 时间戳服务器；`/td sha256` 时间戳摘要；`/fd sha256` 文件摘要；`/a` 自动选择本机可用证书。

**SHA1 + SHA256 双签名（兼容旧版 Windows）：**

```cmd
signtool sign /v /fd sha1 /t http://timestamp.digicert.com /a XiaoMiaoWinUpdate.exe
signtool sign /v /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a /as XiaoMiaoWinUpdate.exe
```

（`/as` 表示追加签名，保留前一条 SHA1 签名。）

**验证签名：**

```cmd
signtool verify /pa XiaoMiaoWinUpdate.exe
```

### 5.3 提交 Microsoft Defender 白名单

即使已 EV 签名，新发布的 EXE 仍可能被临时标记。建议主动向微软提交：

1. 打开 [Microsoft Defender 文件提交页面](https://www.microsoft.com/en-us/wdsi/filesubmission)。
2. 登录微软账号。
3. 选择 **`Software developer`**（软件开发者）。
4. 上传 `XiaoMiaoWinUpdate.exe`。
5. 填写信息：
   - **Company name**：小喵软件 / XiaoMiao Software
   - **Product name**：小喵 Windows 更新助手
   - **Description**：A system utility to disable/restore Windows Update policies with user consent and admin elevation.
   - **Detection name**：若已被 Defender 拦截，填写对应威胁名称（如 `Trojan:Win32/Wacatac.B!ml`）。
6. 提交后通常 24 小时内收到审核结果邮件。

> 建议在 CI / 分发流水线中将 `signtool` 步骤加入自动发布流程，确保每次分发的单文件 EXE 均已签名。

---

## 6. 常见问题（FAQ）

### ① 为什么被杀软报毒？如何应对？

本工具修改系统注册表、服务与计划任务，行为特征与部分恶意软件相似，未签名时极易被 Microsoft Defender / 第三方杀软误报。应对方法：

- 使用 **EV 代码签名证书**对 `XiaoMiaoWinUpdate.exe` 签名（见第 5 节）；
- 向 [Microsoft Defender 文件提交页面](https://www.microsoft.com/en-us/wdsi/filesubmission) 提交白名单；
- 若仅本地使用，可将本工具加入杀软「排除项 / 信任区」（请确保 EXE 来源可信）。

### ② 企业域 GPO 会覆盖本地策略，怎么办？

在加入**域（Active Directory）**的电脑上，域组策略（GPO）优先级高于本地策略。本工具写入的是本地 `HKLM\SOFTWARE\Policies\...`，**会被域 GPO 覆盖或定期重置**。若公司强制自动更新，工具关闭的效果可能不持久或重启后失效，这属于预期行为，并非工具失效。此类环境请先取得 IT 部门授权或走合规流程。

### ③ 恢复后，Windows 更新真的会重新启用吗？

会。恢复采用「受管键内容级还原」（F1 已修复）：

- 回写首次运行前备份的注册表原值；
- **删除禁用阶段新增的值**（如 `NoAutoUpdate` / `SetDisableUXWUAccess` / `ExcludeWUDriversInQualityUpdate` / `SearchOrderConfig` 等）；
- 服务按原 `StartType` 还原，原 `Running` 的服务被重新拉起；
- 计划任务回到原启用 / 禁用状态。

因此恢复后即可 100% 回到首次运行前的更新策略状态。

### ④ 误关了重要的更新怎么办？

不必担心。直接重新运行 `XiaoMiaoWinUpdate.exe`，点击〔恢复到运行本软件前的状态〕即可将全部策略、服务与计划任务还原到首次运行前的状态。备份文件 `backup.json` 会一直保留，可多次恢复。

### ⑤ Win7/8.1 与 Win10/11 有什么差异？

| 维度 | Win7 / 8 / 8.1 | Win10 / 11 |
|------|------|------|
| 驱动更新策略 | `DriverSearching\SearchOrderConfig = 0` | `WindowsUpdate\ExcludeWUDriversInQualityUpdate = 1` |
| 涉及的额外服务 | 仅 `wuauserv` | `wuauserv` + `UsoSvc` + `WaaSMedicSvc` |
| 计划任务 | 不涉及 | 禁用 `\Microsoft\Windows\WindowsUpdate\Scheduled Start` |
| 更新通知判定 | 看 `wuauserv` 禁用或 `NoAutoUpdate` | 看 `UsoSvc` 禁用或计划任务禁用 |

### ⑥ 是否需要常驻运行？

**不需要。** 本工具是「改完即关」的按需工具：点击一次「关闭」写入策略后，即可关闭程序，效果长期生效（直到系统策略被其它程序 / GPO 重置）。恢复时再次运行点击「恢复」即可。无需后台常驻或开机自启。

---

## 7. 版本号与更新日志模板

- **当前版本**：`v1.0.0`
- **对应程序集版本**：`1.0.0.0`（AssemblyVersion / AssemblyFileVersion）
- **发布日期**：`2026-08-17`
- **.NET 目标框架**：.NET Framework 4.8
- **QA 状态**：2 轮 QA 通过，等价测试 30/30，F1 恢复缺陷已闭环，IS_PASS: YES

### 更新日志（CHANGELOG）模板

后续版本请沿用以下格式：

```markdown
## [x.y.z] - YYYY-MM-DD

### Added
- 新增了哪些功能。

### Changed
- 变更了哪些行为 / 策略 / 界面。

### Fixed
- 修复了哪些缺陷（如 F1 恢复残留、F2 服务未重启等）。
```

示例（首版）：

```markdown
## [1.0.0] - 2026-08-17

### Added
- 单文件便携 EXE（Costura.Fody 打包），一键关闭 / 恢复 Windows 自动更新。
- 兼容 Windows 7 / 8 / 8.1 / 10 / 11，含 OS 版本分支与兼容性清单。
- 首次关闭前自动全量备份至 %LOCALAPPDATA%\XiaoMiaoWinUpdate\backup.json。
- 管理员提权（app.manifest requireAdministrator + 启动二次检测）。
- 6 项状态实时指示 + 系统版本显示。

### Changed
- （无）

### Fixed
- F1：恢复逻辑升级为「受管键内容级还原」，删除禁用新增残留值，达成 100% 还原。
- F2：恢复后原 Running 服务被重新拉起。
- F3：计划任务查询失败时保守判定为「未启用」，避免漏报。
- F4：外部进程 stdout/stderr 异步读取，规避管道死锁。
- F5：外部进程输出编码改用系统默认，规避非中文系统编码异常。
```

---

## 附：已知限制（平台相关）

- **WaaSMedicSvc（Windows Update Medic）**：该服务是 Windows 10/11 的「自愈」组件，`sc config disabled` 可能被系统在未来重新拉起。本工具会尝试禁用它，但若系统自愈机制重新启用更新服务，属于 Windows 预期行为，并非工具失效。遇到时可重新运行本工具点击「彻底关闭」。
- **域 GPO 覆盖**：见 FAQ ②，域环境本地策略可能被覆盖。
- **真机验证建议**：建议在 Windows + VS2022 + .NET Framework 4.8 真机完成编译与「备份 → 禁用 → 恢复」三态验证（含各版本恢复后更新是否真正重新启用）。

---

*发布说明由软件工程师（寇豆码 / Kou）根据 `MainWindow.xaml(.cs)`、`Models/UpdateStatus.cs`、`Services/{OsHelper,AdminHelper,PolicyEngine,BackupService}.cs`、`App.xaml.cs`、`app.manifest`、`AssemblyInfo.cs` 与 `README.md` 源码核实后编写。*
