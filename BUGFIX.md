# BUGFIX.md — 小喵 Windows 更新助手 · wuauserv Error 5 拒绝访问

- **缺陷现象**：点击「彻底关闭 Windows 更新」时，`sc.exe config wuauserv start= disabled` 返回 `Error 5 拒绝访问`（`[SC] ChangeServiceConfig 失败 5: 拒绝访问。`）。
- **环境**：Windows 11 专业版；即便右键「以管理员身份运行」程序，仍报同样的 Error 5。
- **结论**：不是 UAC 未提升，而是 Win11 对 `wuauserv` 等系统服务施加了 SCM ACL / 服务安全描述符保护，管理员（已提权）Token 也不足以执行 `ChangeServiceConfig`。
- **修复提交**：新增 `Services/ServiceDisableHelper.cs`，改写 `PolicyEngine.DisableService` 走多重 fallback 链；调整禁用顺序（WaaSMedicSvc 先于 wuauserv）。

---

## 一、根因分析（Root Cause）

### 1.1 失败点
原 `PolicyEngine.DisableService` 仅依赖一条路径：

```csharp
SetServiceStart(serviceName, "disabled");   // => RunSc("config \"wuauserv\" start= disabled")
```

即调用 `sc.exe config ... start= disabled`。在 Win10/11 上，`wuauserv` / `UsoSvc` / `WaaSMedicSvc` 受 **Service Control Manager（SCM）ACL 保护**：服务的 `Security` 描述符拒绝非 SYSTEM 主体对其执行 `ChangeServiceConfig`（写入启动类型）。

### 1.2 为什么"管理员 + 已提权"仍 Error 5
- 程序 `app.manifest` 为 `requireAdministrator`，启动时已获得**提升的管理员令牌**（elevated）。
- 但 Win11 在 SCM 层面对受保护服务额外施加了 **服务级 ACL / 受保护的进程/服务安全描述符**。此时 `ChangeServiceConfig` 需要的访问权被拒绝，错误码 `ERROR_ACCESS_DENIED (5)`。
- 该错误**与 UAC 是否提升无关**——即便已是 elevated 管理员，SCM 仍会因服务 ACL 拒绝。这正是"右键以管理员运行仍失败"的真正原因。
- 唯一能改写这些服务启动类型的主体通常是 **`NT AUTHORITY\SYSTEM`**（或 TrustedInstaller）。普通管理员令牌拿不到该权限。

### 1.3 WaaSMedicSvc 的自愈机制
`WaaSMedicSvc`（Windows 更新中介服务）带自愈逻辑：即使把 `wuauserv` 改成 Disabled，它也可能在后续把 `wuauserv` 的启动类型恢复。因此禁用顺序上必须**先禁用 WaaSMedicSvc，再禁用 wuauserv**。

---

## 二、修复方案与 fallback 链

新增 `Services/ServiceDisableHelper.cs`（自包含，避免与 `PolicyEngine` 形成循环依赖），`PolicyEngine.DisableService` 改为委托：

```csharp
public void DisableService(string serviceName)
{
    ServiceDisableHelper.DisableServiceWithFallbacks(serviceName);
}
```

每个服务的禁用都走以下**按优先级递减的 fallback 链**（每步 `try/catch`，失败不阻断下一步；任一步成功则在后续步骤前短路）：

| 优先级 | 方式 | 说明 |
|------|------|------|
| 1 | `sc.exe config <name> start= disabled` + `sc stop <name>` | 原有方式（兼容性最佳，普通场景足够） |
| 2 | Win32 API：`OpenSCManager` + `OpenService` + `ChangeServiceConfig`（+ `ControlService` 停止） | 与 sc 等价，但可捕获更细的 Win32 错误（`Marshal.GetLastWin32Error`） |
| 3 | 注册表 `Start` 键回退：`HKLM\SYSTEM\CurrentControlSet\Services\<name>\Start = 4`（Disabled） | **绕过 SCM ACL**，直接写注册表；管理员对该键通常有写权限。对 Win7/8.1/10/11 通用。若服务 Running 则 best-effort `ServiceController.Stop` |
| 4 | **SYSTEM 计划任务兜底**：`schtasks.exe /Create /RU SYSTEM /RL HIGHEST /SC ONSTART /TR "cmd /c sc config <name> start= disabled & sc stop <name>"`，立即 `/Run` 后删除 | 以 `NT AUTHORITY\SYSTEM` 身份执行，获得改写受保护服务所需的 Token（管理员权限下获得 SYSTEM 的标准 workaround）。Win10/11 首选，Win7 也兼容。**`/Run` 后轮询注册表 `Start==4`（最多 10s，500ms 间隔）确认真正生效**，避免任务触发但内部 sc 未成功时被误判为"成功" |
| 5 | 最终失败：汇总所有 fallback 错误，抛 `ServiceDisableFailedException` | 异常消息含环境诊断（是否 elevated、是否 Administrators 组成员）+ 各步骤错误 + 手动处理建议 |

### 2.1 成功判定与结果准确性
每步之间用 `IsServiceStartDisabled` 短路判断（只要成功即停止尝试后续步骤）：
- 注册表 `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Start == 4` → 视为已禁用（最可靠，绕过 SCM）；
- 或 `ServiceController.StartType == Disabled`。

**结果准确性（QA 回归建议 P1-1/P1-2 已落实）**：每一步执行后都回读 `IsServiceStartDisabled` 确认是否真正生效，而非仅看命令退出码。`schtasks /Run` 的退出码只表示"任务已触发"，不代表内部 `sc config` 成功；因此第④步在 `/Run` 后**轮询**注册表 `Start==4`（最多 10 秒）验证，未生效则如实记为"失败"并在最终诊断中体现，绝不把"命令返回 0"误记为"成功"。单步结果分三态记录：**成功**（已回读确认禁用）/**未生效**（命令已执行但服务未禁用）/**失败**（异常）。

### 2.2 成功路径留痕（遥测）
服务经 fallback 链成功禁用时，触发静态事件 `ServiceDisableHelper.ServiceDisabled`（参数 `ServiceDisableEventArgs` 含 `ServiceName` 与最终生效的 `WinningStep`），供真机遥测/排障统计"哪一步最终生效"。

### 2.3 各 fallback 的真实作用
- 第①步（sc.exe）与第②步（Win32 `ChangeServiceConfig`）在受 SCM ACL 保护的服务上通常**同样返回 Error 5**；第②步价值主要在于捕获更细的 Win32 错误码（诊断）。
- 真正能绕过 ACL 的兜底是第③步（注册表 `Start=4`）与第④步（SYSTEM 计划任务）。

### 2.2 禁用顺序调整（关键）
`PolicyEngine.DisableWindowsUpdate` 内顺序调整为：

```csharp
if (isWin10Or11)
{
    DisableService(SvcWaaSMedicSvc);   // 先禁用自愈服务
    DisableService(SvcUsoSvc);
    foreach (var taskPath in TasksToManage) DisableTask(taskPath);
}
DisableService(SvcWuauserv);            // 最后禁用 wuauserv
```

确保 `WaaSMedicSvc` 在 `wuauserv` 之前被禁用，避免其自愈机制恢复 `wuauserv`。

### 2.3 未改动范围
- 现有 OS 分支（`OsHelper` 的 7/8.1 与 10/11 判定）、注册表策略写入、计划任务禁用逻辑**保持不变**。
- 备份 / 恢复逻辑（`BackupService`）**保持不变**（`RunSc`/`RunSchTasks` 仍被复用，未删除）。
- 仅替换了「DisableService 相关的服务禁用路径」。

---

## 三、影响范围（OS 版本）

| OS | 受影响服务 | fallback 链是否生效 |
|----|----------|-------------------|
| Windows 7 / 8 / 8.1 | `wuauserv` | 生效；步骤 4（SYSTEM 任务）在 Win7 同样兼容 |
| Windows 10 | `wuauserv` / `UsoSvc` / `WaaSMedicSvc` | 生效 |
| Windows 11 | `wuauserv` / `UsoSvc` / `WaaSMedicSvc` | 生效（本 Bug 主战场；步骤 3/4 为关键兜底） |

> 兼容性说明：实现仅使用 .NET Framework 4.8 可用 API（`RegistryView.Registry64`、`ServiceController`、P/Invoke `advapi32.dll`、`schtasks.exe`、`System.Security.Principal`）。**未引入任何 .NET 9/10 专有 API**，VS2022 + .NET 4.8 可编译。

---

## 四、临时解决方案（用户现在就能试）

> 在修复版本编译发布前，用户可任选其一立即生效：

1. **先手动禁用 WaaSMedicSvc，再点小喵的「关闭」按钮**
   - 以管理员运行 PowerShell：
     ```powershell
     sc config WaaSMedicSvc start= disabled
     sc stop WaaSMedicSvc
     ```
   - 然后再运行本工具点击「彻底关闭 Windows 更新」。可避免自愈机制恢复 `wuauserv`。

2. **用 Sysinternals PsExec 以 SYSTEM 身份执行**
   - 下载 Sysinternals PsExec，管理员命令行：
     ```cmd
     psexec -s -i cmd.exe
     ```
   - 在新开的 SYSTEM 命令行中执行：
     ```cmd
     sc config wuauserv start= disabled
     sc stop wuauserv
     ```

3. **直接改注册表（重启生效）**
   ```cmd
   reg add "HKLM\SYSTEM\CurrentControlSet\Services\wuauserv" /v Start /t REG_DWORD /d 4 /f
   ```
   - 重启后 `wuauserv` 不再自启。对受 SCM ACL 保护、sc.exe 报 Error 5 的场景，注册表直改通常仍可由管理员写入并绕过 SCM。

---

## 五、验证

- **静态 / 等价测试**：`qa/policy_equiv.py` 与 `qa/test_policy_equiv.py` 已扩展，覆盖新的「服务禁用顺序」与「fallback 链优先级」核心逻辑。沙箱运行结果：**36 用例全部通过（30 原有 + 6 新增）**。
- **真机编译（遗留，需在 Windows + VS2022 + .NET 4.8 验证）**：确认 `ServiceDisableHelper.cs` 纳入 `XiaoMiaoWinUpdate.csproj` 的 `<Compile>` 后全量编译通过、Costura.Fody 单文件打包正常。
- **真机行为（遗留）**：在 Win11 专业版上实测「彻底关闭 Windows 更新」，确认不再报 Error 5，且 `wuauserv`/`UsoSvc`/`WaaSMedicSvc` 启动类型最终被置为 Disabled（至少步骤 3 或 4 命中）。

---

*修复人：软件工程师（Kou）| 关联文件：`Services/ServiceDisableHelper.cs`（新增）、`Services/PolicyEngine.cs`（改写）、`XiaoMiaoWinUpdate.csproj`（登记新文件）、`qa/policy_equiv.py` + `qa/test_policy_equiv.py`（等价测试扩展）。*
