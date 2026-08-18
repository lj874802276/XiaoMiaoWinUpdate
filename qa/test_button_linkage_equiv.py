r"""
test_button_linkage_equiv.py

等价格测试：验证「按钮状态联动」核心逻辑（纯函数，不依赖 WPF 运行时）。

对应源码：
  - Models/UpdateStatus.cs :: IsWindowsUpdateDisabled
        get => AutoUpdate != null && AutoUpdate.ValueText == "已关闭";
  - MainWindow.xaml.cs :: UpdateButtonStates()
        bool disabled = _status.IsWindowsUpdateDisabled;
        bool operable = !_isBusy;
        BtnDisable.IsEnabled = operable && !disabled;
        BtnRestore.IsEnabled = operable && disabled;
  - Services/PolicyEngine.cs :: RefreshStatus 写入
        status.AutoUpdate.ValueText = autoUpdateDisabled ? "已关闭" : "正常";

关键不变量（P0）：IsWindowsUpdateDisabled 的取值字符串 "已关闭" 必须与
PolicyEngine.RefreshStatus 真实写入的 AutoUpdate.ValueText 完全一致，否则按钮
永远不联动。本测试用等价函数独立复刻两边，断言其取值始终对齐。

运行（在 winupdate-disabler 目录下）：
  C:\Users\Administrator\.workbuddy\binaries\python\versions\3.13.12\python.exe -m unittest discover -s qa -v
"""

import unittest


# ---- 等价复刻（与 C# 语义逐字对应，不得自行"简化"）----
def policy_engine_auto_update_value_text(no_auto_update):
    """等价 PolicyEngine.RefreshStatus 对 AutoUpdate.ValueText 的写入。

    autoUpdateDisabled = GetDword(AuPolicyPath, NoAutoUpdate) == 1;
    status.AutoUpdate.ValueText = autoUpdateDisabled ? "已关闭" : "正常";
    """
    auto_update_disabled = no_auto_update == 1
    return "已关闭" if auto_update_disabled else "正常"


def is_windows_update_disabled(auto_update_value_text):
    """等价 UpdateStatus.IsWindowsUpdateDisabled 的推导。

    get => AutoUpdate != null && AutoUpdate.ValueText == "已关闭";
    """
    return auto_update_value_text is not None and auto_update_value_text == "已关闭"


def update_button_states(is_disabled, is_busy):
    """等价 UpdateButtonStates() 返回的 (BtnDisable.IsEnabled, BtnRestore.IsEnabled)。"""
    disabled = is_disabled
    operable = not is_busy
    btn_disable_enabled = operable and not disabled
    btn_restore_enabled = operable and disabled
    return btn_disable_enabled, btn_restore_enabled


class TestLinkageValueConsistency(unittest.TestCase):
    """P0 不变量：IsWindowsUpdateDisabled 的取值必须与 PolicyEngine 真实写入一致。"""

    def test_value_strings_match_exactly(self):
        for no_auto in (0, 1):
            vt = policy_engine_auto_update_value_text(no_auto)
            if no_auto == 1:
                self.assertEqual(vt, "已关闭")
                self.assertTrue(is_windows_update_disabled(vt))
            else:
                self.assertEqual(vt, "正常")
                self.assertFalse(is_windows_update_disabled(vt))

    def test_no_silent_divergence_on_known_texts(self):
        # 若 PolicyEngine 写入其它文本（如 "已禁用"/"关闭"），联动应判为「未禁用」，
        # 防止取值字符串漂移导致按钮永久不联动。
        for other in ("已禁用", "正常", "关闭", "", None):
            self.assertEqual(is_windows_update_disabled(other), other == "已关闭")


class TestButtonLinkageTruthTable(unittest.TestCase):
    """完整联动真值表：四种 (已禁用, 操作中) 组合 -> (BtnDisable, BtnRestore)。"""

    def test_disabled_not_busy(self):
        d, r = update_button_states(is_disabled=True, is_busy=False)
        self.assertFalse(d)  # 彻底关闭 -> 变灰
        self.assertTrue(r)   # 恢复 -> 变亮可点

    def test_not_disabled_not_busy(self):
        d, r = update_button_states(is_disabled=False, is_busy=False)
        self.assertTrue(d)   # 彻底关闭 -> 可点
        self.assertFalse(r)  # 恢复 -> 变灰

    def test_disabled_busy(self):
        d, r = update_button_states(is_disabled=True, is_busy=True)
        self.assertFalse(d)  # 操作中两按钮均禁用
        self.assertFalse(r)

    def test_not_disabled_busy(self):
        d, r = update_button_states(is_disabled=False, is_busy=True)
        self.assertFalse(d)  # 操作中两按钮均禁用
        self.assertFalse(r)


class TestBusyTransitionSequence(unittest.TestCase):
    """模拟 SetBusy(true) -> 操作 -> RefreshStatus -> SetBusy(false) 闭环。"""

    def test_disable_operation_loop(self):
        no_auto = 0  # 初始未禁用
        disabled = is_windows_update_disabled(policy_engine_auto_update_value_text(no_auto))

        busy = True  # SetBusy(true)
        d, r = update_button_states(disabled, busy)
        self.assertFalse(d)
        self.assertFalse(r)  # 两按钮禁用

        no_auto = 1  # 执行禁用 -> 写入 NoAutoUpdate=1 -> RefreshStatus 重算
        disabled = is_windows_update_disabled(policy_engine_auto_update_value_text(no_auto))
        d, r = update_button_states(disabled, busy)  # RefreshStatus.finally 仍 _isBusy=true
        self.assertFalse(d)
        self.assertFalse(r)  # 仍禁用（操作中）

        busy = False  # finally SetBusy(false)
        d, r = update_button_states(disabled, busy)
        self.assertFalse(d)  # 彻底关闭 -> 变灰
        self.assertTrue(r)   # 恢复 -> 变亮可点

    def test_restore_operation_loop(self):
        no_auto = 1  # 初始已禁用
        disabled = is_windows_update_disabled(policy_engine_auto_update_value_text(no_auto))

        busy = True  # SetBusy(true)
        d, r = update_button_states(disabled, busy)
        self.assertFalse(d)
        self.assertFalse(r)

        no_auto = 0  # 恢复 -> NoAutoUpdate 还原为 0
        disabled = is_windows_update_disabled(policy_engine_auto_update_value_text(no_auto))
        d, r = update_button_states(disabled, busy)
        self.assertFalse(d)
        self.assertFalse(r)

        busy = False  # SetBusy(false)
        d, r = update_button_states(disabled, busy)
        self.assertTrue(d)   # 彻底关闭 -> 变亮可点
        self.assertFalse(r)  # 恢复 -> 变灰


if __name__ == "__main__":
    unittest.main(verbosity=2)
