using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;

namespace XiaoMiaoWinUpdate.Services
{
    /// <summary>
    /// 注册表值的备份表示，不存在时 Value 为 null。
    /// </summary>
    public class RegistryValueBackup
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public object Value { get; set; }
    }

    /// <summary>
    /// 注册表键的备份表示，包含该键下若干值。
    /// </summary>
    public class RegistryKeyBackup
    {
        public string Path { get; set; }
        public bool Exists { get; set; }
        public List<RegistryValueBackup> Values { get; set; }
    }

    /// <summary>
    /// 服务状态备份表示。
    /// </summary>
    public class ServiceBackup
    {
        public string Name { get; set; }
        public int StartType { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// 计划任务状态备份表示。
    /// </summary>
    public class TaskBackup
    {
        public string Path { get; set; }
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// 完整备份数据模型，可被 JSON 往返序列化。
    /// </summary>
    public class FullBackup
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string CreatedAt { get; set; }
        public string OsVersion { get; set; }
        public List<RegistryKeyBackup> RegistryKeys { get; set; }
        public List<ServiceBackup> Services { get; set; }
        public List<TaskBackup> Tasks { get; set; }
    }

    /// <summary>
    /// 首次运行备份与恢复逻辑。
    /// </summary>
    public class BackupService
    {
        private readonly string _backupFilePath;

        public BackupService()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDir = Path.Combine(localAppData, "XiaoMiaoWinUpdate");
            Directory.CreateDirectory(appDir);
            _backupFilePath = Path.Combine(appDir, "backup.json");
        }

        /// <summary>
        /// 备份文件完整路径。
        /// </summary>
        public string BackupFilePath => _backupFilePath;

        /// <summary>
        /// 判断备份文件是否已存在。
        /// </summary>
        public bool BackupExists()
        {
            return File.Exists(_backupFilePath);
        }

        /// <summary>
        /// 执行全量备份。若已存在则跳过（首次运行原则）。
        /// </summary>
        public void CreateBackupIfNotExists(PolicyEngine engine)
        {
            if (BackupExists())
            {
                return;
            }

            var backup = BuildBackup(engine);
            string json = JsonConvert.SerializeObject(backup, Formatting.Indented);
            File.WriteAllText(_backupFilePath, json);
        }

        /// <summary>
        /// 构建当前系统状态的完整备份。
        /// </summary>
        public FullBackup BuildBackup(PolicyEngine engine)
        {
            var backup = new FullBackup
            {
                CreatedAt = DateTime.UtcNow.ToString("O"),
                OsVersion = OsHelper.GetOsCaption(),
                RegistryKeys = new List<RegistryKeyBackup>(),
                Services = new List<ServiceBackup>(),
                Tasks = new List<TaskBackup>()
            };

            foreach (var path in engine.RegistryPathsToBackup)
            {
                backup.RegistryKeys.Add(BackupRegistryKey(path));
            }

            foreach (var name in engine.ServiceNamesToBackup)
            {
                backup.Services.Add(BackupServiceSnapshot(name));
            }

            if (OsHelper.IsWindows10Or11(OsHelper.GetWindowsVersion()))
            {
                foreach (var path in engine.TaskPathsToBackup)
                {
                    backup.Tasks.Add(BackupTask(path));
                }
            }

            return backup;
        }

        /// <summary>
        /// 读取备份文件。
        /// </summary>
        public FullBackup LoadBackup()
        {
            if (!File.Exists(_backupFilePath))
            {
                return null;
            }

            string json = File.ReadAllText(_backupFilePath);
            return JsonConvert.DeserializeObject<FullBackup>(json);
        }

        /// <summary>
        /// 将备份数据回写到系统。
        /// </summary>
        public void RestoreBackup(FullBackup backup, PolicyEngine engine)
        {
            if (backup == null)
            {
                throw new InvalidOperationException("未找到备份文件。请先用「关闭 Windows 更新」生成备份后再恢复。");
            }

            if (backup.SchemaVersion != FullBackup.CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"备份文件 schema 版本 {backup.SchemaVersion} 与当前程序不兼容。");
            }

            foreach (var keyBackup in backup.RegistryKeys)
            {
                RestoreRegistryKey(keyBackup);
            }

            foreach (var svcBackup in backup.Services)
            {
                RestoreService(svcBackup);
            }

            foreach (var taskBackup in backup.Tasks)
            {
                RestoreTask(taskBackup);
            }
        }

        private static RegistryKeyBackup BackupRegistryKey(string path)
        {
            var result = new RegistryKeyBackup
            {
                Path = path,
                Exists = false,
                Values = new List<RegistryValueBackup>()
            };

            try
            {
                using (var baseKey = PolicyEngine.OpenLocalMachine())
                using (var key = baseKey.OpenSubKey(path, false))
                {
                    if (key != null)
                    {
                        result.Exists = true;
                        foreach (string name in key.GetValueNames())
                        {
                            object value = key.GetValue(name);
                            string kind = key.GetValueKind(name).ToString();
                            result.Values.Add(new RegistryValueBackup
                            {
                                Name = name,
                                Kind = kind,
                                Value = value
                            });
                        }
                    }
                }
            }
            catch
            {
                // 即使读取失败也记录为空列表，避免崩溃。
            }

            return result;
        }

        private static void RestoreRegistryKey(RegistryKeyBackup backup)
        {
            try
            {
                if (!backup.Exists)
                {
                    // 备份时该键不存在，尝试删除当前键以还原原状。
                    try
                    {
                        using (var baseKey = PolicyEngine.OpenLocalMachine())
                        {
                            baseKey.DeleteSubKeyTree(backup.Path, false);
                        }
                    }
                    catch
                    {
                        // 删除失败（权限、子键等）时忽略，避免阻断后续恢复。
                    }
                    return;
                }

                using (var baseKey = PolicyEngine.OpenLocalMachine())
                using (var key = baseKey.OpenSubKey(backup.Path, true) ?? baseKey.CreateSubKey(backup.Path))
                {
                    // F1 修复：删除「首次备份后新增、但不在备份名单里」的值，
                    // 确保恢复后 100% 还原到首次运行前的注册表状态
                    // （例如受管键原本就存在时，禁用操作新增的 NoAutoUpdate /
                    //  NoAutoRebootWithLoggedOnUsers / ExcludeWUDriversInQualityUpdate /
                    //  SetDisableUXWUAccess / SearchOrderConfig 等会被彻底清除）。
                    var backedUpNames = new HashSet<string>(
                        backup.Values != null
                            ? backup.Values
                                .Where(v => v != null && v.Name != null)
                                .Select(v => v.Name)
                            : Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (string currentName in key.GetValueNames())
                    {
                        if (!backedUpNames.Contains(currentName))
                        {
                            try
                            {
                                key.DeleteValue(currentName, false);
                            }
                            catch
                            {
                                // 单个值删除失败不影响其它值恢复。
                            }
                        }
                    }

                    // 回写备份里记录的原始值。
                    if (backup.Values != null)
                    {
                        foreach (var valueBackup in backup.Values)
                        {
                            if (valueBackup.Value == null)
                            {
                                continue;
                            }

                            RegistryValueKind kind = ParseRegistryValueKind(valueBackup.Kind);
                            object normalizedValue = NormalizeRegistryValue(valueBackup.Value, kind);
                            key.SetValue(valueBackup.Name, normalizedValue, kind);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"恢复注册表键失败：{backup.Path}", ex);
            }
        }

        /// <summary>
        /// 将 JSON 反序列化后的值转换为 RegistryKey.SetValue 可接受的 CLR 类型。
        /// </summary>
        private static object NormalizeRegistryValue(object value, RegistryValueKind kind)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    return Convert.ToInt32(value);
                case RegistryValueKind.QWord:
                    return Convert.ToInt64(value);
                case RegistryValueKind.Binary:
                    if (value is string base64)
                    {
                        return System.Convert.FromBase64String(base64);
                    }
                    if (value is byte[] bytes)
                    {
                        return bytes;
                    }
                    throw new InvalidOperationException("Binary 类型注册表值格式错误。");
                case RegistryValueKind.MultiString:
                    if (value is Newtonsoft.Json.Linq.JArray array)
                    {
                        return array.ToObject<string[]>();
                    }
                    if (value is System.Collections.Generic.IEnumerable<string> strList)
                    {
                        return strList;
                    }
                    if (value is string[] strArray)
                    {
                        return strArray;
                    }
                    throw new InvalidOperationException("MultiString 类型注册表值格式错误。");
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                default:
                    return value?.ToString() ?? string.Empty;
            }
        }

        private static RegistryValueKind ParseRegistryValueKind(string kind)
        {
            if (Enum.TryParse<RegistryValueKind>(kind, out var result))
            {
                return result;
            }
            return RegistryValueKind.DWord;
        }

        private static ServiceBackup BackupServiceSnapshot(string name)
        {
            var backup = new ServiceBackup { Name = name };
            try
            {
                using (var controller = new ServiceController(name))
                {
                    backup.StartType = (int)controller.StartType;
                    backup.Status = controller.Status.ToString();
                }
            }
            catch
            {
                // 服务不存在时记录默认值，恢复时跳过。
                backup.StartType = -1;
                backup.Status = "NotFound";
            }

            return backup;
        }

        private static void RestoreService(ServiceBackup backup)
        {
            if (backup.StartType < 0)
            {
                return;
            }

            try
            {
                using (var controller = new ServiceController(backup.Name))
                {
                    var desiredStartType = (ServiceStartMode)backup.StartType;
                    if (controller.StartType != desiredStartType)
                    {
                        // 走与"禁用"相同的多重 fallback 链设置任意启动类型，以绕过 Win10/11
                        // 对受保护服务（如 WaaSMedicSvc）的 SCM ACL / Error 5 拒绝访问。
                        // sc.exe -> Win32 ChangeServiceConfig -> 注册表 Start=N -> SYSTEM 计划任务，
                        // 任一兜底成功即短路，全部失败则抛 ServiceDisableFailedException（被外层包装）。
                        ServiceDisableHelper.SetServiceStartTypeWithFallbacks(backup.Name, desiredStartType);
                        controller.Refresh();
                    }

                    // F2 改进：若原始状态为 Running，且当前已停止，则重新拉起服务，
                    // 否则 Windows 更新相关组件仍停留在停用态，恢复不彻底。
                    if (desiredStartType != ServiceStartMode.Disabled
                        && string.Equals(backup.Status, "Running", StringComparison.OrdinalIgnoreCase)
                        && controller.Status == ServiceControllerStatus.Stopped)
                    {
                        try
                        {
                            controller.Start();
                            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                        }
                        catch
                        {
                            // 启动失败（依赖缺失等）不影响其它恢复步骤。
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"恢复服务失败：{backup.Name}", ex);
            }
        }

        private static TaskBackup BackupTask(string path)
        {
            bool enabled = PolicyEngine.IsTaskEnabled(path);
            return new TaskBackup { Path = path, Enabled = enabled };
        }

        private static void RestoreTask(TaskBackup backup)
        {
            try
            {
                string flag = backup.Enabled ? "/ENABLE" : "/DISABLE";
                PolicyEngine.RunSchTasks($"/Change /TN \"{backup.Path}\" {flag}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"恢复计划任务失败：{backup.Path}", ex);
            }
        }
    }
}
