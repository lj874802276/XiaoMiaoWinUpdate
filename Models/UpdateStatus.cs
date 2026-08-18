using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace XiaoMiaoWinUpdate.Models
{
    /// <summary>
    /// 单个更新项目的显示模型。
    /// </summary>
    public class UpdateItem : INotifyPropertyChanged
    {
        private string _label;
        private string _valueText;
        private Brush _valueBrush;

        public string Label
        {
            get => _label;
            set { _label = value; OnPropertyChanged(); }
        }

        public string ValueText
        {
            get => _valueText;
            set { _valueText = value; OnPropertyChanged(); }
        }

        public Brush ValueBrush
        {
            get => _valueBrush;
            set { _valueBrush = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 主窗口使用的聚合状态模型，包含六个更新开关与系统信息。
    /// </summary>
    public class UpdateStatus : INotifyPropertyChanged
    {
        private string _osCaption;
        private string _statusHeading;
        private Brush _statusHeadingBrush;
        private string _statusSubText;

        public UpdateStatus()
        {
            AutoUpdate = new UpdateItem { Label = "自动更新" };
            DriverUpdate = new UpdateItem { Label = "Windows Update 驱动更新" };
            UpdateNotification = new UpdateItem { Label = "更新通知" };
            AutoRestart = new UpdateItem { Label = "更新自动重启" };
            ManualAccess = new UpdateItem { Label = "Windows Update 手动访问" };
            ExtraBlock = new UpdateItem { Label = "附加更新封锁" };
        }

        /// <summary>
        /// 当前系统描述，例如 "Windows 11"。
        /// </summary>
        public string OsCaption
        {
            get => _osCaption;
            set { _osCaption = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 顶部状态大标题，例如 "Windows 自动更新已关闭"。
        /// </summary>
        public string StatusHeading
        {
            get => _statusHeading;
            set { _statusHeading = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 状态标题颜色。
        /// </summary>
        public Brush StatusHeadingBrush
        {
            get => _statusHeadingBrush;
            set { _statusHeadingBrush = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 状态补充说明。
        /// </summary>
        public string StatusSubText
        {
            get => _statusSubText;
            set { _statusSubText = value; OnPropertyChanged(); }
        }

        public UpdateItem AutoUpdate { get; }
        public UpdateItem DriverUpdate { get; }
        public UpdateItem UpdateNotification { get; }
        public UpdateItem AutoRestart { get; }
        public UpdateItem ManualAccess { get; }
        public UpdateItem ExtraBlock { get; }

        /// <summary>
        /// 是否已彻底关闭 Windows 自动更新（等价于 AutoUpdate 状态文本为「已关闭」）。
        /// 只读计算属性，供 UI 直接驱动「彻底关闭」/「恢复」按钮的可用状态，避免依赖魔法字符串取值。
        /// </summary>
        public bool IsWindowsUpdateDisabled
        {
            get => AutoUpdate != null && AutoUpdate.ValueText == "已关闭";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
