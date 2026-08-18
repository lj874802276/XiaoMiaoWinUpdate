using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace XiaoMiaoWinUpdate.Services
{
    /// <summary>
    /// 服务禁用 / 设置启动类型辅助：对单个 Windows 服务执行"多重 fallback 链"。
    ///
    /// <para>
    /// 根因（详见 BUGFIX.md）：Windows 10/11 对 wuauserv / UsoSvc / WaaSMedicSvc 等服务
    /// 施加了 Service Control Manager（SCM）ACL 保护。即便程序以管理员身份运行并已提权，
    /// 调用 ChangeServiceConfig / sc config 仍会返回 <c>Error 5 拒绝访问</c>。
    /// 单一的 sc.exe 命令在此场景下必然失败，因此需要一条按优先级递减的兜底链。
    /// </para>
    ///
    /// <para>兜底链（按优先级，每步失败不阻断下一步）：</para>
    /// <list type="number">
    ///   <item>sc.exe config &lt;name&gt; start= &lt;mode&gt;（+ 目标为 Disabled 时 sc stop &lt;name&gt;）</item>
    ///   <item>.NET / Win32 API 直接改 StartType（OpenSCManager + ChangeServiceConfig）</item>
    ///   <item>注册表 Start 键回退：HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\Start = N（绕过 SCM ACL，全版本通用）</item>
    ///   <item>SYSTEM 身份计划任务兜底（以 NT AUTHORITY\SYSTEM 运行 sc config，Win10/11 首选，Win7 兼容）</item>
    ///   <item>全部失败：汇总所有错误并抛出 ServiceDisableFailedException（含环境诊断与手动命令建议）</item>
    /// </list>
    ///
    /// <para>
    /// 该链路对"任意启动类型"通用：禁用（Disabled）只是其中一种特例。
    /// <see cref="DisableServiceWithFallbacks"/> 是 <see cref="SetServiceStartTypeWithFallbacks"/> 的
    /// 薄封装，仅把目标模式固定为 <see cref="ServiceStartMode.Disabled"/>，以保持向后兼容。
    /// </para>
    ///
    /// <para>本类为自包含实现（含独立的命令行执行器），不反向依赖 PolicyEngine，避免循环依赖。</para>
    /// </summary>
    public static class ServiceDisableHelper
    {
        // ---- Win32 常量 ----
        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_CHANGE_CONFIG = 0x0002;
        private const uint SERVICE_STOP = 0x0020;
        private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

        // 服务启动类型（与 ServiceStartMode 枚举数值及注册表 Start 值一一对应）：
        // Boot=0, System=1, Automatic=2, Manual=3, Disabled=4。
        private const uint SERVICE_BOOT_START = 0x00000000;
        private const uint SERVICE_SYSTEM_START = 0x00000001;
        private const uint SERVICE_AUTO_START = 0x00000002;
        private const uint SERVICE_DEMAND_START = 0x00000003;
        private const uint SERVICE_DISABLED = 0x00000004;

        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const uint SERVICE_STOPPED = 0x00000001;
        private const uint SERVICE_RUNNING = 0x00000004;

        // ---- P/Invoke（advapi32.dll 服务控制 API）----
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool ChangeServiceConfig(
            IntPtr hService,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string lpDependencies,
            string lpServiceStartName,
            string lpPassword,
            string lpDisplayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        /// <summary>
        /// 管理员环境诊断信息（最终失败提示用）。
        /// </summary>
        public sealed class AdminDiagnostics
        {
            /// <summary>当前进程是否已提权（elevated，即 UAC 已提升的管理员令牌）。</summary>
            public bool IsElevated { get; set; }

            /// <summary>当前用户是否为 Administrators 组成员（与是否提权无关）。</summary>
            public bool IsAdministratorsGroupMember { get; set; }
        }

        /// <summary>
        /// 单次 fallback 尝试的记录（用于最终失败汇总）。
        /// </summary>
        private sealed class StepAttempt
        {
            public string StepName { get; set; }
            public string Detail { get; set; }
            public Exception Exception { get; set; }
        }

        /// <summary>
        /// 服务经 fallback 链成功"禁用"时触发（遥测/排障用）。
        /// 事件参数包含服务名与最终生效的 fallback 步骤名（成功路径留痕）。
        /// 注意：仅当目标模式为 Disabled 时才触发本事件（见 RestoreService 等恢复场景不应误报"已禁用"）。
        /// </summary>
        public static event EventHandler<ServiceDisableEventArgs> ServiceDisabled;

        /// <summary>
        /// 服务成功禁用事件参数。
        /// </summary>
        public sealed class ServiceDisableEventArgs : EventArgs
        {
            /// <summary>被禁用的服务名。</summary>
            public string ServiceName { get; }

            /// <summary>最终生效（真正禁用服务）的 fallback 步骤名；若无法确定则为 null。</summary>
            public string WinningStep { get; }

            public ServiceDisableEventArgs(string serviceName, string winningStep)
            {
                ServiceName = serviceName;
                WinningStep = winningStep;
            }
        }

        /// <summary>
        /// 将服务启动类型设置为任意目标模式，执行多重 fallback 链。
        /// </summary>
        /// <remarks>
        /// 按优先级依次尝试各 fallback；任一 fallback 成功后会在后续步骤前短路（不再重复尝试）。
        /// 若所有 fallback 结束后服务启动类型仍不匹配 <paramref name="startMode"/>，则抛出
        /// <see cref="ServiceDisableFailedException"/>，异常消息包含环境诊断与手动处理建议。
        ///
        /// <para>当 <paramref name="startMode"/> 为 <see cref="ServiceStartMode.Disabled"/> 时，
        /// 每个成功写入启动类型的步骤会附带 best-effort 停止运行实例（受 SCM ACL 保护时停止仍可能失败，
        /// 但 Start 已生效，下次不再自启），并在成功时触发 <see cref="ServiceDisabled"/> 遥测事件。</para>
        /// </remarks>
        /// <param name="serviceName">服务名，如 "wuauserv"。</param>
        /// <param name="startMode">目标启动类型（Boot / System / Automatic / Manual / Disabled）。</param>
        /// <param name="allowSystemTask">
        /// 是否允许"SYSTEM 计划任务"兜底（Win10/11 首选，Win7 也兼容）。默认为 true。
        /// </param>
        /// <exception cref="ArgumentException">serviceName 为空。</exception>
        /// <exception cref="ServiceDisableFailedException">全部 fallback 失败后抛出。</exception>
        public static void SetServiceStartTypeWithFallbacks(
            string serviceName,
            ServiceStartMode startMode,
            bool allowSystemTask = true)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException("serviceName 不能为空。", nameof(serviceName));
            }

            var attempts = new List<StepAttempt>();

            // 1) sc.exe config（+ 目标为 Disabled 时 sc stop）。
            TryStep("sc.exe config", serviceName, startMode, attempts, () => ScSetStartType(serviceName, startMode));

            // 2) Win32 API：OpenSCManager + ChangeServiceConfig。
            //    说明：对受 SCM ACL 保护的服务，本步与第①步通常同样返回 Error 5，
            //    主要用于捕获更细的 Win32 错误码（诊断价值）；真正能绕过 ACL 的兜底是第③、④步。
            if (!IsServiceStartMode(serviceName, startMode))
            {
                TryStep("Win32 ChangeServiceConfig", serviceName, startMode, attempts, () => ApiSetStartType(serviceName, startMode));
            }

            // 3) 注册表 Start=N 回退（绕过 SCM ACL，全版本通用，真正兜底之一）。
            if (!IsServiceStartMode(serviceName, startMode))
            {
                TryStep("Registry Start=N", serviceName, startMode, attempts, () => RegistrySetStartType(serviceName, startMode));
            }

            // 4) SYSTEM 计划任务兜底（以 NT AUTHORITY\SYSTEM 改写受保护服务，真正兜底之一）。
            if (allowSystemTask && !IsServiceStartMode(serviceName, startMode))
            {
                TryStep("SYSTEM scheduled task", serviceName, startMode, attempts, () => SystemTaskSetStartType(serviceName, startMode));
            }

            // 5) 最终校验：仍未匹配目标模式则汇总失败并抛出友好诊断异常。
            if (!IsServiceStartMode(serviceName, startMode))
            {
                throw BuildFailure(serviceName, startMode, attempts);
            }

            // 成功路径留痕：仅"禁用"场景触发遥测事件，便于真机排障/统计哪一步最终生效；
            // 恢复（设置为非 Disabled）场景不应误报"已禁用"。
            if (startMode == ServiceStartMode.Disabled)
            {
                FireServiceDisabled(serviceName, attempts);
            }
        }

        /// <summary>
        /// 执行单个服务的"禁用" fallback 链（向后兼容薄封装）。
        /// </summary>
        /// <remarks>
        /// 等价于 <c>SetServiceStartTypeWithFallbacks(serviceName, ServiceStartMode.Disabled, allowSystemTask)</c>。
        /// 保留此方法以维持既有调用（如 <see cref="PolicyEngine.DisableService"/>）的兼容性。
        /// </remarks>
        /// <param name="serviceName">服务名，如 "wuauserv"。</param>
        /// <param name="allowSystemTask">
        /// 是否允许"SYSTEM 计划任务"兜底（Win10/11 首选，Win7 也兼容）。默认为 true。
        /// </param>
        public static void DisableServiceWithFallbacks(string serviceName, bool allowSystemTask = true)
        {
            SetServiceStartTypeWithFallbacks(serviceName, ServiceStartMode.Disabled, allowSystemTask);
        }

        /// <summary>
        /// 触发 ServiceDisabled 遥测事件（仅禁用成功路径留痕）。
        /// </summary>
        private static void FireServiceDisabled(string serviceName, List<StepAttempt> attempts)
        {
            string winningStep = null;
            foreach (var attempt in attempts)
            {
                // 仅"成功"（已回读确认服务真正禁用）的步骤记为生效步骤；
                // "未生效"/"失败"均不计入，避免把"命令返回 0"误当作生效。
                if (attempt.Exception == null
                    && string.Equals(attempt.Detail, "成功", StringComparison.Ordinal))
                {
                    winningStep = attempt.StepName;
                }
            }

            ServiceDisabled?.Invoke(null, new ServiceDisableEventArgs(serviceName, winningStep));
        }

        /// <summary>
        /// 判断服务启动类型是否已匹配目标模式：注册表 Start 值 == 目标值，
        /// 或 ServiceController.StartType == 目标模式。注册表判定优先（可直接绕过 SCM ACL，最可靠）。
        /// </summary>
        /// <param name="serviceName">服务名。</param>
        /// <param name="desiredMode">期望的启动类型。</param>
        /// <returns>已匹配目标模式返回 true，否则 false。</returns>
        public static bool IsServiceStartMode(string serviceName, ServiceStartMode desiredMode)
        {
            int desiredRegistryValue = MapToRegistryStartValue(desiredMode);
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName, false))
                {
                    if (key != null)
                    {
                        object startValue = key.GetValue("Start");
                        if (startValue != null && Convert.ToInt32(startValue) == desiredRegistryValue)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // 注册表读取失败则继续用 ServiceController 判定。
            }

            try
            {
                using (var controller = new ServiceController(serviceName))
                {
                    return controller.StartType == desiredMode;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断服务是否已禁用：等价于 <see cref="IsServiceStartMode(string, ServiceStartMode)"/> 传入 Disabled。
        /// 注册表 Start==4 或 ServiceController.StartType==Disabled。
        /// </summary>
        public static bool IsServiceStartDisabled(string serviceName)
        {
            return IsServiceStartMode(serviceName, ServiceStartMode.Disabled);
        }

        /// <summary>
        /// 将 ServiceStartMode 映射为 sc.exe 的 start= 参数文本。
        /// Boot="boot", System="system", Automatic="auto", Manual="demand", Disabled="disabled"。
        /// </summary>
        private static string MapToScStartMode(ServiceStartMode mode)
        {
            switch (mode)
            {
                case ServiceStartMode.Boot: return "boot";
                case ServiceStartMode.System: return "system";
                case ServiceStartMode.Automatic: return "auto";
                case ServiceStartMode.Manual: return "demand";
                case ServiceStartMode.Disabled: return "disabled";
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的启动类型。");
            }
        }

        /// <summary>
        /// 将 ServiceStartMode 映射为注册表 Start 值（与 ServiceStartMode 枚举数值一一对应）：
        /// Boot=0, System=1, Automatic=2, Manual=3, Disabled=4。
        /// </summary>
        private static int MapToRegistryStartValue(ServiceStartMode mode)
        {
            return (int)mode;
        }

        /// <summary>
        /// 将 ServiceStartMode 映射为 Win32 ChangeServiceConfig 的 dwStartType：
        /// 与注册表值/枚举数值一致（Boot=0, System=1, Automatic=2, Manual=3, Disabled=4）。
        /// </summary>
        private static uint MapToWin32StartType(ServiceStartMode mode)
        {
            switch (mode)
            {
                case ServiceStartMode.Boot: return SERVICE_BOOT_START;
                case ServiceStartMode.System: return SERVICE_SYSTEM_START;
                case ServiceStartMode.Automatic: return SERVICE_AUTO_START;
                case ServiceStartMode.Manual: return SERVICE_DEMAND_START;
                case ServiceStartMode.Disabled: return SERVICE_DISABLED;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的启动类型。");
            }
        }

        /// <summary>
        /// 生成目标启动类型的中文可读描述（用于诊断异常消息）。
        /// </summary>
        private static string DescribeStartMode(ServiceStartMode mode)
        {
            switch (mode)
            {
                case ServiceStartMode.Boot: return "Boot（内核加载时启动）";
                case ServiceStartMode.System: return "System（系统初始化时启动）";
                case ServiceStartMode.Automatic: return "Automatic（自动）";
                case ServiceStartMode.Manual: return "Manual（手动）";
                case ServiceStartMode.Disabled: return "Disabled（禁用）";
                default: return mode.ToString();
            }
        }

        /// <summary>
        /// Fallback 1：sc.exe config start= &lt;mode&gt;，目标为 Disabled 时附带 best-effort sc stop。
        /// </summary>
        private static void ScSetStartType(string serviceName, ServiceStartMode startMode)
        {
            // 核心步骤：写入目标启动类型。若此处抛 Error 5，由 TryStep 捕获并记录。
            string scMode = MapToScStartMode(startMode);
            RunCli("sc.exe", $"config \"{serviceName}\" start= {scMode}");

            // 仅当目标为 Disabled 时停止运行实例（best-effort）：即使停止失败，Start 已生效。
            if (startMode == ServiceStartMode.Disabled)
            {
                try
                {
                    RunCli("sc.exe", $"stop \"{serviceName}\"");
                }
                catch
                {
                    // 服务未运行或被保护时停止可能失败，不影响禁用结果。
                }
            }
        }

        /// <summary>
        /// Fallback 2：通过 Win32 服务控制 API 直接改 StartType。
        /// 与 sc.exe 等价，但可捕获更细的 Win32 错误（如 ERROR_ACCESS_DENIED=5）。
        /// 目标为 Disabled 且服务正在运行时，best-effort 停止并等待。
        /// </summary>
        private static void ApiSetStartType(string serviceName, ServiceStartMode startMode)
        {
            IntPtr scm = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;
            try
            {
                scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager 失败");
                }

                service = OpenService(scm, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_STOP | SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenService 失败");
                }

                uint desiredStartType = MapToWin32StartType(startMode);
                if (!ChangeServiceConfig(
                        service,
                        SERVICE_NO_CHANGE,
                        desiredStartType,
                        SERVICE_NO_CHANGE,
                        null,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null,
                        null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ChangeServiceConfig 失败");
                }

                // 仅当目标为 Disabled 且服务正在运行时，best-effort 停止并等待。
                if (startMode == ServiceStartMode.Disabled)
                {
                    var status = new SERVICE_STATUS();
                    if (QueryServiceStatus(service, ref status) && status.dwCurrentState == SERVICE_RUNNING)
                    {
                        ControlService(service, SERVICE_CONTROL_STOP, ref status);
                        for (int i = 0; i < 15; i++)
                        {
                            if (!QueryServiceStatus(service, ref status) || status.dwCurrentState == SERVICE_STOPPED)
                            {
                                break;
                            }

                            Thread.Sleep(1000);
                        }
                    }
                }
            }
            finally
            {
                if (service != IntPtr.Zero)
                {
                    CloseServiceHandle(service);
                }

                if (scm != IntPtr.Zero)
                {
                    CloseServiceHandle(scm);
                }
            }
        }

        /// <summary>
        /// Fallback 3：直接写注册表 Start=N（目标值），绕过 SCM ACL。
        /// 对 Win7/8.1/10/11 通用；管理员通常对该键拥有写权限。
        /// 目标为 Disabled 且服务当前 Running 时，best-effort 停止（受 ACL 保护时可能失败，
        /// 但 Start 已确保下次不再自启）。
        /// </summary>
        private static void RegistrySetStartType(string serviceName, ServiceStartMode startMode)
        {
            int startValue = MapToRegistryStartValue(startMode);
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException(
                        $"注册表服务键不存在：HKLM\\SYSTEM\\CurrentControlSet\\Services\\{serviceName}");
                }

                key.SetValue("Start", startValue, RegistryValueKind.DWord);
            }

            if (startMode == ServiceStartMode.Disabled)
            {
                try
                {
                    using (var controller = new ServiceController(serviceName))
                    {
                        if (controller.Status == ServiceControllerStatus.Running)
                        {
                            controller.Stop();
                            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        }
                    }
                }
                catch
                {
                    // 停止失败（ACL/依赖）不致命；Start 已确保启动类型被禁用。
                }
            }
        }

        /// <summary>
        /// Fallback 4：创建临时 SYSTEM 计划任务执行 sc config，以 SYSTEM 令牌获得足够权限。
        /// 这是管理员权限下获得 SYSTEM Token 改写受保护服务的标准 workaround。
        /// 目标为 Disabled 时，命令中追加 sc stop。
        /// </summary>
        private static void SystemTaskSetStartType(string serviceName, ServiceStartMode startMode)
        {
            // 服务名无空格，sc 命令无需内嵌引号，避免 schtasks /TR 引号解析问题。
            string taskName = "XiaoMiaoTempSet_" + serviceName + "_" + Guid.NewGuid().ToString("N");
            string scMode = MapToScStartMode(startMode);
            string action = $"cmd.exe /c sc config {serviceName} start= {scMode}";
            if (startMode == ServiceStartMode.Disabled)
            {
                action += $" & sc stop {serviceName}";
            }

            try
            {
                // 以 NT AUTHORITY\SYSTEM（/RU SYSTEM）创建最高权限的一次性任务。
                RunCli("schtasks.exe",
                    $"/Create /TN \"{taskName}\" /RU SYSTEM /RL HIGHEST /SC ONSTART /TR \"{action}\" /F");

                try
                {
                    RunCli("schtasks.exe", $"/Run /TN \"{taskName}\"");

                    // 轮询注册表 Start==目标值（最多 10 秒，500ms 间隔）确认真正生效，
                    // 而非固定 sleep——避免 SYSTEM 任务写入完成前 IsServiceStartMode 误判为 false。
                    bool matched = false;
                    for (int i = 0; i < 20; i++)
                    {
                        if (IsServiceStartMode(serviceName, startMode))
                        {
                            matched = true;
                            break;
                        }

                        Thread.Sleep(500);
                    }

                    if (!matched)
                    {
                        // 任务已触发但服务未真正变更：如实记为失败，供最终诊断（不误导为"成功"）。
                        throw new InvalidOperationException(
                            $"SYSTEM 计划任务已触发，但服务启动类型在超时内未变为 {DescribeStartMode(startMode)}" +
                            "（可能本机 SYSTEM 令牌仍无法改写该受保护服务）。");
                    }
                }
                finally
                {
                    // 无论成功与否，删除临时任务以清理痕迹。
                    try
                    {
                        RunCli("schtasks.exe", $"/Delete /TN \"{taskName}\" /F");
                    }
                    catch
                    {
                        // 清理失败不影响主流程。
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SYSTEM 计划任务兜底失败（task={taskName}）", ex);
            }
        }

        /// <summary>
        /// 包装单次 fallback 尝试：命令执行后回读 IsServiceStartMode 确认是否真正生效。
        /// "失败"（异常）或"未生效"（命令已执行但服务仍未匹配目标模式）均不向外抛出（不阻断后续步骤）。
        /// </summary>
        private static void TryStep(string stepName, string serviceName, ServiceStartMode startMode, List<StepAttempt> attempts, Action action)
        {
            try
            {
                action();

                // 校验是否真正生效：如 schtasks /Run 仅表示"任务已触发"，不代表内部 sc config 成功；
                // 故以 IsServiceStartMode 实际回读确认，避免把"命令返回 0"误记为"成功"。
                if (IsServiceStartMode(serviceName, startMode))
                {
                    attempts.Add(new StepAttempt { StepName = stepName, Detail = "成功" });
                }
                else
                {
                    attempts.Add(new StepAttempt
                    {
                        StepName = stepName,
                        Detail = "未生效（命令已执行，但服务启动类型仍未变更为目标模式）"
                    });
                }
            }
            catch (Exception ex)
            {
                attempts.Add(new StepAttempt { StepName = stepName, Detail = "失败", Exception = ex });
            }
        }

        /// <summary>
        /// 收集管理员环境诊断信息（提权状态 + Administrators 组成员）。
        /// </summary>
        private static AdminDiagnostics GetAdminDiagnostics()
        {
            return new AdminDiagnostics
            {
                IsElevated = AdminHelper.IsCurrentProcessAdmin(),
                IsAdministratorsGroupMember = IsMemberOfAdministrators()
            };
        }

        /// <summary>
        /// 判断当前用户是否为 Administrators 组成员（与是否提权无关，基于令牌组 SID 检测）。
        /// </summary>
        private static bool IsMemberOfAdministrators()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    if (identity.Groups != null)
                    {
                        foreach (var group in identity.Groups)
                        {
                            if (group is SecurityIdentifier sid && sid == adminSid)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 检测失败时不致命，按非成员处理。
            }

            return false;
        }

        /// <summary>
        /// 汇总所有 fallback 失败信息、环境诊断与手动处理建议，构造友好诊断异常。
        /// </summary>
        private static ServiceDisableFailedException BuildFailure(string serviceName, ServiceStartMode startMode, List<StepAttempt> attempts)
        {
            AdminDiagnostics diag = GetAdminDiagnostics();
            string scMode = MapToScStartMode(startMode);
            int registryValue = MapToRegistryStartValue(startMode);

            var sb = new StringBuilder();
            sb.AppendLine($"无法将服务「{serviceName}」的启动类型设置为 {DescribeStartMode(startMode)} —— 已尝试全部 fallback 仍失败。");
            sb.AppendLine();
            sb.AppendLine("【环境诊断】");
            sb.AppendLine($"  · 当前进程是否已提权(elevated)：{(diag.IsElevated ? "是" : "否")}");
            sb.AppendLine($"  · 当前用户是否 Administrators 组成员：{(diag.IsAdministratorsGroupMember ? "是" : "否")}");
            sb.AppendLine("  · 说明：即便已提权且为管理员组成员，Win10/11 仍可能对受保护服务施加");
            sb.AppendLine("    SCM ACL，导致 sc.exe / ChangeServiceConfig 返回 Error 5（拒绝访问）。");
            sb.AppendLine();
            sb.AppendLine("【各 fallback 步骤结果】");
            foreach (var attempt in attempts)
            {
                if (attempt.Exception == null)
                {
                    sb.AppendLine($"  · {attempt.StepName}：{attempt.Detail}");
                }
                else
                {
                    sb.AppendLine($"  · {attempt.StepName}：{attempt.Detail} —— {attempt.Exception.Message}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("【手动处理建议】");
            sb.AppendLine("  1) 以 SYSTEM 身份执行：下载 Sysinternals PsExec，运行 `psexec -s -i cmd.exe`，");
            sb.AppendLine($"     再执行 `sc config {serviceName} start= {scMode}`");
            sb.AppendLine($"  2) 注册表直改（重启生效）：reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{serviceName}\" /v Start /t REG_DWORD /d {registryValue} /f");
            sb.AppendLine("  3) 或先手动调整 WaaSMedicSvc 再重试本工具（避免其自愈机制恢复 wuauserv）");

            return new ServiceDisableFailedException(sb.ToString());
        }

        /// <summary>
        /// 自包含的命令行执行器（镜像 PolicyEngine.RunProcess 的异步读取以避免管道死锁，
        /// 但独立实现以避免与 PolicyEngine 形成循环依赖）。
        /// 非零退出码时抛出异常。
        /// </summary>
        private static string RunCli(string fileName, string arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            };

            using (var process = Process.Start(info))
            {
                if (process == null)
                {
                    throw new InvalidOperationException($"无法启动进程：{fileName}");
                }

                // stdout / stderr 分别在后台线程读取，避免双向管道在输出较大时死锁。
                var outTask = System.Threading.Tasks.Task.Run(() => process.StandardOutput.ReadToEnd());
                var errTask = System.Threading.Tasks.Task.Run(() => process.StandardError.ReadToEnd());
                process.WaitForExit();
                string output = outTask.GetAwaiter().GetResult();
                string error = errTask.GetAwaiter().GetResult();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"命令执行失败（{fileName} {arguments}）：{error}{output}");
                }

                return output;
            }
        }
    }

    /// <summary>
    /// 服务启动类型设置全部 fallback 仍失败时抛出的诊断异常。消息中包含环境诊断与手动处理建议，
    /// 可直接展示给用户。
    /// </summary>
    public sealed class ServiceDisableFailedException : Exception
    {
        /// <summary>构造带诊断消息的异常。</summary>
        public ServiceDisableFailedException(string message)
            : base(message)
        {
        }
    }
}
