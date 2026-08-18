# -*- coding: utf-8 -*-
"""
等价测试：模拟 setup.nsi 中 .NET Framework 4.8 检测逻辑的真值表。

setup.nsi 的检测逻辑（节选自 .onInit）：
    SetRegView 64
    ReadRegDWORD $R0 HKLM "SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full" "Release"
    ${If} $R0 >= ${DOTNET48_RELEASE}   ; DOTNET48_RELEASE = 528040
        Goto dotNetReady               ; 已安装 .NET 4.8
    ${EndIf}

微软官方 .NET Framework 4.8 的 Release 常量为 528040；任何 >= 528040 的
Release 值都代表「已安装 .NET Framework 4.8 运行时」（含 4.8 后续累积更新、
Win10 1903+/2004+ 的 528049/528372，以及更高的 4.8.1 等）。

本测试用纯 Python 复刻 `release >= 528040` 判定，并用微软官方发布说明中的
典型 Release 值构建真值表，验证阈值边界正确。
"""

import unittest

# 微软官方 .NET Framework 4.8 的 Release 阈值（setup.nsi 中 DOTNET48_RELEASE）
DOTNET48_RELEASE = 528040


def is_dotnet48_installed(release):
    """复刻 setup.nsi 的判定：Release >= 528040 即视为已安装 .NET 4.8。

    :param release: 注册表 HKLM\\...\\v4\\Full\\Release 的 DWORD 值（int）
    :return: True 表示系统已安装 .NET Framework 4.8 运行时
    """
    return release >= DOTNET48_RELEASE


class TestDotNet48DetectionEquivalence(unittest.TestCase):
    """真值表：覆盖典型 Release 值与阈值边界。"""

    # (Release 值, 对应版本说明, 是否应判定为已装 4.8)
    TRUTH_TABLE = [
        (0, "未安装 .NET 4.x（ReadRegDWORD 失败/缺键）", False),
        (378389, ".NET Framework 4.5", False),
        (394802, ".NET Framework 4.6.1", False),
        (460798, ".NET Framework 4.7", False),
        (461808, ".NET Framework 4.7.1", False),
        (461814, ".NET Framework 4.7.2", False),
        (528039, "4.8 阈值前一个整数（未达 4.8）", False),
        (DOTNET48_RELEASE, ".NET Framework 4.8（最小阈值）", True),
        (528041, ".NET Framework 4.8（阈值+1）", True),
        (528049, ".NET Framework 4.8 on Windows 10 1903+", True),
        (528372, ".NET Framework 4.8 on Windows 10 2004 / 11", True),
        (533320, ".NET Framework 4.8.1（高于 4.8）", True),
    ]

    def test_truth_table(self):
        for release, desc, expected in self.TRUTH_TABLE:
            with self.subTest(release=release, desc=desc):
                actual = is_dotnet48_installed(release)
                self.assertEqual(
                    actual,
                    expected,
                    msg=(
                        "Release=%d (%s) 判定错误：期望 %s，实际 %s"
                        % (release, desc, expected, actual)
                    ),
                )

    def test_threshold_boundary_monotonic(self):
        """边界单调性：528039 -> False，528040 -> True，且递增保持 True。"""
        self.assertFalse(is_dotnet48_installed(DOTNET48_RELEASE - 1))
        self.assertTrue(is_dotnet48_installed(DOTNET48_RELEASE))
        self.assertTrue(is_dotnet48_installed(DOTNET48_RELEASE + 1))

    def test_official_constant(self):
        """确认阈值等于微软官方 4.8 Release 常量 528040。"""
        self.assertEqual(DOTNET48_RELEASE, 528040)

    def test_regression_absent_key_returns_false(self):
        """模拟键/值缺失时 ReadRegDWORD 失败，初始化为 0 -> 判定未装。"""
        simulated_missing = 0
        self.assertFalse(is_dotnet48_installed(simulated_missing))


if __name__ == "__main__":
    unittest.main(verbosity=2)
