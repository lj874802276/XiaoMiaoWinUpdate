using System;
using System.Security.Principal;

namespace XiaoMiaoWinUpdate.Services
{
    /// <summary>
    /// 管理员权限检测辅助类。
    /// </summary>
    public static class AdminHelper
    {
        /// <summary>
        /// 检测当前进程是否以管理员身份运行。
        /// </summary>
        public static bool IsCurrentProcessAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }
}
