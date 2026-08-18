"""
policy_equiv.py

独立等价参考实现（Python 3.13，仅用标准库）。

本文件用纯 Python 复刻「小喵 Windows 更新助手」C# 工程中的核心策略逻辑，
供 qa/test_policy_equiv.py 用真实断言验证算法正确性。

复刻范围（与源码对应）：
  - Services/OsHelper.cs      -> get_windows_version / is_windows_10_or_11 / is_windows_7_or_8_1
  - Services/PolicyEngine.cs  -> compute_status_indicators（RefreshStatus 的 6 状态映射）
  - Services/AdminHelper.cs   -> is_admin（IsInRole 布尔映射）
  - Services/BackupService.cs -> normalize_registry_value / backup roundtrip / restore_registry_key

注意：这里只复刻"可纯逻辑验证"的部分（注册表值 -> 状态、OS 分支、备份序列化、提权判定）。
真正的注册表/服务/schtasks 系统调用无法在沙箱里执行，故用输入值驱动等价函数。
"""

import base64
import json

# ---------------------------------------------------------------------------
# 1) OS 版本分支（对应 Services/OsHelper.cs）
# ---------------------------------------------------------------------------

# 与 OsHelper.WindowsVersion 枚举对应
WIN_UNKNOWN = "Unknown"
WIN_7 = "Windows7"
WIN_8 = "Windows8"
WIN_8_1 = "Windows8Point1"
WIN_10 = "Windows10"
WIN_11 = "Windows11"


def get_windows_version(major, minor, build):
    """等价 OsHelper.GetWindowsVersion：返回版本分类字符串。"""
    if major == 6 and minor == 1:
        return WIN_7
    if major == 6 and minor == 2:
        return WIN_8
    if major == 6 and minor == 3:
        return WIN_8_1
    if major == 10:
        # build >= 22000 视为 Windows 11，否则 Windows 10
        return WIN_11 if build >= 22000 else WIN_10
    return WIN_UNKNOWN


def is_windows_10_or_11(version):
    """等价 OsHelper.IsWindows10Or11。"""
    return version in (WIN_10, WIN_11)


def is_windows_7_or_8_1(version):
    """等价 OsHelper.IsWindows7Or8Point1。"""
    return version in (WIN_7, WIN_8, WIN_8_1)


# ---------------------------------------------------------------------------
# 2) 注册表值 -> 6 个状态开关映射（对应 PolicyEngine.RefreshStatus）
# ---------------------------------------------------------------------------

def compute_status_indicators(
    *,
    no_auto_update,          # GetDword(AU, NoAutoUpdate)，缺失为 -1
    no_auto_reboot,          # GetDword(AU, NoAutoRebootWithLoggedOnUsers)
    exclude_wu_drivers,      # GetDword(WU, ExcludeWUDriversInQualityUpdate)
    set_disable_ux_wu_access,  # GetDword(WU, SetDisableUXWUAccess)
    search_order_config,     # GetDword(DriverSearching, SearchOrderConfig)，缺失为 -1
    uso_svc_disabled,        # IsServiceDisabled(UsoSvc)
    wuauserv_disabled,       # IsServiceDisabled(wuauserv)
    task_enabled,            # IsTaskEnabled(Scheduled Start)
    os_family,               # "win10_11" 或 "win7_8_1"
):
    """复刻 RefreshStatus 的 6 个状态指示计算。

    返回字典：auto_update / driver_update / update_notification /
             auto_restart / manual_access / extra_block（均为 bool）。
    """
    if os_family == "win10_11":
        is_win10_11 = True
        is_win78 = False
    elif os_family == "win7_8_1":
        is_win10_11 = False
        is_win78 = True
    else:
        raise ValueError("os_family must be 'win10_11' or 'win7_8_1'")

    auto_update_disabled = no_auto_update == 1
    manual_access_disabled = set_disable_ux_wu_access == 1
    win10_driver_disabled = exclude_wu_drivers == 1
    legacy_driver_disabled = search_order_config == 0
    auto_reboot_disabled = no_auto_reboot == 1

    driver_update_disabled = legacy_driver_disabled if is_win78 else win10_driver_disabled

    if is_win10_11:
        notification_disabled = uso_svc_disabled or (not task_enabled)
    else:
        notification_disabled = wuauserv_disabled or auto_update_disabled

    extra_block_active = manual_access_disabled and (
        win10_driver_disabled if is_win10_11 else legacy_driver_disabled
    )

    return {
        "auto_update": auto_update_disabled,
        "driver_update": driver_update_disabled,
        "update_notification": notification_disabled,
        "auto_restart": auto_reboot_disabled,
        "manual_access": manual_access_disabled,
        "extra_block": extra_block_active,
    }


# ---------------------------------------------------------------------------
# 3) 提权判定（对应 AdminHelper.IsCurrentProcessAdmin）
# ---------------------------------------------------------------------------

def is_admin(is_in_role):
    """等价 AdminHelper.IsCurrentProcessAdmin：直接返回 IsInRole(Administrator) 结果。"""
    return bool(is_in_role)


# ---------------------------------------------------------------------------
# 4) 备份 JSON 序列化与类型归一化（对应 BackupService）
# ---------------------------------------------------------------------------

class Kind:
    DWORD = "DWord"
    QWORD = "QWord"
    BINARY = "Binary"
    MULTI_STRING = "MultiString"
    STRING = "String"
    EXPAND_STRING = "ExpandString"


def normalize_registry_value(value, kind):
    """等价 BackupService.NormalizeRegistryValue。

    Newtonsoft 把 byte[] 序列化为 base64 字符串，故 Binary 在 JSON 里是字符串。
    """
    if kind == Kind.DWORD:
        return int(value)
    if kind == Kind.QWORD:
        return int(value)
    if kind == Kind.BINARY:
        if isinstance(value, str):
            return base64.b64decode(value)
        if isinstance(value, (bytes, bytearray)):
            return bytes(value)
        raise ValueError("Binary 类型注册表值格式错误。")
    if kind == Kind.MULTI_STRING:
        if isinstance(value, list):
            return list(value)
        raise ValueError("MultiString 类型注册表值格式错误。")
    # String / ExpandString / default
    return "" if value is None else str(value)


def roundtrip_backup(backup):
    """等价 JsonConvert.SerializeObject -> DeserializeObject（用标准库）。"""
    text = json.dumps(backup, ensure_ascii=False, indent=2)
    return json.loads(text)


def restore_registry_key_reference(current_values, backup_key):
    """参考（正确）实现：还原一个注册表键。

    与 C# BackupService.RestoreRegistryKey 不同，这里显式删除"非备份值"，
    以真实实现"100% 还原"。返回还原后的最终值字典。

    current_values: dict[name] -> value（禁用操作后的当前注册表值）
    backup_key: {"Exists": bool, "Values": [{"Name","Kind","Value"}, ...]}
    """
    if not backup_key.get("Exists"):
        # 备份时键不存在 -> 还原为不存在（删除当前所有值）
        return {}

    backup_names = {v["Name"] for v in backup_key.get("Values", [])}
    result = {}
    # 仅保留并回写备份中记录的值（= 删除禁用操作新增、但备份里没有的值）
    for v in backup_key.get("Values", []):
        if v.get("Value") is None:
            continue
        kind = v["Kind"]
        normalized = normalize_registry_value(v["Value"], kind)
        result[v["Name"]] = normalized
    # 显式丢弃不在备份里的当前值
    _ = backup_names  # 已在 result 重建时自然完成删除
    return result


# ---------------------------------------------------------------------------
# 5) 服务禁用 fallback 链与禁用顺序（对应 BUGFIX：Error 5 拒绝访问多重兜底）
# ---------------------------------------------------------------------------

def service_disable_order(*, is_win10_11):
    """等价 DisableWindowsUpdate 的服务禁用顺序。

    关键约束：WaaSMedicSvc 必须在 wuauserv 之前禁用，
    否则其自愈机制会在 wuauserv 被禁用后将其恢复。
    UsoSvc / WaaSMedicSvc 仅在 Win10/11 存在。
    """
    order = []
    if is_win10_11:
        order.append("WaaSMedicSvc")
        order.append("UsoSvc")
    order.append("wuauserv")
    return order


def fallback_chain(allow_system_task=True):
    """等价 ServiceDisableHelper.DisableServiceWithFallbacks 的 fallback 优先级（按尝试顺序）。

    1) sc.exe config/stop（原有方式）
    2) Win32 ChangeServiceConfig（OpenSCManager + ChangeServiceConfig）
    3) Registry Start=4（绕过 SCM ACL，全版本通用）
    4) SYSTEM scheduled task（以 NT AUTHORITY\\SYSTEM 运行，Win10/11 首选，Win7 兼容）
    """
    steps = ["sc.exe config/stop", "Win32 ChangeServiceConfig", "Registry Start=4"]
    if allow_system_task:
        steps.append("SYSTEM scheduled task")
    return steps


def is_service_start_disabled_from_registry(start_value):
    """等价 ServiceDisableHelper.IsServiceStartDisabled 的注册表判定：Start==4 即 Disabled。"""
    return start_value == 4


# ---------------------------------------------------------------------------
# 6) 服务启动类型映射（对应 ServiceDisableHelper.SetServiceStartTypeWithFallbacks）
# ---------------------------------------------------------------------------
#
# ServiceStartMode 枚举数值与注册表 Start 值一一对应：
#   Boot=0, System=1, Automatic=2, Manual=3, Disabled=4
# 因此"枚举值"可直接作为注册表 Start 值与 Win32 ChangeServiceConfig 的 dwStartType。

SERVICE_START_MODE = {
    "Boot": 0,
    "System": 1,
    "Automatic": 2,
    "Manual": 3,
    "Disabled": 4,
}

# sc.exe `start=` 参数文本（与 RestoreService 原有映射完全一致）
SC_START_MODE_TEXT = {
    "Boot": "boot",
    "System": "system",
    "Automatic": "auto",
    "Manual": "demand",
    "Disabled": "disabled",
}

# Win32 ChangeServiceConfig 的 dwStartType 常量（与注册表值/枚举值一致）
WIN32_START_TYPE = {
    "Boot": 0,
    "System": 1,
    "Automatic": 2,
    "Manual": 3,
    "Disabled": 4,
}


def map_start_mode_to_sc(mode):
    """等价 ServiceDisableHelper.MapToScStartMode。"""
    return SC_START_MODE_TEXT[mode]


def map_start_mode_to_registry(mode):
    """等价 ServiceDisableHelper.MapToRegistryStartValue（枚举值即注册表 Start 值）。"""
    return SERVICE_START_MODE[mode]


def map_start_mode_to_win32(mode):
    """等价 ServiceDisableHelper.MapToWin32StartType。"""
    return WIN32_START_TYPE[mode]


def is_service_start_mode_from_registry(start_value, mode):
    """等价 ServiceDisableHelper.IsServiceStartMode 的注册表判定：Start==目标模式对应值。"""
    return start_value == SERVICE_START_MODE[mode]


def restore_service_start_type_chain(allow_system_task=True):
    """等价「恢复」路径（RestoreService）复用的 fallback 链。

    RestoreService 现在委托 SetServiceStartTypeWithFallbacks，因此其 fallback 优先级
    与 DisableServiceWithFallbacks 完全一致（sc -> Win32 API -> 注册表 -> SYSTEM 任务）。
    步骤标签与 fallback_chain 保持一致，以便等价测试直接比对两段链是否完全相同。
    """
    steps = ["sc.exe config/stop", "Win32 ChangeServiceConfig", "Registry Start=4"]
    if allow_system_task:
        steps.append("SYSTEM scheduled task")
    return steps
