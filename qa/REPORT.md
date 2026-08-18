# 小喵 Windows 更新助手 — QA 测试报告

- **QA 工程师**：严过关（Yan）
- **被测工程**：`E:\.workbuddy\2026-08-17-20-07-51\winupdate-disabler\`
- **验证手段**：静态代码审查 + Python 3.13 等价参考实现单元测试（沙箱无法编译运行 net48 WPF）
- **工程师自报 IS_PASS**：YES（已核实，部分结论需修正）

---

## 一、文件落盘确认

16 个文件全部落盘、非空、无占位符（Glob/Read 全量确认）：

| 文件 | 大小 | 抽查结论 |
|------|------|----------|
| XiaoMiaoWinUpdate.sln / .csproj | 890 / 4141 | 工程结构完整 |
| app.manifest | 1052 | requireAdministrator + 4 个 supportedOS GUID 齐全 |
| FodyWeavers.xml | 137 | `<Costura />` 正确 |
| App.xaml / .cs | 3135 / 787 | 入口 + 启动二次管理员检测 |
| MainWindow.xaml / .cs | 8703 / 4767 | 主窗口 + 按钮事件，无 TODO |
| Models/UpdateStatus.cs | 3532 | 6 状态模型 + INPC |
| Services/OsHelper.cs | 4045 | 版本分支完整 |
| Services/AdminHelper.cs | 629 | IsInRole 检测 |
| Services/PolicyEngine.cs | 13919 | 注册表/服务/计划任务 + 状态计算 |
| Services/BackupService.cs | 13571 | 备份/JSON/恢复 |
| Properties/AssemblyInfo.cs / Settings.settings / Settings.Designer.cs | 709 / 239 / 1085 | 正常 |
| README.md | 5353 | 文档完整 |

> 全局 Grep `TODO|FIXME|NotImplemented|占位|未实现` → **0 命中**。无空文件、无未完成方法。

---

## 二、静态代码审查发现

严重度说明：P0=致命/阻断（编译失败、崩溃、数据丢失）；P1=高（核心功能缺陷）；P2=中（健壮/边界/可移植问题）。

### P0：未发现
> 未发现和编译阻断、崩溃或数据丢失级别的缺陷。常见致命坑（见下"已核实正确点"）均已被规避。

### P1（高，必须修复）

**F1 — 恢复逻辑未达到"100% 还原"，禁用新增的注册表值会残留（核心安全承诺缺陷）**
- 现象：`BackupService.RestoreRegistryKey` 在 `backup.Exists == true` 时，只做"打开/创建键 + 回写备份里记录的值"，**不删除**禁用操作新增、但首次备份中不存在的值：`NoAutoUpdate`、`NoAutoRebootWithLoggedOnUsers`、`ExcludeWUDriversInQualityUpdate`、`SetDisableUXWUAccess`、`SearchOrderConfig`。
- 触发条件：当对应策略键在首次备份时已存在时（企业 GPO 机器、被其它工具改过的机器、或 `WindowsUpdate` 键本身已存在），禁用写入的新增值在"恢复"后**残留**，更新仍被关闭——与 README "恢复时回写注册表、100% 还原" 的承诺相悖。
- 关键证据（死代码）：`PolicyEngine.DeleteValueIfExists`（line 225）已实现却**从未被任何调用方使用**。工程师明显写了删除辅助方法，却忘记接入恢复路径——这正是缺口所在。
- 注意：在"策略键原本不存在"的干净消费机上，`backup.Exists==false` → `DeleteSubKeyTree` 会整体删除该键，恢复反而正常。因此该缺陷是**环境相关**的，但在真实受管理/被改过的机器上必然出现，属高危。
- **修复建议**：恢复时显式删除 DisableWindowsUpdate 写入的已知受管值（可复用 `DeleteValueIfExists`），或在 `RestoreRegistryKey` 中删除"当前键中存在、但备份值列表里不存在"的值；当 `backup.Exists==false` 已正确删除整键，无需改。

### P2（中，建议改进，非阻断）

**F2 — 服务恢复不重启被停用的服务**
`RestoreService` 仅用 `sc config` 还原 `StartType`，不重启服务。例：wuauserv 原为 Automatic+Running，禁用后 Stopped+Disabled；恢复后变 Automatic 但仍 Stopped，需系统事件触发才启动。对"恢复到运行前状态"承诺偏弱。

**F3 — `IsTaskEnabled` 异常时返回 `true`，可能漏报"更新通知已关闭"**
`PolicyEngine.IsTaskEnabled`（line 306-318）catch 块返回 `true`，导致 Win10/11 上若 schtasks 查询失败/任务不存在，会被判为"已启用"，进而 `notificationDisabled = !IsTaskEnabled(...)` 为 false，UI 可能漏报已关闭的更新通知。属信息准确性问题，非崩溃。

**F4 — `RunProcess` 顺序读取 stdout 再 stderr，存在管道死锁风险**
`RunProcess`（line 361-365）先 `ReadToEnd()` stdout 再 stderr，进程输出较大时经典死锁模式。当前 schtasks/sc 输出很小，概率低；建议改用异步读取（`BeginOutputRead`/`BeginErrorRead`）或先读 stderr。

**F5 — `RunProcess` 固定 `Encoding.GetEncoding(936)`（GBK）解析外部进程输出**
在非中文 Windows（如英文系统）上：`StandardOutputEncoding` 固定 GBK 在缺少该代码页的系统可能抛 `ArgumentException`；且解析"已启用"/"Enabled"虽仍匹配，但中文异常文本可能乱码。建议用 `Console.OutputEncoding` 或系统默认/UTF-8。

**F6 — `RefreshStatus` 中 Win10/11 的 `notificationDisabled` 不参考 `autoUpdateDisabled`（设计确认项，非 bug）**
符合代码注释与现有设计：Win10/11 用 UsoSvc/任务抑制通知。仅提醒——"仅禁用 NoAutoUpdate 而 UsoSvc/任务仍运行"时，UI 显示"更新通知未关闭"，易让测试者误判，建议产品确认文案。

**F7 — WaaSMedicSvc 受 Windows 自愈机制保护（平台限制，非代码 bug）**
`sc config disabled` 常被系统恢复；禁用本身可能不持久，恢复亦无意义。建议在产品文档说明，不必改代码。

**F8 — old-style csproj 使用 PackageReference（构建验证项，P2）**
csproj 为 `ToolsVersion="15.0"` + `Microsoft.Common.props` 的传统工程，却用 `PackageReference`（Costura.Fody 5.7.0 / Newtonsoft.Json 13.0.3）。VS2022 / MSBuild 15+ 支持 legacy 项目用 PackageReference，但首次 `msbuild /restore` 必须成功生成 assets；偶见 "are you missing an assembly reference?"。README 已给出 `/restore`，建议在真机验证首次还原。

**F9 — 单文件/exe 签名说明（信息项，非不一致）**
README 宣称 Costura 单文件嵌入 ✓（与 csproj 一致）；EV 签名在 README 中是手动 `signtool` 步骤，csproj 未自动签名，属预期（需证书）。提示：CI/分发需补签名否则 Defender 误报。

### 已核实正确的点（正面，降低风险）
- ✅ **Registry64 视图**：`PolicyEngine.OpenLocalMachine()` 正确使用 `RegistryView.Registry64`，规避 WOW64 重定向（常见致命坑已规避）。
- ✅ **OS 分支正确 + 版本谎报坑已规避**：`OsHelper` 完整覆盖 6.1/6.2/6.3/10(<22000)/10(>=22000)；`app.manifest` 声明了全部 `supportedOS` GUID（含 Win10 `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`），故 `Environment.OSVersion` 在 Win10/11 **不会**谎报为 6.2（这是同类工具最高频的 P0 缺陷，本工程已正确处理）。
- ✅ **提权一致**：`requireAdministrator` + `App.OnStartup` 二次 `IsInRole` 检测 + 非管理员 `Shutdown(1)`，逻辑自洽。
- ✅ **XAML 绑定完整**：`PrimaryButtonStyle`/`SecondaryButtonStyle` 在 `App.xaml` 资源中定义；`Status.*` 绑定路径与 `UpdateStatus` 模型字段一一对应，无断绑、无占位符。
- ✅ **异常覆盖**：每个写操作（SetDword/DisableService/CreateSubKey/RunSc/RunSchTasks/Restore*）均有 try/catch 或向上抛出由 UI 捕获，无明显的静默吞错；`RunProcess` 非零退出抛异常由调用方处理。

---

## 三、Python 等价测试通过率

- 测试文件：`qa/policy_equiv.py`（等价参考实现，仅标准库）、`qa/test_policy_equiv.py`（unittest）
- 运行命令：
  `C:\Users\Administrator\.workbuddy\binaries\python\versions\3.13.12\python.exe -m unittest discover -s qa -v`
- **结果：30 个用例，30 通过，0 失败（100%）**
- 覆盖：
  - OS 版本分支（8 例：6.1/6.2/6.3/10<22000/10=22000/10>22000/10=21999/6.0 未知）
  - Win10/11 状态映射（5 例，含 OR 逻辑、分支差异）
  - Win7/8/8.1 状态映射（4 例）
  - 提权判定（3 例）
  - 备份 JSON 往返一致性 + 类型归一化（8 例：DWord/QWord/Binary base64/MultiString/String/Exists/服务 StartType）
  - 恢复完整性参考实现（2 例：键预存时删除新增值 / 键缺失时整体删除）
- 测试代码缺陷：初版模块 docstring 含 `C:\Users\...` 触发 `\U` unicode 转义 `SyntaxError` → 已自修（改为 raw 字符串），复跑全绿。属"测试代码自身 bug，已自行修复"，不计入源码缺陷。

---

## 四、智能路由判定

**判定：Engineer（反馈软件工程师 software-engineer 修复）**

理由：
1. 发现 1 处确凿的**源码缺陷 F1**（恢复逻辑不完整，"100% 还原"未达成），由 `DeleteValueIfExists` 成为死代码直接证明——实现漏接了删除步骤，需工程师修改 `BackupService.RestoreRegistryKey` / `PolicyEngine` 恢复路径。
2. Python 等价测试套件本身正确（仅遇 1 个测试文档字符串编码 bug，已自修），因此**不是 QA 路由**。
3. 另有 F2–F9 等 P2 健壮/可移植改进建议，一并交付工程师评估。
4. 不是 NoOne：因存在需源码修复的 P1 缺陷。

---

## 五、遗留项清单（需在 Windows + VS2022 + .NET Framework 4.8 真机编译运行验证）

1. **[阻断前置] 真机编译**：WPF/MSBuild(net48) 全量编译，确认 Costura.Fody 单文件打包、Newtonsoft.Json 嵌入、无编译错误；验证 `msbuild /restore` 首次还原成功（F8）。
2. **[核心] 恢复真值验证**：在 Win7/8.1/10/11 各跑"备份→禁用→恢复"三态，重点验证恢复后更新是否**真正重新启用**（验证本 QA 提出的 F1 残留风险；在策略键预存的机器上必须确认已修复）。
3. **UAC 提权实际行为**：manifest `requireAdministrator` + `App.OnStartup` 二次检测，在标准用户/管理员取消 consent 时的表现。
4. **WaaSMedicSvc 持久性**：Windows 自愈是否会恢复其启动类型（F7）。
5. **多语言 schtasks 解析**：中文("已启用")/英文("Enabled")系统下 `IsTaskEnabled` 正确性（F3/F5）。
6. **杀软误报**：EV 签名 + Microsoft Defender 文件白名单提交（F9）。
7. **服务恢复后运行状态**：恢复后 wuauserv/UsoSvc 是否回到原始 Running/Stopped 状态（F2）。

---
*报告生成：QA 工程师 严过关 | 等价测试脚本已落盘于 `qa/`，可复跑。*
