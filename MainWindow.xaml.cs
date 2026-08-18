using System.Windows;
using XiaoMiaoWinUpdate.Models;
using XiaoMiaoWinUpdate.Services;

namespace XiaoMiaoWinUpdate
{
    /// <summary>
    /// 主窗口。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly PolicyEngine _engine;
        private readonly BackupService _backupService;
        private readonly UpdateStatus _status;
        private bool _isBusy;

        public MainWindow()
        {
            InitializeComponent();

            _engine = new PolicyEngine();
            _backupService = new BackupService();
            _status = new UpdateStatus();

            DataContext = new MainViewModel { Status = _status };

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 防御性复位：确保初始态 _isBusy 为 false，
            // 避免任何残留/旧构建导致两个按钮被永久置灰（双方均不可点）。
            _isBusy = false;
            RefreshStatus();
            // 双保险：依据最新状态再次联动按钮可用态（RefreshStatus 的 finally 也会调用一次）。
            UpdateButtonStates();
        }

        /// <summary>
        /// 刷新状态显示。
        /// </summary>
        private void RefreshStatus()
        {
            try
            {
                _engine.RefreshStatus(_status);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"刷新状态时出错：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateButtonStates();
            }
        }

        /// <summary>
        /// 「彻底关闭 Windows 更新」按钮。
        /// </summary>
        private void BtnDisable_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "确定要彻底关闭 Windows 自动更新吗？\n本工具会先备份当前设置，之后可以通过「恢复」还原。",
                "确认关闭",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            SetBusy(true);

            try
            {
                // 首次运行或备份缺失时自动全量备份。
                _backupService.CreateBackupIfNotExists(_engine);
                _engine.DisableWindowsUpdate();
                RefreshStatus();

                MessageBox.Show(
                    "Windows 自动更新已关闭。\n原始设置已备份到：\n" + _backupService.BackupFilePath,
                    "操作成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"关闭更新失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// 「恢复到运行本软件前的状态」按钮。
        /// </summary>
        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (!_backupService.BackupExists())
            {
                MessageBox.Show(
                    "未找到备份文件，无法恢复。\n请先点击「彻底关闭 Windows 更新」生成备份。",
                    "没有备份",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                "确定要恢复到本软件第一次运行前的状态吗？",
                "确认恢复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            SetBusy(true);

            try
            {
                var backup = _backupService.LoadBackup();
                _backupService.RestoreBackup(backup, _engine);
                RefreshStatus();

                MessageBox.Show(
                    "已恢复到本软件第一次运行前的状态。",
                    "恢复成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"恢复失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// 根据当前是否处于「自动更新已关闭」状态，联动两个主按钮的可用状态：
        /// 已关闭 -> 禁用「彻底关闭」，启用「恢复」；
        /// 未关闭 -> 启用「彻底关闭」，禁用「恢复」。
        /// 同时受 _isBusy 限制（操作进行中两个按钮均不可用）。
        /// </summary>
        private void UpdateButtonStates()
        {
            bool disabled = _status.IsWindowsUpdateDisabled;
            bool operable = !_isBusy;
            BtnDisable.IsEnabled = operable && !disabled;
            BtnRestore.IsEnabled = operable && disabled;
        }

        /// <summary>
        /// 设置按钮忙状态（操作进行中禁用两个按钮），随后重新联动可用状态。
        /// </summary>
        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            UpdateButtonStates();
        }
    }

    /// <summary>
    /// 主窗口视图模型，用于 XAML 绑定。
    /// </summary>
    public class MainViewModel
    {
        public UpdateStatus Status { get; set; }
    }
}
