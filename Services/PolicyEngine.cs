using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;
using System.Windows.Media;
using XiaoMiaoWinUpdate.Models;

namespace XiaoMiaoWinUpdate.Services
{
    /// <summary>
    /// 策略引擎：负责读取、计算、写入 Windows 更新相关的注册表、服务与计划任务。
    /// 该类不依赖 UI，所有异常均向上抛出，由调用方处理。
    /// </summary>
    public class PolicyEngine
    {
        // 注册表路径常量。
        public const string AuPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        public const string WuPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        public const string DriverSearchPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";

        /// <summary>
        /// 打开 HKLM 的 64 位视图，避免在 64 位系统上被 WOW64 重定向到 32 位节点。
        /// 在 32 位系统上会自动回退到默认视图。
        /// </summary>
        public static RegistryKey OpenLocalMachine()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        // 注册表值名称常量。
        public const string ValNoAutoUpdate = "NoAutoUpdate";
        public const string ValAuOptions = "AUOptions";
        public const string ValNoAutoReboot = "NoAutoRebootWithLoggedOnUsers";
        public const string ValExcludeWUDrivers = "ExcludeWUDriversInQualityUpdate";
        public const string ValDisableUXWUAccess = "SetDisableUXWUAccess";
        public const string ValSearchOrderConfig = "SearchOrderConfig";

        // 服务名称常量。
        public const string SvcWuauserv = "wuauserv";
        public const string SvcUsoSvc = "UsoSvc";
        public const string SvcWaaSMedicSvc = "WaaSMedicSvc";

        // 计划任务路径常量。
        public static readonly string[] TasksToManage = new[]
        {
            @"\Microsoft\Windows\WindowsUpdate\Scheduled Start"
        };

        /// <summary>
        /// 备份时需要记录的注册表路径集合。
        /// </summary>
        public IReadOnlyList<string> RegistryPathsToBackup => new[]
        {
            AuPolicyPath,
            WuPolicyPath,
            DriverSearchPath
        };

        /// <summary>
        /// 备份时需要记录的服务名称集合。
        /// </summary>
        public IReadOnlyList<string> ServiceNamesToBackup => new[]
        {
            SvcWuauserv,
            SvcUsoSvc,
            SvcWaaSMedicSvc
        };

        /// <summary>
        /// 备份时需要记录的计划任务路径集合。
        /// </summary>
        public IReadOnlyList<string> TaskPathsToBackup => TasksToManage;

        /// <summary>
        /// 读取当前状态并填充到 UpdateStatus 模型。
        /// </summary>
        public void RefreshStatus(UpdateStatus status)
        {
            var version = OsHelper.GetWindowsVersion();
            bool isWin10Or11 = OsHelper.IsWindows10Or11(version);
            bool isWin78 = OsHelper.IsWindows7Or8Point1(version);

            status.OsCaption = $"当前系统：{OsHelper.GetOsCaption()}";

            bool autoUpdateDisabled = GetDword(AuPolicyPath, ValNoAutoUpdate) == 1;
            bool manualAccessDisabled = GetDword(WuPolicyPath, ValDisableUXWUAccess) == 1;
            bool win10DriverDisabled = GetDword(WuPolicyPath, ValExcludeWUDrivers) == 1;
            bool legacyDriverDisabled = GetDword(DriverSearchPath, ValSearchOrderConfig) == 0;
            bool autoRebootDisabled = GetDword(AuPolicyPath, ValNoAutoReboot) == 1;

            bool driverUpdateDisabled = isWin78
                ? legacyDriverDisabled
                : win10DriverDisabled;

            bool notificationDisabled = isWin10Or11
                ? (IsServiceDisabled(SvcUsoSvc) || !IsTaskEnabled(TasksToManage[0]))
                : (IsServiceDisabled(SvcWuauserv) || autoUpdateDisabled);

            status.AutoUpdate.ValueText = autoUpdateDisabled ? "已关闭" : "正常";
            status.AutoUpdate.ValueBrush = autoUpdateDisabled ? Brushes.OrangeRed : Brushes.Green;

            status.DriverUpdate.ValueText = driverUpdateDisabled ? "已关闭" : "正常";
            status.DriverUpdate.ValueBrush = driverUpdateDisabled ? Brushes.OrangeRed : Brushes.Green;

            status.UpdateNotification.ValueText = notificationDisabled ? "已关闭" : "未关闭";
            status.UpdateNotification.ValueBrush = notificationDisabled ? Brushes.OrangeRed : Brushes.Green;

            status.AutoRestart.ValueText = autoRebootDisabled ? "已限制" : "未限制";
            status.AutoRestart.ValueBrush = autoRebootDisabled ? Brushes.OrangeRed : Brushes.Green;

            status.ManualAccess.ValueText = manualAccessDisabled ? "已禁用" : "未禁用";
            status.ManualAccess.ValueBrush = manualAccessDisabled ? Brushes.OrangeRed : Brushes.Green;

            // ExtraBlock 作为附加封锁策略的综合指示。
            bool extraBlockActive = manualAccessDisabled && (isWin10Or11 ? win10DriverDisabled : legacyDriverDisabled);
            status.ExtraBlock.ValueText = extraBlockActive ? "已生效" : "未生效";
            status.ExtraBlock.ValueBrush = extraBlockActive ? Brushes.OrangeRed : Brushes.Green;

            // 状态大标题。
            int disabledCount = 0;
            if (autoUpdateDisabled) disabledCount++;
            if (driverUpdateDisabled) disabledCount++;
            if (notificationDisabled) disabledCount++;
            if (autoRebootDisabled) disabledCount++;
            if (manualAccessDisabled) disabledCount++;

            if (disabledCount == 0)
            {
                status.StatusHeading = "Windows 自动更新正常运行";
                status.StatusHeadingBrush = Brushes.Green;
                status.StatusSubText = "系统可按正常流程接收安全补丁与驱动更新。";
            }
            else if (disabledCount >= 4)
            {
                status.StatusHeading = "Windows 自动更新已关闭";
                status.StatusHeadingBrush = Brushes.OrangeRed;
                status.StatusSubText = "大部分自动更新相关策略已生效，Windows 不会按正常流程获取更新。";
            }
            else
            {
                status.StatusHeading = "Windows 自动更新已关闭，但部分封锁策略未生效";
                status.StatusHeadingBrush = Brushes.DarkOrange;
                status.StatusSubText = "部分附加或访问封锁策略未生效。";
            }
        }

        /// <summary>
        /// 彻底关闭 Windows 更新：写入注册表策略并禁用相关服务与计划任务。
        /// </summary>
        public void DisableWindowsUpdate()
        {
            var version = OsHelper.GetWindowsVersion();
            bool isWin10Or11 = OsHelper.IsWindows10Or11(version);
            bool isWin78 = OsHelper.IsWindows7Or8Point1(version);

            // 注册表策略。
            SetDword(AuPolicyPath, ValNoAutoUpdate, 1);
            SetDword(AuPolicyPath, ValAuOptions, 2);
            SetDword(AuPolicyPath, ValNoAutoReboot, 1);

            if (isWin10Or11)
            {
                SetDword(WuPolicyPath, ValExcludeWUDrivers, 1);
            }

            if (isWin78)
            {
                SetDword(DriverSearchPath, ValSearchOrderConfig, 0);
            }

            SetDword(WuPolicyPath, ValDisableUXWUAccess, 1);

            // 服务禁用（多重 fallback 链，见 ServiceDisableHelper）。
            // 顺序要求：先 WaaSMedicSvc，再 UsoSvc，最后 wuauserv，
            // 避免 WaaSMedicSvc 自愈机制在 wuauserv 被禁用后又将其恢复。
            if (isWin10Or11)
            {
                DisableService(SvcWaaSMedicSvc);
                DisableService(SvcUsoSvc);

                foreach (var taskPath in TasksToManage)
                {
                    DisableTask(taskPath);
                }
            }

            DisableService(SvcWuauserv);
        }

        /// <summary>
        /// 读取指定 HKLM 子键下的 DWORD 值，不存在或异常时返回 -1。
        /// </summary>
        public int GetDword(string subKey, string valueName)
        {
            try
            {
                using (var baseKey = OpenLocalMachine())
                using (var key = baseKey.OpenSubKey(subKey, false))
                {
                    if (key == null) return -1;
                    object value = key.GetValue(valueName);
                    if (value == null) return -1;
                    return Convert.ToInt32(value);
                }
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 写入 HKLM 子键下的 DWORD 值。
        /// </summary>
        public void SetDword(string subKey, string valueName, int value)
        {
            using (var baseKey = OpenLocalMachine())
            using (var key = baseKey.CreateSubKey(subKey))
            {
                key.SetValue(valueName, value, RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// 判断服务是否被禁用。
        /// </summary>
        public bool IsServiceDisabled(string serviceName)
        {
            try
            {
                using (var controller = new ServiceController(serviceName))
                {
                    return controller.StartType == ServiceStartMode.Disabled;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 禁用指定服务（立即停用 + 设置 Disabled）。
        /// 改用 ServiceDisableHelper 的多重 fallback 链：
        /// sc.exe -> Win32 API -> 注册表 Start=4 -> SYSTEM 计划任务，
        /// 以绕过 Win10/11 对受保护服务的 SCM ACL（Error 5 拒绝访问）。
        /// 全部 fallback 失败且服务仍非 Disabled 时，抛出 ServiceDisableFailedException。
        /// </summary>
        public void DisableService(string serviceName)
        {
            ServiceDisableHelper.DisableServiceWithFallbacks(serviceName);
        }

        /// <summary>
        /// 执行 SC 命令并等待完成。
        /// </summary>
        public static void RunSc(string arguments)
        {
            RunProcess("sc.exe", arguments);
        }

        /// <summary>
        /// 判断计划任务是否启用。
        /// </summary>
        public static bool IsTaskEnabled(string taskPath)
        {
            try
            {
                string output = RunSchTasks($"/Query /TN \"{taskPath}\" /FO LIST /V");
                return output.IndexOf("已启用", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       output.IndexOf("Enabled", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                // F3 改进：查询失败时保守判定为「未启用」，避免漏报「更新通知已关闭」。
                // （任务不存在亦视作未启用，符合禁用语义。）
                return false;
            }
        }

        /// <summary>
        /// 禁用计划任务。
        /// </summary>
        public static void DisableTask(string taskPath)
        {
            RunSchTasks($"/Change /TN \"{taskPath}\" /DISABLE");
        }

        /// <summary>
        /// 启用计划任务。
        /// </summary>
        public static void EnableTask(string taskPath)
        {
            RunSchTasks($"/Change /TN \"{taskPath}\" /ENABLE");
        }

        /// <summary>
        /// 执行 schtasks 命令并返回标准输出。
        /// </summary>
        public static string RunSchTasks(string arguments)
        {
            return RunProcess("schtasks.exe", arguments);
        }

        /// <summary>
        /// 启动外部进程并返回标准输出，失败时抛出异常。
        /// </summary>
        public static string RunProcess(string fileName, string arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // F5 改进：使用系统 ANSI 默认编码，避免硬编码 GBK(936) 在非中文系统抛
                // ArgumentException 或产生乱码。
                StandardOutputEncoding = System.Text.Encoding.Default,
                StandardErrorEncoding = System.Text.Encoding.Default
            };

            using (var process = Process.Start(info))
            {
                // F4 改进：stdout / stderr 分别在后台线程读取，避免双向管道在输出较大时死锁。
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
}
