r"""
test_policy_equiv.py

对 policy_equiv.py 中复刻的核心策略逻辑做单元测试（Python 3.13 标准库 unittest）。
运行方式（在 winupdate-disabler 目录下）：
  C:\Users\Administrator\.workbuddy\binaries\python\versions\3.13.12\python.exe -m unittest discover -s qa -v
"""

import base64
import json
import os
import sys
import unittest

# 让 discover 能找到同目录的 policy_equiv
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import policy_equiv as pe  # noqa: E402


class TestOsVersionBranch(unittest.TestCase):
    """OS 版本分支判断（对应 OsHelper.GetWindowsVersion / IsWindows*）。"""

    def test_win7(self):
        self.assertEqual(pe.get_windows_version(6, 1, 0), pe.WIN_7)
        self.assertFalse(pe.is_windows_10_or_11(pe.get_windows_version(6, 1, 0)))
        self.assertTrue(pe.is_windows_7_or_8_1(pe.get_windows_version(6, 1, 0)))

    def test_win8(self):
        self.assertEqual(pe.get_windows_version(6, 2, 0), pe.WIN_8)
        self.assertTrue(pe.is_windows_7_or_8_1(pe.get_windows_version(6, 2, 0)))

    def test_win81(self):
        self.assertEqual(pe.get_windows_version(6, 3, 0), pe.WIN_8_1)
        self.assertTrue(pe.is_windows_7_or_8_1(pe.get_windows_version(6, 3, 0)))

    def test_win10_below_22000(self):
        v = pe.get_windows_version(10, 0, 19045)
        self.assertEqual(v, pe.WIN_10)
        self.assertTrue(pe.is_windows_10_or_11(v))

    def test_win11_at_22000(self):
        # build == 22000 边界，应判为 Windows 11
        self.assertEqual(pe.get_windows_version(10, 0, 22000), pe.WIN_11)

    def test_win11_above(self):
        v = pe.get_windows_version(10, 0, 22631)
        self.assertEqual(v, pe.WIN_11)
        self.assertTrue(pe.is_windows_10_or_11(v))

    def test_win10_build_21999(self):
        # 临界：< 22000 仍为 Windows 10
        self.assertEqual(pe.get_windows_version(10, 0, 21999), pe.WIN_10)

    def test_unknown_vista(self):
        self.assertEqual(pe.get_windows_version(6, 0, 0), pe.WIN_UNKNOWN)
        self.assertFalse(pe.is_windows_10_or_11(pe.WIN_UNKNOWN))
        self.assertFalse(pe.is_windows_7_or_8_1(pe.WIN_UNKNOWN))


class TestStatusIndicatorsWin10_11(unittest.TestCase):
    """Win10/11 下 6 个状态开关映射（对应 PolicyEngine.RefreshStatus）。"""

    def test_clean_state(self):
        ind = pe.compute_status_indicators(
            no_auto_update=0, no_auto_reboot=0, exclude_wu_drivers=0,
            set_disable_ux_wu_access=0, search_order_config=1,
            uso_svc_disabled=False, wuauserv_disabled=False,
            task_enabled=True, os_family="win10_11",
        )
        self.assertEqual(ind, {
            "auto_update": False, "driver_update": False,
            "update_notification": False, "auto_restart": False,
            "manual_access": False, "extra_block": False,
        })

    def test_fully_disabled(self):
        ind = pe.compute_status_indicators(
            no_auto_update=1, no_auto_reboot=1, exclude_wu_drivers=1,
            set_disable_ux_wu_access=1, search_order_config=1,
            uso_svc_disabled=True, wuauserv_disabled=True,
            task_enabled=False, os_family="win10_11",
        )
        expected = {
            "auto_update": True, "driver_update": True,
            "update_notification": True, "auto_restart": True,
            "manual_access": True, "extra_block": True,
        }
        self.assertEqual(ind, expected)

    def test_notification_via_task_only(self):
        # UsoSvc 仍在运行，但计划任务被禁用 -> 更新通知应判为已关闭（OR 逻辑）
        ind = pe.compute_status_indicators(
            no_auto_update=1, no_auto_reboot=1, exclude_wu_drivers=1,
            set_disable_ux_wu_access=1, search_order_config=1,
            uso_svc_disabled=False, wuauserv_disabled=False,
            task_enabled=False, os_family="win10_11",
        )
        self.assertTrue(ind["update_notification"])
        self.assertTrue(ind["auto_update"])
        self.assertTrue(ind["extra_block"])

    def test_auto_update_only_no_notification(self):
        # Win10/11：仅 NoAutoUpdate=1，UsoSvc 运行且任务启用 -> 通知不算关闭
        ind = pe.compute_status_indicators(
            no_auto_update=1, no_auto_reboot=0, exclude_wu_drivers=0,
            set_disable_ux_wu_access=0, search_order_config=1,
            uso_svc_disabled=False, wuauserv_disabled=False,
            task_enabled=True, os_family="win10_11",
        )
        self.assertTrue(ind["auto_update"])
        self.assertFalse(ind["update_notification"])
        self.assertFalse(ind["extra_block"])

    def test_driver_update_uses_exclude_flag(self):
        # Win10/11 驱动更新由 ExcludeWUDrivers 决定（与 SearchOrderConfig 无关）
        ind = pe.compute_status_indicators(
            no_auto_update=0, no_auto_reboot=0, exclude_wu_drivers=1,
            set_disable_ux_wu_access=0, search_order_config=0,
            uso_svc_disabled=False, wuauserv_disabled=False,
            task_enabled=True, os_family="win10_11",
        )
        self.assertTrue(ind["driver_update"])
        # SearchOrderConfig=0 在 Win10/11 不应影响 driver_update
        self.assertTrue(ind["driver_update"])  # 强调由 exclude 决定


class TestStatusIndicatorsWin7_8_1(unittest.TestCase):
    """Win7/8/8.1 下 6 个状态开关映射（旧分支：SearchOrderConfig + wuauserv）。"""

    def test_fully_disabled(self):
        ind = pe.compute_status_indicators(
            no_auto_update=1, no_auto_reboot=1, exclude_wu_drivers=0,
            set_disable_ux_wu_access=1, search_order_config=0,
            uso_svc_disabled=False, wuauserv_disabled=True,
            task_enabled=True, os_family="win7_8_1",
        )
        expected = {
            "auto_update": True, "driver_update": True,
            "update_notification": True, "auto_restart": True,
            "manual_access": True, "extra_block": True,
        }
        self.assertEqual(ind, expected)

    def test_clean_state(self):
        ind = pe.compute_status_indicators(
            no_auto_update=0, no_auto_reboot=0, exclude_wu_drivers=1,
            set_disable_ux_wu_access=0, search_order_config=1,
            uso_svc_disabled=False, wuauserv_disabled=False,
            task_enabled=True, os_family="win7_8_1",
        )
        self.assertEqual(ind, {
            "auto_update": False, "driver_update": False,
            "update_notification": False, "auto_restart": False,
            "manual_access": False, "extra_block": False,
        })

    def test_driver_update_uses_search_order(self):
        # Win7/8.1 驱动更新由 SearchOrderConfig==0 决定
        ind = pe.compute_status_indicators(
            no_auto_update=0, no_auto_reboot=0, exclude_wu_drivers=1,
            set_disable_ux_wu_access=0, search_order_config=0,
            uso_svc_disabled=False, wuauserv_disabled=False,
            task_enabled=True, os_family="win7_8_1",
        )
        self.assertTrue(ind["driver_update"])

    def test_notification_via_wuauserv(self):
        # Win7/8.1：wuauserv 禁用即通知关闭（与 UsoSvc/任务无关）
        ind = pe.compute_status_indicators(
            no_auto_update=0, no_auto_reboot=0, exclude_wu_drivers=0,
            set_disable_ux_wu_access=0, search_order_config=1,
            uso_svc_disabled=True, wuauserv_disabled=True,
            task_enabled=False, os_family="win7_8_1",
        )
        self.assertTrue(ind["update_notification"])


class TestAdminElevation(unittest.TestCase):
    """提权判定等价逻辑（对应 AdminHelper.IsCurrentProcessAdmin）。"""

    def test_admin_true(self):
        self.assertTrue(pe.is_admin(True))

    def test_admin_false(self):
        self.assertFalse(pe.is_admin(False))

    def test_admin_non_bool(self):
        # 任意真值都应归一为 bool
        self.assertTrue(pe.is_admin(1))
        self.assertFalse(pe.is_admin(0))


class TestBackupRoundtrip(unittest.TestCase):
    """备份 JSON 往返一致性 + 类型归一化（对应 BackupService）。"""

    def _sample_backup(self):
        binary_bytes = b"\x01\x02\x03\xff"
        binary_b64 = base64.b64encode(binary_bytes).decode("ascii")
        return {
            "SchemaVersion": 1,
            "CreatedAt": "2026-08-17T12:00:00.0000000Z",
            "OsVersion": "Windows 11",
            "RegistryKeys": [
                {
                    "Path": r"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
                    "Exists": True,
                    "Values": [
                        {"Name": "NoAutoUpdate", "Kind": "DWord", "Value": 1},
                        {"Name": "AUOptions", "Kind": "DWord", "Value": 2},
                        {"Name": "NoAutoRebootWithLoggedOnUsers", "Kind": "DWord", "Value": 1},
                        {"Name": "Blob", "Kind": "Binary", "Value": binary_b64},
                        {"Name": "Multi", "Kind": "MultiString", "Value": ["a", "b", "c"]},
                        {"Name": "Desc", "Kind": "String", "Value": "hello"},
                    ],
                },
                {
                    "Path": r"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
                    "Exists": True,
                    "Values": [
                        {"Name": "ExcludeWUDriversInQualityUpdate", "Kind": "DWord", "Value": 1},
                        {"Name": "SetDisableUXWUAccess", "Kind": "DWord", "Value": 1},
                    ],
                },
                {
                    "Path": r"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching",
                    "Exists": True,
                    "Values": [
                        {"Name": "SearchOrderConfig", "Kind": "DWord", "Value": 0},
                    ],
                },
            ],
            "Services": [
                {"Name": "wuauserv", "StartType": 2, "Status": "Running"},
                {"Name": "UsoSvc", "StartType": 3, "Status": "Stopped"},
                {"Name": "WaaSMedicSvc", "StartType": -1, "Status": "NotFound"},
            ],
            "Tasks": [
                {"Path": r"\Microsoft\Windows\WindowsUpdate\Scheduled Start", "Enabled": False},
            ],
        }

    def test_roundtrip_equality(self):
        backup = self._sample_backup()
        restored = pe.roundtrip_backup(backup)
        self.assertEqual(restored, backup)

    def test_roundtrip_preserves_types(self):
        backup = self._sample_backup()
        restored = pe.roundtrip_backup(backup)
        # DWord 仍是 int
        self.assertIsInstance(restored["RegistryKeys"][0]["Values"][0]["Value"], int)
        # MultiString 仍是 list
        self.assertIsInstance(restored["RegistryKeys"][0]["Values"][4]["Value"], list)
        self.assertEqual(restored["RegistryKeys"][0]["Values"][4]["Value"], ["a", "b", "c"])
        # Binary 仍以 base64 字符串存在（Newtonsoft 序列化 byte[] 为 base64）
        self.assertIsInstance(restored["RegistryKeys"][0]["Values"][3]["Value"], str)
        self.assertEqual(
            base64.b64decode(restored["RegistryKeys"][0]["Values"][3]["Value"]),
            b"\x01\x02\x03\xff",
        )

    def test_normalize_binary_roundtrip(self):
        raw = b"\xde\xad\xbe\xef"
        b64 = base64.b64encode(raw).decode("ascii")
        normalized = pe.normalize_registry_value(b64, pe.Kind.BINARY)
        self.assertEqual(normalized, raw)

    def test_normalize_dword_qword(self):
        self.assertEqual(pe.normalize_registry_value(7, pe.Kind.DWORD), 7)
        self.assertEqual(pe.normalize_registry_value(2 ** 40, pe.Kind.QWORD), 2 ** 40)

    def test_normalize_multistring(self):
        self.assertEqual(
            pe.normalize_registry_value(["x", "y"], pe.Kind.MULTI_STRING), ["x", "y"]
        )

    def test_normalize_string_none(self):
        self.assertEqual(pe.normalize_registry_value(None, pe.Kind.STRING), "")

    def test_service_starttype_preserved(self):
        backup = self._sample_backup()
        restored = pe.roundtrip_backup(backup)
        svcs = {s["Name"]: s for s in restored["Services"]}
        self.assertEqual(svcs["wuauserv"]["StartType"], 2)
        self.assertEqual(svcs["UsoSvc"]["StartType"], 3)
        # NotFound 服务 StartType == -1，恢复时应跳过
        self.assertEqual(svcs["WaaSMedicSvc"]["StartType"], -1)

    def test_missing_key_flag(self):
        backup = self._sample_backup()
        restored = pe.roundtrip_backup(backup)
        self.assertTrue(all(k["Exists"] for k in restored["RegistryKeys"]))


class TestRestoreCompleteness(unittest.TestCase):
    """验证"正确"的还原逻辑：必须删除禁用操作新增、但备份里没有的值。

    这对应源码 RestoreRegistryKey 的缺口——C# 实现在当前键已存在时只回写备份值、
    不删除新增值；本测试用参考实现证明"正确算法"应当删除它们。
    """

    def test_removes_added_values_when_key_existed(self):
        # 备份时 AU 键已存在，且仅有 AUOptions=4（无 NoAutoUpdate/NoAutoReboot）
        backup_key = {
            "Exists": True,
            "Values": [
                {"Name": "AUOptions", "Kind": "DWord", "Value": 4},
            ],
        }
        # 禁用操作后当前键被写入了 NoAutoUpdate=1 与 NoAutoReboot=1
        current_values = {
            "AUOptions": 4,
            "NoAutoUpdate": 1,
            "NoAutoRebootWithLoggedOnUsers": 1,
        }
        result = pe.restore_registry_key_reference(current_values, backup_key)
        # 正确的还原：只保留备份值，禁用新增的被删除
        self.assertEqual(result, {"AUOptions": 4})
        self.assertNotIn("NoAutoUpdate", result)
        self.assertNotIn("NoAutoRebootWithLoggedOnUsers", result)

    def test_deletes_key_when_backup_absent(self):
        backup_key = {"Exists": False, "Values": []}
        current_values = {"NoAutoUpdate": 1}
        result = pe.restore_registry_key_reference(current_values, backup_key)
        self.assertEqual(result, {})


class TestServiceDisableOrder(unittest.TestCase):
    """禁用顺序：Win10/11 下 WaaSMedicSvc 必须先于 wuauserv（规避自愈恢复）。"""

    def test_win10_11_order(self):
        order = pe.service_disable_order(is_win10_11=True)
        self.assertEqual(order, ["WaaSMedicSvc", "UsoSvc", "wuauserv"])

    def test_waasmedic_before_wuauserv(self):
        order = pe.service_disable_order(is_win10_11=True)
        self.assertLess(order.index("WaaSMedicSvc"), order.index("wuauserv"))

    def test_win7_8_1_only_wuauserv(self):
        order = pe.service_disable_order(is_win10_11=False)
        self.assertEqual(order, ["wuauserv"])


class TestFallbackChain(unittest.TestCase):
    """Error 5 fallback 链优先级（sc -> Win32 API -> 注册表 -> SYSTEM 任务）。"""

    def test_all_steps_present_win10_11(self):
        steps = pe.fallback_chain(allow_system_task=True)
        self.assertEqual(steps, [
            "sc.exe config/stop",
            "Win32 ChangeServiceConfig",
            "Registry Start=4",
            "SYSTEM scheduled task",
        ])

    def test_system_task_skipped_when_disallowed(self):
        steps = pe.fallback_chain(allow_system_task=False)
        self.assertEqual(steps, [
            "sc.exe config/stop",
            "Win32 ChangeServiceConfig",
            "Registry Start=4",
        ])

    def test_registry_start_4_means_disabled(self):
        self.assertTrue(pe.is_service_start_disabled_from_registry(4))
        self.assertFalse(pe.is_service_start_disabled_from_registry(2))
        self.assertFalse(pe.is_service_start_disabled_from_registry(3))
        self.assertFalse(pe.is_service_start_disabled_from_registry(-1))


class TestStartModeMappings(unittest.TestCase):
    """服务启动类型 -> (sc.exe 文本 / 注册表值 / Win32 启动类型) 映射一致性。

    对应 RestoreService 原有映射与 SetServiceStartTypeWithFallbacks 的映射表，
    确保恢复路径使用与"禁用"完全相同的字符串/数值映射，不会因重构而漂移。
    """

    def test_sc_start_mode_text(self):
        expected = {
            "Boot": "boot",
            "System": "system",
            "Automatic": "auto",
            "Manual": "demand",
            "Disabled": "disabled",
        }
        for mode, text in expected.items():
            self.assertEqual(pe.map_start_mode_to_sc(mode), text)

    def test_registry_start_value(self):
        # 枚举值即注册表 Start 值
        expected = {
            "Boot": 0,
            "System": 1,
            "Automatic": 2,
            "Manual": 3,
            "Disabled": 4,
        }
        for mode, value in expected.items():
            self.assertEqual(pe.map_start_mode_to_registry(mode), value)

    def test_win32_start_type(self):
        expected = {
            "Boot": 0,
            "System": 1,
            "Automatic": 2,
            "Manual": 3,
            "Disabled": 4,
        }
        for mode, value in expected.items():
            self.assertEqual(pe.map_start_mode_to_win32(mode), value)

    def test_registry_and_win32_and_enum_consistent(self):
        # 三种表示必须完全对齐（这是 SetServiceStartTypeWithFallbacks 正确性的前提）
        for mode in pe.SERVICE_START_MODE:
            self.assertEqual(
                pe.map_start_mode_to_registry(mode),
                pe.map_start_mode_to_win32(mode),
            )

    def test_sc_text_matches_restore_original(self):
        # 复刻 RestoreService 重构前的 if/else 映射，确保行为不变
        def original(start_type_int):
            if start_type_int == 0:
                return "boot"
            if start_type_int == 1:
                return "system"
            if start_type_int == 2:
                return "auto"
            if start_type_int == 3:
                return "demand"
            if start_type_int == 4:
                return "disabled"
            raise ValueError(start_type_int)

        for mode, value in pe.SERVICE_START_MODE.items():
            self.assertEqual(pe.map_start_mode_to_sc(mode), original(value))


class TestRestoreServiceStartTypeChain(unittest.TestCase):
    """恢复路径复用的 fallback 链优先级（与禁用等价）。"""

    def test_chain_equals_disable_chain(self):
        # RestoreService 现委托 SetServiceStartTypeWithFallbacks，链优先级须与禁用一致
        self.assertEqual(
            pe.restore_service_start_type_chain(allow_system_task=True),
            pe.fallback_chain(allow_system_task=True),
        )

    def test_chain_skips_system_task_when_disallowed(self):
        self.assertEqual(
            pe.restore_service_start_type_chain(allow_system_task=False),
            ["sc.exe config/stop", "Win32 ChangeServiceConfig", "Registry Start=4"],
        )

    def test_is_service_start_mode_from_registry(self):
        # Manual(3) / Automatic(2) / System(1) / Boot(0) 的注册表判定
        self.assertTrue(pe.is_service_start_mode_from_registry(3, "Manual"))
        self.assertFalse(pe.is_service_start_mode_from_registry(4, "Manual"))
        self.assertTrue(pe.is_service_start_mode_from_registry(2, "Automatic"))
        self.assertTrue(pe.is_service_start_mode_from_registry(1, "System"))
        self.assertTrue(pe.is_service_start_mode_from_registry(0, "Boot"))
        self.assertFalse(pe.is_service_start_mode_from_registry(-1, "Disabled"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
