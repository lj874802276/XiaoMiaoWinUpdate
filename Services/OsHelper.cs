using System;
using System.Management;

namespace XiaoMiaoWinUpdate.Services
{
    /// <summary>
    /// 操作系统版本检测辅助类。
    /// </summary>
    public static class OsHelper
    {
        /// <summary>
        /// 当前支持的 Windows 大版本分类。
        /// </summary>
        public enum WindowsVersion
        {
            Unknown,
            Windows7,
            Windows8,
            Windows8Point1,
            Windows10,
            Windows11
        }

        /// <summary>
        /// 获取当前 Windows 版本分类。
        /// </summary>
        public static WindowsVersion GetWindowsVersion()
        {
            var os = Environment.OSVersion;
            if (os.Platform != PlatformID.Win32NT)
            {
                return WindowsVersion.Unknown;
            }

            var version = os.Version;
            int major = version.Major;
            int minor = version.Minor;
            int build = version.Build;

            if (major == 6 && minor == 1)
            {
                return WindowsVersion.Windows7;
            }

            if (major == 6 && minor == 2)
            {
                return WindowsVersion.Windows8;
            }

            if (major == 6 && minor == 3)
            {
                return WindowsVersion.Windows8Point1;
            }

            if (major == 10)
            {
                if (build >= 22000)
                {
                    return WindowsVersion.Windows11;
                }
                return WindowsVersion.Windows10;
            }

            return WindowsVersion.Unknown;
        }

        /// <summary>
        /// 获取显示用系统描述，优先使用 WMI Caption，失败则回退到版本分类文本。
        /// </summary>
        public static string GetOsCaption()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string caption = obj["Caption"] as string;
                        if (!string.IsNullOrWhiteSpace(caption))
                        {
                            return caption.Trim();
                        }
                    }
                }
            }
            catch
            {
                // WMI 失败时回退。
            }

            return GetVersionDisplayName(GetWindowsVersion());
        }

        /// <summary>
        /// 将版本分类转换为显示文本。
        /// </summary>
        public static string GetVersionDisplayName(WindowsVersion version)
        {
            switch (version)
            {
                case WindowsVersion.Windows7:
                    return "Windows 7";
                case WindowsVersion.Windows8:
                    return "Windows 8";
                case WindowsVersion.Windows8Point1:
                    return "Windows 8.1";
                case WindowsVersion.Windows10:
                    return "Windows 10";
                case WindowsVersion.Windows11:
                    return "Windows 11";
                default:
                    return "未知系统";
            }
        }

        /// <summary>
        /// 是否为 Windows 10/11（用于需要 UsoSvc / WaaSMedicSvc 分支的场景）。
        /// </summary>
        public static bool IsWindows10Or11(WindowsVersion version)
        {
            return version == WindowsVersion.Windows10 || version == WindowsVersion.Windows11;
        }

        /// <summary>
        /// 是否为 Windows 7 / 8 / 8.1（用于需要 SearchOrderConfig 分支的旧版本场景）。
        /// </summary>
        public static bool IsWindows7Or8Point1(WindowsVersion version)
        {
            return version == WindowsVersion.Windows7
                || version == WindowsVersion.Windows8
                || version == WindowsVersion.Windows8Point1;
        }
    }
}
