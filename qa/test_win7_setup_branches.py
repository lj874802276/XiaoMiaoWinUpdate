# -*- coding: utf-8 -*-
"""
等价测试：模拟 setup.nsi 中 `Function .onInit` 的 .NET Framework 4.8 检测状态机，
覆盖「标准版」与「Win7 专用版（/DINCLUDE_DOTNET）」两条编译分支的全部走向。

本测试用纯 Python 复刻 setup.nsi（L128-200）的 .onInit 逻辑，不依赖 NSIS 编译器
（沙箱无 makensis）。重点验证工程师本轮改造后的双包逻辑：

  1. 已装 .NET 4.8                       -> 直接 dotNetReady（不触发任何安装）
  2. 未装 + 标准版 + 同目录有外置 ndp48   -> runOfflineInstaller（用 $EXEDIR 路径静默装）
  3. 未装 + 标准版 + 同目录无 ndp48       -> dotNetNeedManual（开下载页 + Abort）
  4. 未装 + Win7 版（包内含 ndp48）       -> 释放 $PLUGINSDIR 并用包内路径静默装
                                             （绝不使用 $EXEDIR）
  5. 未装 + 装 .NET 失败（Release 仍<528040）-> Abort（绝不带病进入主安装流程）

NSIS 关键语义映射：
  * $R0 初始 "0"；ReadRegDWORD 失败保持 0；${If} $R0 >= ${DOTNET48_RELEASE}
    为带符号数值比较（DOTNET48_RELEASE = 528040，微软官方 .NET 4.8 常量）。
  * Win7 分支：$R1 = "$PLUGINSDIR\\ndp48-x86-x64-allos-enu.exe"，ExecWait 用 $R1；
    标准分支：IfFileExists "$EXEDIR\\ndp48..." 决定 runOfflineInstaller / dotNetNeedManual，
    全程不使用 $R1。
  * 两轮分支在 ExecWait 后都会「二次检测」Release；仅当 >= 528040 才 Goto dotNetReady，
    否则 Abort（即本轮改造已修复旧版「无条件 Goto」的已知限制）。

运行方式：
    python3 -m unittest qa.test_win7_setup_branches -v
    python3 qa/test_win7_setup_branches.py
"""

import unittest

# —— 与 setup.nsi 保持一致的常量 ——
DOTNET48_RELEASE = 528040                       # L123: !define DOTNET48_RELEASE 528040
NORMAL_INSTALLER_NAME = "XiaoMiaoWinUpdate_Setup.exe"        # L87  OutFile（标准版）
WIN7_INSTALLER_NAME = "XiaoMiaoWinUpdate_Setup_Win7.exe"     # L84  OutFile（Win7 版）
NDP48_FILE = "ndp48-x86-x64-allos-enu.exe"
EXEDIR = "$EXEDIR"          # 标准版外置离线包所在目录
PLUGINSDIR = "$PLUGINSDIR"  # Win7 版释放内置离线包到的临时目录


def outfile_for(include_dotnet):
    """复刻 setup.nsi L82-88 的 OutFile 编译开关映射（每包仅一个 OutFile）。"""
    return WIN7_INSTALLER_NAME if include_dotnet else NORMAL_INSTALLER_NAME


class OnInitResult:
    """对 .onInit 一次执行的走向建模，便于断言。"""

    def __init__(self, branch, final, installer_path=None,
                 opened_download=False, dotnet_installed_after=False):
        # branch: 进入的分支标签
        #   'already_installed' | 'runOfflineInstaller' | 'dotNetNeedManual' | 'win7_internal'
        # final: 最终处置 'dotNetReady' | 'Abort'
        self.branch = branch
        self.final = final
        self.installer_path = installer_path   # 传给 ExecWait 的路径；None 表示未执行安装
        self.opened_download = opened_download  # 是否打开了官方下载页
        self.dotnet_installed_after = dotnet_installed_after

    def __repr__(self):
        return (
            "OnInitResult(branch=%r, final=%r, installer_path=%r, "
            "opened_download=%r, dotnet_installed_after=%r)"
            % (self.branch, self.final, self.installer_path,
               self.opened_download, self.dotnet_installed_after)
        )


def simulate_oninit(include_dotnet, initial_release, exedir_has_ndp48,
                    install_succeeds):
    """复刻 setup.nsi Function .onInit（L128-200）。

    :param include_dotnet: 是否定义 INCLUDE_DOTNET（Win7 专用版）
    :param initial_release: 安装前系统 HKLM...\\v4\\Full\\Release 的 DWORD；
                            0 表示未安装 / 键缺失（等价于 ReadRegDWORD 失败）
    :param exedir_has_ndp48: 标准版下，安装包同目录 $EXEDIR 是否存在外置离线包
    :param install_succeeds: 执行离线安装包后，系统是否成功装上 .NET 4.8
                            （成功则二次检测时 Release >= 528040）
    :return: OnInitResult
    """
    # —— 复刻 L130: StrCpy $R0 "0" 后的读取结果（负数视为缺失 -> 0）——
    release = initial_release if initial_release >= 0 else 0

    # —— 复刻 L139-142: 已装 .NET 4.8 -> 直接 dotNetReady ——
    if release >= DOTNET48_RELEASE:
        return OnInitResult(
            branch="already_installed", final="dotNetReady",
            installer_path=None, opened_download=False,
            dotnet_installed_after=True,
        )

    # —— 未安装 .NET 4.8，进入条件分支 ——
    if include_dotnet:
        # 复刻 L151-157: Win7 版 -> SetOutPath $PLUGINSDIR + File + $R1 + ExecWait $R1
        installer_path = PLUGINSDIR + "\\" + NDP48_FILE
        if install_succeeds:
            release = DOTNET48_RELEASE  # 安装成功，二次检测通过
        # 复刻 L161-169: 二次检测
        if release >= DOTNET48_RELEASE:
            return OnInitResult(
                branch="win7_internal", final="dotNetReady",
                installer_path=installer_path, opened_download=False,
                dotnet_installed_after=True,
            )
        # 复刻 L167-169: 安装失败 -> Abort
        return OnInitResult(
            branch="win7_internal", final="Abort",
            installer_path=installer_path, opened_download=False,
            dotnet_installed_after=False,
        )
    else:
        # 复刻 L174: IfFileExists "$EXEDIR\\ndp48..." runOfflineInstaller dotNetNeedManual
        if exedir_has_ndp48:
            # 复刻 L176-190: runOfflineInstaller（使用 $EXEDIR 路径）
            installer_path = EXEDIR + "\\" + NDP48_FILE
            if install_succeeds:
                release = DOTNET48_RELEASE
            if release >= DOTNET48_RELEASE:
                return OnInitResult(
                    branch="runOfflineInstaller", final="dotNetReady",
                    installer_path=installer_path, opened_download=False,
                    dotnet_installed_after=True,
                )
            return OnInitResult(
                branch="runOfflineInstaller", final="Abort",
                installer_path=installer_path, opened_download=False,
                dotnet_installed_after=False,
            )
        else:
            # 复刻 L192-196: dotNetNeedManual -> 开下载页 + Abort
            return OnInitResult(
                branch="dotNetNeedManual", final="Abort",
                installer_path=None, opened_download=True,
                dotnet_installed_after=False,
            )


class TestWin7SetupBranches(unittest.TestCase):
    """复刻 .onInit 状态机，验证双包分支走向与关键不变量。"""

    # ---------- 任务要求的 5 个核心用例 ----------

    def test_case1_already_installed_goes_dotnet_ready_without_install(self):
        """已装 .NET 4.8 -> 直接 dotNetReady，绝不执行任何安装。"""
        for rel in (DOTNET48_RELEASE, DOTNET48_RELEASE + 1, 533320):
            with self.subTest(initial_release=rel):
                r = simulate_oninit(
                    include_dotnet=False, initial_release=rel,
                    exedir_has_ndp48=True, install_succeeds=True,
                )
                self.assertEqual(r.branch, "already_installed")
                self.assertEqual(r.final, "dotNetReady")
                self.assertIsNone(r.installer_path, "已装时不应触发离线安装")
                self.assertFalse(r.opened_download)
                self.assertTrue(r.dotnet_installed_after)

    def test_case2_standard_with_external_ndp48_runs_offline_installer(self):
        """未装 + 标准版 + 同目录有外置 ndp48 -> runOfflineInstaller（用 $EXEDIR 静默装）。"""
        r = simulate_oninit(
            include_dotnet=False, initial_release=0,
            exedir_has_ndp48=True, install_succeeds=True,
        )
        self.assertEqual(r.branch, "runOfflineInstaller")
        self.assertEqual(r.final, "dotNetReady", "外置安装成功应进入主安装流程")
        # 关键：必须使用 $EXEDIR 外置路径，而非 $PLUGINSDIR 包内路径
        self.assertEqual(r.installer_path, EXEDIR + "\\" + NDP48_FILE)
        self.assertNotEqual(r.installer_path, PLUGINSDIR + "\\" + NDP48_FILE)
        self.assertFalse(r.opened_download)
        self.assertTrue(r.dotnet_installed_after)

    def test_case3_standard_without_external_ndp48_opens_download_and_aborts(self):
        """未装 + 标准版 + 同目录无 ndp48 -> dotNetNeedManual（开下载页 + Abort）。"""
        r = simulate_oninit(
            include_dotnet=False, initial_release=0,
            exedir_has_ndp48=False, install_succeeds=True,
        )
        self.assertEqual(r.branch, "dotNetNeedManual")
        self.assertEqual(r.final, "Abort")
        self.assertIsNone(r.installer_path, "无外置包时不应执行任何安装")
        self.assertTrue(r.opened_download, "应打开官方下载页")
        self.assertFalse(r.dotnet_installed_after)

    def test_case4_win7_internal_installer_uses_pluginsdir_not_exedir(self):
        """未装 + Win7 版（包内含 ndp48）-> 用 $PLUGINSDIR 包内路径静默装，绝不用 $EXEDIR。"""
        r = simulate_oninit(
            include_dotnet=True, initial_release=0,
            exedir_has_ndp48=False, install_succeeds=True,
        )
        self.assertEqual(r.branch, "win7_internal")
        self.assertEqual(r.final, "dotNetReady")
        # 关键：Win7 版必须使用包内 $PLUGINSDIR 路径
        self.assertEqual(r.installer_path, PLUGINSDIR + "\\" + NDP48_FILE)
        self.assertNotEqual(
            r.installer_path, EXEDIR + "\\" + NDP48_FILE,
            "Win7 版误用了 $EXEDIR 外置路径（应为包内 $PLUGINSDIR）",
        )
        self.assertFalse(r.opened_download)
        self.assertTrue(r.dotnet_installed_after)

    def test_case5_install_failure_aborts_in_both_branches(self):
        """未装 + 装 .NET 失败（Release 仍 < 528040）-> Abort，绝不进入主安装流程。"""
        for include_dotnet, branch, exedir in (
            (False, "runOfflineInstaller", True),
            (True, "win7_internal", False),
        ):
            with self.subTest(include_dotnet=include_dotnet, branch=branch):
                r = simulate_oninit(
                    include_dotnet=include_dotnet, initial_release=0,
                    exedir_has_ndp48=exedir, install_succeeds=False,
                )
                self.assertEqual(r.branch, branch)
                self.assertEqual(r.final, "Abort", "安装失败必须 Abort")
                self.assertFalse(
                    r.dotnet_installed_after,
                    "安装失败后不应误判为已具备 .NET 4.8",
                )
                # 安装确实被尝试过（路径已确定），只是最终失败
                self.assertIsNotNone(r.installer_path)

    # ---------- 额外不变量 / 健壮性用例 ----------

    def test_standard_branch_never_uses_pluginsdir_or_r1(self):
        """标准版（未定义 INCLUDE_DOTNET）绝不应引用 $PLUGINSDIR / $R1 路径。"""
        # 有外置包
        r1 = simulate_oninit(
            include_dotnet=False, initial_release=0,
            exedir_has_ndp48=True, install_succeeds=True,
        )
        self.assertEqual(r1.installer_path, EXEDIR + "\\" + NDP48_FILE)
        # 无外置包
        r2 = simulate_oninit(
            include_dotnet=False, initial_release=0,
            exedir_has_ndp48=False, install_succeeds=True,
        )
        self.assertIsNone(r2.installer_path)
        # 两条标准分支路径都不应是 $PLUGINSDIR（即 $R1 未被误用）
        for r in (r1, r2):
            self.assertNotEqual(r.installer_path, PLUGINSDIR + "\\" + NDP48_FILE)

    def test_outfile_switch_maps_to_correct_package_name(self):
        """OutFile 由 !ifdef INCLUDE_DOTNET 控制，每包仅一个文件名。"""
        self.assertEqual(outfile_for(False), NORMAL_INSTALLER_NAME)
        self.assertEqual(outfile_for(True), WIN7_INSTALLER_NAME)
        self.assertNotEqual(outfile_for(False), outfile_for(True))

    def test_missing_registry_key_treated_as_not_installed(self):
        """键/值缺失（ReadRegDWORD 失败，$R0 保持 0）应判为未装并走安装分支。"""
        r = simulate_oninit(
            include_dotnet=False, initial_release=0,
            exedir_has_ndp48=True, install_succeeds=True,
        )
        self.assertEqual(r.branch, "runOfflineInstaller")
        self.assertEqual(r.final, "dotNetReady")


if __name__ == "__main__":
    unittest.main(verbosity=2)
