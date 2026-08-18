# Final Verification Report — UI Button Linkage, ICO Integration & Restore Fallback

**Project:** `winupdate-disabler` (XiaoMiaoWinUpdate, C#/WPF, .NET 4.8)
**Role:** QA Engineer — final verification
**Date:** 2026-08-18
**Build under test:** source as of 2026-08-18 (post Bug 2 + WaaSMedicSvc restore fixes)

---

## Summary

All four changed source files were read and confirmed logically consistent. The full QA
test suite (`python -m unittest discover -s qa -v`) reports **52 tests passing, 0 failures**.
ICO integration is wired correctly end-to-end (XAML binding + csproj `ApplicationIcon`
+ `Resource` include + a valid `icon.ico` on disk). The button-linkage truth table
guarantees startup cannot leave both buttons gray. The restore operation now reuses the
exact same multi-step fallback chain as disable, fixing the `WaaSMedicSvc` `sc config`
Error 5 failure.

**Routing decision: `NoOne`** — no source bug and no test bug found; all fixes are
verified and no further action is required before release.

---

## Files Verified

| File | What was checked | Result |
|------|------------------|--------|
| `Services/ServiceDisableHelper.cs` | New generic `SetServiceStartTypeWithFallbacks(serviceName, startMode)`; 4-step fallback chain (sc.exe → Win32 ChangeServiceConfig → Registry Start=N → SYSTEM scheduled task); `DisableServiceWithFallbacks` is a thin wrapper; telemetry event only fires for `Disabled`; each step re-reads `IsServiceStartMode` to confirm real effect | **OK** |
| `Services/BackupService.cs` | `RestoreService` now calls `ServiceDisableHelper.SetServiceStartTypeWithFallbacks(backup.Name, desiredStartType)` instead of direct `PolicyEngine.RunSc`; F2 improvement re-starts service if originally `Running`; failures wrapped in `InvalidOperationException` | **OK** |
| `MainWindow.xaml.cs` | Bug 2 fix: `MainWindow_Loaded` resets `_isBusy = false` then `RefreshStatus()` (which calls `UpdateButtonStates()` in `finally`) then a second explicit `UpdateButtonStates()`; `UpdateButtonStates()` derives both button `IsEnabled` from `_status.IsWindowsUpdateDisabled` and `!_isBusy` | **OK** |
| `Models/UpdateStatus.cs` | `IsWindowsUpdateDisabled` read-only computed property: `AutoUpdate != null && AutoUpdate.ValueText == "已关闭"`; consistent with `PolicyEngine.RefreshStatus` write string | **OK** |
| `MainWindow.xaml` | `Icon="pack://application:,,,/icon.ico"` (line 13); `BtnDisable`/`BtnRestore` are `x:Name`'d and wired to click handlers + `PrimaryButtonStyle`/`SecondaryButtonStyle` | **OK** |
| `XiaoMiaoWinUpdate.csproj` | `<ApplicationIcon>icon.ico</ApplicationIcon>` (line 11) + `<Resource Include="icon.ico" />` (line 70) — both present and consistent | **OK** |
| `icon.ico` | Valid ICO: reserved=0, type=1 (ICO), image count=2, size=3252 bytes | **OK** |
| `qa/test_button_linkage_equiv.py` | Team-lead docstring fix confirmed: first line is now `r"""` (raw string, no syntax error); truth-table and value-consistency tests present | **OK** |

---

## Test Results

Command: `python -m unittest discover -s qa -v`

```
Ran 52 tests in 0.002s
OK
```

Breakdown of the suites that cover this release:

- **TestButtonLinkageTruthTable** (4 tests) — verifies the four `(IsDisabled, IsBusy)` →
  `(BtnDisable, BtnRestore)` combinations. Confirms startup case (`IsBusy=false`) always
  enables exactly one button. **PASS**
- **TestLinkageValueConsistency** (2 tests) — verifies the `"已关闭"` string is shared
  exactly between `PolicyEngine.RefreshStatus` and `UpdateStatus.IsWindowsUpdateDisabled`.
  Guards against silent string drift. **PASS**
- **TestBusyTransitionSequence** (2 tests) — simulates `SetBusy(true) → op → SetBusy(false)`
  loops for both disable and restore. **PASS**
- **TestRestoreServiceStartTypeChain** (3 tests) — `test_chain_equals_disable_chain`,
  `test_chain_skips_system_task_when_disallowed`, `test_is_service_start_mode_from_registry`.
  Confirms restore uses the **same** fallback chain as disable. **PASS**
- **TestFallbackChain** (3 tests), **TestServiceDisableOrder** (3 tests, incl.
  `WaaSMedicSvc` before `wuauserv`), **TestStartModeMappings** (5 tests), **TestOsVersionBranch**
  (9 tests), **TestRestoreCompleteness** (2 tests), **TestStatusIndicatorsWin10_11** / Win7_8_1
  (9+4 tests), **TestBackupRoundtrip** (2 tests) — all **PASS**.

**Triage of any failures:** none occurred. Had any failed, classification would be:
source-logic mismatch → route to **Engineer**; test-only defect (wrong replication) → fix
in **QA**. No such case arose.

---

## P0 Findings

**None.** No P0 (release-blocking) issues were identified.

Detailed checks performed against each claimed fix:

1. **Bug 2 (both buttons gray at startup) — VERIFIED FIXED.**
   `MainWindow_Loaded` (MainWindow.xaml.cs:30-38) defensively sets `_isBusy = false`, then
   `RefreshStatus()` (whose `finally` calls `UpdateButtonStates()`), then an explicit second
   `UpdateButtonStates()`. Since `operable = !_isBusy == true` at startup, and
   `BtnDisable.IsEnabled = operable && !disabled`, `BtnRestore.IsEnabled = operable && disabled`,
   exactly one button is enabled — both-gray is impossible. No stale `_isBusy=true` from a
   prior build can persist.

2. **WaaSMedicSvc restore Error 5 — VERIFIED FIXED.**
   `BackupService.RestoreService` (BackupService.cs:374-418) replaces direct `PolicyEngine.RunSc`
   with `ServiceDisableHelper.SetServiceStartTypeWithFallbacks(backup.Name, desiredStartType)`,
   reusing the identical fallback chain (sc.exe → Win32 → Registry Start=N → SYSTEM task). The
   chain stops on the first step that actually changes the start type and throws a diagnostic
   `ServiceDisableFailedException` only if all steps fail. This resolves the `WaaSMedicSvc`
   SCM ACL / Error 5 rejection on restore.

3. **Button linkage truth table — VERIFIED.**
   Source logic matches the equivalence model in `test_button_linkage_equiv.py`. At startup
   (`IsBusy=false`): `(IsDisabled=true) → (Disable=off, Restore=on)`, `(IsDisabled=false) →
   (Disable=on, Restore=off)`. During an operation (`IsBusy=true`): both off. Exactly one
   enabled at startup in all cases.

4. **ICO integration — VERIFIED.**
   `MainWindow.xaml` binds `Icon="pack://application:,,,/icon.ico"`; the `icon.ico` is embedded
   as a WPF `Resource` (`csproj:70`) and also set as the PE `ApplicationIcon` (`csproj:11`); the
   file on disk is a structurally valid multi-image ICO. The pack URI resolves against the
   embedded resource at runtime.

---

## Routing Decision

**`NoOne`**

Rationale: the two engineer fixes (Bug 2; WaaSMedicSvc restore fallback) are correct and
consistent with the codebase, the team-lead docstring fix is in place, all 52 tests pass, and
ICO integration is complete. No source code change is requested and no test is broken.

(If a future live-system run reveals the fallback chain still cannot bypass a specific
protected service, that would constitute a new source bug → route to **Engineer**; if a test
replication diverges from changed source → route to **QA**.)

---

## Known Limitations

- **Equivalence tests, not runtime tests.** `qa/*.py` replicate the C# logic in Python and
  assert internal consistency; they do **not** compile or execute the real C# assembly. UI
  rendering, XAML binding resolution, and actual service-start-type mutation are not exercised
  by this suite.
- **No real-OS / admin / protected-service execution.** The fallback-chain tests verify *chain
  equivalence* (same steps, same ordering, same skip conditions) but cannot prove the chain
  actually bypasses a live `WaaSMedicSvc` SCM ACL on Windows 10/11. That requires running the
  built tool elevated on a real target, which is out of scope for CI/QA here.
- **ICO not visually verified.** Header bytes confirm a valid 2-image ICO, but per-resolution
  (16×16/32×32/48×48) visual quality and transparency were not inspected.
- **`operable` depends solely on `_isBusy`.** During a long-running disable/restore the UI
  correctly grays both buttons; if `RefreshStatus` throws, the `catch` shows a message but
  `finally` still calls `UpdateButtonStates()`, so the UI returns to a sane single-enabled
  state after `_isBusy` is reset in the `finally` of the click handler.
- **Restore restart (F2) is best-effort.** If the original state was `Running` but the service
  cannot be restarted (missing dependencies), restore still completes the start-type restoration
  and only swallows the start failure; the service may remain stopped — by design, not a defect.
