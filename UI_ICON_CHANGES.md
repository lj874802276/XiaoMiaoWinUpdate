# UI 按钮状态联动 + 多尺寸 ICO 图标集成 — 变更说明

- 工程：小喵 Windows 更新助手（XiaoMiaoWinUpdate，WPF / .NET Framework 4.8 / C# 7.3）
- 工程目录：`E:\.workbuddy\2026-08-17-20-07-51\winupdate-disabler\`
- 图标文件：`icon.ico`（已就绪，16x16 / 32x32 / 256x256 三尺寸，未修改、未重命名）

---

## 任务一：ICO 图标集成

### 1. csproj 改动（`XiaoMiaoWinUpdate.csproj`）

**(a) exe 图标**：在第一个 `<PropertyGroup>`（含 `TargetFrameworkVersion` 的那个）中新增一行，使生成的 exe 拥有该图标：

```xml
<AssemblyName>XiaoMiaoWinUpdate</AssemblyName>
<ApplicationIcon>icon.ico</ApplicationIcon>   <!-- 新增 -->
<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
```

**(b) 资源登记**：在含 `Page`/`Compile` 的 `<ItemGroup>` 末尾新增 `<Resource>`，使 `icon.ico` 作为托管资源嵌入程序集，Costura 单文件打包时也会随程序集一起嵌入：

```xml
<Compile Include="MainWindow.xaml.cs">
  <DependentUpon>MainWindow.xaml</DependentUpon>
</Compile>
<Resource Include="icon.ico" />   <!-- 新增 -->
```

### 2. MainWindow Icon 设置（`MainWindow.xaml`）

在 `<Window ...>` 标签上新增 `Icon` 属性，使用 pack URI 从程序集资源解析（最稳妥，配合上面的 `<Resource>` 登记）：

```xml
<Window x:Class="XiaoMiaoWinUpdate.MainWindow"
        ...
        Background="White"
        Icon="pack://application:,,,/icon.ico">   <!-- 新增 -->
```

> 说明：因已用 `<Resource Include="icon.ico" />` 将图标登记为程序集资源，`pack://application:,,,/icon.ico` 可稳定解析，窗口标题栏会显示该多尺寸图标。未删除或重命名 `icon.ico`。

---

## 任务二：按钮状态联动

### 涉及文件
- `Models\UpdateStatus.cs`（新增只读属性）
- `MainWindow.xaml.cs`（在 `RefreshStatus()` 中联动按钮状态；新增 `UpdateButtonStates()`；改造 `SetBusy()`）

### 1. 状态标志来源（真实字段，已确认非臆造）

阅读 `Services\PolicyEngine.cs` 的 `RefreshStatus()` 确认：当自动更新被关闭时，`NoAutoUpdate=1`，对应写入：

```csharp
status.AutoUpdate.ValueText = autoUpdateDisabled ? "已关闭" : "正常";   // PolicyEngine.cs 第 100 行
```

因此在 `Models\UpdateStatus.cs` 新增 **只读计算属性** `IsWindowsUpdateDisabled`，由 `AutoUpdate.ValueText` 推导（不引入魔法字符串散落、不改动 `PolicyEngine.cs`，完全落在本任务允许的修改文件范围内）：

```csharp
public bool IsWindowsUpdateDisabled
{
    get => AutoUpdate != null && AutoUpdate.ValueText == "已关闭";
}
```

> 该属性即「是否已彻底关闭 Windows 自动更新（等价于 `NoAutoUpdate=1`）」的便捷标志，供 UI 使用。

### 2. 联动实现位置：`RefreshStatus()` 末尾

原有 `RefreshStatus()` 只负责刷新状态文本。本次在其 `finally` 中调用新增的 `UpdateButtonStates()`，保证每次状态刷新后按钮可用态立即联动：

```csharp
private void RefreshStatus()
{
    try { _engine.RefreshStatus(_status); }
    catch (System.Exception ex) { MessageBox.Show(...); }
    finally { UpdateButtonStates(); }   // 新增：刷新后联动按钮
}
```

`UpdateButtonStates()` 依据 `_status.IsWindowsUpdateDisabled` 与忙标志 `_isBusy` 设置两个按钮的 `IsEnabled`：

```csharp
private void UpdateButtonStates()
{
    bool disabled = _status.IsWindowsUpdateDisabled;   // 是否已彻底关闭自动更新
    bool operable = !_isBusy;                           // 非操作进行中才可被点
    BtnDisable.IsEnabled = operable && !disabled;      // 「彻底关闭」：未禁用时可点，已禁用时变灰
    BtnRestore.IsEnabled = operable && disabled;       // 「恢复」：已禁用时可点，未禁用时变灰
}
```

### 3. 目标行为对照（已实现）

| 当前状态 | 自动更新状态 | 「彻底关闭」(BtnDisable) | 「恢复」(BtnRestore) |
|---|---|---|---|
| 禁用/已关闭 | `IsWindowsUpdateDisabled == true` | `IsEnabled = false`（变灰） | `IsEnabled = true`（变亮可点） |
| 正常/未禁用（初始态、或恢复成功后） | `IsWindowsUpdateDisabled == false` | `IsEnabled = true`（可点） | `IsEnabled = false`（变灰） |

### 4. 与原有 `SetBusy()` 的冲突修复（关键）

原 `SetBusy(bool)` 直接写死 `BtnDisable.IsEnabled = BtnRestore.IsEnabled = !busy`，会在操作结束后（finally 中 `SetBusy(false)`）把两个按钮强制恢复为可用，从而**覆盖**上面的联动结果。

本次将 `SetBusy()` 改为只更新忙标志 `_isBusy` 并重新调用 `UpdateButtonStates()`：

```csharp
private void SetBusy(bool busy)
{
    _isBusy = busy;
    UpdateButtonStates();
}
```

调用顺序（以「彻底关闭」为例）：`SetBusy(true)` → 两个按钮禁用 → 执行禁用逻辑 → `RefreshStatus()`（内部 `UpdateButtonStates` 仍因 `_isBusy=true` 保持两按钮禁用）→ `finally { SetBusy(false); }`（`_isBusy=false`，此时才按真实联动结果设置：BtnDisable 禁用、BtnRestore 启用）。**逻辑闭环，无覆盖问题。**

### 5. 颜色/样式

`App.xaml` 中两个按钮样式已自带 `IsEnabled=False` 触发器：
- `PrimaryButtonStyle`（BtnDisable）：`IsEnabled=false` → 背景 `#B0B0B0`（明显灰色），与蓝色启用态区分清晰。
- `SecondaryButtonStyle`（BtnRestore）：`IsEnabled=false` → 前景 `#999999`（灰字），与浅灰底区分清晰。

满足「关闭后变灰、恢复变亮」的视觉要求，无需额外样式改动（不过度设计）。

---

## 全局一致性审查结论

**IS_PASS: YES**

- 跨文件引用一致：`MainWindow.xaml.cs` 引用的 `_status.IsWindowsUpdateDisabled`、`BtnDisable`、`BtnRestore`、`UpdateButtonStates`、`SetBusy` 均已在对应文件/类正确定义；`x:Name` 与代码一致。
- 接口契约一致：`PolicyEngine.RefreshStatus(UpdateStatus)`、`BackupService.*`、各事件处理方法签名未改动，现有禁用/恢复业务逻辑、备份/恢复、提权、fallback 链均未被破坏。
- 数据流正确：`IsWindowsUpdateDisabled` 由 `AutoUpdate.ValueText` 推导，取值与 `PolicyEngine` 写入的 `"已关闭"`/`"正常"` 完全对应，无臆造字段。
- 资源与图标：`<ApplicationIcon>` 与 `<Resource Include="icon.ico">` 指向同一已存在文件；`pack://application:,,,/icon.ico` 与资源登记匹配。`icon.ico` 未被删除/重命名。
- 语言/框架约束：仅使用 C# 7.3 可用语法（表达式体属性、Lambda、`&&` 短路），无 `record`/`init`/`required`/`switch` 表达式/`Span` 等高级 API；工程仍可在 VS2022 + .NET Framework 4.8 下编译。
- 无重复实现、无循环依赖、无缺失导入。

---

## 代码变更摘要

| 文件 | 改动 |
|---|---|
| `XiaoMiaoWinUpdate.csproj` | 第一个 PropertyGroup 加 `<ApplicationIcon>icon.ico</ApplicationIcon>`；ItemGroup 加 `<Resource Include="icon.ico" />` |
| `MainWindow.xaml` | `<Window>` 加 `Icon="pack://application:,,,/icon.ico"` |
| `Models\UpdateStatus.cs` | 新增只读属性 `IsWindowsUpdateDisabled`（get 由 `AutoUpdate.ValueText == "已关闭"` 推导） |
| `MainWindow.xaml.cs` | 新增字段 `_isBusy`；`RefreshStatus()` 的 `finally` 调用 `UpdateButtonStates()`；新增 `UpdateButtonStates()` 按 `IsWindowsUpdateDisabled` 联动两按钮；`SetBusy()` 改为更新 `_isBusy` 后调用 `UpdateButtonStates()`（修复覆盖问题） |

未改动的文件（保持业务不变）：`Services\PolicyEngine.cs`、`Services\BackupService.cs`、`App.xaml`、`MainWindow.xaml` 按钮区域以外的所有内容。
