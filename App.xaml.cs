using System.Windows;
using XiaoMiaoWinUpdate.Services;

namespace XiaoMiaoWinUpdate
{
    /// <summary>
    /// 应用程序入口。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!AdminHelper.IsCurrentProcessAdmin())
            {
                MessageBox.Show(
                    "本工具需要管理员权限才能修改 Windows 更新策略、注册表与服务。\n请右键选择「以管理员身份运行」。",
                    "需要管理员权限",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Shutdown(1);
                return;
            }
        }
    }
}
