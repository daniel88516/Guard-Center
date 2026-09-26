using GuardCenter;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GuardCenter.Tests
{
    internal static class Program
    {
        private static int failures;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "uac-guard-gsudo-live-test", StringComparison.OrdinalIgnoreCase))
            {
                UacGuardActionResult result = new UacGuardModule().StartGsudoSessionAsync().GetAwaiter().GetResult();
                Console.WriteLine((result.Success ? "[PASS] " : "[FAIL] ") + result.Message);
                return result.Success ? 0 : 1;
            }
            if (args.Length > 0 && string.Equals(args[0], "uac-guard-lifecycle-live-test",
                StringComparison.OrdinalIgnoreCase))
            {
                UacGuardActionResult result = new UacGuardModule().StartLifecycleGsudoSessionAsync()
                    .GetAwaiter().GetResult();
                Console.WriteLine((result.Success ? "[PASS] " : "[FAIL] ") + result.Message);
                if (!result.Success)
                {
                    return 1;
                }
                UacGuardStatus status = new UacGuardModule().GetStatusAsync().GetAwaiter().GetResult();
                Console.WriteLine("GSUDO_SESSION_MODE=" + status.GsudoSessionMode);
                Console.WriteLine("GSUDO_SESSION_ACTIVE=" + status.GsudoSessionActive);
                return status.GsudoSessionActive
                    && string.Equals(status.GsudoSessionMode, UacGuardModule.GuardCenterLifecycleMode,
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }
            if (args.Length > 0 && string.Equals(args[0], "uac-guard-status-live-test", StringComparison.OrdinalIgnoreCase))
            {
                UacGuardStatus status = new UacGuardModule().GetStatusAsync().GetAwaiter().GetResult();
                Console.WriteLine("GSUDO_INSTALLED=" + status.GsudoInstalled);
                Console.WriteLine("GSUDO_SESSION_ACTIVE=" + status.GsudoSessionActive);
                Console.WriteLine("GSUDO_TARGET_PID=" + status.GsudoTargetProcessId);
                Console.WriteLine("GSUDO_SESSION_MODE=" + status.GsudoSessionMode);
                Console.WriteLine("CODEX_INSTALLED=" + status.CodexInstalled);
                Console.WriteLine("RUNNER_INSTALLED=" + status.RunnerInstalled);
                Console.WriteLine("RUNNER_ACL_PROTECTED=" + status.RunnerAclProtected);
                Console.WriteLine("LIFECYCLE_TASK_INSTALLED=" + status.LifecycleTaskInstalled);
                Console.WriteLine("LIFECYCLE_TASK_CONFIGURATION_VALID=" + status.LifecycleTaskConfigurationValid);
                Console.WriteLine("ISSUES=" + string.Join(" | ", status.Issues.ToArray()));
                Console.WriteLine("STATUS_TEXT=" + status.StatusText);
                return status.GsudoInstalled && status.GsudoSessionActive ? 0 : 1;
            }
            if (args.Length > 0 && string.Equals(args[0], "uac-guard-test", StringComparison.OrdinalIgnoreCase))
            {
                Run("UAC Guard automatic authorization readiness", UacGuardAutomaticAuthorizationReadiness);
                Run("UAC Guard one-click setup uses pinned non-interactive gsudo package",
                    UacGuardOneClickSetupUsesPinnedGsudoPackage);
                return failures == 0 ? 0 : 1;
            }
            if (args.Length > 0 && string.Equals(args[0], "link-guard-test", StringComparison.OrdinalIgnoreCase))
            {
                Run("link guard rules round trip every independent option", LinkGuardRulesRoundTrip);
                Run("link guard rejects duplicate reverse and self links", LinkGuardRejectsInvalidLinks);
                Run("link guard closes B after an observed A-to-closed transition",
                    LinkGuardClosesAfterObservedTriggerExit);
                Run("link guard detects a windowed app closing while its background process remains",
                    LinkGuardDetectsClosedWindowWithBackgroundProcess);
                Run("link guard starts a missing linked process", LinkGuardStartsMissingLinkedProcess);
                Run("link guard leaves a manually closed B stopped when keep-running is off",
                    LinkGuardLeavesClosedLinkedProcessStopped);
                Run("link guard closes the rule-started linked window process",
                    LinkGuardClosesRuleStartedWindowProcess);
                Run("link guard closes an already-running B after monitor restart",
                    LinkGuardClosesAlreadyRunningLinkedProcess);
                return failures == 0 ? 0 : 1;
            }
            if (args.Length > 0 && string.Equals(args[0], "pointer-precision-guard-test",
                StringComparison.OrdinalIgnoreCase))
            {
                Run("pointer precision guard uses a thirty-second production interval",
                    PointerPrecisionGuardUsesThirtySecondInterval);
                Run("pointer precision guard setting persists",
                    PointerPrecisionGuardSettingPersists);
                Run("pointer precision guard disables acceleration immediately and on polling",
                    PointerPrecisionGuardDisablesImmediatelyAndOnPolling);
                Run("pointer precision service reads the live Windows setting",
                    PointerPrecisionServiceReadsLiveSetting);
                return failures == 0 ? 0 : 1;
            }
            if (args.Length > 0 && string.Equals(args[0], "link-guard-chrome-wmp-live-test",
                StringComparison.OrdinalIgnoreCase))
            {
                return RunLinkGuardChromeWmpLiveTest();
            }
            if (args.Length > 0 && string.Equals(args[0], "game-helper-window", StringComparison.OrdinalIgnoreCase))
            {
                return RunGameHelperWindowProbe(args);
            }
            if (args.Length > 0 && string.Equals(args[0], "game-helper-live-test", StringComparison.OrdinalIgnoreCase))
            {
                return RunGameHelperLiveTest(true);
            }
            if (args.Length > 0
                && string.Equals(args[0], "game-helper-language-live-test",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunGameHelperLiveTest(false);
            }
            if (args.Length > 0
                && string.Equals(args[0], "crosshair-position-live-test",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunCrosshairPositionLiveTest();
            }
            if (args.Length > 0
                && string.Equals(args[0], "game-helper-window-events-live-test",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RunGameHelperWindowEventsLiveTest();
            }
            if (args.Length > 0 && string.Equals(args[0], "keyboard-width-probe-test", StringComparison.OrdinalIgnoreCase))
            {
                return RunKeyboardWidthProbeTest();
            }
            if (args.Length > 0 && string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
            {
                return ProbeHardware(false);
            }
            if (args.Length > 0 && string.Equals(args[0], "write-current", StringComparison.OrdinalIgnoreCase))
            {
                return ProbeHardware(true);
            }
            if (args.Length > 0 && string.Equals(args[0], "device-probe", StringComparison.OrdinalIgnoreCase))
            {
                return ProbeDevices();
            }
            if (args.Length > 0 && string.Equals(args[0], "core-device-probe", StringComparison.OrdinalIgnoreCase))
            {
                return ProbeCoreDevices();
            }
            if (args.Length > 0 && string.Equals(args[0], "input-stack-probe", StringComparison.OrdinalIgnoreCase))
            {
                return ProbeInputStack();
            }
            if (args.Length > 0 && string.Equals(args[0], "display-set-test", StringComparison.OrdinalIgnoreCase))
            {
                return TestDisplayWriteAndRestore();
            }
            if (args.Length > 0 && string.Equals(args[0], "display-handle-lifetime-test", StringComparison.OrdinalIgnoreCase))
            {
                return TestDisplayHandleLifetime();
            }
            if (args.Length > 0 && string.Equals(args[0], "sticky-keys-live-test", StringComparison.OrdinalIgnoreCase))
            {
                return TestStickyKeysLiveAndRestore();
            }
            if (args.Length > 0 && string.Equals(args[0], "sticky-keys-restore-hotkey", StringComparison.OrdinalIgnoreCase))
            {
                return RestoreStickyKeysHotkeyBitLive();
            }
            if (args.Length > 0 && string.Equals(args[0], "audio-endpoint-live-test", StringComparison.OrdinalIgnoreCase))
            {
                return TestAudioEndpointVolumePersists(args);
            }
            if (args.Length > 0 && string.Equals(args[0], "power-request-live-test", StringComparison.OrdinalIgnoreCase))
            {
                return TestPowerRequestLive(args);
            }
            Run("application paths follow the current runtime directory", ApplicationPathsFollowRuntimeDirectory);
            Run("startup shortcut validation rejects stale portable paths",
                StartupShortcutValidationRejectsStalePortablePaths);
            Run("UAC Guard automatic authorization readiness", UacGuardAutomaticAuthorizationReadiness);
            Run("UAC Guard one-click setup uses pinned non-interactive gsudo package",
                UacGuardOneClickSetupUsesPinnedGsudoPackage);
            Run("corrupted profile falls back", CorruptedProfileFallsBack);
            Run("initial profiles use per-mode deltas", InitialProfilesUsePerModeDeltas);
            Run("capture skips unreliable monitors", CaptureSkipsUnreliableMonitors);
            Run("live mode captures individual adjustments", LiveModeCapturesIndividualAdjustments);
            Run("apply plan handles skipped and clamped values", ApplyPlanHandlesSkippedAndClampedValues);
            Run("stable id normalization matches WMI and DisplayConfig", StableIdNormalizationMatches);
            Run("composite provider merges WMI brightness with DDC contrast", CompositeProviderMergesControls);
            Run("device queue is latest value wins", DeviceQueueLatestValueWins);
            Run("display write requires matching readback", DisplayWriteRequiresMatchingReadback);
            Run("device guard policy includes only approved classes", DeviceGuardPolicyIncludesOnlyApprovedClasses);
            Run("device guard refresh preserves repair summary", DeviceGuardRefreshPreservesRepairSummary);
            Run("core hardware resolver separates anchors and blocks critical USB descendants",
                CoreHardwareResolverSeparatesAnchorsAndBlocksUsb);
            Run("core hardware resolver reports disabled driver-missing and baseline-missing states",
                CoreHardwareResolverReportsFailureStates);
            Run("core hardware resolver correlates exact-port USB descriptor failures",
                CoreHardwareResolverCorrelatesUsbRecoveryTarget);
            Run("core Repair All policy never restarts healthy or high-risk hardware",
                CoreRepairAllPolicyIsRiskBounded);
            Run("core hardware baseline is persistent atomic and corruption recoverable",
                CoreBaselineIsPersistentAndRecoverable);
            Run("device guard refresh shares one complete PnP snapshot", DeviceGuardRefreshSharesSnapshot);
            Run("input stack numbering remains diagnostic only",
                InputStackNumberingRemainsDiagnosticOnly);
            Run("input stack parser accepts only numbered class device objects",
                InputStackParserAcceptsNumberedClassObjects);
            Run("HuaJuan compatibility isolates keyboard while retaining mouse filters",
                HuaJuanCompatibilityIsolatesKeyboardOnly);
            Run("HuaJuan compatibility apply and restore round trip is transactional",
                HuaJuanCompatibilityApplyRestoreRoundTrip);
            Run("HuaJuan compatibility rolls back a failed registry update",
                HuaJuanCompatibilityRollsBackRegistryFailure);
            Run("progressive list filters sorts batches and deduplicates", ProgressiveListFiltersSortsAndBatches);
            Run("progressive list rejects stale batch", ProgressiveListRejectsStaleBatch);
            Run("list presentation settings persist", ListPresentationSettingsPersist);
            Run("pointer precision guard uses a thirty-second production interval",
                PointerPrecisionGuardUsesThirtySecondInterval);
            Run("pointer precision guard setting persists",
                PointerPrecisionGuardSettingPersists);
            Run("pointer precision guard disables acceleration immediately and on polling",
                PointerPrecisionGuardDisablesImmediatelyAndOnPolling);
            Run("pointer precision service reads the live Windows setting",
                PointerPrecisionServiceReadsLiveSetting);
            Run("module navigation order normalizes missing duplicate and unknown ids",
                ModuleNavigationOrderNormalizes);
            Run("module navigation insertion and edge scrolling use stable boundaries",
                ModuleNavigationInsertionAndScrolling);
            Run("VSR Guard preserves GPU preference fields", VsrGuardPreservesGpuPreferenceFields);
            Run("VSR Guard reads Chrome acceleration state", VsrGuardReadsChromeAccelerationState);
            Run("VSR Guard preserves unrelated NVIDIA RTX Video flags", VsrGuardPreservesNvidiaFlags);
            Run("link guard rules round trip every independent option",
                LinkGuardRulesRoundTrip);
            Run("link guard rejects duplicate reverse and self links",
                LinkGuardRejectsInvalidLinks);
            Run("link guard closes B after an observed A-to-closed transition",
                LinkGuardClosesAfterObservedTriggerExit);
            Run("link guard detects a windowed app closing while its background process remains",
                LinkGuardDetectsClosedWindowWithBackgroundProcess);
            Run("link guard starts a missing linked process",
                LinkGuardStartsMissingLinkedProcess);
            Run("link guard closes the rule-started linked window process",
                LinkGuardClosesRuleStartedWindowProcess);
            Run("link guard closes an already-running B after monitor restart",
                LinkGuardClosesAlreadyRunningLinkedProcess);
            Run("focused refresh runs only for an active visible window",
                FocusedRefreshRequiresActiveVisibleWindow);
            Run("focused refresh fingerprints ignore order and detect value changes",
                FocusedRefreshFingerprintsAreStable);
            Run("audio zero property changes check topology without immediately zeroing",
                AudioZeroPropertyChangesCheckTopologyWithoutImmediateZero);
            Run("audio zero-on-enable setting persists",
                AudioZeroOnEnableSettingPersists);
            Run("automatic display topology refresh is silent and single flight",
                AutomaticDisplayTopologyRefreshIsSilentAndSingleFlight);
            Run("legacy page size migrates to batch size", LegacyPageSizeMigrates);
            Run("slider mouse wheel step precedence clamps and preserves fractional values",
                SliderMouseWheelStepAndBounds);
            Run("slider PreviewMouseWheel class handler consumes the routed event in a ScrollViewer",
                SliderMouseWheelRoutedEventInScrollViewer);
            Run("game helper legacy app settings use safe input-language defaults",
                GameHelperLegacySettingsUseSafeDefaults);
            Run("crosshair selected apps round trip deduplicate and reject malformed identities",
                CrosshairSelectedAppsRoundTrip);
            Run("crosshair visibility policy preserves preview and fail-closed restrictions",
                CrosshairVisibilityPolicyMatchesRequirements);
            Run("event-driven foreground hooks are shared and centrally released",
                CrosshairForegroundMonitoringIsShared);
            Run("game helper protected index matches installed candidates by executable path",
                GameHelperProtectionIndexMatchesCandidatePaths);
            Run("game helper new protected apps enable every protection by default",
                GameHelperNewAppsEnableEveryProtection);
            Run("game helper input-language options round trip", GameHelperInputLanguageOptionsRoundTrip);
            Run("game helper per-app keyboard and language resources stay independent",
                GameHelperPerAppResourcesStayIndependent);
            Run("game helper blocks paired Windows keys repeats and rapid presses",
                GameHelperWindowsKeyStateIsPaired);
            Run("game helper Windows-key state passes outside protected apps and resets",
                GameHelperWindowsKeyStatePassesAndResets);
            Run("game helper Right Ctrl+D triggers once and never accepts Left Ctrl",
                GameHelperRightControlDIsExactAndSingleShot);
            Run("game helper Right Ctrl+D handles reverse order repeats and cancellation",
                GameHelperRightControlDHandlesEdgeCases);
            Run("game helper show-desktop fallback preserves tool-window overlays",
                GameHelperShowDesktopFallbackPreservesToolWindows);
            Run("game helper input layout comparison uses the HKL low 32 bits",
                GameHelperInputLayoutComparisonUsesLow32Bits);
            Run("game helper preserves original layout until a confirmed restore",
                GameHelperOriginalLayoutCacheRequiresConfirmedRestore);
            Run("game helper elevated host rejects untrusted requests",
                GameHelperElevatedHostRejectsUntrustedRequests);
            Run("keyboard guard locks Microsoft and ASUS width settings without changing key events",
                KeyboardGuardLocksAndRestoresCharacterWidthSettings);
            Run("keyboard guard rolls back partial character-width failures",
                KeyboardGuardRollsBackCharacterWidthFailures);
            Run("keyboard guard migrates the deployed legacy IMM backup",
                KeyboardGuardMigratesDeployedLegacyImmBackup);
            Run("sticky keys hotkey changes preserve unrelated flags and avoid notification loops",
                StickyKeysHotkeyPreservesFlagsAndAvoidsLoops);
            Run("sticky keys restore respects an originally disabled hotkey",
                StickyKeysRestoreRespectsOriginalDisabledState);
            Run("sticky keys failures do not report or persist success", StickyKeysFailuresDoNotPersistSuccess);
            Run("device guard presentation separates health from repair result",
                DeviceGuardPresentationSeparatesHealthAndRepairResult);
            Run("app guard command line is manage only", AppGuardCommandLineIsManageOnly);
            Run("app guard IPC allow list is manage target only", AppGuardIpcAllowListIsManageTargetOnly);
            Run("app guard blocks current-process terminate and restart", AppGuardBlocksSelfDestructiveActions);
            Run("app guard batch details share one process snapshot", AppGuardBatchDetailsShareProcessSnapshot);
            Run("app guard prefetch warms the detail cache", AppGuardPrefetchWarmsDetailCache);
            Run("app guard card expansion scrolls smoothly to the viewport center",
                AppGuardExpansionScrollsSmoothlyToViewportCenter);
            Run("app catalog imports direct executable", AppCatalogImportsDirectExecutable);
            Run("empty shortcut icon location is ignored", EmptyShortcutIconLocationIsIgnored);
            Run("app catalog merges Steam registry metadata by app id", AppCatalogMergesSteamIdentity);
            Run("app catalog prefers launch targets over installer entries", AppCatalogPrefersLaunchTarget);
            Run("app catalog merges localized products with launcher shortcuts",
                AppCatalogMergesLocalizedProductLauncher);
            Run("app catalog keeps unrelated same-name apps separate", AppCatalogKeepsUnrelatedNamesSeparate);
            Run("app catalog prefers explicit icons for internet shortcuts",
                AppCatalogPrefersExplicitInternetShortcutIcon);
            Run("application icon replacement is fixed-name and orphan-free",
                ApplicationIconReplacementIsOrphanFree);
            Run("tray menu remains open only after item clicks",
                TrayMenuRemainsOpenOnlyAfterItemClicks);
            Run("tray menu checked states keep one DPI-aware row height",
                TrayMenuCheckedStatesKeepOneRowHeight);
            Run("power guard duration bounds and formatting include days and seconds",
                PowerGuardDurationBoundsAndFormattingIncludeDaysAndSeconds);
            Run("power guard restores persisted duration and display preferences",
                PowerGuardRestoresPersistedPreferences);
            Run("power guard duration selection stays passive while off", PowerGuardSelectionStaysPassiveWhileOff);
            Run("power guard request transitions are atomic", PowerGuardRequestTransitionsAreAtomic);
            Run("power guard failures retain the last confirmed state", PowerGuardFailuresRetainConfirmedState);
            Run("power guard countdown uses an absolute deadline", PowerGuardCountdownUsesAbsoluteDeadline);
            Run("power guard expiry and zero adjustment clear requests", PowerGuardExpiryAndZeroClearRequests);
            Run("power guard resume reapplies or expires requests", PowerGuardResumeReappliesOrExpires);
            Run("power guard rapid toggles and disposal do not leak leases", PowerGuardRapidTogglesDoNotLeak);
            Run("power request interop layout matches Windows ABI", PowerRequestInteropLayoutMatchesAbi);

            if (failures == 0)
            {
                Console.WriteLine("All tests passed.");
                return 0;
            }

            Console.WriteLine(failures + " test(s) failed.");
            return 1;
        }

        private static NativeWindowProc gameHelperProbeWindowProc;
        private static string gameHelperProbeLogPath;
        private static IntPtr gameHelperProbeMainWindow;
        private static IntPtr gameHelperProbeFocusWindow;
        private static IntPtr gameHelperProbePopupWindow;

        private static int RunGameHelperLiveTest(bool includeKeyboardChordChecks)
        {
            ReleaseInjectedKeyboardState();
            string testRoot = Path.Combine(Path.GetTempPath(), "GuardCenter.GameHelper.Live."
                + Guid.NewGuid().ToString("N"));
            var processes = new List<Process>();
            KeyboardGuardModule keyboardGuard = null;
            GameHelperModule gameHelper = null;
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    _ = new System.Windows.Application
                    {
                        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                    };
                }

                string appA = CopyTestRuntime(Path.Combine(testRoot, "A"));
                string appB = CopyTestRuntime(Path.Combine(testRoot, "B"));
                string appC = CopyTestRuntime(Path.Combine(testRoot, "C"));
                string logA = Path.Combine(testRoot, "A.log");
                string logB = Path.Combine(testRoot, "B.log");
                string logC = Path.Combine(testRoot, "C.log");

                GameHelperProtectedApp protectedA = LiveProtectedApp("Probe A", appA, true);
                protectedA.BlockWindowsKey = true;
                protectedA.RightControlDShowsDesktop = true;
                GameHelperProtectedApp protectedB = LiveProtectedApp("Probe B", appB, true);
                protectedB.BlockWindowsKey = true;
                protectedB.RightControlDShowsDesktop = true;
                var gameSettings = new GameHelperSettings
                {
                    CrosshairEnabled = true,
                    CrosshairRestrictToSelectedApps = true,
                    CrosshairSelectedApps = CrosshairAppSelectionCodec.Encode(
                        new List<CrosshairSelectedApp>
                        {
                            new CrosshairSelectedApp
                            {
                                Name = "Probe A",
                                TargetPath = appA
                            }
                        }),
                    ProtectedApps = GameHelperModule.EncodeProtectedApps(new List<GameHelperProtectedApp>
                    {
                        protectedA,
                        protectedB
                    })
                };
                gameHelper = new GameHelperModule(gameSettings);
                gameHelper.ApplySavedState();
                AssertTrue(gameHelper.IsForegroundEventHookActive,
                    "crosshair and game protection share one active foreground hook");
                AssertTrue(gameHelper.ForegroundEventHookCount >= 10,
                    "foreground monitoring installs the window lifecycle event set");
                AssertEqual(TimeSpan.FromMilliseconds(75),
                    GameHelperModule.ForegroundReconciliationDelay,
                    "window event reconciliation uses a short one-shot settling delay");
                AssertTrue(gameHelper.IsCrosshairOverlayCreated
                    && !gameHelper.IsCrosshairOverlayVisible,
                    "restricted crosshair exists but starts hidden outside its selected app");

                keyboardGuard = new KeyboardGuardModule(new KeyboardGuardSettings());
                AssertTrue(keyboardGuard.DisableShiftSpaceWidthToggle(),
                    "Keyboard Guard disables the runtime IME shape hotkeys before the game starts");
                Process processA = StartGameHelperProbe(appA, logA, "Probe A");
                processes.Add(processA);
                LiveProbeInfo probeA = WaitForProbe(logA, 5000);
                ActivateProbe(probeA, "game A becomes foreground");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.IsCrosshairOverlayVisible;
                }, 1500), "restricted crosshair appears for selected game A");
                AssertTrue(PumpUntil(delegate
                {
                    return IsCrosshairCenteredOnTarget(gameHelper, probeA.Hwnd);
                }, 1500), "restricted crosshair is centered on game A's client area");
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    1500, "normal game A locks to Microsoft ENG");
                AssertTrue(probeA.FocusHwnd != probeA.Hwnd && probeA.FocusHwnd != IntPtr.Zero,
                    "probe exposes a distinct focused child HWND");

                PostMessage(probeA.FocusHwnd, GameInputLanguageNative.WmInputLangChangeRequest,
                    IntPtr.Zero, new IntPtr(0x04040404));
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    700, "100 ms watchdog corrects a game-side layout change");
                string focusChange = "INPUTLANGCHANGE layout=0x4090409 hwnd="
                    + probeA.FocusHwnd.ToInt64();
                AssertTrue(Array.Exists(ReadSharedLogLines(logA), delegate(string line)
                {
                    return line.IndexOf(focusChange, StringComparison.Ordinal) >= 0;
                }), "watchdog sends the correction to GetGUIThreadInfo.hwndFocus first");

                if (includeKeyboardChordChecks)
                {
                    AssertChordPassesGameHelper(logA, 0x10, 0x20, true,
                        "Shift+Space reaches the game after the IME action is disabled");
                    AssertChordPassesGameHelper(logA, 0x12, 0x10, true,
                        "Alt+Shift reaches the game while Game Helper is active");
                    AssertChordPassesGameHelper(logA, 0x11, 0x10, true,
                        "Ctrl+Shift reaches the game while Game Helper is active");
                    AssertChordPassesGameHelper(logA, 0x5B, 0x20, false,
                        "Win+Space is not swallowed by Guard Center even when Windows translates key-down");
                }
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    700, "input-switch chords leave game A on Microsoft ENG");

                AssertTrue(keyboardGuard.RestoreShiftSpaceWidthToggle(),
                    "Keyboard Guard restores the captured runtime IME shape hotkeys");
                keyboardGuard.Dispose();
                keyboardGuard = null;

                Process processB = StartGameHelperProbe(appB, logB, "Probe B");
                processes.Add(processB);
                LiveProbeInfo probeB = WaitForProbe(logB, 5000);
                ActivateProbe(probeB, "game B becomes foreground");
                AssertTrue(PumpUntil(delegate
                {
                    return !gameHelper.IsCrosshairOverlayVisible;
                }, 1500), "restricted crosshair hides for unselected game B");
                AssertLayout(probeB.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    1500, "game B locks after game A to game B transition");
                AssertLayout(probeA.ThreadId, new IntPtr(0x04040404), 1500,
                    "game A restores its own original HKL when game B becomes foreground");

                for (int i = 0; i < 4; i++)
                {
                    SetForegroundWindow(i % 2 == 0 ? probeA.Hwnd : probeB.Hwnd);
                    PumpFor(25);
                }
                SetForegroundWindow(probeA.Hwnd);
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.IsCrosshairOverlayVisible;
                }, 1500), "rapid switching restores the crosshair for selected game A");
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    1500, "rapid Alt-Tab style switches lock the final game");
                AssertLayout(probeB.ThreadId, new IntPtr(0x04040404), 1500,
                    "rapid switching restores the game that lost foreground");

                ForceForegroundWindow(probeA.PopupHwnd);
                AssertTrue(PumpUntil(delegate
                {
                    return GameInputLanguageNative.GetCurrentForegroundWindow() == probeA.PopupHwnd;
                }, 1500), "game A small top-level popup becomes foreground");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.IsCrosshairOverlayVisible
                        && gameHelper.ForegroundProtectionHwnd == probeA.PopupHwnd
                        && gameHelper.CrosshairTargetHwnd == probeA.Hwnd
                        && IsCrosshairCenteredOnTarget(gameHelper, probeA.Hwnd);
                }, 2500), "window events keep crosshair on the main game client while the popup is foreground");
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    2500, "input-language protection remains active on the game popup");
                PostMessage(probeA.PopupHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                ActivateProbe(probeA, "game A main window returns after its popup closes");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.IsCrosshairOverlayVisible
                        && gameHelper.ForegroundProtectionHwnd == probeA.Hwnd
                        && IsCrosshairCenteredOnTarget(gameHelper, probeA.Hwnd);
                }, 2500), "window events recover all foreground state after the popup closes");
                CrosshairTargetWindow.NativeRect beforeResize;
                AssertTrue(CrosshairTargetWindow.TryGetClientBounds(probeA.Hwnd, out beforeResize),
                    "game A exposes client bounds before its resolution changes");
                AssertTrue(SetWindowPos(probeA.Hwnd, IntPtr.Zero, 300, 180, 1040, 640, 0x0014),
                    "game A window resolution changes while it remains foreground");
                ActivateProbe(probeA, "resized game A remains foreground");
                CrosshairTargetWindow.NativeRect afterResize = default(CrosshairTargetWindow.NativeRect);
                bool crosshairRecentered = PumpUntil(delegate
                {
                    return CrosshairTargetWindow.TryGetClientBounds(probeA.Hwnd, out afterResize)
                        && (Math.Abs(afterResize.CenterX - beforeResize.CenterX) > 25
                            || Math.Abs(afterResize.CenterY - beforeResize.CenterY) > 25)
                        && IsCrosshairCenteredOnTarget(gameHelper, probeA.Hwnd);
                }, 2500);
                if (!crosshairRecentered)
                {
                    System.Windows.Point actualCenter =
                        gameHelper.CrosshairOverlayCenterInDevicePixels;
                    throw new InvalidOperationException("location-change events recenter crosshair after"
                        + " the game resolution changes; before=" + beforeResize.CenterX + ","
                        + beforeResize.CenterY + " after=" + afterResize.CenterX + ","
                        + afterResize.CenterY + " overlay=" + actualCenter.X + ","
                        + actualCenter.Y + " target=" + gameHelper.CrosshairTargetHwnd.ToInt64());
                }
                ActivateProbe(probeB, "game B becomes foreground while the hook is unavailable");
                AssertTrue(PumpUntil(delegate
                {
                    return !gameHelper.IsCrosshairOverlayVisible;
                }, 2500), "foreground events hide restricted crosshair for game B");
                AssertLayout(probeB.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    2500, "foreground events start game B input-language protection");
                AssertLayout(probeA.ThreadId, new IntPtr(0x04040404), 2500,
                    "foreground events restore game A after switching away");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.ForegroundProtectionHwnd == probeB.Hwnd;
                }, 2500), "foreground events update keyboard protection to game B");
                ActivateProbe(probeA, "game A returns through event-driven monitoring");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.IsCrosshairOverlayVisible
                        && gameHelper.ForegroundProtectionHwnd == probeA.Hwnd;
                }, 2500), "foreground events restore crosshair and keyboard protection for game A");

                Process processC = StartGameHelperProbe(appC, logC, "Probe C");
                processes.Add(processC);
                LiveProbeInfo probeC = WaitForProbe(logC, 5000);
                ActivateProbe(probeA, "game A regains foreground before the non-game transition");
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    2500, "game A relocks before switching to non-game C");
                PostMessage(probeC.FocusHwnd, GameInputLanguageNative.WmInputLangChangeRequest,
                    IntPtr.Zero, GameInputLanguageNative.MicrosoftEnglishUsLayout);
                AssertLayout(probeC.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    700, "non-game C starts with a distinguishable ENG layout");
                ActivateProbe(probeC, "non-game C becomes foreground");
                AssertTrue(PumpUntil(delegate
                {
                    return !gameHelper.IsCrosshairOverlayVisible;
                }, 2500), "restricted crosshair stays hidden for unselected non-game C");
                AssertLayout(probeC.ThreadId, new IntPtr(0x04040404), 2500,
                    "the saved pre-game HKL follows the user to non-game C");
                AssertLayout(probeA.ThreadId, new IntPtr(0x04040404), 2500,
                    "game A also restores its own original HKL after losing foreground");

                ActivateProbe(probeA, "game A becomes foreground before module shutdown");
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    2500, "game A relocks before module shutdown");
                gameHelper.Dispose();
                gameHelper = null;
                AssertLayout(probeA.ThreadId, new IntPtr(0x04040404), 1500,
                    "module shutdown restores the active game's own HKL");

                var noWatchdogApp = LiveProtectedApp("Probe A", appA, true);
                noWatchdogApp.BlockInputLanguageSwitch = false;
                var noWatchdogSettings = new GameHelperSettings
                {
                    ProtectedApps = GameHelperModule.EncodeProtectedApps(new List<GameHelperProtectedApp>
                    {
                        noWatchdogApp
                    })
                };
                gameHelper = new GameHelperModule(noWatchdogSettings);
                gameHelper.ApplySavedState();
                SetForegroundWindow(probeA.Hwnd);
                AssertLayout(probeA.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    1500, "language lock applies once when its app becomes foreground");
                PostMessage(probeA.FocusHwnd, GameInputLanguageNative.WmInputLangChangeRequest,
                    IntPtr.Zero, new IntPtr(0x04040404));
                AssertLayout(probeA.ThreadId, new IntPtr(0x04040404), 700,
                    "game can change HKL when its bottom-level watchdog option is disabled");
                PumpFor(350);
                AssertTrue(GameInputLanguageNative.LayoutsEqual(GetKeyboardLayout(probeA.ThreadId),
                    new IntPtr(0x04040404)), "disabled watchdog does not inspect shortcut chords");
                gameHelper.Dispose();
                gameHelper = null;

                var disabledSettings = new GameHelperSettings
                {
                    ProtectedApps = GameHelperModule.EncodeProtectedApps(new List<GameHelperProtectedApp>
                    {
                        LiveProtectedApp("Probe A", appA, false)
                    })
                };
                gameHelper = new GameHelperModule(disabledSettings);
                gameHelper.ApplySavedState();
                SetForegroundWindow(probeA.Hwnd);
                PumpFor(400);
                AssertTrue(GameInputLanguageNative.LayoutsEqual(GetKeyboardLayout(probeA.ThreadId),
                    new IntPtr(0x04040404)), "disabled language lock leaves the game HKL unchanged");

                gameHelper.Dispose();
                gameHelper = null;
                PostMessage(probeC.FocusHwnd, GameInputLanguageNative.WmInputLangChangeRequest,
                    IntPtr.Zero, GameInputLanguageNative.MicrosoftEnglishUsLayout);
                AssertLayout(probeC.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    700, "non-game C uses ENG before the game-exit restore test");
                ActivateProbe(probeC, "non-game C is the previous foreground window before game B");

                var closeSettings = new GameHelperSettings
                {
                    ProtectedApps = GameHelperModule.EncodeProtectedApps(new List<GameHelperProtectedApp>
                    {
                        LiveProtectedApp("Probe B", appB, true)
                    })
                };
                gameHelper = new GameHelperModule(closeSettings);
                gameHelper.ApplySavedState();
                ActivateProbe(probeB, "game B becomes foreground before closing");
                AssertLayout(probeB.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout,
                    1500, "game B locks before its window is destroyed");
                PostMessage(probeB.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                AssertTrue(processB.WaitForExit(3000), "game B exits after WM_CLOSE");
                AssertTrue(PumpUntil(delegate
                {
                    return GameInputLanguageNative.GetCurrentForegroundWindow() == probeC.Hwnd;
                }, 1500), "Windows returns foreground to non-game C after game B exits");
                AssertLayout(probeC.ThreadId, GameInputLanguageNative.MicrosoftEnglishUsLayout, 1500,
                    "closing game B preserves the actual pre-game HKL on non-game C");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.PendingInputLanguageRestoreCount == 0;
                }, 1000), "destroyed game HWND does not leave a retrying restore behind");

                Console.WriteLine("[PASS] restricted crosshair A->B/C visibility, foreground hook plus"
                    + " event-driven window lifecycle monitoring, normal game, focus HWND, bottom-level watchdog,"
                    + " all input chords, A->B, rapid switching, non-game follow restore, game-exit"
                    + " restore, watchdog disable, feature disable, and shutdown restore");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] game-helper-live-test: " + ex);
                return 1;
            }
            finally
            {
                if (keyboardGuard != null)
                {
                    keyboardGuard.RestoreShiftSpaceWidthToggle();
                    keyboardGuard.Dispose();
                }
                if (gameHelper != null)
                {
                    gameHelper.Dispose();
                }
                for (int i = 0; i < processes.Count; i++)
                {
                    try
                    {
                        if (!processes[i].HasExited)
                        {
                            processes[i].Kill(true);
                            processes[i].WaitForExit(2000);
                        }
                    }
                    catch
                    {
                    }
                    processes[i].Dispose();
                }
                ReleaseInjectedKeyboardState();
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static int RunCrosshairPositionLiveTest()
        {
            string testRoot = Path.Combine(Path.GetTempPath(), "GuardCenter.Crosshair.Position."
                + Guid.NewGuid().ToString("N"));
            Process process = null;
            GameHelperModule gameHelper = null;
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    _ = new System.Windows.Application
                    {
                        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                    };
                }

                string executable = CopyTestRuntime(testRoot);
                string log = Path.Combine(testRoot, "probe.log");
                var settings = new GameHelperSettings
                {
                    CrosshairEnabled = true,
                    CrosshairRestrictToSelectedApps = true,
                    CrosshairSelectedApps = CrosshairAppSelectionCodec.Encode(
                        new List<CrosshairSelectedApp>
                        {
                            new CrosshairSelectedApp
                            {
                                Name = "Crosshair position probe",
                                TargetPath = executable
                            }
                        })
                };
                gameHelper = new GameHelperModule(settings);
                gameHelper.ApplySavedState();
                process = StartGameHelperProbe(executable, log, "Crosshair position probe");
                LiveProbeInfo probe = WaitForProbe(log, 5000);
                ActivateProbe(probe, "crosshair position main window becomes foreground");
                bool initiallyCentered = PumpUntil(delegate
                {
                    return gameHelper.IsCrosshairOverlayVisible
                        && IsCrosshairCenteredOnTarget(gameHelper, probe.Hwnd);
                }, 2500);
                if (!initiallyCentered)
                {
                    CrosshairTargetWindow.NativeRect initialBounds;
                    CrosshairTargetWindow.TryGetClientBounds(probe.Hwnd, out initialBounds);
                    System.Windows.Point actualCenter =
                        gameHelper.CrosshairOverlayCenterInDevicePixels;
                    throw new InvalidOperationException("crosshair starts at the main client-area"
                        + " center; visible=" + gameHelper.IsCrosshairOverlayVisible
                        + " expected=" + initialBounds.CenterX + "," + initialBounds.CenterY
                        + " overlay=" + actualCenter.X + "," + actualCenter.Y
                        + " target=" + gameHelper.CrosshairTargetHwnd.ToInt64()
                        + " main=" + probe.Hwnd.ToInt64());
                }

                ForceForegroundWindow(probe.PopupHwnd);
                AssertTrue(PumpUntil(delegate
                {
                    return GameInputLanguageNative.GetCurrentForegroundWindow() == probe.PopupHwnd;
                }, 1500), "small same-process popup becomes foreground");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.CrosshairTargetHwnd == probe.Hwnd
                        && IsCrosshairCenteredOnTarget(gameHelper, probe.Hwnd);
                }, 2500), "popup events keep crosshair centered on the main client");

                PostMessage(probe.PopupHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                ActivateProbe(probe, "main window returns after popup closes");
                CrosshairTargetWindow.NativeRect beforeResize;
                AssertTrue(CrosshairTargetWindow.TryGetClientBounds(probe.Hwnd, out beforeResize),
                    "main client bounds are available before resize");
                AssertTrue(SetWindowPos(probe.Hwnd, IntPtr.Zero, 300, 180, 1040, 640, 0x0014),
                    "probe main window changes position and resolution");
                ActivateProbe(probe, "resized main window remains foreground");
                CrosshairTargetWindow.NativeRect afterResize = default(CrosshairTargetWindow.NativeRect);
                AssertTrue(PumpUntil(delegate
                {
                    return CrosshairTargetWindow.TryGetClientBounds(probe.Hwnd, out afterResize)
                        && (Math.Abs(afterResize.CenterX - beforeResize.CenterX) > 25
                            || Math.Abs(afterResize.CenterY - beforeResize.CenterY) > 25)
                        && IsCrosshairCenteredOnTarget(gameHelper, probe.Hwnd);
                }, 2500), "location-change events recenter after the client resolution changes");

                Console.WriteLine("[PASS] crosshair uses the main game client center, ignores a small"
                    + " foreground popup, and recenters after event-driven resolution changes");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] crosshair-position-live-test: " + ex);
                return 1;
            }
            finally
            {
                if (gameHelper != null)
                {
                    gameHelper.Dispose();
                }
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                            process.WaitForExit(2000);
                        }
                    }
                    catch
                    {
                    }
                    process.Dispose();
                }
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static int RunGameHelperWindowEventsLiveTest()
        {
            string testRoot = Path.Combine(Path.GetTempPath(), "GuardCenter.WindowEvents."
                + Guid.NewGuid().ToString("N"));
            var processes = new List<Process>();
            GameHelperModule gameHelper = null;
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    _ = new System.Windows.Application
                    {
                        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                    };
                }

                string appA = CopyTestRuntime(Path.Combine(testRoot, "A"));
                string appB = CopyTestRuntime(Path.Combine(testRoot, "B"));
                string logA = Path.Combine(testRoot, "A.log");
                string logB = Path.Combine(testRoot, "B.log");
                var protectedApps = new List<GameHelperProtectedApp>
                {
                    new GameHelperProtectedApp
                    {
                        Name = "Window event A", TargetPath = appA,
                        ProcessName = Path.GetFileNameWithoutExtension(appA),
                        BlockWindowsKey = true
                    },
                    new GameHelperProtectedApp
                    {
                        Name = "Window event B", TargetPath = appB,
                        ProcessName = Path.GetFileNameWithoutExtension(appB),
                        BlockWindowsKey = true
                    }
                };
                var settings = new GameHelperSettings
                {
                    CrosshairEnabled = true,
                    CrosshairRestrictToSelectedApps = true,
                    CrosshairSelectedApps = CrosshairAppSelectionCodec.Encode(
                        new List<CrosshairSelectedApp>
                        {
                            new CrosshairSelectedApp
                            {
                                Name = "Window event A", TargetPath = appA
                            }
                        }),
                    ProtectedApps = GameHelperModule.EncodeProtectedApps(protectedApps)
                };
                gameHelper = new GameHelperModule(settings);
                gameHelper.ApplySavedState();
                AssertTrue(gameHelper.IsForegroundEventHookActive
                    && gameHelper.ForegroundEventHookCount >= 10,
                    "window event monitor installs its complete hook set");

                Process processA = StartGameHelperProbe(appA, logA, "Window event A");
                Process processB = StartGameHelperProbe(appB, logB, "Window event B");
                processes.Add(processA);
                processes.Add(processB);
                LiveProbeInfo probeA = WaitForProbe(logA, 5000);
                LiveProbeInfo probeB = WaitForProbe(logB, 5000);

                ActivateProbe(probeA, "event probe A becomes foreground");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.ForegroundProtectionHwnd == probeA.Hwnd
                        && gameHelper.IsCrosshairOverlayVisible
                        && gameHelper.CrosshairTargetHwnd == probeA.Hwnd;
                }, 1500), "foreground event activates protection and crosshair for A");

                ForceForegroundWindow(probeA.PopupHwnd);
                AssertTrue(PumpUntil(delegate
                {
                    return GameInputLanguageNative.GetCurrentForegroundWindow() == probeA.PopupHwnd
                        && gameHelper.ForegroundProtectionHwnd == probeA.PopupHwnd
                        && gameHelper.CrosshairTargetHwnd == probeA.Hwnd;
                }, 1500), "same-process popup event preserves the main game target");

                PostMessage(probeA.PopupHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                ActivateProbe(probeA, "A main window returns after popup close");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.ForegroundProtectionHwnd == probeA.Hwnd
                        && gameHelper.CrosshairTargetHwnd == probeA.Hwnd;
                }, 1500), "hide/destroy and foreground events restore the main window state");

                for (int i = 0; i < 8; i++)
                {
                    ForceForegroundWindow(i % 2 == 0 ? probeB.Hwnd : probeA.Hwnd);
                    PumpFor(35);
                }
                ActivateProbe(probeB, "rapid switching settles on B");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.ForegroundProtectionHwnd == probeB.Hwnd
                        && !gameHelper.IsCrosshairOverlayVisible;
                }, 1500), "coalesced events settle on the final unselected app B");

                ActivateProbe(probeA, "A returns after rapid switching");
                AssertTrue(PumpUntil(delegate
                {
                    return gameHelper.ForegroundProtectionHwnd == probeA.Hwnd
                        && gameHelper.IsCrosshairOverlayVisible;
                }, 1500), "event monitor restores A after rapid switching");

                PumpFor(250);
                AssertFalse(gameHelper.IsForegroundReconciliationPending,
                    "event reconciliation becomes idle after the final event");
                PumpFor(1250);
                AssertFalse(gameHelper.IsForegroundReconciliationPending,
                    "idle monitoring does not restart a periodic reconciliation timer");

                Console.WriteLine("[PASS] event-driven Game Helper handles game/popup clicks,"
                    + " popup close, rapid app switching, and stays idle without polling");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] game-helper-window-events-live-test: " + ex);
                return 1;
            }
            finally
            {
                if (gameHelper != null) gameHelper.Dispose();
                for (int i = 0; i < processes.Count; i++)
                {
                    try
                    {
                        if (!processes[i].HasExited)
                        {
                            processes[i].Kill(true);
                            processes[i].WaitForExit(2000);
                        }
                    }
                    catch
                    {
                    }
                    processes[i].Dispose();
                }
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static int RunKeyboardWidthProbeTest()
        {
            ReleaseInjectedKeyboardState();
            string testRoot = Path.Combine(Path.GetTempPath(), "GuardCenter.KeyboardWidth.Live."
                + Guid.NewGuid().ToString("N"));
            string asusConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ASUS", "AsusIME", "zhtw", "data", Environment.UserName, "AsusIMEConfig.ini");
            string originalAsusMode = ReadIniValue(asusConfigPath, "IsInFullWidthMode");
            Process process = null;
            KeyboardGuardModule keyboardGuard = null;
            try
            {
                AssertTrue(originalAsusMode == "0" || originalAsusMode == "1",
                    "installed ASUS IME exposes a valid character-width state");
                string executable = CopyTestRuntime(testRoot);
                string log = Path.Combine(testRoot, "probe.log");
                var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false };
                startInfo.ArgumentList.Add("game-helper-window");
                startInfo.ArgumentList.Add("--title");
                startInfo.ArgumentList.Add("Keyboard width probe");
                startInfo.ArgumentList.Add("--log");
                startInfo.ArgumentList.Add(log);
                startInfo.ArgumentList.Add("--layout");
                startInfo.ArgumentList.Add("00000409");
                startInfo.ArgumentList.Add("--focus-edit");
                startInfo.ArgumentList.Add("true");
                process = Process.Start(startInfo) ?? throw new InvalidOperationException("Probe did not start.");
                LiveProbeInfo probe = WaitForProbe(log, 5000);
                ActivateProbe(probe, "keyboard width probe becomes foreground");

                for (int i = 0; i < 3 && !GameInputLanguageNative.LayoutsEqual(
                    GetKeyboardLayout(probe.ThreadId), new IntPtr(0x04040404)); i++)
                {
                    keybd_event(0x5B, 0, 0, UIntPtr.Zero);
                    keybd_event(0x20, 0, 0, UIntPtr.Zero);
                    keybd_event(0x20, 0, 0x0002, UIntPtr.Zero);
                    keybd_event(0x5B, 0, 0x0002, UIntPtr.Zero);
                    PumpFor(250);
                }
                AssertLayout(probe.ThreadId, new IntPtr(0x04040404), 1000,
                    "Win+Space activates the installed Traditional Chinese TSF profile");

                keyboardGuard = new KeyboardGuardModule(new KeyboardGuardSettings());
                AssertTrue(keyboardGuard.DisableShiftSpaceWidthToggle(),
                    "final Keyboard Guard enables ASUS/Microsoft character-width protection");
                AssertChordPassesGameHelper(log, 0x10, 0x20, true,
                    "Shift+Space down/up still reaches the focused application");
                AssertTrue(PumpUntil(delegate
                {
                    return ReadIniValue(asusConfigPath, "IsInFullWidthMode") == originalAsusMode;
                }, 1000), "ASUS IME character width returns to its captured state");

                AssertTrue(keyboardGuard.RestoreShiftSpaceWidthToggle(),
                    "Keyboard Guard restores the vendor shortcut behavior");
                keyboardGuard.Dispose();
                keyboardGuard = null;
                AssertChordPassesGameHelper(log, 0x10, 0x20, true,
                    "Shift+Space still reaches the application after protection is disabled");
                AssertTrue(PumpUntil(delegate
                {
                    return ReadIniValue(asusConfigPath, "IsInFullWidthMode") != originalAsusMode;
                }, 1000), "ASUS IME width toggle works again after restore");

                Console.WriteLine("[PASS] final Keyboard Guard preserves Shift+Space key events,"
                    + " locks ASUS IME width state, and restores vendor behavior");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] keyboard-width-probe-test: " + ex);
                return 1;
            }
            finally
            {
                if (keyboardGuard != null)
                {
                    keyboardGuard.RestoreShiftSpaceWidthToggle();
                    keyboardGuard.Dispose();
                }
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                    }
                    catch { }
                    process.Dispose();
                }
                if (originalAsusMode == "0" || originalAsusMode == "1")
                {
                    WriteIniValue(asusConfigPath, "IsInFullWidthMode", originalAsusMode);
                }
                ReleaseInjectedKeyboardState();
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static string ReadIniValue(string path, string name)
        {
            if (!File.Exists(path)) return string.Empty;
            string prefix = name + "=";
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return lines[i].Substring(prefix.Length).Trim();
            }
            return string.Empty;
        }

        private static void WriteIniValue(string path, string name, string value)
        {
            const int attempts = 10;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (WritePrivateProfileString("Setting", name, value, path)) return;
                if (attempt + 1 < attempts) Thread.Sleep(25);
            }
            throw new InvalidOperationException("Unable to write INI value: " + name
                + " (Win32 error " + Marshal.GetLastWin32Error() + ")");
        }

        private static GameHelperProtectedApp LiveProtectedApp(string name, string path, bool enabled)
        {
            return new GameHelperProtectedApp
            {
                Name = name,
                TargetPath = path,
                Publisher = "Guard Center live test",
                LockMicrosoftEnglish = enabled,
                BlockInputLanguageSwitch = true,
                RestorePreviousInputLanguage = true
            };
        }

        private static string CopyTestRuntime(string destination)
        {
            string source = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }

            string appHostName = Path.GetFileNameWithoutExtension(
                typeof(Program).Assembly.Location) + ".exe";
            string appHostPath = Path.Combine(destination, appHostName);
            if (!File.Exists(appHostPath))
                throw new FileNotFoundException("Test app host was not copied.", appHostPath);
            return appHostPath;
        }

        private static Process StartGameHelperProbe(string executable, string log, string title)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("game-helper-window");
            startInfo.ArgumentList.Add("--title");
            startInfo.ArgumentList.Add(title);
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(log);
            startInfo.ArgumentList.Add("--layout");
            startInfo.ArgumentList.Add("00000404");
            return Process.Start(startInfo) ?? throw new InvalidOperationException("Probe did not start.");
        }

        private static LiveProbeInfo WaitForProbe(string log, int timeoutMs)
        {
            LiveProbeInfo result = null;
            AssertTrue(PumpUntil(delegate
            {
                if (!File.Exists(log)) return false;
                string[] lines = ReadSharedLogLines(log);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(" READY ", StringComparison.Ordinal) < 0) continue;
                    result = LiveProbeInfo.Parse(lines[i]);
                    return result != null;
                }
                return false;
            }, timeoutMs), "probe window becomes ready");
            return result;
        }

        private static void AssertLayout(uint threadId, IntPtr expected, int timeoutMs, string message)
        {
            AssertTrue(PumpUntil(delegate
            {
                return GameInputLanguageNative.LayoutsEqual(GetKeyboardLayout(threadId), expected);
            }, timeoutMs), message + "; actual=0x" + GetKeyboardLayout(threadId).ToInt64().ToString("X"));
        }

        private static void ActivateProbe(LiveProbeInfo probe, string message)
        {
            AssertTrue(PumpUntil(delegate
            {
                ForceForegroundWindow(probe.Hwnd);
                return GameInputLanguageNative.GetCurrentForegroundWindow() == probe.Hwnd;
            }, 1500), message);
            PumpFor(120);
        }

        private static bool IsCrosshairCenteredOnTarget(GameHelperModule module, IntPtr targetHwnd)
        {
            CrosshairTargetWindow.NativeRect bounds;
            if (module == null || module.CrosshairTargetHwnd != targetHwnd
                || !CrosshairTargetWindow.TryGetClientBounds(targetHwnd, out bounds))
            {
                return false;
            }

            System.Windows.Point actual = module.CrosshairOverlayCenterInDevicePixels;
            return !double.IsNaN(actual.X) && !double.IsNaN(actual.Y)
                && Math.Abs(actual.X - bounds.CenterX) <= 2.0
                && Math.Abs(actual.Y - bounds.CenterY) <= 2.0;
        }

        private static void ForceForegroundWindow(IntPtr hwnd)
        {
            uint currentThread = GetCurrentThreadId();
            IntPtr currentForeground = GameInputLanguageNative.GetCurrentForegroundWindow();
            uint ignored;
            uint foregroundThread = currentForeground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(currentForeground, out ignored);
            uint targetThread = GetWindowThreadProcessId(hwnd, out ignored);
            bool attachedForeground = foregroundThread != 0 && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            bool attachedTarget = targetThread != 0 && targetThread != currentThread
                && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                ShowWindow(hwnd, 5);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
                if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        private static void AssertChordPassesGameHelper(string log, byte firstKey, byte secondKey,
            bool expectFirstKey, string message)
        {
            int startLine = File.Exists(log) ? ReadSharedLogLines(log).Length : 0;
            keybd_event(firstKey, 0, 0, UIntPtr.Zero);
            keybd_event(secondKey, 0, 0, UIntPtr.Zero);
            keybd_event(secondKey, 0, 0x0002, UIntPtr.Zero);
            keybd_event(firstKey, 0, 0x0002, UIntPtr.Zero);
            bool received = PumpUntil(delegate
            {
                string[] lines = ReadSharedLogLines(log);
                bool secondDown = false;
                bool secondUp = false;
                bool firstDown = false;
                bool firstUp = false;
                for (int i = startLine; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf("QUEUEDKEY", StringComparison.Ordinal) < 0) continue;
                    bool down = lines[i].IndexOf("message=0x100", StringComparison.Ordinal) >= 0
                        || lines[i].IndexOf("message=0x104", StringComparison.Ordinal) >= 0;
                    bool up = lines[i].IndexOf("message=0x101", StringComparison.Ordinal) >= 0
                        || lines[i].IndexOf("message=0x105", StringComparison.Ordinal) >= 0;
                    bool processKey = lines[i].IndexOf(" vk=0xE5", StringComparison.Ordinal) >= 0;
                    bool second = processKey || lines[i].IndexOf(" vk=0x"
                        + secondKey.ToString("X"), StringComparison.Ordinal) >= 0;
                    bool first = lines[i].IndexOf(" vk=0x" + firstKey.ToString("X"),
                        StringComparison.Ordinal) >= 0;
                    if (second && down) secondDown = true;
                    if (second && up) secondUp = true;
                    if (first && down) firstDown = true;
                    if (first && up) firstUp = true;
                }
                return secondDown && secondUp && (!expectFirstKey || (firstDown && firstUp));
            }, 1000);
            if (!received)
            {
                string[] lines = ReadSharedLogLines(log);
                int safeStart = Math.Min(startLine, lines.Length);
                throw new InvalidOperationException(message + "; observed: "
                    + string.Join(" || ", lines, safeStart, lines.Length - safeStart));
            }
        }

        private static void ReleaseInjectedKeyboardState()
        {
            byte[] keys =
            {
                0x5B, 0x5C,
                0x10, 0xA0, 0xA1,
                0x11, 0xA2, 0xA3,
                0x12, 0xA4, 0xA5,
                0x09, 0x20, 0x44
            };
            for (int i = 0; i < keys.Length; i++)
            {
                keybd_event(keys[i], 0, 0x0002, UIntPtr.Zero);
            }
            Thread.Sleep(50);
        }


        private static string[] ReadSharedLogLines(string path)
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string text = reader.ReadToEnd();
                return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static bool PumpUntil(Func<bool> condition, int timeoutMs)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                PumpFor(10);
            }
            return condition();
        }

        private static void PumpFor(int milliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds))
            };
            timer.Tick += delegate
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        private sealed class LiveProbeInfo
        {
            public IntPtr Hwnd;
            public IntPtr FocusHwnd;
            public IntPtr PopupHwnd;
            public uint ThreadId;

            public static LiveProbeInfo Parse(string line)
            {
                var result = new LiveProbeInfo();
                string[] parts = line.Split(' ');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].StartsWith("hwnd=", StringComparison.Ordinal))
                        result.Hwnd = new IntPtr(long.Parse(parts[i].Substring(5)));
                    else if (parts[i].StartsWith("focus=", StringComparison.Ordinal))
                        result.FocusHwnd = new IntPtr(long.Parse(parts[i].Substring(6)));
                    else if (parts[i].StartsWith("popup=", StringComparison.Ordinal))
                        result.PopupHwnd = new IntPtr(long.Parse(parts[i].Substring(6)));
                    else if (parts[i].StartsWith("thread=", StringComparison.Ordinal))
                        result.ThreadId = uint.Parse(parts[i].Substring(7));
                }
                return result.Hwnd != IntPtr.Zero && result.PopupHwnd != IntPtr.Zero
                    && result.ThreadId != 0 ? result : null;
            }
        }

        private static int RunGameHelperWindowProbe(string[] args)
        {
            string title = GetTestArg(args, "--title", "GuardCenter Game Probe");
            gameHelperProbeLogPath = GetTestArg(args, "--log", string.Empty);
            string initialLayout = GetTestArg(args, "--layout", string.Empty);
            if (!string.IsNullOrWhiteSpace(initialLayout))
            {
                IntPtr loadedLayout = LoadKeyboardLayout(initialLayout, 0x00000001);
                if (loadedLayout == IntPtr.Zero || ActivateKeyboardLayout(loadedLayout, 0) == IntPtr.Zero)
                {
                    WriteGameHelperProbeLog("LAYOUT_SETUP_FAILED error=" + Marshal.GetLastWin32Error());
                }
            }
            IntPtr console = GetConsoleWindow();
            if (console != IntPtr.Zero) ShowWindow(console, 0);

            gameHelperProbeWindowProc = GameHelperProbeWindowProc;
            IntPtr instance = GetModuleHandle(null);
            string className = "GuardCenter.GameHelperProbe." + Process.GetCurrentProcess().Id;
            var windowClass = new NativeWindowClass
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeWindowClass)),
                lpfnWndProc = gameHelperProbeWindowProc,
                hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                lpszClassName = className
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                return Marshal.GetLastWin32Error();
            }

            IntPtr hwnd = CreateWindowEx(0, className, title, 0x00CF0000,
                160, 160, 720, 420, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            gameHelperProbeMainWindow = hwnd;
            string focusClass = string.Equals(GetTestArg(args, "--focus-edit", "false"), "true",
                StringComparison.OrdinalIgnoreCase) ? "EDIT" : className;
            gameHelperProbeFocusWindow = CreateWindowEx(0, focusClass, title + " Focus",
                0x50010000, 24, 24, 640, 320, hwnd, IntPtr.Zero, instance, IntPtr.Zero);
            if (gameHelperProbeFocusWindow == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            gameHelperProbePopupWindow = CreateWindowEx(0x00000080, className, title + " Popup",
                0x80C80000, 260, 220, 360, 180, hwnd, IntPtr.Zero, instance, IntPtr.Zero);
            if (gameHelperProbePopupWindow == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            ShowWindow(hwnd, 5);
            UpdateWindow(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(gameHelperProbeFocusWindow);
            WriteGameHelperProbeLog("READY hwnd=" + hwnd.ToInt64() + " thread="
                + GetCurrentThreadId() + " focus=" + gameHelperProbeFocusWindow.ToInt64()
                + " popup=" + gameHelperProbePopupWindow.ToInt64()
                + " layout=0x" + GetKeyboardLayout(0).ToInt64().ToString("X"));

            NativeMessage message;
            while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == 0x0100 || message.message == 0x0101
                    || message.message == 0x0104 || message.message == 0x0105)
                {
                    WriteGameHelperProbeLog("QUEUEDKEY message=0x" + message.message.ToString("X")
                        + " vk=0x" + message.wParam.ToInt64().ToString("X")
                        + " hwnd=" + message.hwnd.ToInt64());
                }
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            return 0;
        }

        private static IntPtr GameHelperProbeWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == 0x0010 && hwnd == gameHelperProbeMainWindow)
            {
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            if (message == 0x0002 && hwnd == gameHelperProbeMainWindow)
            {
                WriteGameHelperProbeLog("DESTROY layout=0x" + GetKeyboardLayout(0).ToInt64().ToString("X"));
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
            if (message == 0x0051)
            {
                WriteGameHelperProbeLog("INPUTLANGCHANGE layout=0x"
                    + GetKeyboardLayout(0).ToInt64().ToString("X") + " hwnd=" + hwnd.ToInt64());
            }
            if (message == 0x0100 || message == 0x0101 || message == 0x0104 || message == 0x0105)
            {
                WriteGameHelperProbeLog("KEY message=0x" + message.ToString("X")
                    + " vk=0x" + wParam.ToInt64().ToString("X") + " hwnd=" + hwnd.ToInt64());
            }
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        private static void WriteGameHelperProbeLog(string message)
        {
            if (string.IsNullOrWhiteSpace(gameHelperProbeLogPath))
            {
                return;
            }
            using (var stream = new FileStream(gameHelperProbeLogPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(DateTime.UtcNow.ToString("O") + " " + message);
            }
        }

        private static string GetTestArg(string[] args, string key, string fallback)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return fallback;
        }

        private static int ProbeHardware(bool writeCurrent)
        {
            try
            {
                List<DisplayGuardDevice> devices = new CompositeDisplayGuardProvider().EnumerateMonitors();
                Console.WriteLine("Detected " + devices.Count + " display record(s).");
                for (int i = 0; i < devices.Count; i++)
                {
                    DisplayGuardMonitorInfo info = devices[i].ToInfo();
                    Console.WriteLine(info.DisplayName
                        + " | runtime=" + info.RuntimeId
                        + " | stable=" + (info.StableIdReliable ? info.StableId : "unmatched")
                        + " | primary=" + info.IsPrimary
                        + " | kind=" + info.ControlKind
                        + " | brightness=" + (info.SupportsBrightness ? info.BrightnessPercent + "%" : "no")
                        + " | contrast=" + (info.SupportsContrast ? info.ContrastPercent + "%" : "no")
                        + (string.IsNullOrWhiteSpace(info.LastError) ? string.Empty : " | error=" + info.LastError));

                    if (writeCurrent)
                    {
                        string error;
                        if (info.SupportsBrightness)
                        {
                            Console.WriteLine("  set brightness current: "
                                + (devices[i].SetBrightnessPercent(info.BrightnessPercent, out error) ? "ok" : error));
                        }
                        if (info.SupportsContrast)
                        {
                            Console.WriteLine("  set contrast current: "
                                + (devices[i].SetContrastPercent(info.ContrastPercent, out error) ? "ok" : error));
                        }
                    }
                }

                for (int i = 0; i < devices.Count; i++)
                {
                    devices[i].Dispose();
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Probe failed: " + ex);
                return 1;
            }
        }

        private static int TestPowerRequestLive(string[] args)
        {
            bool keepDisplayOn = args.Length > 1
                && string.Equals(args[1], "display", StringComparison.OrdinalIgnoreCase);
            int holdSeconds = 2;
            if (args.Length > 2)
            {
                int.TryParse(args[2], out holdSeconds);
                holdSeconds = Math.Max(1, Math.Min(120, holdSeconds));
            }

            var factory = new WindowsPowerRequestLeaseFactory();
            IPowerRequestLease lease;
            string error;
            if (!factory.TryCreate(keepDisplayOn, out lease, out error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            try
            {
                Console.WriteLine("Power Request active. PID=" + Environment.ProcessId
                    + " system=true display=" + keepDisplayOn + " holdSeconds=" + holdSeconds);
                Thread.Sleep(TimeSpan.FromSeconds(holdSeconds));
                Console.WriteLine("Power Request validation interval completed.");
                return 0;
            }
            finally
            {
                lease.Dispose();
                Console.WriteLine("Power Request cleared and handle disposed.");
            }
        }

        private static int ProbeDevices()
        {
            try
            {
                List<DeviceGuardDevice> devices = new WindowsDeviceInventory().Scan();
                Console.WriteLine("Detected " + devices.Count + " Device Guard target(s).");
                for (int i = 0; i < devices.Count; i++)
                {
                    DeviceGuardDevice device = devices[i];
                    Console.WriteLine(device.DisplayName + " | kind=" + device.Kind
                        + " | present=" + device.IsPresent + " | problem=" + device.ProblemCode
                        + " | repair=" + device.CanRepair + " | repairAll=" + device.IncludeInRepairAll
                        + " | service=" + device.Service + " | driver=" + device.DriverVersion);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Device probe failed: " + ex);
                return 1;
            }
        }

        private static int ProbeCoreDevices()
        {
            try
            {
                var inventory = new WindowsPnPInventory();
                WindowsPnPSnapshot snapshot = inventory.Capture();
                var baseline = new CoreHardwareBaselineStore(AppPaths.DeviceGuardBaselinePath).Load();
                List<CoreHardwareItem> items = new CoreHardwareResolver().Resolve(snapshot, baseline);
                Console.WriteLine("Detected " + items.Count + " core hardware item(s) from "
                    + snapshot.Devices.Count + " installed PnP node(s).");
                for (int i = 0; i < items.Count; i++)
                {
                    CoreHardwareItem item = items[i];
                    Console.WriteLine(item.Capability + " | " + item.DisplayName + " | health=" + item.Health
                        + " | present=" + item.IsPresent + " | problem=" + item.ProblemCode
                        + " | baseline=" + item.FromBaseline + " | usbBlocked=" + item.UsbRestartBlocked
                        + " | driver=" + item.DriverInfPath);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Core device probe failed: " + ex);
                return 1;
            }
        }

        private static int ProbeInputStack()
        {
            try
            {
                WindowsPnPSnapshot pnpSnapshot = new WindowsPnPInventory().Capture();
                InputStackSnapshot snapshot = new WindowsInputStackInventory().Capture(pnpSnapshot);
                Console.WriteLine("Input stack health: " + snapshot.Health);
                Console.WriteLine(snapshot.Summary);
                Console.WriteLine("Keyboard UpperFilters: "
                    + string.Join(", ", snapshot.KeyboardUpperFilters));
                Console.WriteLine("Mouse UpperFilters: "
                    + string.Join(", ", snapshot.MouseUpperFilters));
                Console.WriteLine("Keyboard class objects: "
                    + string.Join(", ", snapshot.KeyboardClassDevices));
                Console.WriteLine("Pointer class objects: "
                    + string.Join(", ", snapshot.PointerClassDevices));
                Console.WriteLine("Highest class indexes: keyboard="
                    + snapshot.HighestKeyboardClassIndex + ", pointer="
                    + snapshot.HighestPointerClassIndex);
                Console.WriteLine("Surprise removals since boot: HID="
                    + snapshot.HidSurpriseRemovalCount + ", recognized input="
                    + snapshot.InputSurpriseRemovalCount);
                Console.WriteLine("keyboard.sys: " + snapshot.KeyboardDriverProduct + " "
                    + snapshot.KeyboardDriverVersion + " signature="
                    + snapshot.KeyboardDriverSignatureValid);
                Console.WriteLine("mouse.sys: " + snapshot.MouseDriverProduct + " "
                    + snapshot.MouseDriverVersion + " signature="
                    + snapshot.MouseDriverSignatureValid);
                if (!string.IsNullOrWhiteSpace(snapshot.DiagnosticError))
                    Console.WriteLine("Diagnostic limitations: " + snapshot.DiagnosticError);
                return snapshot.Health == InputStackHealth.Unknown ? 2 : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Input stack probe failed: " + ex);
                return 1;
            }
        }

        private static int TestStickyKeysLiveAndRestore()
        {
            var api = new WindowsStickyKeysSettingsApi();
            uint original;
            string error;
            if (!api.TryRead(out original, out error))
            {
                Console.WriteLine("StickyKeys read failed: " + error);
                return 1;
            }

            var settings = new KeyboardGuardSettings();
            var module = new KeyboardGuardModule(settings, api);
            bool restored = false;
            try
            {
                Console.WriteLine("Original StickyKeys flags: 0x" + original.ToString("X8"));
                if (!module.DisableStickyKeysHotkey())
                {
                    Console.WriteLine(module.StatusText);
                    return 1;
                }

                uint protectedFlags;
                if (!api.TryRead(out protectedFlags, out error)
                    || (protectedFlags & KeyboardGuardModule.StickyKeysHotkeyActive) != 0)
                {
                    Console.WriteLine("SKF_HOTKEYACTIVE was not cleared: " + error);
                    return 1;
                }
                Console.WriteLine("Protected StickyKeys flags: 0x" + protectedFlags.ToString("X8"));

                for (int i = 0; i < 5; i++)
                {
                    if (!SendShiftKey())
                    {
                        Console.WriteLine("SendInput failed: " + Marshal.GetLastWin32Error());
                        return 1;
                    }
                    Thread.Sleep(80);
                }
                Thread.Sleep(600);

                uint afterFiveShifts;
                if (!api.TryRead(out afterFiveShifts, out error))
                {
                    Console.WriteLine(error);
                    return 1;
                }
                if ((afterFiveShifts & KeyboardGuardModule.StickyKeysHotkeyActive) != 0
                    || (afterFiveShifts & 0x00000001) != (original & 0x00000001))
                {
                    Console.WriteLine("Five Shift presses changed StickyKeys unexpectedly: 0x"
                        + afterFiveShifts.ToString("X8"));
                    return 1;
                }
                Console.WriteLine("Five Shift presses left StickyKeys inactive: 0x"
                    + afterFiveShifts.ToString("X8"));

                if (!module.RestoreStickyKeysHotkey())
                {
                    Console.WriteLine(module.StatusText);
                    return 1;
                }
                restored = true;
                uint finalFlags;
                if (!api.TryRead(out finalFlags, out error)
                    || (finalFlags & KeyboardGuardModule.StickyKeysHotkeyActive)
                        != (original & KeyboardGuardModule.StickyKeysHotkeyActive))
                {
                    Console.WriteLine("Original hotkey bit was not restored: " + error);
                    return 1;
                }
                Console.WriteLine("Restored StickyKeys flags: 0x" + finalFlags.ToString("X8"));
                return 0;
            }
            finally
            {
                if (!restored)
                {
                    restored = module.RestoreStickyKeysHotkey();
                }
                module.Dispose();
                if (!restored)
                {
                    uint current;
                    if (api.TryRead(out current, out error))
                    {
                        api.TryWrite(KeyboardGuardModule.SetStickyKeysHotkey(current,
                            (original & KeyboardGuardModule.StickyKeysHotkeyActive) != 0), out error);
                    }
                }
            }
        }

        private static int RestoreStickyKeysHotkeyBitLive()
        {
            var api = new WindowsStickyKeysSettingsApi();
            uint current;
            string error;
            if (!api.TryRead(out current, out error))
            {
                Console.WriteLine(error);
                return 1;
            }
            uint desired = KeyboardGuardModule.SetStickyKeysHotkey(current, true);
            if (!api.TryWrite(desired, out error))
            {
                Console.WriteLine(error);
                return 1;
            }
            uint verified;
            if (!api.TryRead(out verified, out error)
                || (verified & KeyboardGuardModule.StickyKeysHotkeyActive) == 0)
            {
                Console.WriteLine("Restore verification failed: " + error);
                return 1;
            }
            Console.WriteLine("StickyKeys flags restored to 0x" + verified.ToString("X8"));
            return 0;
        }

        private static bool SendShiftKey()
        {
            var down = new Input
            {
                Type = 1,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x10 } }
            };
            var up = new Input
            {
                Type = 1,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput { VirtualKey = 0x10, Flags = 0x0002 }
                }
            };
            Input[] inputs = new[] { down, up };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) == inputs.Length;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KeyboardInput Keyboard;

            [FieldOffset(0)]
            public MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        private static int TestDisplayHandleLifetime()
        {
            List<DisplayGuardDevice> first = null;
            List<DisplayGuardDevice> second = null;
            try
            {
                var provider = new CompositeDisplayGuardProvider();
                first = provider.EnumerateMonitors();
                second = provider.EnumerateMonitors();
                DisposeDisplays(first);
                first = null;

                for (int i = 0; i < second.Count; i++)
                {
                    DisplayGuardMonitorInfo info = second[i].ToInfo();
                    if (!info.SupportsBrightness)
                    {
                        continue;
                    }

                    string error;
                    bool ok = second[i].SetBrightnessPercent(info.BrightnessPercent, out error);
                    Console.WriteLine(info.DisplayName + " after prior enumeration disposal: "
                        + (ok ? "ok" : error));
                    return ok ? 0 : 1;
                }

                Console.WriteLine("No brightness-capable display was found.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Handle lifetime test failed: " + ex);
                return 1;
            }
            finally
            {
                DisposeDisplays(first);
                DisposeDisplays(second);
            }
        }

        private static int TestAudioEndpointVolumePersists(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.WriteLine("Usage: audio-endpoint-live-test <endpoint-id>");
                return 2;
            }

            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioEndpointVolume volume = null;
            object activated = null;
            float originalLevel = 0.0f;
            bool originalRead = false;
            Guid eventContext = new Guid("F5C7192B-F5D4-4A33-B746-1611D8374894");

            try
            {
                Type enumeratorType = Type.GetTypeFromCLSID(
                    new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
                AssertHResult(enumerator.GetDevice(args[1], out device), "IMMDeviceEnumerator.GetDevice");

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                AssertHResult(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out activated),
                    "IMMDevice.Activate(IAudioEndpointVolume)");
                volume = (IAudioEndpointVolume)activated;

                AssertHResult(volume.GetMasterVolumeLevelScalar(out originalLevel),
                    "IAudioEndpointVolume.GetMasterVolumeLevelScalar(original)");
                originalRead = true;

                const float targetLevel = 0.12f;
                AssertHResult(volume.SetMasterVolumeLevelScalar(targetLevel, ref eventContext),
                    "IAudioEndpointVolume.SetMasterVolumeLevelScalar(test)");
                Thread.Sleep(2200);

                float actualLevel;
                AssertHResult(volume.GetMasterVolumeLevelScalar(out actualLevel),
                    "IAudioEndpointVolume.GetMasterVolumeLevelScalar(actual)");
                Console.WriteLine("Audio endpoint volume target=" + targetLevel.ToString("0.000")
                    + " actual=" + actualLevel.ToString("0.000")
                    + " after 2200ms endpoint=" + args[1]);

                return Math.Abs(actualLevel - targetLevel) <= 0.01f ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Audio endpoint live test failed: " + ex);
                return 1;
            }
            finally
            {
                if (volume != null && originalRead)
                {
                    AssertHResult(volume.SetMasterVolumeLevelScalar(originalLevel, ref eventContext),
                        "IAudioEndpointVolume.SetMasterVolumeLevelScalar(restore)");
                }

                ReleaseComObject(volume);
                if (volume == null)
                {
                    ReleaseComObject(activated);
                }
                ReleaseComObject(device);
                ReleaseComObject(enumerator);
            }
        }

        private static void AssertHResult(int hr, string operation)
        {
            if (hr < 0)
            {
                throw new InvalidOperationException(operation + " failed with HRESULT 0x" + hr.ToString("X8"),
                    Marshal.GetExceptionForHR(hr));
            }
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }

        private static void AudioZeroPropertyChangesCheckTopologyWithoutImmediateZero()
        {
            AssertEqual(AudioZeroEventAction.CheckTopology,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.PropertyChanged, true),
                "enabled property-change handling checks topology");
            AssertEqual(AudioZeroEventAction.Ignore,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.PropertyChanged, false),
                "disabled property-change handling ignores the notification");
            AssertEqual(AudioZeroEventAction.ZeroImmediately,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.DeviceStateChanged, true,
                    DeviceStateMask.Active),
                "active device state changes retain immediate protection");
            AssertEqual(AudioZeroEventAction.CheckTopology,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.DeviceStateChanged, true,
                    DeviceStateMask.Disabled),
                "disabled device state changes do not zero unrelated active endpoints");
            AssertEqual(AudioZeroEventAction.CheckTopology,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.DeviceStateChanged, true,
                    DeviceStateMask.NotPresent),
                "not-present device state changes do not zero unrelated active endpoints");
            AssertEqual(AudioZeroEventAction.CheckTopology,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.DeviceStateChanged, true,
                    DeviceStateMask.Unplugged),
                "unplugged device state changes do not zero unrelated active endpoints");
            AssertEqual(AudioZeroEventAction.ZeroImmediately,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.DeviceAdded, true),
                "device additions retain immediate protection");
            AssertEqual(AudioZeroEventAction.ZeroImmediately,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.DeviceRemoved, true),
                "device removals retain immediate protection");
            AssertEqual(AudioZeroEventAction.ZeroImmediately,
                AudioZeroEventPolicy.Resolve(AudioZeroDeviceEvent.DefaultRenderChanged, true),
                "default render changes retain immediate protection");
        }

        private static void DisposeDisplays(List<DisplayGuardDevice> devices)
        {
            if (devices == null)
            {
                return;
            }

            for (int i = 0; i < devices.Count; i++)
            {
                devices[i].Dispose();
            }
        }

        private static int TestDisplayWriteAndRestore()
        {
            List<DisplayGuardDevice> devices = new CompositeDisplayGuardProvider().EnumerateMonitors();
            try
            {
                DisplayGuardDevice target = devices.Find(delegate(DisplayGuardDevice device)
                {
                    return device.SupportsBrightness
                        && string.Equals(device.ControlKind, "DDC/CI", StringComparison.OrdinalIgnoreCase);
                });
                if (target == null)
                {
                    Console.WriteLine("No active DDC/CI brightness target was found.");
                    return 2;
                }

                DisplayGuardMonitorInfo before = target.ToInfo();
                int testValue = before.BrightnessPercent > 0
                    ? before.BrightnessPercent - 1
                    : before.BrightnessPercent + 1;
                string error;
                Console.WriteLine("Testing " + target.DisplayName + " brightness "
                    + before.BrightnessPercent + "% -> " + testValue + "% -> restore.");
                bool changed = target.SetBrightnessPercent(testValue, out error);
                DisplayGuardMonitorInfo changedInfo = target.ToInfo();
                Console.WriteLine("Change result=" + changed + " readback=" + changedInfo.BrightnessPercent
                    + "% error=" + error);
                bool restored = target.SetBrightnessPercent(before.BrightnessPercent, out error);
                DisplayGuardMonitorInfo restoredInfo = target.ToInfo();
                Console.WriteLine("Restore result=" + restored + " readback=" + restoredInfo.BrightnessPercent
                    + "% error=" + error);
                return changed && restored && changedInfo.BrightnessPercent == testValue
                    && restoredInfo.BrightnessPercent == before.BrightnessPercent ? 0 : 1;
            }
            finally
            {
                for (int i = 0; i < devices.Count; i++) devices[i].Dispose();
            }
        }

        private static void UacGuardAutomaticAuthorizationReadiness()
        {
            var status = new UacGuardStatus
            {
                CodexInstalled = true,
                RunnerInstalled = true,
                RunnerAclProtected = true,
                LifecycleTaskInstalled = true,
                LifecycleTaskConfigurationValid = true,
                GsudoInstalled = true
            };

            AssertTrue(status.IsReady, "installed automatic authorization components make UAC Guard ready");
            AssertTrue(status.StatusText.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0,
                "ready status describes automatic authorization readiness");
            status.CodexIsRunning = true;
            AssertTrue(status.StatusText.IndexOf("automatic authorization", StringComparison.OrdinalIgnoreCase) >= 0,
                "a running GUI without a session is reported as preparing automatic authorization");
            status.GsudoSessionActive = true;
            status.GsudoSessionMode = UacGuardModule.CodexProcessMode;
            status.GsudoTargetProcessId = 4321;
            AssertTrue(status.StatusText.IndexOf("gsudo", StringComparison.OrdinalIgnoreCase) >= 0
                    && status.StatusText.IndexOf("4321", StringComparison.OrdinalIgnoreCase) >= 0,
                "active gsudo session reports its bound Codex PID");
            status.GsudoSessionMode = UacGuardModule.GuardCenterLifecycleMode;
            AssertTrue(status.StatusText.IndexOf("Guard Center lifecycle", StringComparison.OrdinalIgnoreCase) >= 0,
                "lifecycle session status does not claim to be bound to a Codex PID");
            status.GsudoInstalled = false;
            AssertFalse(status.IsReady, "missing required gsudo engine invalidates UAC Guard");
        }

        private static void UacGuardOneClickSetupUsesPinnedGsudoPackage()
        {
            string arguments = UacGuardModule.GetGsudoWingetInstallArguments();
            AssertTrue(arguments.IndexOf("--id gerardog.gsudo", StringComparison.OrdinalIgnoreCase) >= 0,
                "one-click setup requests the official gsudo package id");
            AssertTrue(arguments.IndexOf("--exact", StringComparison.OrdinalIgnoreCase) >= 0
                    && arguments.IndexOf("--source winget", StringComparison.OrdinalIgnoreCase) >= 0
                    && arguments.IndexOf("--force", StringComparison.OrdinalIgnoreCase) >= 0,
                "one-click setup pins and repairs the exact package from the winget source");
            AssertTrue(arguments.IndexOf("--silent", StringComparison.OrdinalIgnoreCase) >= 0
                    && arguments.IndexOf("--disable-interactivity", StringComparison.OrdinalIgnoreCase) >= 0
                    && arguments.IndexOf("--accept-package-agreements", StringComparison.OrdinalIgnoreCase) >= 0
                    && arguments.IndexOf("--accept-source-agreements", StringComparison.OrdinalIgnoreCase) >= 0,
                "one-click setup is fully non-interactive after the single UAC approval");
        }

        private static void ApplicationPathsFollowRuntimeDirectory()
        {
            string expectedRoot = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            AssertEqual(expectedRoot, AppPaths.Root,
                "portable application root follows the directory containing the running binary");
            AssertEqual(Path.Combine(expectedRoot, "Shared"), AppPaths.SharedRoot,
                "portable shared-data directory follows the application root");
            AssertEqual(Path.Combine(expectedRoot, "Guard Center.exe"), AppPaths.InstalledExePath,
                "portable executable path follows the application root");
        }

        private static void StartupShortcutValidationRejectsStalePortablePaths()
        {
            string currentRoot = Path.Combine(Path.GetTempPath(), "Guard Center", "Current");
            string currentExe = Path.Combine(currentRoot, "Guard Center.exe");
            string oldRoot = Path.Combine(Path.GetTempPath(), "Guard Center", "Old");

            AssertTrue(StartupManager.IsShortcutCurrent(currentExe, "--minimized", currentRoot,
                    currentExe, currentRoot),
                "the shortcut for the current portable location is enabled");
            AssertTrue(StartupManager.IsShortcutCurrent(currentExe.ToUpperInvariant(), "--MINIMIZED",
                    currentRoot + Path.DirectorySeparatorChar, currentExe, currentRoot),
                "Windows path and startup argument comparisons are case insensitive");
            AssertFalse(StartupManager.IsShortcutCurrent(Path.Combine(oldRoot, "Guard Center.exe"),
                    "--minimized", oldRoot, currentExe, currentRoot),
                "a shortcut left behind by a moved portable folder is stale");
            AssertFalse(StartupManager.IsShortcutCurrent(currentExe, string.Empty, currentRoot,
                    currentExe, currentRoot),
                "a shortcut missing the minimized startup argument is incomplete");
            AssertFalse(StartupManager.IsShortcutCurrent(currentExe, "--minimized", oldRoot,
                    currentExe, currentRoot),
                "a shortcut with an old working directory is stale");
        }

        private static void CorruptedProfileFallsBack()
        {
            bool hadError;
            DisplayGuardProfileStore store = DisplayGuardProfileCodec.Decode("not-base64", out hadError);
            AssertTrue(hadError, "decode should report error");
            string encoded = DisplayGuardProfileCodec.Encode(store);
            DisplayGuardProfileStore decoded = DisplayGuardProfileCodec.Decode(encoded, out hadError);
            AssertFalse(hadError, "encoded fallback should decode");
            AssertNotNull(DisplayGuardProfileCodec.FindMode(decoded, DisplayGuardModes.Standard), "standard mode exists");
        }

        private static void InitialProfilesUsePerModeDeltas()
        {
            var store = new DisplayGuardProfileStore();
            List<DisplayGuardMonitorInfo> monitors = CreateMonitors();
            bool changed = DisplayGuardModeService.EnsureInitialProfiles(store, monitors);
            AssertTrue(changed, "profiles should be created");

            DisplayGuardMonitorProfile standard = FindProfile(store, DisplayGuardModes.Standard, "DISPLAY\\AAA");
            DisplayGuardMonitorProfile reading = FindProfile(store, DisplayGuardModes.Reading, "DISPLAY\\AAA");
            DisplayGuardMonitorProfile scenery = FindProfile(store, DisplayGuardModes.Scenery, "DISPLAY\\AAA");
            DisplayGuardMonitorProfile movie = FindProfile(store, DisplayGuardModes.Movie, "DISPLAY\\AAA");
            DisplayGuardMonitorProfile game = FindProfile(store, DisplayGuardModes.Game, "DISPLAY\\AAA");
            DisplayGuardMonitorProfile custom = FindProfile(store, DisplayGuardModes.Custom, "DISPLAY\\AAA");
            DisplayGuardMonitorProfile live = FindProfile(store, DisplayGuardModes.Live, "DISPLAY\\AAA");

            AssertEqual(50, standard.BrightnessPercent, "standard brightness");
            AssertEqual(70, standard.ContrastPercent, "standard contrast");
            AssertEqual(30, reading.BrightnessPercent, "reading brightness delta");
            AssertEqual(65, reading.ContrastPercent, "reading contrast delta");
            AssertEqual(60, scenery.BrightnessPercent, "scenery brightness delta");
            AssertEqual(78, scenery.ContrastPercent, "scenery contrast delta");
            AssertEqual(35, movie.BrightnessPercent, "movie brightness delta");
            AssertEqual(78, movie.ContrastPercent, "movie contrast delta");
            AssertEqual(65, game.BrightnessPercent, "game brightness delta");
            AssertEqual(75, game.ContrastPercent, "game contrast delta");
            AssertEqual(50, custom.BrightnessPercent, "custom starts from current brightness");
            AssertEqual(70, custom.ContrastPercent, "custom starts from current contrast");
            AssertEqual(50, live.BrightnessPercent, "live starts from current brightness");
            AssertEqual(70, live.ContrastPercent, "live starts from current contrast");

            DisplayGuardMonitorProfile brightnessOnly = FindProfile(store, DisplayGuardModes.Standard, "DISPLAY\\BBB");
            AssertTrue(brightnessOnly.HasBrightness, "brightness-only profile keeps brightness");
            AssertFalse(brightnessOnly.HasContrast, "brightness-only profile omits contrast");
            AssertNull(FindProfileOrNull(store, DisplayGuardModes.Standard, "DISPLAY\\UNMATCHED"),
                "unmatched monitor should not be persisted");
        }

        private static void CaptureSkipsUnreliableMonitors()
        {
            var store = new DisplayGuardProfileStore();
            List<DisplayGuardMonitorInfo> monitors = CreateMonitors();
            DisplayGuardModeService.EnsureInitialProfiles(store, monitors);

            monitors[0].BrightnessPercent = 42;
            monitors[0].ContrastPercent = 73;
            int saved;
            int skipped;
            bool changed = DisplayGuardModeService.CaptureCurrent(store, DisplayGuardModes.Custom, monitors,
                out saved, out skipped);

            AssertTrue(changed, "capture should change mode");
            AssertEqual(2, saved, "only two reliable controllable monitors saved");
            AssertEqual(1, skipped, "one unmatched monitor skipped");
            DisplayGuardMonitorProfile custom = FindProfile(store, DisplayGuardModes.Custom, "DISPLAY\\AAA");
            AssertEqual(42, custom.BrightnessPercent, "custom captured brightness has no mode delta");
            AssertEqual(73, custom.ContrastPercent, "custom captured contrast has no mode delta");
        }

        private static void LiveModeCapturesIndividualAdjustments()
        {
            var store = new DisplayGuardProfileStore();
            List<DisplayGuardMonitorInfo> monitors = CreateMonitors();
            DisplayGuardModeService.EnsureInitialProfiles(store, monitors);

            bool brightnessChanged = DisplayGuardModeService.CaptureAdjustment(store, DisplayGuardModes.Live,
                monitors, "one", DisplayGuardFeature.Brightness, 42);
            bool contrastChanged = DisplayGuardModeService.CaptureAdjustment(store, DisplayGuardModes.Live,
                monitors, "one", DisplayGuardFeature.Contrast, 73);
            bool unmatchedChanged = DisplayGuardModeService.CaptureAdjustment(store, DisplayGuardModes.Live,
                monitors, "three", DisplayGuardFeature.Brightness, 21);
            DisplayGuardMonitorProfile live = FindProfile(store, DisplayGuardModes.Live, "DISPLAY\\AAA");

            AssertTrue(brightnessChanged, "live brightness adjustment is captured");
            AssertTrue(contrastChanged, "live contrast adjustment is captured");
            AssertFalse(unmatchedChanged, "live mode skips unmatched displays");
            AssertEqual(42, live.BrightnessPercent, "live stores the adjusted brightness");
            AssertEqual(73, live.ContrastPercent, "live stores the adjusted contrast");
        }

        private static void ApplyPlanHandlesSkippedAndClampedValues()
        {
            var store = new DisplayGuardProfileStore();
            DisplayGuardProfileCodec.Encode(store);
            DisplayGuardModeProfile mode = DisplayGuardProfileCodec.FindMode(store, DisplayGuardModes.Game);
            mode.Monitors.Add(new DisplayGuardMonitorProfile
            {
                StableId = "DISPLAY\\AAA",
                Name = "Known",
                HasBrightness = true,
                BrightnessPercent = 120,
                HasContrast = true,
                ContrastPercent = -10
            });
            mode.Monitors.Add(new DisplayGuardMonitorProfile
            {
                StableId = "DISPLAY\\OFFLINE",
                Name = "Offline",
                HasBrightness = true,
                BrightnessPercent = 30
            });

            var monitors = new List<DisplayGuardMonitorInfo>
            {
                new DisplayGuardMonitorInfo
                {
                    RuntimeId = "one",
                    StableId = "DISPLAY\\AAA",
                    StableIdReliable = true,
                    DisplayName = "Known",
                    SupportsBrightness = true,
                    SupportsContrast = false
                },
                new DisplayGuardMonitorInfo
                {
                    RuntimeId = "two",
                    StableId = "DISPLAY\\NEW",
                    StableIdReliable = true,
                    DisplayName = "New",
                    SupportsBrightness = true
                },
                new DisplayGuardMonitorInfo
                {
                    RuntimeId = "three",
                    StableIdReliable = false,
                    DisplayName = "Unmatched",
                    SupportsBrightness = true
                }
            };

            DisplayGuardApplyPlan plan = DisplayGuardModeService.BuildApplyPlan(store, DisplayGuardModes.Game, monitors);
            AssertEqual(1, plan.Items.Count, "one matched item");
            AssertTrue(plan.Items[0].HasBrightness, "brightness is applied");
            AssertFalse(plan.Items[0].HasContrast, "unsupported contrast is skipped");
            AssertEqual(100, plan.Items[0].BrightnessPercent, "brightness is clamped");
            AssertTrue(plan.Items[0].BrightnessClamped, "clamp flag set");
            AssertEqual(1, plan.UnsupportedFeatureCount, "unsupported contrast counted");
            AssertEqual(1, plan.NewMonitorCount, "new monitor counted");
            AssertEqual(1, plan.UnmatchedMonitorCount, "unmatched monitor counted");
            AssertEqual(1, plan.OfflineProfileCount, "offline profile counted");
        }

        private static void StableIdNormalizationMatches()
        {
            string fromDisplayConfig = DisplayGuardIdentity.NormalizeStableId(
                @"\\?\DISPLAY#AUS2704#4&32b84bc6&0&UID4145#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");
            string fromWmi = DisplayGuardIdentity.NormalizeStableId(@"DISPLAY\AUS2704\4&32b84bc6&0&UID4145_0");
            AssertEqual(fromDisplayConfig, fromWmi, "normalized IDs match");
        }

        private static void CompositeProviderMergesControls()
        {
            var ddcEndpoint = new FakeEndpoint("DDC/CI", 10, 50, 100, 0, 70, 100);
            var wmiEndpoint = new FakeEndpoint("WMI", 0, 35, 100, 0, 0, 0);

            var ddc = new DisplayGuardDevice
            {
                RuntimeId = "native",
                StableId = "DISPLAY\\AAA",
                StableIdReliable = true,
                DisplayName = "Panel",
                ControlKind = "DDC/CI",
                SupportsBrightness = true,
                BrightnessMinimum = 10,
                BrightnessCurrent = 50,
                BrightnessMaximum = 100,
                SupportsContrast = true,
                ContrastMinimum = 0,
                ContrastCurrent = 70,
                ContrastMaximum = 100,
                BrightnessEndpoint = ddcEndpoint,
                ContrastEndpoint = ddcEndpoint
            };
            var wmi = new DisplayGuardDevice
            {
                RuntimeId = "wmi",
                StableId = "DISPLAY\\AAA",
                StableIdReliable = true,
                DisplayName = "Panel",
                ControlKind = "WMI",
                SupportsBrightness = true,
                BrightnessMinimum = 0,
                BrightnessCurrent = 35,
                BrightnessMaximum = 100,
                BrightnessEndpoint = wmiEndpoint
            };

            var provider = new CompositeDisplayGuardProvider(new IDisplayGuardProvider[]
            {
                new FakeProvider(ddc),
                new FakeProvider(wmi)
            });

            List<DisplayGuardDevice> devices = provider.EnumerateMonitors();
            AssertEqual(1, devices.Count, "duplicate stable ID merged");
            AssertTrue(devices[0].SupportsBrightness, "merged brightness");
            AssertTrue(devices[0].SupportsContrast, "merged contrast");
            AssertEqual(35, devices[0].BrightnessCurrent, "WMI brightness preferred");
            AssertEqual(70, devices[0].ContrastCurrent, "DDC contrast retained");
            devices[0].Dispose();
        }

        private static void DeviceQueueLatestValueWins()
        {
            var endpoint = new FakeEndpoint("Fake", 0, 10, 100, 0, 0, 100);
            var device = new DisplayGuardDevice
            {
                RuntimeId = "queued",
                StableId = "DISPLAY\\QUEUE",
                StableIdReliable = true,
                DisplayName = "Queued",
                SupportsBrightness = true,
                BrightnessMinimum = 0,
                BrightnessCurrent = 10,
                BrightnessMaximum = 100,
                BrightnessEndpoint = endpoint
            };

            var firstApplyStarted = new ManualResetEventSlim(false);
            var releaseFirstApply = new ManualResetEventSlim(false);
            var appliedValues = new List<int>();
            var busyTransitions = new List<bool>();
            var queue = new DisplayGuardDeviceQueue(device, delegate(DisplayGuardDevice d, int? brightness, int? contrast)
            {
                bool firstApply;
                lock (appliedValues)
                {
                    if (brightness.HasValue)
                    {
                        appliedValues.Add(brightness.Value);
                    }
                    firstApply = appliedValues.Count == 1;
                }
                if (firstApply)
                {
                    firstApplyStarted.Set();
                    releaseFirstApply.Wait(2000);
                }

                var result = new DisplayGuardOperationResult();
                if (brightness.HasValue)
                {
                    result.AttemptedCount++;
                    string error;
                    if (d.SetBrightnessPercent(brightness.Value, out error))
                    {
                        result.SucceededCount++;
                    }
                    else
                    {
                        result.FailedCount++;
                        result.LastError = error;
                    }
                }

                return result;
            }, delegate(DisplayGuardDevice d, bool busy)
            {
                lock (busyTransitions)
                {
                    busyTransitions.Add(busy);
                }
            });

            queue.Queue(DisplayGuardFeature.Brightness, 20);
            bool startedWithoutDebounce = firstApplyStarted.Wait(180);
            queue.Queue(DisplayGuardFeature.Brightness, 40);
            queue.Queue(DisplayGuardFeature.Brightness, 60);
            releaseFirstApply.Set();
            bool becameIdle = queue.WaitForIdle(2000);

            int[] appliedSnapshot;
            bool[] busySnapshot;
            lock (appliedValues)
            {
                appliedSnapshot = appliedValues.ToArray();
            }
            lock (busyTransitions)
            {
                busySnapshot = busyTransitions.ToArray();
            }

            AssertTrue(startedWithoutDebounce, "idle queue starts without the former 220ms debounce");
            AssertTrue(becameIdle, "queue drains after applying the latest pending value");
            AssertEqual(2, appliedSnapshot.Length, "busy queue collapses intermediate values");
            AssertEqual(20, appliedSnapshot[0], "first idle value applies immediately");
            AssertEqual(60, appliedSnapshot[1], "only latest value queued during hardware write applies next");
            AssertEqual(60, endpoint.LastBrightnessSet, "latest brightness wins");
            AssertEqual(2, busySnapshot.Length, "busy state changes only at queue start and drain");
            AssertTrue(busySnapshot[0], "queue reports busy before the first hardware write");
            AssertFalse(busySnapshot[1], "queue reports idle only after all pending writes drain");
            queue.Dispose();
            device.Dispose();
        }

        private static void DisplayWriteRequiresMatchingReadback()
        {
            var endpoint = new NonApplyingEndpoint();
            var device = new DisplayGuardDevice
            {
                RuntimeId = "mismatch",
                DisplayName = "Mismatch monitor",
                SupportsBrightness = true,
                BrightnessMinimum = 0,
                BrightnessCurrent = 20,
                BrightnessMaximum = 100,
                BrightnessEndpoint = endpoint
            };
            string error;
            AssertFalse(device.SetBrightnessPercent(70, out error), "mismatched readback fails");
            AssertTrue(error.IndexOf("did not match", StringComparison.OrdinalIgnoreCase) >= 0,
                "mismatch error is meaningful");
            device.Dispose();
        }

        private static void DeviceGuardPolicyIncludesOnlyApprovedClasses()
        {
            DeviceGuardKind kind;
            string category;
            bool all;
            AssertTrue(DeviceGuardPolicy.TryClassify("Bluetooth", "BTHUSB", @"USB\VID_1",
                out kind, out category, out all), "Bluetooth adapter is included");
            AssertEqual(DeviceGuardKind.BluetoothAdapter, kind, "Bluetooth kind");
            AssertTrue(all, "Bluetooth is included in repair all");
            AssertTrue(DeviceGuardPolicy.TryClassify("Camera", "usbvideo", @"USB\VID_2",
                out kind, out category, out all), "USB camera is included");
            AssertTrue(DeviceGuardPolicy.TryClassify("MEDIA", "usbaudio2", @"USB\VID_3",
                out kind, out category, out all), "USB audio is included");
            AssertFalse(DeviceGuardPolicy.TryClassify("Net", "mtkwlex", @"PCI\VEN_14C3",
                out kind, out category, out all), "network adapter is excluded");
            AssertFalse(DeviceGuardPolicy.TryClassify("Display", "nvlddmkm", @"PCI\VEN_10DE",
                out kind, out category, out all), "display adapter is excluded");
            AssertFalse(DeviceGuardPolicy.TryClassify("HIDClass", "HidUsb", @"USB\VID_4",
                out kind, out category, out all), "input device is excluded");
        }

        private static void DeviceGuardRefreshPreservesRepairSummary()
        {
            using (var module = new DeviceGuardModule(new FakeDeviceInventory()))
            {
                const string summary = "Device repair finished: 1 succeeded, 0 skipped, 0 failed, 2 restart required.";
                var method = typeof(DeviceGuardModule).GetMethod("RefreshAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
                AssertNotNull(method, "summary-preserving refresh overload exists");
                method.Invoke(module, new object[] { summary });
                AssertTrue(SpinWait.SpinUntil(delegate { return !module.IsScanning; }, 3000),
                    "device refresh should complete");
                AssertEqual(summary, module.StatusText, "repair summary remains visible after verification scan");
            }
        }

        private static void InputStackNumberingRemainsDiagnosticOnly()
        {
            var snapshot = new InputStackSnapshot
            {
                KeyboardInterceptionInstalled = true,
                HighestKeyboardClassIndex = 7,
                HighestPointerClassIndex = 2
            };
            InputStackHealthAnalyzer.Analyze(snapshot);
            AssertEqual(InputStackHealth.Healthy, snapshot.Health,
                "a readable input stack is healthy regardless of its class index");
            string summary = snapshot.Summary;

            snapshot.HighestKeyboardClassIndex = 12;
            snapshot.HighestPointerClassIndex = 14;
            InputStackHealthAnalyzer.Analyze(snapshot);
            AssertEqual(InputStackHealth.Healthy, snapshot.Health,
                "KeyboardClass12 remains read-only diagnostic information");
            AssertEqual(summary, snapshot.Summary,
                "class numbering does not alter health or repair behavior");

            snapshot.KeyboardInterceptionInstalled = false;
            InputStackHealthAnalyzer.Analyze(snapshot);
            AssertEqual(InputStackHealth.Healthy, snapshot.Health,
                "keyboard isolation remains healthy at any observed class index");
        }

        private static void InputStackParserAcceptsNumberedClassObjects()
        {
            int index;
            AssertTrue(WindowsInputStackInventory.TryParseIndex("KeyboardClass12",
                "KeyboardClass", out index), "multi-digit keyboard class object is parsed");
            AssertEqual(12, index, "multi-digit class index is preserved");
            AssertFalse(WindowsInputStackInventory.TryParseIndex("KeyboardClass",
                "KeyboardClass", out index), "missing numeric suffix is rejected");
            AssertFalse(WindowsInputStackInventory.TryParseIndex("KeyboardClass4Extra",
                "KeyboardClass", out index), "non-numeric suffix is rejected");
            AssertFalse(WindowsInputStackInventory.TryParseIndex("PointerClass2",
                "KeyboardClass", out index), "wrong object family is rejected");
        }

        private static void HuaJuanCompatibilityIsolatesKeyboardOnly()
        {
            string[] isolated = HuaJuanCompatibilityEngine.BuildIsolatedKeyboardFilters(
                new[] { "keyboard", "VendorFilter", "kbdclass", "vendorfilter" });
            AssertEqual(2, isolated.Length, "keyboard filter is removed and duplicates collapse");
            AssertEqual("VendorFilter", isolated[0], "unrelated keyboard filter order is preserved");
            AssertEqual("kbdclass", isolated[1], "Windows keyboard class driver is preserved");

            isolated = HuaJuanCompatibilityEngine.BuildIsolatedKeyboardFilters(
                new[] { "keyboard" });
            AssertEqual(1, isolated.Length, "missing kbdclass is repaired");
            AssertEqual("kbdclass", isolated[0], "kbdclass is added when absent");

            var snapshot = new InputStackSnapshot
            {
                KeyboardInterceptionInstalled = false,
                MouseInterceptionInstalled = true,
                HighestKeyboardClassIndex = 15,
                HighestPointerClassIndex = 15
            };
            InputStackHealthAnalyzer.Analyze(snapshot);
            AssertEqual(InputStackHealth.Healthy, snapshot.Health,
                "pointer numbering remains diagnostic when keyboard Interception is isolated");
        }

        private static void HuaJuanCompatibilityApplyRestoreRoundTrip()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "GuardCenter-huajuan-test-" + Guid.NewGuid().ToString("N"));
            string huaJuanRoot = Path.Combine(root, "HuaJuan");
            string stateRoot = Path.Combine(root, "state");
            string shimPayload = Path.Combine(root, "interception-shim.dll");
            string realPayload = Path.Combine(root, "interception-sparse.dll");
            string configPayload = Path.Combine(root, "guardcenter-huajuan-shim.ini");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(shimPayload, "hardware-identity-shim");
                File.WriteAllText(realPayload, "sparse-context-backend");
                File.WriteAllText(configPayload, "stable-hardware-id");
                string[] originals = CreateFakeHuaJuanDlls(huaJuanRoot);
                var filters = new FakeUpperFilterStore(
                    new[] { "keyboard", "kbdclass" }, new[] { "mouse", "mouclass" });
                var postReboot = new FakeHuaJuanPostRebootStore(
                    "HuaJuanInterceptionRepair", "legacy repair");
                var engine = new HuaJuanCompatibilityEngine(huaJuanRoot, shimPayload,
                    realPayload, configPayload, stateRoot, filters,
                    postReboot,
                    delegate { return false; });

                HuaJuanCompatibilityResult applied = engine.Apply();
                AssertTrue(applied.Succeeded, "compatibility apply succeeds");
                AssertTrue(applied.RebootRequired, "compatibility apply requires reboot");
                for (int i = 0; i < originals.Length; i++)
                {
                    AssertEqual("hardware-identity-shim", File.ReadAllText(originals[i]),
                        "every HuaJuan DLL is replaced by the shim");
                    string directory = Path.GetDirectoryName(originals[i]);
                    AssertEqual("sparse-context-backend", File.ReadAllText(Path.Combine(
                        directory, "guardcenter-interception-real.dll")),
                        "every shim receives the sparse real backend");
                    AssertEqual("stable-hardware-id", File.ReadAllText(Path.Combine(
                        directory, "guardcenter-huajuan-shim.ini")),
                        "every shim receives the stable hardware configuration");
                }
                AssertEqual(1, filters.Keyboard.Length,
                    "keyboard UpperFilters is reduced to kbdclass");
                AssertEqual("kbdclass", filters.Keyboard[0],
                    "keyboard Interception is detached");
                AssertEqual("mouse", filters.Mouse[0],
                    "mouse Interception remains untouched");
                AssertEqual(0, postReboot.Values.Count,
                    "conflicting legacy post-reboot repair is canceled");

                HuaJuanCompatibilitySnapshot inspected = engine.Inspect(
                    DateTime.Now.AddHours(-1), filters.Keyboard, filters.Mouse);
                AssertEqual(HuaJuanCompatibilityState.AppliedPendingReboot, inspected.State,
                    "same-boot inspection reports pending reboot");
                AssertTrue(inspected.BackupAvailable, "inspection finds rollback backup");

                HuaJuanCompatibilityResult restored = engine.Restore();
                AssertTrue(restored.Succeeded, "compatibility restore succeeds");
                for (int i = 0; i < originals.Length; i++)
                {
                    AssertEqual("original-" + i, File.ReadAllText(originals[i]),
                        "every original HuaJuan DLL is restored");
                    string directory = Path.GetDirectoryName(originals[i]);
                    AssertFalse(File.Exists(Path.Combine(directory,
                        "guardcenter-interception-real.dll")),
                        "managed sparse backend is removed on restore");
                    AssertFalse(File.Exists(Path.Combine(directory,
                        "guardcenter-huajuan-shim.ini")),
                        "managed shim configuration is removed on restore");
                }
                AssertEqual(2, filters.Keyboard.Length,
                    "original keyboard filter list is restored");
                AssertEqual("keyboard", filters.Keyboard[0],
                    "original keyboard Interception filter returns on restore");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static void HuaJuanCompatibilityRollsBackRegistryFailure()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "GuardCenter-huajuan-rollback-" + Guid.NewGuid().ToString("N"));
            string huaJuanRoot = Path.Combine(root, "HuaJuan");
            string stateRoot = Path.Combine(root, "state");
            string shimPayload = Path.Combine(root, "interception-shim.dll");
            string realPayload = Path.Combine(root, "interception-sparse.dll");
            string configPayload = Path.Combine(root, "guardcenter-huajuan-shim.ini");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(shimPayload, "hardware-identity-shim");
                File.WriteAllText(realPayload, "sparse-context-backend");
                File.WriteAllText(configPayload, "stable-hardware-id");
                string[] originals = CreateFakeHuaJuanDlls(huaJuanRoot);
                var filters = new FakeUpperFilterStore(
                    new[] { "keyboard", "kbdclass" }, new[] { "mouse", "mouclass" })
                {
                    WriteFailuresRemaining = 1
                };
                var postReboot = new FakeHuaJuanPostRebootStore(
                    "HuaJuanInterceptionRepair", "legacy repair");
                var engine = new HuaJuanCompatibilityEngine(huaJuanRoot, shimPayload,
                    realPayload, configPayload, stateRoot, filters,
                    postReboot,
                    delegate { return false; });

                HuaJuanCompatibilityResult result = engine.Apply();
                AssertFalse(result.Succeeded, "registry write failure is reported");
                for (int i = 0; i < originals.Length; i++)
                {
                    AssertEqual("original-" + i, File.ReadAllText(originals[i]),
                        "DLL replacement rolls back after registry failure");
                    string directory = Path.GetDirectoryName(originals[i]);
                    AssertFalse(File.Exists(Path.Combine(directory,
                        "guardcenter-interception-real.dll")),
                        "sparse backend rolls back after registry failure");
                    AssertFalse(File.Exists(Path.Combine(directory,
                        "guardcenter-huajuan-shim.ini")),
                        "shim configuration rolls back after registry failure");
                }
                AssertEqual("keyboard", filters.Keyboard[0],
                    "keyboard filters roll back after registry failure");
                AssertEqual("legacy repair",
                    postReboot.Values["HuaJuanInterceptionRepair"],
                    "conflicting RunOnce value is retained when apply fails");
                AssertFalse(File.Exists(Path.Combine(stateRoot, "active-backup.json")),
                    "failed first apply does not leave an active repair marker");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static string[] CreateFakeHuaJuanDlls(string root)
        {
            var paths = new List<string>();
            for (int i = 0; i < HuaJuanCompatibilityPaths.RelativeDllPaths.Length; i++)
            {
                string path = Path.Combine(root,
                    HuaJuanCompatibilityPaths.RelativeDllPaths[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "original-" + i);
                paths.Add(path);
            }
            return paths.ToArray();
        }

        private static void CoreHardwareResolverSeparatesAnchorsAndBlocksUsb()
        {
            var nodes = new List<PnPDeviceNode>
            {
                CoreNode(@"USB\VID_BT", "Bluetooth adapter", DeviceClassGuids.Bluetooth, "USB", true, true),
                CoreNode(@"PCI\DISPLAY", "Graphics adapter", DeviceClassGuids.Display, "PCI", true, true),
                new PnPDeviceNode
                {
                    InstanceId = @"USB\KLIPSCH", DisplayName = "Klipsch Heritage Wireless",
                    ClassGuid = DeviceClassGuids.Media, EnumeratorName = "USB", RemovalPolicy = 3,
                    IsPresent = true, IsStarted = true
                },
                CoreNode(@"PCI\USBHOST", "USB host controller", DeviceClassGuids.Usb, "PCI", true, true),
                new PnPDeviceNode
                {
                    InstanceId = @"HID\KEYBOARD", ParentInstanceId = @"PCI\USBHOST",
                    DisplayName = "Keyboard", ClassGuid = DeviceClassGuids.Keyboard,
                    EnumeratorName = "HID", IsPresent = true, IsStarted = true
                }
            };
            List<CoreHardwareItem> items = new CoreHardwareResolver().Resolve(
                new WindowsPnPSnapshot(nodes), new CoreHardwareBaselineDocument());
            AssertEqual(3, items.Count, "only physical/core anchors are included");
            AssertFalse(items.Exists(delegate(CoreHardwareItem item)
            { return item.DisplayName.IndexOf("Klipsch", StringComparison.OrdinalIgnoreCase) >= 0; }),
                "removable USB audio endpoint is not a core controller");
            CoreHardwareItem usb = items.Find(delegate(CoreHardwareItem item)
            { return item.Capability == CoreHardwareCapability.Usb; });
            AssertNotNull(usb, "USB controller is classified");
            AssertTrue(usb.UsbRestartBlocked, "present keyboard descendant blocks USB restart");
        }

        private static void CoreRepairAllPolicyIsRiskBounded()
        {
            var healthyBluetooth = new CoreHardwareItem
            {
                Capability = CoreHardwareCapability.Bluetooth, Health = CoreHardwareHealth.Healthy,
                IsPresent = true, IsStarted = true
            };
            var allBluetooth = new DeviceRepairTarget
            {
                TargetType = DeviceGuardTargetType.CoreHardware,
                CoreCapability = CoreHardwareCapability.Bluetooth,
                Mode = DeviceRepairMode.RepairAll,
                MaximumAuthorizedRisk = CoreHardwareRepairPolicy.MaximumRiskForRepairAll(
                    CoreHardwareCapability.Bluetooth)
            };
            AssertFalse(CoreHardwareRepairPolicy.MayRestart(allBluetooth, CoreHardwareHealth.Healthy,
                healthyBluetooth), "healthy Repair All item is verify-only");

            var degradedGraphics = new CoreHardwareItem
            {
                Capability = CoreHardwareCapability.Graphics, Health = CoreHardwareHealth.Degraded,
                IsPresent = true
            };
            var allGraphics = new DeviceRepairTarget
            {
                CoreCapability = CoreHardwareCapability.Graphics, Mode = DeviceRepairMode.RepairAll,
                MaximumAuthorizedRisk = CoreHardwareRepairPolicy.MaximumRiskForRepairAll(
                    CoreHardwareCapability.Graphics)
            };
            AssertEqual(RepairRiskLevel.Low, allGraphics.MaximumAuthorizedRisk,
                "graphics Repair All authorization is scan/verify only");
            AssertFalse(CoreHardwareRepairPolicy.MayRestart(allGraphics, CoreHardwareHealth.Degraded,
                degradedGraphics), "graphics Repair All never restarts");

            var usb = new CoreHardwareItem
            {
                Capability = CoreHardwareCapability.Usb, Health = CoreHardwareHealth.Degraded,
                IsPresent = true, UsbRestartBlocked = true
            };
            var manualUsb = new DeviceRepairTarget
            {
                CoreCapability = CoreHardwareCapability.Usb, Mode = DeviceRepairMode.Manual,
                MaximumAuthorizedRisk = RepairRiskLevel.Critical
            };
            AssertFalse(CoreHardwareRepairPolicy.MayRestart(manualUsb, CoreHardwareHealth.Degraded, usb),
                "critical USB descendants block even explicitly authorized restart");
        }

        private static void CoreHardwareResolverReportsFailureStates()
        {
            PnPDeviceNode disabled = CoreNode(@"USB\BT_DISABLED", "Disabled Bluetooth",
                DeviceClassGuids.Bluetooth, "USB", true, false);
            disabled.ProblemCode = 22;
            PnPDeviceNode driverMissing = CoreNode(@"PCI\DISPLAY_MISSING_DRIVER", "Display without driver",
                DeviceClassGuids.Display, "PCI", true, false);
            driverMissing.ProblemCode = 28;
            var baseline = new CoreHardwareBaselineDocument
            {
                Entries = new List<CoreHardwareBaselineEntry>
                {
                    new CoreHardwareBaselineEntry
                    {
                        Id = "core:network:baseline", Capability = CoreHardwareCapability.Network,
                        DisplayName = "Expected network adapter", LastInstanceId = @"PCI\NET_GONE",
                        HardwareIds = new[] { @"PCI\VEN_EXPECTED" },
                        LocationPaths = new[] { "PCIROOT(0)#PCI(0100)" }
                    }
                }
            };
            List<CoreHardwareItem> items = new CoreHardwareResolver().Resolve(
                new WindowsPnPSnapshot(new[] { disabled, driverMissing }), baseline);
            CoreHardwareItem bluetooth = items.Find(delegate(CoreHardwareItem item)
            { return item.Capability == CoreHardwareCapability.Bluetooth; });
            CoreHardwareItem graphics = items.Find(delegate(CoreHardwareItem item)
            { return item.Capability == CoreHardwareCapability.Graphics; });
            CoreHardwareItem network = items.Find(delegate(CoreHardwareItem item)
            { return item.Id == "core:network:baseline"; });
            AssertEqual(CoreHardwareHealth.Disabled, bluetooth.Health, "problem code 22 is disabled");
            AssertEqual(CoreHardwareHealth.DriverMissing, graphics.Health, "problem code 28 needs a driver");
            AssertNotNull(network, "unmatched healthy baseline remains visible");
            AssertEqual(CoreHardwareHealth.Missing, network.Health, "unmatched baseline is missing");
            AssertTrue(network.FromBaseline, "missing identity is explicitly baseline-derived");
        }

        private static void CoreHardwareResolverCorrelatesUsbRecoveryTarget()
        {
            const string bluetoothId = @"USB\VID_13D3&PID_3563&MI_00\6&BT&0&0000";
            const string failedUsbId = @"USB\VID_0000&PID_0002\5&HUB&0&14";
            const string bluetoothLocation =
                "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS14)#USBMI(0)";
            const string portLocation =
                "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS14)";

            PnPDeviceNode bluetooth = CoreNode(bluetoothId, "MediaTek Bluetooth Adapter",
                DeviceClassGuids.Bluetooth, "USB", false, false);
            bluetooth.DriverInfPath = string.Empty;
            bluetooth.LocationPaths = new[] { bluetoothLocation };
            var failedUsb = new PnPDeviceNode
            {
                InstanceId = failedUsbId,
                DisplayName = "Unknown USB Device (Device Descriptor Request Failed)",
                ClassGuid = DeviceClassGuids.Usb,
                EnumeratorName = "USB",
                IsPresent = true,
                IsStarted = false,
                ProblemCode = 43,
                HardwareIds = new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" },
                CompatibleIds = new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" },
                LocationPaths = new[] { portLocation }
            };
            var wrongPort = failedUsb.Clone();
            wrongPort.InstanceId = @"USB\VID_0000&PID_0002\5&HUB&0&7";
            wrongPort.LocationPaths = new[]
            {
                "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS07)"
            };
            var baseline = new CoreHardwareBaselineDocument
            {
                Entries = new List<CoreHardwareBaselineEntry>
                {
                    new CoreHardwareBaselineEntry
                    {
                        Id = "core:bluetooth:baseline",
                        Capability = CoreHardwareCapability.Bluetooth,
                        DisplayName = "MediaTek Bluetooth Adapter",
                        LastInstanceId = bluetoothId,
                        HardwareIds = bluetooth.HardwareIds,
                        LocationPaths = new[] { bluetoothLocation },
                        DriverInfPath = "oem159.inf",
                        DriverVersion = "1.3.17.162",
                        Manufacturer = "Mediatek Inc."
                    }
                }
            };

            CoreHardwareItem item = new CoreHardwareResolver().Resolve(
                new WindowsPnPSnapshot(new[] { bluetooth, failedUsb, wrongPort }), baseline)
                .Find(delegate(CoreHardwareItem candidate)
                { return candidate.Id == "core:bluetooth:baseline"; });
            AssertNotNull(item, "baseline Bluetooth item is resolved");
            AssertEqual("oem159.inf", item.DriverInfPath,
                "phantom anchor hydrates driver metadata from baseline");
            AssertNotNull(item.RecoveryCandidate, "same-port failure creates a recovery candidate");
            AssertEqual(failedUsbId, item.RecoveryCandidate.InstanceId,
                "same physical USB port supplies the recovery target");
            AssertEqual(43u, item.RecoveryCandidate.ProblemCode,
                "descriptor failure problem code is retained");
            AssertFalse(item.IdentityAmbiguous, "different physical USB ports do not create ambiguity");

            PnPDeviceNode duplicate = failedUsb.Clone();
            duplicate.InstanceId = @"USB\VID_0000&PID_0002\5&HUB2&0&14";
            CoreHardwareItem ambiguous = new CoreHardwareResolver().Resolve(
                new WindowsPnPSnapshot(new[] { bluetooth, failedUsb, duplicate }), baseline)
                .Find(delegate(CoreHardwareItem candidate)
                { return candidate.Id == "core:bluetooth:baseline"; });
            AssertTrue(ambiguous.IdentityAmbiguous,
                "multiple descriptor failures on the saved port fail closed");
            AssertNull(ambiguous.RecoveryCandidate,
                "ambiguous recovery does not expose an actionable instance");

            var repairAll = new DeviceRepairTarget
            {
                CoreCapability = CoreHardwareCapability.Bluetooth,
                Mode = DeviceRepairMode.RepairAll,
                MaximumAuthorizedRisk = RepairRiskLevel.Medium
            };
            AssertTrue(CoreHardwareRepairPolicy.MayRestartRecoveryTarget(repairAll, item),
                "Repair All may restart the exact medium-risk failed Bluetooth port node");
            AssertFalse(CoreHardwareRepairPolicy.MayRemoveRecoveryTarget(repairAll, item),
                "Repair All never removes the failed node");
            repairAll.Mode = DeviceRepairMode.Manual;
            repairAll.MaximumAuthorizedRisk = RepairRiskLevel.High;
            AssertTrue(CoreHardwareRepairPolicy.MayRemoveRecoveryTarget(repairAll, item),
                "manual high-risk authorization may remove the exact failed node");

            const string cameraId = @"USB\VID_322E&PID_202C&MI_00\6&CAMERA&0&0000";
            const string cameraLocation =
                "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS07)#ACPI(WCAM)";
            PnPDeviceNode camera = CoreNode(cameraId, "USB camera", DeviceClassGuids.Camera,
                "USB", false, false);
            camera.RemovalPolicy = 1;
            camera.LocationPaths = new[] { cameraLocation };
            PnPDeviceNode failedCameraPort = failedUsb.Clone();
            failedCameraPort.InstanceId = @"USB\VID_0000&PID_0002\5&HUB&0&7";
            failedCameraPort.LocationPaths = new[]
            {
                "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS07)"
            };
            var cameraBaseline = new CoreHardwareBaselineDocument
            {
                Entries = new List<CoreHardwareBaselineEntry>
                {
                    new CoreHardwareBaselineEntry
                    {
                        Id = "core:camera:baseline",
                        Capability = CoreHardwareCapability.Camera,
                        DisplayName = "USB camera",
                        LastInstanceId = cameraId,
                        HardwareIds = camera.HardwareIds,
                        LocationPaths = new[] { cameraLocation },
                        DriverInfPath = "usbvideo.inf"
                    }
                }
            };
            CoreHardwareItem cameraItem = new CoreHardwareResolver().Resolve(
                new WindowsPnPSnapshot(new[] { camera, failedCameraPort }), cameraBaseline)
                .Find(delegate(CoreHardwareItem candidate)
                { return candidate.Id == "core:camera:baseline"; });
            AssertNotNull(cameraItem.RecoveryCandidate,
                "camera ACPI child path normalizes to its physical HS07 port");
            AssertEqual(failedCameraPort.InstanceId, cameraItem.RecoveryCandidate.InstanceId,
                "USB camera uses the exact same-port descriptor failure");
            var cameraRepairAll = new DeviceRepairTarget
            {
                CoreCapability = CoreHardwareCapability.Camera,
                Mode = DeviceRepairMode.RepairAll,
                MaximumAuthorizedRisk = RepairRiskLevel.Medium
            };
            AssertTrue(CoreHardwareRepairPolicy.MayRestartRecoveryTarget(cameraRepairAll, cameraItem),
                "Repair All may restart the exact failed USB camera port node");
            AssertFalse(CoreHardwareRepairPolicy.MayRemoveRecoveryTarget(cameraRepairAll, cameraItem),
                "Repair All never removes the failed camera node");

            const string audioId = @"USB\VID_1234&PID_5678&MI_01\6&AUDIO&0&0001";
            const string audioLocation =
                "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS05)#USBMI(1)";
            PnPDeviceNode audio = CoreNode(audioId, "USB audio", DeviceClassGuids.Media,
                "USB", false, false);
            audio.RemovalPolicy = 1;
            audio.LocationPaths = new[] { audioLocation };
            PnPDeviceNode failedAudioPort = failedUsb.Clone();
            failedAudioPort.InstanceId = @"USB\VID_0000&PID_0002\5&HUB&0&5";
            failedAudioPort.LocationPaths = new[]
            {
                "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS05)"
            };
            var audioBaseline = new CoreHardwareBaselineDocument
            {
                Entries = new List<CoreHardwareBaselineEntry>
                {
                    new CoreHardwareBaselineEntry
                    {
                        Id = "core:audio:usb-baseline",
                        Capability = CoreHardwareCapability.Audio,
                        DisplayName = "USB audio",
                        LastInstanceId = audioId,
                        HardwareIds = audio.HardwareIds,
                        LocationPaths = new[] { audioLocation },
                        DriverInfPath = "usbaudio2.inf"
                    }
                }
            };
            CoreHardwareItem audioItem = new CoreHardwareResolver().Resolve(
                new WindowsPnPSnapshot(new[] { audio, failedAudioPort }), audioBaseline)
                .Find(delegate(CoreHardwareItem candidate)
                { return candidate.Id == "core:audio:usb-baseline"; });
            AssertNotNull(audioItem.RecoveryCandidate,
                "true USB audio correlates its exact descriptor-failure port");
            var audioRepairAll = new DeviceRepairTarget
            {
                CoreCapability = CoreHardwareCapability.Audio,
                Mode = DeviceRepairMode.RepairAll,
                MaximumAuthorizedRisk = RepairRiskLevel.Medium
            };
            AssertTrue(CoreHardwareRepairPolicy.MayRestartRecoveryTarget(audioRepairAll, audioItem),
                "Repair All may restart the exact failed USB audio port node");

            PnPDeviceNode intelAudio = CoreNode(@"INTELAUDIO\TEST", "Intel audio",
                DeviceClassGuids.Media, "INTELAUDIO", false, false);
            intelAudio.LocationPaths = Array.Empty<string>();
            var intelAudioBaseline = new CoreHardwareBaselineDocument
            {
                Entries = new List<CoreHardwareBaselineEntry>
                {
                    new CoreHardwareBaselineEntry
                    {
                        Id = "core:audio:intel-baseline",
                        Capability = CoreHardwareCapability.Audio,
                        DisplayName = "Intel audio",
                        LastInstanceId = intelAudio.InstanceId,
                        HardwareIds = intelAudio.HardwareIds,
                        LocationPaths = Array.Empty<string>(),
                        DriverInfPath = "intelaudio.inf"
                    }
                }
            };
            CoreHardwareItem intelAudioItem = new CoreHardwareResolver().Resolve(
                new WindowsPnPSnapshot(new[] { intelAudio, failedAudioPort }), intelAudioBaseline)
                .Find(delegate(CoreHardwareItem candidate)
                { return candidate.Id == "core:audio:intel-baseline"; });
            AssertNull(intelAudioItem.RecoveryCandidate,
                "INTELAUDIO without USB transport evidence never receives a USB recovery target");
            AssertFalse(CoreHardwareRepairPolicy.MayRestartRecoveryTarget(audioRepairAll, intelAudioItem),
                "non-USB audio cannot enter the USB recovery policy");
        }

        private static void CoreBaselineIsPersistentAndRecoverable()
        {
            string directory = Path.Combine(Path.GetTempPath(), "GuardCenter.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "device-guard-baseline.json");
            try
            {
                var store = new CoreHardwareBaselineStore(path);
                var document = new CoreHardwareBaselineDocument();
                var healthy = new CoreHardwareItem
                {
                    Id = "core:bluetooth:test", Capability = CoreHardwareCapability.Bluetooth,
                    DisplayName = "Test Bluetooth", AnchorInstanceId = @"USB\TEST",
                    HardwareIds = new[] { @"USB\VID_TEST" }, LocationPaths = new[] { "PCIROOT(0)#USBROOT(0)" },
                    DriverInfPath = "test.inf", Health = CoreHardwareHealth.Healthy,
                    IsPresent = true, IsStarted = true
                };
                AssertTrue(store.MergeHealthy(document, new[] { healthy }), "healthy item writes baseline");
                AssertTrue(File.Exists(path), "primary baseline exists");
                store.SaveAtomic(document);
                AssertTrue(File.Exists(path + ".bak"), "atomic replacement keeps backup");

                // A missing scan does not delete or rewrite the healthy identity.
                AssertFalse(store.MergeHealthy(document, new CoreHardwareItem[0]),
                    "empty/missing scan does not mutate baseline");
                AssertEqual(1, document.Entries.Count, "healthy baseline is retained");

                File.WriteAllText(path, "{ corrupt", System.Text.Encoding.UTF8);
                CoreHardwareBaselineDocument recovered = store.Load();
                AssertEqual(1, recovered.Entries.Count, "corrupt primary recovers from backup");
                AssertTrue(Directory.GetFiles(directory, "*.corrupt.*.json").Length == 1,
                    "corrupt primary is archived for diagnosis");
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void DeviceGuardRefreshSharesSnapshot()
        {
            string directory = Path.Combine(Path.GetTempPath(), "GuardCenter.SnapshotTest."
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var inventory = new CountingPnPInventory(new WindowsPnPSnapshot(new[]
                {
                    CoreNode(@"USB\VID_BT", "Bluetooth adapter", DeviceClassGuids.Bluetooth,
                        "USB", true, true)
                }));
                using (var module = new DeviceGuardModule(inventory,
                    Path.Combine(directory, "device-guard-baseline.json")))
                {
                    module.RefreshAsync();
                    AssertTrue(SpinWait.SpinUntil(delegate { return !module.IsScanning; }, 3000),
                        "shared snapshot refresh completes");
                    AssertEqual(1, inventory.CaptureCount,
                        "connected and core lists use one full snapshot capture");
                    AssertEqual(1, module.GetDevices().Count, "connected list uses shared snapshot");
                    AssertEqual(1, module.GetCoreHardware().Count, "core list uses shared snapshot");
                }
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static PnPDeviceNode CoreNode(string id, string name, string classGuid,
            string enumerator, bool present, bool started)
        {
            return new PnPDeviceNode
            {
                InstanceId = id, DisplayName = name, ClassGuid = classGuid,
                ClassName = classGuid == DeviceClassGuids.Bluetooth ? "Bluetooth" : string.Empty,
                Service = classGuid == DeviceClassGuids.Bluetooth ? "BTHUSB" : string.Empty,
                EnumeratorName = enumerator, IsPresent = present, IsStarted = started,
                DevNodeStatus = started ? 8u : 0u, ProblemCode = 0,
                HardwareIds = new[] { id }, LocationPaths = new[] { "ROOT#" + id }
            };
        }

        private static void ProgressiveListFiltersSortsAndBatches()
        {
            var settings = new ListPresentationSettings
            {
                BatchSize = 2,
                SortKey = "name"
            };
            var state = new ProgressiveListState<AppCatalogItem>(settings,
                delegate(AppCatalogItem app) { return app.Id; }, new int[] { 2, 4 });
            var apps = new List<AppCatalogItem>
            {
                new AppCatalogItem { Id = "g", Name = "Gamma", Publisher = "C", TargetPath = @"C:\Apps\Gamma.exe" },
                new AppCatalogItem { Id = "a", Name = "Alpha", Publisher = "A", TargetPath = @"C:\Apps\Alpha.exe" },
                new AppCatalogItem { Id = "b", Name = "Beta", Publisher = "B", TargetPath = @"C:\Apps\Beta.exe" },
                new AppCatalogItem { Id = "b", Name = "Beta duplicate", Publisher = "B", TargetPath = @"C:\Apps\Beta.exe" }
            };

            state.Reset(apps, AppIdentityService.Matches, AppIdentityService.CompareName);
            List<AppCatalogItem> visible = state.GetVisibleItems();
            AssertEqual(3, state.TotalItems, "duplicate source identity is removed");
            AssertEqual(2, visible.Count, "first batch count");
            AssertEqual("Alpha", visible[0].Name, "sorted first item");
            AssertEqual("Beta", visible[1].Name, "sorted second item");

            ProgressiveBatch<AppCatalogItem> batch;
            AssertTrue(state.TryBeginNextBatch(out batch), "next batch starts");
            ProgressiveBatch<AppCatalogItem> overlapping;
            AssertFalse(state.TryBeginNextBatch(out overlapping), "overlapping batch is rejected");
            List<AppCatalogItem> added = state.CompleteBatch(batch);
            AssertEqual(1, added.Count, "duplicate identity is not appended");
            AssertEqual("Gamma", added[0].Name, "next unique item appended");
            AssertEqual(3, state.VisibleCount, "visible identities are unique");
            AssertFalse(state.HasMore, "all source positions consumed");

            state.SearchText = "g";
            state.Reset(apps, AppIdentityService.Matches, AppIdentityService.CompareName);
            visible = state.GetVisibleItems();
            AssertEqual(1, state.TotalItems, "one-character search filters immediately");
            AssertEqual("Gamma", visible[0].Name, "one-character search result");

            var hiddenMetadataMatch = new AppCatalogItem
            {
                Id = "hidden",
                Name = "極速快感",
                Publisher = "A Publisher",
                Source = "Apps",
                TargetPath = @"C:\Games\Apex\Game.exe"
            };
            AssertFalse(AppIdentityService.Matches(hiddenMetadataMatch, "a"),
                "one-character app search ignores hidden publisher source and path matches");
            AssertTrue(AppIdentityService.Matches(hiddenMetadataMatch, "ap"),
                "multi-character app search still includes publisher source and path");

            state.SearchText = "ga";
            state.Reset(apps, AppIdentityService.Matches, AppIdentityService.CompareName);
            visible = state.GetVisibleItems();
            AssertEqual(1, state.TotalItems, "two-character search filters immediately");
            AssertEqual("Gamma", visible[0].Name, "two-character search result");

            state.SearchText = "alp";
            state.Reset(apps, AppIdentityService.Matches, AppIdentityService.CompareName);
            visible = state.GetVisibleItems();
            AssertEqual(1, state.TotalItems, "filtered count");
            AssertEqual("Alpha", visible[0].Name, "filtered item");

            state.ClearSearch();
            state.Reset(apps, AppIdentityService.Matches, AppIdentityService.CompareName);
            AssertEqual(3, state.TotalItems, "page-search refresh clears the query");

            ProgressiveBatch<AppCatalogItem> staleAfterSearchRefresh;
            AssertTrue(state.TryBeginNextBatch(out staleAfterSearchRefresh),
                "page-search refresh test starts a pending batch");
            state.ClearSearch();
            state.Reset(apps, AppIdentityService.Matches, AppIdentityService.CompareName);
            AssertEqual(0, state.CompleteBatch(staleAfterSearchRefresh).Count,
                "page-search refresh rejects the stale pending batch");

            state.BatchSize = 999;
            AssertEqual(2, state.BatchSize, "invalid batch size falls back to first allowed size");
        }

        private static void ProgressiveListRejectsStaleBatch()
        {
            var settings = new ListPresentationSettings { BatchSize = 2 };
            var state = new ProgressiveListState<AppCatalogItem>(settings,
                delegate(AppCatalogItem app) { return app.Id; }, new int[] { 2 });
            var first = new List<AppCatalogItem>
            {
                new AppCatalogItem { Id = "1", Name = "One" },
                new AppCatalogItem { Id = "2", Name = "Two" },
                new AppCatalogItem { Id = "3", Name = "Three" }
            };
            state.Reset(first, null, null);
            ProgressiveBatch<AppCatalogItem> stale;
            AssertTrue(state.TryBeginNextBatch(out stale), "batch starts before reset");
            state.Reset(new List<AppCatalogItem> { new AppCatalogItem { Id = "n", Name = "New" } }, null, null);
            AssertEqual(0, state.CompleteBatch(stale).Count, "stale batch adds nothing");
            AssertEqual("New", state.GetVisibleItems()[0].Name, "new generation remains intact");
        }

        private static void ListPresentationSettingsPersist()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "GuardCenter-settings-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var settings = new AppSettings();
                settings.AudioMixer.AppsList.BatchSize = 10;
                settings.AppGuard.AppsList.BatchSize = 50;
                settings.GameHelper.KeepEnhancedPointerPrecisionOff = true;
                settings.GameHelper.CrosshairEnabled = true;
                settings.GameHelper.CrosshairRestrictToSelectedApps = true;
                settings.GameHelper.CrosshairAppsList.BatchSize = 50;
                settings.GameHelper.CrosshairSelectedApps = CrosshairAppSelectionCodec.Encode(
                    new List<CrosshairSelectedApp>
                    {
                        new CrosshairSelectedApp
                        {
                            Name = "Persisted crosshair app",
                            TargetPath = @"C:\Games\Crosshair.exe",
                            Publisher = "Test"
                        }
                    });
                settings.GameHelper.AddAppsList.BatchSize = 100;
                settings.GameHelper.ProtectedAppsList.BatchSize = 10;
                settings.GameHelper.ProtectedApps = GameHelperModule.EncodeProtectedApps(
                    new List<GameHelperProtectedApp>
                    {
                        new GameHelperProtectedApp
                        {
                            Name = "Persisted game",
                            TargetPath = @"C:\Games\Persisted.exe",
                            BlockWindowsKey = false,
                            RightControlDShowsDesktop = true,
                            LockMicrosoftEnglish = true,
                            BlockInputLanguageSwitch = false,
                            RestorePreviousInputLanguage = true
                        }
                    });
                settings.DisplayGuard.MonitorsList.BatchSize = 50;
                settings.PowerGuard.DurationKind = "Custom";
                settings.PowerGuard.CustomDurationMinutes = 1503;
                settings.PowerGuard.KeepDisplayOn = true;
                settings.Keyboard.DisableStickyKeysHotkey = true;
                settings.Keyboard.StickyKeysHotkeyBackupCaptured = true;
                settings.Keyboard.StickyKeysOriginalHotkeyActive = true;
                settings.Keyboard.DisableShiftSpaceWidthToggle = true;
                settings.Keyboard.ShiftSpaceMicrosoftBackupCaptured = true;
                settings.Keyboard.ShiftSpaceMicrosoftOriginalExists = true;
                settings.Keyboard.ShiftSpaceMicrosoftOriginalValue = "0x00000001";
                settings.Keyboard.ShiftSpaceAsusBackupCaptured = true;
                settings.Keyboard.ShiftSpaceAsusOriginalFullWidth = false;
                settings.Layout.ModuleOrder = "power-guard,display-guard,app-guard,game-helper,keyboard,device,audio,uac-guard";
                settings.Appearance.CustomIconPath = @"C:\Icons\GuardCenter.png";

                var store = new SettingsStore(path);
                store.Save(settings);
                AppSettings loaded = store.Load();

                AssertEqual(10, loaded.AudioMixer.AppsList.BatchSize, "audio app batch size persists");
                AssertEqual(50, loaded.AppGuard.AppsList.BatchSize, "app guard batch size persists");
                AssertTrue(loaded.GameHelper.KeepEnhancedPointerPrecisionOff,
                    "pointer acceleration guard state persists");
                AssertTrue(loaded.GameHelper.CrosshairEnabled, "screen crosshair state persists");
                AssertTrue(loaded.GameHelper.CrosshairRestrictToSelectedApps,
                    "crosshair app restriction persists");
                AssertEqual(50, loaded.GameHelper.CrosshairAppsList.BatchSize,
                    "crosshair app list presentation persists independently");
                AssertEqual(1, CrosshairAppSelectionCodec.Decode(
                    loaded.GameHelper.CrosshairSelectedApps).Count,
                    "crosshair selected apps persist independently");
                AssertTrue(string.Equals("Screen crosshair: On",
                    GuardCenterController.FormatScreenCrosshairTrayText(loaded.GameHelper.CrosshairEnabled),
                    StringComparison.Ordinal), "tray label reflects the persisted screen crosshair state");
                AssertFalse(File.ReadAllText(path).Contains("AppGuard.DetailLoadingMode="),
                    "obsolete app guard detail loading mode is no longer persisted");
                AssertEqual(100, loaded.GameHelper.AddAppsList.BatchSize, "add apps batch size persists");
                AssertEqual(10, loaded.GameHelper.ProtectedAppsList.BatchSize,
                    "protected apps batch size persists");
                List<GameHelperProtectedApp> loadedGames = GameHelperModule.DecodeProtectedApps(
                    loaded.GameHelper.ProtectedApps);
                AssertEqual(1, loadedGames.Count, "per-app protection record persists");
                AssertFalse(loadedGames[0].BlockWindowsKey,
                    "per-app Windows-key setting persists through SettingsStore");
                AssertTrue(loadedGames[0].RightControlDShowsDesktop,
                    "per-app Right Ctrl+D setting persists through SettingsStore");
                AssertTrue(loadedGames[0].LockMicrosoftEnglish,
                    "per-app input-language setting persists through SettingsStore");
                AssertEqual(50, loaded.DisplayGuard.MonitorsList.BatchSize,
                    "display batch size persists");
                AssertEqual("Custom", loaded.PowerGuard.DurationKind,
                    "power guard duration mode persists");
                AssertEqual(1503, loaded.PowerGuard.CustomDurationMinutes,
                    "power guard custom duration persists");
                AssertTrue(loaded.PowerGuard.KeepDisplayOn,
                    "power guard display preference persists");
                AssertTrue(loaded.Keyboard.DisableStickyKeysHotkey, "sticky keys protection persists");
                AssertTrue(loaded.Keyboard.StickyKeysHotkeyBackupCaptured, "sticky keys backup marker persists");
                AssertTrue(loaded.Keyboard.StickyKeysOriginalHotkeyActive, "sticky keys original bit persists");
                AssertTrue(loaded.Keyboard.DisableShiftSpaceWidthToggle, "width protection persists");
                AssertTrue(loaded.Keyboard.ShiftSpaceMicrosoftBackupCaptured,
                    "Microsoft width backup marker persists");
                AssertEqual("0x00000001", loaded.Keyboard.ShiftSpaceMicrosoftOriginalValue,
                    "Microsoft width setting backup persists");
                AssertTrue(loaded.Keyboard.ShiftSpaceAsusBackupCaptured,
                    "ASUS width backup marker persists");
                AssertEqual("power-guard,display-guard,vsr-guard,app-guard,link-guard,game-helper,keyboard,device,audio,uac-guard",
                    loaded.Layout.ModuleOrder,
                    "module navigation order persists and inserts new guards beside their related modules");
                AssertEqual(settings.Appearance.CustomIconPath, loaded.Appearance.CustomIconPath,
                    "custom application icon path persists");
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static void AudioZeroOnEnableSettingPersists()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "GuardCenter-audio-zero-on-enable-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var settings = new AppSettings();
                AssertFalse(settings.Audio.ZeroOnEnable,
                    "zero-on-enable is opt-in for existing installations");

                settings.Audio.ZeroOnEnable = true;
                var store = new SettingsStore(path);
                store.Save(settings);
                AppSettings loaded = store.Load();

                AssertTrue(loaded.Audio.ZeroOnEnable,
                    "zero-on-enable survives a settings round trip");
                AssertTrue(loaded.Audio.Clone().ZeroOnEnable,
                    "zero-on-enable survives the module settings clone");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static void ModuleNavigationOrderNormalizes()
        {
            const string expected = "power-guard,audio,device,keyboard,game-helper,app-guard,link-guard,display-guard,vsr-guard,uac-guard";
            string normalized = ModuleNavigationOrder.Normalize(
                " power-guard,unknown,AUDIO,power-guard,audio ");
            AssertEqual(expected, normalized,
                "known ids stay ordered while missing ids append once in default order");
            AssertEqual(ModuleNavigationOrder.Default, ModuleNavigationOrder.Normalize(string.Empty),
                "empty order falls back to the complete default order");
        }

        private static void VsrGuardPreservesGpuPreferenceFields()
        {
            AssertEqual("GpuPreference=2;VRROptimizeEnable=1;",
                VsrGuardModule.MergeGpuPreference(
                    "VRROptimizeEnable=1;GpuPreference=1;", 2),
                "high-performance preference replaces only the existing GPU preference field");
            AssertEqual("GpuPreference=2;",
                VsrGuardModule.MergeGpuPreference(string.Empty, 2),
                "an empty Windows graphics preference receives a canonical high-performance value");
        }

        private static void VsrGuardReadsChromeAccelerationState()
        {
            bool enabled;
            AssertTrue(VsrGuardModule.TryReadHardwareAcceleration(
                "{\"hardware_acceleration_mode\":{\"enabled\":true}}", out enabled),
                "explicit Chrome acceleration preference is recognized");
            AssertTrue(enabled, "explicit enabled preference remains true");
            AssertTrue(VsrGuardModule.TryReadHardwareAcceleration(
                "{\"hardware_acceleration_mode_previous\":false}", out enabled),
                "Chrome's previous effective acceleration state is a supported fallback");
            AssertFalse(enabled, "fallback disabled state remains false");
        }

        private static void VsrGuardPreservesNvidiaFlags()
        {
            AssertEqual(0x00000101u, VsrGuardModule.EnableVsrValue(0x00000100u),
                "enabling VSR preserves the unrelated RTX Video HDR bit");
            AssertEqual(0x80000101u, VsrGuardModule.EnableVsrExplicitValue(0x00000100u),
                "the explicit user override adds required VSR bits without clearing existing flags");
            AssertEqual(0x00000100u, VsrGuardModule.DisableVsrValue(0x00000101u),
                "disabling VSR clears only the VSR bit");
            AssertEqual(0x80000100u, VsrGuardModule.DisableVsrExplicitValue(0x00000101u),
                "the explicit user override remains present when VSR is disabled");
        }

        private static void ModuleNavigationInsertionAndScrolling()
        {
            var centers = new List<double> { 50, 150, 250 };
            AssertEqual(0, ModuleNavigationOrder.GetInsertionIndex(20, centers),
                "pointer above the first center inserts first");
            AssertEqual(1, ModuleNavigationOrder.GetInsertionIndex(100, centers),
                "pointer between centers inserts between items");
            AssertEqual(3, ModuleNavigationOrder.GetInsertionIndex(300, centers),
                "pointer below all centers inserts last");
            AssertEqual(-1, ModuleNavigationOrder.GetAutoScrollDirection(20, 300, 40, 200, 48),
                "top edge scrolls upward when possible");
            AssertEqual(1, ModuleNavigationOrder.GetAutoScrollDirection(280, 300, 40, 200, 48),
                "bottom edge scrolls downward when possible");
            AssertEqual(0, ModuleNavigationOrder.GetAutoScrollDirection(20, 300, 0, 200, 48),
                "top edge stays idle at the top boundary");
            AssertEqual(0, ModuleNavigationOrder.GetAutoScrollDirection(280, 300, 200, 200, 48),
                "bottom edge stays idle at the bottom boundary");
        }

        private static void FocusedRefreshRequiresActiveVisibleWindow()
        {
            AssertTrue(FocusedRefreshPolicy.ShouldPoll(true, true, false),
                "active visible normal window polls");
            AssertFalse(FocusedRefreshPolicy.ShouldPoll(false, true, false),
                "hidden window does not poll");
            AssertFalse(FocusedRefreshPolicy.ShouldPoll(true, false, false),
                "inactive window does not poll");
            AssertFalse(FocusedRefreshPolicy.ShouldPoll(true, true, true),
                "minimized window does not poll");
            AssertEqual(1000, FocusedRefreshPolicy.IntervalMilliseconds,
                "focused polling interval is one second");
            AssertTrue(FocusedRefreshPolicy.ShouldRefreshDisplayOnFocus(true, true, false, true),
                "focused Display Guard page refreshes display topology");
            AssertFalse(FocusedRefreshPolicy.ShouldRefreshDisplayOnFocus(true, true, false, false),
                "focused non-display page does not refresh display topology");
            AssertFalse(FocusedRefreshPolicy.ShouldRefreshDisplayOnFocus(true, false, false, true),
                "inactive Display Guard page does not refresh display topology");
            AssertTrue(FocusedRefreshPolicy.ShouldRefreshDisplayOnNavigation(false, true),
                "entering Display Guard refreshes display topology");
            AssertFalse(FocusedRefreshPolicy.ShouldRefreshDisplayOnNavigation(true, true),
                "reselecting Display Guard does not duplicate the navigation refresh");
            AssertFalse(FocusedRefreshPolicy.ShouldRefreshDisplayOnNavigation(false, false),
                "navigating among other pages does not refresh display topology");
        }

        private static void FocusedRefreshFingerprintsAreStable()
        {
            var firstAudio = new AudioAppVolume
            {
                Key = "audio-a",
                Name = "A",
                Description = "PID 1",
                VolumePercent = 25,
                Active = true
            };
            var secondAudio = new AudioAppVolume
            {
                Key = "audio-b",
                Name = "B",
                Description = "PID 2",
                VolumePercent = 75,
                Muted = true
            };
            string audioForward = FocusedRefreshPolicy.GetAudioFingerprint(
                new List<AudioAppVolume> { firstAudio, secondAudio });
            string audioReverse = FocusedRefreshPolicy.GetAudioFingerprint(
                new List<AudioAppVolume> { secondAudio, firstAudio });
            AssertEqual(audioForward, audioReverse, "audio source order does not change the fingerprint");
            firstAudio.VolumePercent = 26;
            AssertFalse(string.Equals(audioForward, FocusedRefreshPolicy.GetAudioFingerprint(
                new List<AudioAppVolume> { firstAudio, secondAudio }), StringComparison.Ordinal),
                "audio value change updates the fingerprint");

            var firstDisplay = new DisplayGuardMonitorInfo
            {
                RuntimeId = "display-a",
                StableId = "stable-a",
                DisplayName = "A",
                SupportsBrightness = true,
                BrightnessPercent = 30
            };
            var secondDisplay = new DisplayGuardMonitorInfo
            {
                RuntimeId = "display-b",
                StableId = "stable-b",
                DisplayName = "B",
                SupportsContrast = true,
                ContrastPercent = 80
            };
            string displayForward = FocusedRefreshPolicy.GetDisplayFingerprint(
                new List<DisplayGuardMonitorInfo> { firstDisplay, secondDisplay });
            string displayReverse = FocusedRefreshPolicy.GetDisplayFingerprint(
                new List<DisplayGuardMonitorInfo> { secondDisplay, firstDisplay });
            AssertEqual(displayForward, displayReverse,
                "display source order does not change the fingerprint");
            secondDisplay.ContrastPercent = 81;
            AssertFalse(string.Equals(displayForward, FocusedRefreshPolicy.GetDisplayFingerprint(
                new List<DisplayGuardMonitorInfo> { firstDisplay, secondDisplay }), StringComparison.Ordinal),
                "display value change updates the fingerprint");
        }

        private static void AutomaticDisplayTopologyRefreshIsSilentAndSingleFlight()
        {
            var provider = new BlockingDisplayGuardProvider();
            var module = new DisplayGuardModule(new DisplayGuardSettings(), delegate { }, provider);
            try
            {
                AssertTrue(module.TryAutomaticTopologyRefreshAsync(), "first automatic refresh starts");
                AssertTrue(provider.Started.Wait(2000), "provider receives the polling refresh");
                AssertFalse(provider.LastWriteDiagnosticLog,
                    "automatic polling suppresses routine diagnostic log writes");
                AssertFalse(module.TryAutomaticTopologyRefreshAsync(),
                    "a second polling refresh is rejected while the first is running");

                provider.Release.Set();
                bool restarted = SpinWait.SpinUntil(delegate
                {
                    return module.TryAutomaticTopologyRefreshAsync();
                }, 2000);
                AssertTrue(restarted, "polling can start again after the previous refresh completes");
                AssertTrue(SpinWait.SpinUntil(delegate { return provider.ActiveCalls == 0; }, 2000),
                    "restarted polling refresh completes before cleanup");
            }
            finally
            {
                provider.Release.Set();
                module.Dispose();
                provider.Dispose();
            }
        }

        private static void LegacyPageSizeMigrates()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "GuardCenter-legacy-settings-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                File.WriteAllText(path, "AppGuard.AppsList.PageSize=50" + Environment.NewLine);
                var store = new SettingsStore(path);
                AppSettings loaded = store.Load();
                AssertEqual(50, loaded.AppGuard.AppsList.BatchSize, "legacy value is loaded");
                store.Save(loaded);
                string saved = File.ReadAllText(path);
                AssertTrue(saved.IndexOf("AppGuard.AppsList.BatchSize=50", StringComparison.Ordinal) >= 0,
                    "new batch key is saved");
                AssertTrue(saved.IndexOf("AppGuard.AppsList.PageSize", StringComparison.Ordinal) < 0,
                    "legacy page key is removed on save");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void GameHelperLegacySettingsUseSafeDefaults()
        {
            string record = "Legacy Game\tC:\\Games\\Legacy.exe\tPublisher";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(record));
            List<GameHelperProtectedApp> apps = GameHelperModule.DecodeProtectedApps(encoded);
            AssertEqual(1, apps.Count, "legacy app decodes");
            AssertFalse(apps[0].LockMicrosoftEnglish, "legacy lock defaults off");
            AssertTrue(apps[0].BlockInputLanguageSwitch, "legacy switch blocking defaults on");
            AssertTrue(apps[0].RestorePreviousInputLanguage, "legacy restore defaults on");
            AssertTrue(apps[0].BlockWindowsKey, "legacy Windows-key protection defaults on");
            AssertTrue(apps[0].RightControlDShowsDesktop, "legacy Right Ctrl+D defaults on");
        }

        private static void SliderMouseWheelStepAndBounds()
        {
            AssertNear(2, SliderMouseWheelBehavior.GetStep(2, 5), 0.0000001,
                "TickFrequency takes precedence");
            AssertNear(0.25, SliderMouseWheelBehavior.GetStep(0, 0.25), 0.0000001,
                "SmallChange is used when TickFrequency is invalid");
            AssertNear(1, SliderMouseWheelBehavior.GetStep(double.NaN, 0), 0.0000001,
                "invalid step values fall back to one");
            AssertNear(12, SliderMouseWheelBehavior.CalculateNextValue(
                10, 0, 20, 2, 1, 120), 0.0000001, "wheel up adds one tick");
            AssertNear(6, SliderMouseWheelBehavior.CalculateNextValue(
                10, 0, 20, 2, 1, -240), 0.0000001,
                "rapid wheel delta applies multiple ticks");
            AssertNear(20, SliderMouseWheelBehavior.CalculateNextValue(
                19, 0, 20, 2, 1, 120), 0.0000001, "maximum is clamped");
            AssertNear(0, SliderMouseWheelBehavior.CalculateNextValue(
                0.2, 0, 20, 1, 1, -120), 0.0000001, "minimum is clamped");
            AssertNear(0.3, SliderMouseWheelBehavior.CalculateNextValue(
                0.2, 0, 1, 0.1, 0.25, 120), 0.0000001,
                "fractional ticks remain stable");
        }

        private static void SliderMouseWheelRoutedEventInScrollViewer()
        {
            SliderMouseWheelBehavior.Initialize();
            var slider = new System.Windows.Controls.Slider
            {
                Minimum = 0,
                Maximum = 10,
                Value = 4,
                TickFrequency = 2,
                SmallChange = 1,
                Width = 220,
                Height = 36,
                IsSnapToTickEnabled = true
            };
            var content = new System.Windows.Controls.StackPanel();
            content.Children.Add(slider);
            content.Children.Add(new System.Windows.Controls.Border { Height = 1200 });
            var scrollViewer = new System.Windows.Controls.ScrollViewer
            {
                Content = content,
                Width = 280,
                Height = 160,
                VerticalScrollBarVisibility =
                    System.Windows.Controls.ScrollBarVisibility.Visible
            };
            int routedThroughParent = 0;
            scrollViewer.AddHandler(System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                new System.Windows.Input.MouseWheelEventHandler(delegate
                {
                    routedThroughParent++;
                }), true);

            var window = new System.Windows.Window
            {
                Left = 120,
                Top = 120,
                Width = 320,
                Height = 220,
                ShowActivated = true,
                ShowInTaskbar = false,
                Topmost = true,
                Content = scrollViewer
            };

            TestNativePoint originalCursor;
            AssertTrue(GetCursorPos(out originalCursor), "original cursor position is available");
            try
            {
                window.Show();
                window.Activate();
                window.UpdateLayout();
                System.Windows.Point target = slider.PointToScreen(
                    new System.Windows.Point(slider.ActualWidth / 2, slider.ActualHeight / 2));
                AssertTrue(SetCursorPos((int)Math.Round(target.X), (int)Math.Round(target.Y)),
                    "cursor moves over the test Slider");
                System.Windows.Input.Mouse.Synchronize();
                AssertTrue(PumpUntil(delegate { return slider.IsMouseOver; }, 1500),
                    "WPF reports the Slider as hovered");

                double originalOffset = scrollViewer.VerticalOffset;
                var args = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                    Source = slider
                };
                slider.RaiseEvent(args);
                AssertTrue(args.Handled, "Slider class handler marks PreviewMouseWheel handled");
                AssertEqual(1, routedThroughParent,
                    "PreviewMouseWheel follows the real parent ScrollViewer route");
                AssertNear(6, slider.Value, 0.0000001, "routed wheel changes Slider value");
                AssertNear(originalOffset, scrollViewer.VerticalOffset, 0.0000001,
                    "parent ScrollViewer remains stationary");

                slider.Value = slider.Maximum;
                var boundaryArgs = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                    Source = slider
                };
                slider.RaiseEvent(boundaryArgs);
                AssertTrue(boundaryArgs.Handled, "wheel remains handled at Slider maximum");
                AssertNear(slider.Maximum, slider.Value, 0.0000001,
                    "boundary wheel does not exceed maximum");
                AssertNear(originalOffset, scrollViewer.VerticalOffset, 0.0000001,
                    "boundary wheel still leaves ScrollViewer stationary");

                slider.IsEnabled = false;
                var disabledArgs = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                    Source = slider
                };
                slider.RaiseEvent(disabledArgs);
                AssertFalse(disabledArgs.Handled, "disabled Slider does not consume wheel");
            }
            finally
            {
                SetCursorPos(originalCursor.X, originalCursor.Y);
                window.Close();
                PumpFor(20);
            }
        }

        private static void CrosshairSelectedAppsRoundTrip()
        {
            var apps = new List<CrosshairSelectedApp>
            {
                new CrosshairSelectedApp
                {
                    Name = "Game A",
                    TargetPath = @"C:\Games\GameA.exe",
                    Publisher = "Publisher A",
                    Source = "Shortcut",
                    IconPath = @"C:\Games\GameA.exe"
                },
                new CrosshairSelectedApp
                {
                    Name = "Duplicate casing",
                    TargetPath = @"c:\games\gamea.EXE"
                },
                new CrosshairSelectedApp
                {
                    Name = "Missing but valid",
                    TargetPath = @"C:\Missing\StillSelected.exe"
                },
                new CrosshairSelectedApp
                {
                    Name = "Not executable",
                    TargetPath = @"C:\Games\Readme.txt"
                }
            };

            string encoded = CrosshairAppSelectionCodec.Encode(apps);
            encoded += ",not-base64";
            List<CrosshairSelectedApp> decoded = CrosshairAppSelectionCodec.Decode(encoded);
            AssertEqual(2, decoded.Count,
                "duplicate paths and malformed identities are removed without losing valid records");
            AssertEqual("Game A", decoded[0].Name, "first identity metadata wins");
            AssertEqual(@"C:\Missing\StillSelected.exe", decoded[1].TargetPath,
                "nonexistent but valid executable path is retained");

            var index = new CrosshairAppSelectionIndex(decoded);
            AssertTrue(index.ContainsExecutable(@"c:\GAMES\gamea.exe"),
                "selected executable identity is case-insensitive");
            AssertFalse(index.ContainsExecutable(@"C:\Games\Other.exe"),
                "unselected executable does not match");
            AssertFalse(index.ContainsExecutable(string.Empty),
                "unidentifiable foreground path fails closed");
        }

        private static void CrosshairVisibilityPolicyMatchesRequirements()
        {
            AssertFalse(CrosshairVisibilityPolicy.ShouldExist(false, 0),
                "disabled crosshair without preview does not keep an overlay");
            AssertTrue(CrosshairVisibilityPolicy.ShouldExist(true, 0),
                "enabled crosshair keeps one overlay instance");
            AssertTrue(CrosshairVisibilityPolicy.ShouldExist(false, 1),
                "Customize preview keeps an overlay instance");
            AssertTrue(CrosshairVisibilityPolicy.ShouldShow(true, false, false, 0),
                "global mode shows without an app match");
            AssertFalse(CrosshairVisibilityPolicy.ShouldShow(true, true, false, 0),
                "restricted mode hides for empty, unknown, or unmatched foreground");
            AssertTrue(CrosshairVisibilityPolicy.ShouldShow(true, true, true, 0),
                "restricted mode shows for a selected executable");
            AssertTrue(CrosshairVisibilityPolicy.ShouldShow(false, true, false, 1),
                "Customize preview overrides the real restricted state");
        }

        private static void CrosshairForegroundMonitoringIsShared()
        {
            string selectedApps = CrosshairAppSelectionCodec.Encode(
                new List<CrosshairSelectedApp>
                {
                    new CrosshairSelectedApp
                    {
                        Name = "Crosshair only",
                        TargetPath = @"C:\Games\CrosshairOnly.exe"
                    }
                });
            var crosshairOnly = new GameHelperSettings
            {
                CrosshairEnabled = true,
                CrosshairRestrictToSelectedApps = true,
                CrosshairSelectedApps = selectedApps
            };
            using (var module = new GameHelperModule(crosshairOnly))
            {
                module.ApplySavedState();
                AssertTrue(module.IsForegroundEventHookActive,
                    "restricted enabled Crosshair installs the shared foreground hook");
                AssertTrue(module.ForegroundEventHookCount >= 10,
                    "restricted enabled Crosshair installs lifecycle, focus and geometry hooks");
                AssertEqual(TimeSpan.FromMilliseconds(75),
                    GameHelperModule.ForegroundReconciliationDelay,
                    "event reconciliation is debounced without a periodic timer");
                AssertFalse(module.IsKeyboardProtectionHookActive,
                    "Crosshair-only monitoring does not install the keyboard hook");
                AssertFalse(module.IsInputLanguageWatchdogActive,
                    "Crosshair-only monitoring does not start the input-language watchdog");
                module.SetCrosshairEnabled(false);
                AssertFalse(module.IsForegroundMonitoringActive,
                    "all foreground event hooks stop when no feature needs them");
            }

            var shared = new GameHelperSettings
            {
                CrosshairEnabled = true,
                CrosshairRestrictToSelectedApps = true,
                CrosshairSelectedApps = selectedApps,
                ProtectedApps = GameHelperModule.EncodeProtectedApps(
                    new List<GameHelperProtectedApp>
                    {
                        new GameHelperProtectedApp
                        {
                            Name = "Keyboard protected",
                            TargetPath = @"C:\Games\Protected.exe",
                            BlockWindowsKey = true,
                            RightControlDShowsDesktop = false,
                            LockMicrosoftEnglish = false
                        }
                    })
            };
            using (var module = new GameHelperModule(shared))
            {
                module.ApplySavedState();
                AssertTrue(module.IsForegroundEventHookActive
                    && module.ForegroundEventHookCount >= 10,
                    "both features share the event-driven foreground monitor");
                module.SetCrosshairEnabled(false);
                AssertTrue(module.IsForegroundEventHookActive
                    && module.ForegroundEventHookCount >= 10,
                    "disabling Crosshair keeps Game Protection window hooks active");
                string protectedId = module.GetProtectedApps()[0].Id;
                module.RemoveProtectedApp(protectedId);
                AssertFalse(module.IsForegroundMonitoringActive,
                    "both monitor paths stop after the final owning feature is removed");
            }
        }

        private static void GameHelperProtectionIndexMatchesCandidatePaths()
        {
            var protectedApp = new GameHelperProtectedApp
            {
                Id = @"C:\GAMES\APEX\R5APEX_DX12.EXE",
                Name = "Apex",
                TargetPath = @"C:\Games\Apex\r5apex_dx12.exe"
            };
            var index = new GameHelperAppProtectionIndex(new[] { protectedApp });

            GameHelperProtectedApp matched;
            AssertTrue(index.TryGet(@"c:\games\apex\R5APEX_DX12.exe", out matched),
                "candidate path resolves to the protected app regardless of path casing");
            AssertTrue(object.ReferenceEquals(protectedApp, matched),
                "index returns the matching protected app record");
            AssertFalse(index.TryGet(@"C:\Games\Other\other.exe", out matched),
                "unprotected executable remains available for Add");
        }

        private static void GameHelperNewAppsEnableEveryProtection()
        {
            var settings = new GameHelperSettings();
            using (var module = new GameHelperModule(settings))
            {
                module.AddProtectedApp(new GameHelperAppCandidate
                {
                    Name = "New protected app",
                    Publisher = "Test",
                    TargetPath = @"C:\Games\NewProtected.exe"
                });

                List<GameHelperProtectedApp> apps = module.GetProtectedApps();
                AssertEqual(1, apps.Count, "new app is added to protection");
                AssertTrue(apps[0].BlockWindowsKey, "Windows-key protection defaults on");
                AssertTrue(apps[0].RightControlDShowsDesktop, "Right Ctrl+D protection defaults on");
                AssertTrue(apps[0].LockMicrosoftEnglish, "Microsoft ENG lock defaults on");
                AssertTrue(apps[0].BlockInputLanguageSwitch, "input-language switch blocking defaults on");
                AssertTrue(apps[0].RestorePreviousInputLanguage, "input-language restoration defaults on");
            }
        }

        private static void GameHelperInputLanguageOptionsRoundTrip()
        {
            var apps = new List<GameHelperProtectedApp>
            {
                new GameHelperProtectedApp
                {
                    Name = "Game A",
                    TargetPath = @"C:\Games\A.exe",
                    Publisher = "A",
                    BlockWindowsKey = true,
                    RightControlDShowsDesktop = false,
                    LockMicrosoftEnglish = true,
                    BlockInputLanguageSwitch = false,
                    RestorePreviousInputLanguage = true
                },
                new GameHelperProtectedApp
                {
                    Name = "Game B",
                    TargetPath = @"C:\Games\B.exe",
                    Publisher = "B",
                    BlockWindowsKey = false,
                    RightControlDShowsDesktop = true,
                    LockMicrosoftEnglish = false,
                    BlockInputLanguageSwitch = true,
                    RestorePreviousInputLanguage = false
                }
            };

            List<GameHelperProtectedApp> decoded = GameHelperModule.DecodeProtectedApps(
                GameHelperModule.EncodeProtectedApps(apps));
            AssertEqual(2, decoded.Count, "both app records round trip");
            AssertTrue(decoded[0].BlockWindowsKey, "game A Windows-key setting persists");
            AssertFalse(decoded[0].RightControlDShowsDesktop, "game A desktop shortcut persists");
            AssertTrue(decoded[0].LockMicrosoftEnglish, "game A lock persists");
            AssertFalse(decoded[0].BlockInputLanguageSwitch, "game A blocking persists");
            AssertTrue(decoded[0].RestorePreviousInputLanguage, "game A restore persists");
            AssertFalse(decoded[1].BlockWindowsKey, "game B Windows-key setting persists");
            AssertTrue(decoded[1].RightControlDShowsDesktop, "game B desktop shortcut persists");
            AssertFalse(decoded[1].LockMicrosoftEnglish, "game B lock persists");
            AssertTrue(decoded[1].BlockInputLanguageSwitch, "game B blocking persists");
            AssertFalse(decoded[1].RestorePreviousInputLanguage, "game B restore persists");
        }

        private static void GameHelperPerAppResourcesStayIndependent()
        {
            var winOnlySettings = new GameHelperSettings
            {
                ProtectedApps = GameHelperModule.EncodeProtectedApps(new List<GameHelperProtectedApp>
                {
                    new GameHelperProtectedApp
                    {
                        Name = "Win only",
                        TargetPath = @"C:\Games\WinOnly.exe",
                        BlockWindowsKey = true,
                        RightControlDShowsDesktop = false,
                        LockMicrosoftEnglish = false
                    }
                })
            };
            using (var module = new GameHelperModule(winOnlySettings))
            {
                module.ApplySavedState();
                AssertTrue(module.IsKeyboardProtectionHookActive,
                    "Win-only app installs the keyboard hook");
                AssertTrue(module.IsKeyboardProtectionHookThreadAlive,
                    "Win-only app hosts the low-level hook on its dedicated message-loop thread");
                AssertFalse(module.IsInputLanguageWatchdogActive,
                    "Win-only app does not start the HKL watchdog");
            }

            var languageOnlySettings = new GameHelperSettings
            {
                ProtectedApps = GameHelperModule.EncodeProtectedApps(new List<GameHelperProtectedApp>
                {
                    new GameHelperProtectedApp
                    {
                        Name = "Language only",
                        TargetPath = @"C:\Games\LanguageOnly.exe",
                        BlockWindowsKey = false,
                        RightControlDShowsDesktop = false,
                        LockMicrosoftEnglish = true,
                        BlockInputLanguageSwitch = true
                    }
                })
            };
            using (var module = new GameHelperModule(languageOnlySettings))
            {
                module.ApplySavedState();
                AssertFalse(module.IsKeyboardProtectionHookActive,
                    "language-only app does not install the keyboard hook");
                AssertTrue(module.IsInputLanguageWatchdogActive,
                    "language-only app starts the HKL watchdog");
            }

            var removableSettings = new GameHelperSettings
            {
                ProtectedApps = GameHelperModule.EncodeProtectedApps(new List<GameHelperProtectedApp>
                {
                    new GameHelperProtectedApp
                    {
                        Name = "Removable",
                        TargetPath = @"C:\Games\Removable.exe",
                        BlockWindowsKey = true,
                        RightControlDShowsDesktop = true,
                        LockMicrosoftEnglish = true
                    }
                })
            };
            using (var module = new GameHelperModule(removableSettings))
            {
                module.ApplySavedState();
                string id = module.GetProtectedApps()[0].Id;
                module.RemoveProtectedApp(id);
                AssertEqual(0, module.GetProtectedApps().Count, "Remove deletes the app record");
                AssertFalse(module.IsKeyboardProtectionHookActive,
                    "Remove releases the keyboard hook when no option remains");
                AssertFalse(module.IsKeyboardProtectionHookThreadAlive,
                    "Remove stops the dedicated keyboard hook thread");
                AssertFalse(module.IsInputLanguageWatchdogActive,
                    "Remove stops the HKL watchdog when no option remains");
                AssertTrue(module.IsKeyboardProtectionStateClean,
                    "Remove clears Win, Ctrl, and D runtime state");
            }
        }

        private static void GameHelperWindowsKeyStateIsPaired()
        {
            var state = new GameKeyboardProtectionState();
            for (int cycle = 0; cycle < 10; cycle++)
            {
                AssertTrue(state.Process(GameKeyboardProtectionState.VkLeftWin, 0,
                    true, false, true, false).Suppress, "left Win down is blocked");
                AssertTrue(state.Process(GameKeyboardProtectionState.VkLeftWin, 0,
                    true, false, false, false).Suppress, "left Win repeat remains blocked");
                AssertTrue(state.Process(GameKeyboardProtectionState.VkLeftWin, 0,
                    false, true, false, false).Suppress, "left Win up pairs with its blocked down");
            }
            AssertTrue(state.Process(GameKeyboardProtectionState.VkRightWin, 0,
                true, false, true, false).Suppress, "right Win down is blocked");
            AssertTrue(state.Process(GameKeyboardProtectionState.VkRightWin, 0,
                false, true, false, false).Suppress, "right Win up is blocked");
            AssertTrue(state.IsClean, "all Windows-key state clears after paired releases");
        }

        private static void GameHelperWindowsKeyStatePassesAndResets()
        {
            var state = new GameKeyboardProtectionState();
            AssertFalse(state.Process(GameKeyboardProtectionState.VkLeftWin, 0,
                true, false, false, false).Suppress, "Win passes outside protected apps");
            AssertFalse(state.Process(GameKeyboardProtectionState.VkLeftWin, 0,
                false, true, false, false).Suppress, "unprotected Win up passes");
            AssertFalse(state.Process(0x57, 0, true, false, true, true).Suppress,
                "W is never consumed by Windows-key protection");
            state.Process(GameKeyboardProtectionState.VkRightWin, 0,
                true, false, true, false);
            state.Reset();
            AssertTrue(state.IsClean, "disable or shutdown clears keyboard state");
        }

        private static void GameHelperRightControlDIsExactAndSingleShot()
        {
            var state = new GameKeyboardProtectionState();
            GameKeyboardDecision leftControl = state.Process(0xA2, 0,
                true, false, false, true);
            AssertFalse(leftControl.ShowDesktop, "Left Ctrl is not Right Ctrl");
            AssertFalse(state.Process(GameKeyboardProtectionState.VkD, 0,
                true, false, false, true).ShowDesktop, "Left Ctrl+D does not trigger");
            state.Process(GameKeyboardProtectionState.VkD, 0, false, true, false, true);
            state.Process(0xA2, 0, false, true, false, true);

            state.Process(GameKeyboardProtectionState.VkRightControl,
                GameKeyboardProtectionState.LlkhfExtended, true, false, false, true);
            GameKeyboardDecision trigger = state.Process(GameKeyboardProtectionState.VkD, 0,
                true, false, false, true);
            AssertTrue(trigger.ShowDesktop, "Right Ctrl+D triggers show desktop");
            AssertTrue(trigger.Suppress, "the triggering D down is consumed");
            GameKeyboardDecision repeat = state.Process(GameKeyboardProtectionState.VkD, 0,
                true, false, false, true);
            AssertFalse(repeat.ShowDesktop, "D autorepeat does not retrigger");
            AssertTrue(repeat.Suppress, "D autorepeat in the consumed chord stays consumed");
            AssertTrue(state.Process(GameKeyboardProtectionState.VkD, 0,
                false, true, false, true).Suppress, "triggering D up is paired and consumed");
            state.Process(GameKeyboardProtectionState.VkRightControl,
                GameKeyboardProtectionState.LlkhfExtended, false, true, false, true);
            AssertTrue(state.IsClean, "Right Ctrl+D state clears after full release");
        }

        private static void GameHelperRightControlDHandlesEdgeCases()
        {
            var state = new GameKeyboardProtectionState();
            AssertFalse(state.Process(GameKeyboardProtectionState.VkD, 0,
                true, false, false, true).ShowDesktop, "D alone passes without triggering");
            GameKeyboardDecision reverseTrigger = state.Process(
                GameKeyboardProtectionState.VkControl,
                GameKeyboardProtectionState.LlkhfExtended, true, false, false, true);
            AssertTrue(reverseTrigger.ShowDesktop, "D then extended Right Ctrl also triggers");
            AssertFalse(reverseTrigger.Suppress, "Right Ctrl itself is never consumed");
            AssertFalse(state.Process(GameKeyboardProtectionState.VkControl,
                GameKeyboardProtectionState.LlkhfExtended, true, false, false, true).ShowDesktop,
                "Right Ctrl repeat does not retrigger");
            state.Process(GameKeyboardProtectionState.VkControl,
                GameKeyboardProtectionState.LlkhfExtended, false, true, false, true);
            AssertFalse(state.IsClean, "D still held keeps the chord latched");
            state.Process(GameKeyboardProtectionState.VkD, 0, false, true, false, true);
            AssertTrue(state.IsClean, "cancelled reverse-order chord clears after both releases");

            state.Process(GameKeyboardProtectionState.VkRightControl,
                GameKeyboardProtectionState.LlkhfExtended, true, false, false, false);
            AssertFalse(state.Process(GameKeyboardProtectionState.VkD, 0,
                true, false, false, false).ShowDesktop, "disabled shortcut never triggers");
            AssertFalse(state.Process(GameKeyboardProtectionState.VkD, 0,
                false, true, false, false).Suppress, "disabled shortcut does not consume D up");
            state.Process(GameKeyboardProtectionState.VkRightControl,
                GameKeyboardProtectionState.LlkhfExtended, false, true, false, false);
            AssertTrue(state.IsClean, "disabled shortcut leaves no Ctrl or D state");
        }

        private static void GameHelperShowDesktopFallbackPreservesToolWindows()
        {
            AssertTrue(GameHelperModule.ShouldForceMinimizeForShowDesktop(true, false, 0),
                "ordinary visible application windows are minimized");
            AssertFalse(GameHelperModule.ShouldForceMinimizeForShowDesktop(false, false, 0),
                "hidden windows are ignored");
            AssertFalse(GameHelperModule.ShouldForceMinimizeForShowDesktop(true, true, 0),
                "shell desktop windows are ignored");
            AssertFalse(GameHelperModule.ShouldForceMinimizeForShowDesktop(
                true, false, 0x00000080),
                "WS_EX_TOOLWINDOW overlays, including the crosshair, are preserved");
            AssertFalse(GameHelperModule.ShouldForceMinimizeForShowDesktop(
                true, false, unchecked((int)0x08080080)),
                "the tool-window bit remains authoritative alongside other overlay styles");
        }

        private static void GameHelperInputLayoutComparisonUsesLow32Bits()
        {
            AssertTrue(GameInputLanguageNative.LayoutsEqual(new IntPtr(unchecked((long)0xFFFFFFFF04090409)),
                new IntPtr(0x04090409)), "sign extension does not change HKL identity");
            AssertFalse(GameInputLanguageNative.LayoutsEqual(new IntPtr(0x04040404),
                new IntPtr(0x04090409)), "different HKLs do not match");
        }

        private static void GameHelperOriginalLayoutCacheRequiresConfirmedRestore()
        {
            var cache = new GameInputLanguageOriginalCache();
            const uint threadId = 42;
            const string executable = @"C:\Games\Apex\r5apex_dx12.exe";
            IntPtr chinese = new IntPtr(0x04040404);
            IntPtr english = GameInputLanguageNative.MicrosoftEnglishUsLayout;

            cache.Remember(threadId, executable, chinese);
            IntPtr remembered;
            AssertTrue(cache.TryGet(threadId, executable, out remembered)
                && GameInputLanguageNative.LayoutsEqual(chinese, remembered),
                "a failed restore retains the pre-game layout");

            cache.Forget(threadId, executable, english);
            AssertTrue(cache.TryGet(threadId, executable, out remembered)
                && cache.Count == 1,
                "a mismatched readback cannot discard the pre-game layout");

            cache.Forget(threadId, executable, chinese);
            AssertTrue(!cache.TryGet(threadId, executable, out remembered)
                && cache.Count == 0,
                "a confirmed matching readback releases the remembered layout");
        }

        private static void GameHelperElevatedHostRejectsUntrustedRequests()
        {
            GameHelperElevationResponse missing = GameHelperElevationHost.HandleRequest(null, "token");
            AssertFalse(missing.Success, "missing request is rejected");

            GameHelperElevationResponse wrongToken = GameHelperElevationHost.HandleRequest(
                new GameHelperElevationRequest
                {
                    Type = "post",
                    Token = "wrong",
                    ThreadId = 1,
                    FallbackHwnd = 1,
                    Layout = 0x04090409,
                    ExpectedPath = @"C:\Games\A.exe"
                }, "token");
            AssertFalse(wrongToken.Success, "wrong token is rejected before native access");
            AssertEqual(87, wrongToken.Error, "invalid request has ERROR_INVALID_PARAMETER");
        }

        private static void KeyboardGuardLocksAndRestoresCharacterWidthSettings()
        {
            var original11 = new InputMethodHotKeyState(true, 0xC004, 0x20, IntPtr.Zero);
            var original71 = new InputMethodHotKeyState(true, 0xC004, 0x20, IntPtr.Zero);
            var inputApi = new FakeInputMethodHotKeyApi(original11, original71);
            var microsoftApi = new FakeMicrosoftImeCharacterWidthApi(
                new RegistryTextValueState(true, "0x00000001"));
            var asusApi = new FakeAsusImeCharacterWidthApi(false);
            var settings = new KeyboardGuardSettings();
            using (var module = new KeyboardGuardModule(settings,
                new FakeStickyKeysSettingsApi(0), inputApi, microsoftApi, asusApi))
            {
                AssertTrue(module.DisableShiftSpaceWidthToggle(), "width protection enables");
                AssertTrue(microsoftApi.State.Equals(
                    new RegistryTextValueState(true, "0x00000000")),
                    "Microsoft IME vendor shortcut setting is disabled");
                AssertFalse(asusApi.FullWidth, "ASUS IME captured width state is retained");
                AssertTrue(inputApi.Read(KeyboardGuardModule.SimplifiedChineseShapeToggleHotKey)
                    .Equals(original11), "IMM hotkey remains untouched");

                microsoftApi.State = new RegistryTextValueState(true, "0x00000001");
                asusApi.FullWidth = true;
                module.EnsureCharacterWidthProtection();
                AssertTrue(microsoftApi.State.Equals(
                    new RegistryTextValueState(true, "0x00000000")),
                    "Microsoft setting watchdog corrects external changes");
                AssertFalse(asusApi.FullWidth, "ASUS setting watchdog corrects any width change");

                AssertTrue(module.RestoreShiftSpaceWidthToggle(), "vendor settings restore");
                AssertTrue(microsoftApi.State.Equals(
                    new RegistryTextValueState(true, "0x00000001")),
                    "Microsoft original value restores exactly");
                AssertFalse(asusApi.FullWidth, "ASUS original width restores exactly");
                AssertFalse(settings.DisableShiftSpaceWidthToggle, "restored setting is off");
            }
        }

        private static void KeyboardGuardRollsBackCharacterWidthFailures()
        {
            var original11 = new InputMethodHotKeyState(true, 0xC004, 0x20, IntPtr.Zero);
            var original71 = new InputMethodHotKeyState(true, 0xC004, 0x20, IntPtr.Zero);
            var inputApi = new FakeInputMethodHotKeyApi(original11, original71);
            var microsoftApi = new FakeMicrosoftImeCharacterWidthApi(
                new RegistryTextValueState(true, "0x00000001"));
            var asusApi = new FakeAsusImeCharacterWidthApi(false) { RemainingWriteFailures = 1 };
            var settings = new KeyboardGuardSettings();
            using (var module = new KeyboardGuardModule(settings,
                new FakeStickyKeysSettingsApi(0), inputApi, microsoftApi, asusApi))
            {
                AssertFalse(module.DisableShiftSpaceWidthToggle(), "ASUS write failure is reported");
                AssertTrue(microsoftApi.State.Equals(
                    new RegistryTextValueState(true, "0x00000001")),
                    "Microsoft change rolls back after the ASUS failure");
                AssertFalse(settings.DisableShiftSpaceWidthToggle, "failure does not persist enabled state");
                AssertFalse(settings.ShiftSpaceMicrosoftBackupCaptured,
                    "provisional Microsoft backup rolls back");
                AssertFalse(settings.ShiftSpaceAsusBackupCaptured,
                    "provisional ASUS backup rolls back");
            }
        }

        private static void KeyboardGuardMigratesDeployedLegacyImmBackup()
        {
            var expected11 = new InputMethodHotKeyState(true, 0xC000, 0xFF, IntPtr.Zero);
            var expected71 = new InputMethodHotKeyState(true, 0xC004, 0x20, IntPtr.Zero);
            var inputApi = new FakeInputMethodHotKeyApi(InputMethodHotKeyState.Disabled,
                InputMethodHotKeyState.Disabled);
            var settings = new KeyboardGuardSettings
            {
                ShiftSpaceBackupCaptured = true,
                ShiftSpaceBackup00000011 = "1|FF000000|00C00000|00000000",
                ShiftSpaceBackup00000071 = "1|20000000|04C00000|00000000"
            };

            using (var module = new KeyboardGuardModule(settings,
                new FakeStickyKeysSettingsApi(0), inputApi,
                new FakeMicrosoftImeCharacterWidthApi(
                    new RegistryTextValueState(true, "0x00000001")),
                new FakeAsusImeCharacterWidthApi(false)))
            {
                AssertTrue(module.DisableShiftSpaceWidthToggle(), "width protection enables during migration");
                AssertTrue(inputApi.Read(KeyboardGuardModule.SimplifiedChineseShapeToggleHotKey)
                    .Equals(expected11), "legacy Simplified Chinese IMM hotkey restores exactly");
                AssertTrue(inputApi.Read(KeyboardGuardModule.TraditionalChineseShapeToggleHotKey)
                    .Equals(expected71), "legacy Traditional Chinese IMM hotkey restores exactly");
                AssertFalse(settings.ShiftSpaceBackupCaptured, "deployed legacy backup is cleared");
                AssertEqual(string.Empty, settings.ShiftSpaceBackup00000011,
                    "deployed legacy 0x11 value is cleared");
                AssertEqual(string.Empty, settings.ShiftSpaceBackup00000071,
                    "deployed legacy 0x71 value is cleared");
            }
        }

        private static void AppGuardCommandLineIsManageOnly()
        {
            const string path = @"C:\Program Files\Example App\Example.exe";
            AppActionRequest request;
            string error;

            AssertTrue(AppActionCommandLine.TryParse(new string[] { AppActionCommandLine.ManageTargetArg, path },
                out request, out error), "manage target parses");
            AssertNotNull(request, "manage target request exists");
            AssertEqual(AppActionType.Manage, request.Action, "manage action");
            AssertEqual(path, request.TargetPath, "target path retained");
            AssertTrue(request.FromShell, "request is marked from shell");

            AssertFalse(AppActionCommandLine.TryParse(new string[] { "--app-guard-action", "Run", path },
                out request, out error), "old multi-action command does not parse");

            string args = AppActionCommandLine.BuildManageArgs(path);
            AssertTrue(args.StartsWith(AppActionCommandLine.ManageTargetArg + " ", StringComparison.Ordinal),
                "built args start with manage flag");
            AssertTrue(args.IndexOf("\"" + path + "\"", StringComparison.Ordinal) >= 0,
                "built args quote path with spaces");
        }

        private static void AppGuardIpcAllowListIsManageTargetOnly()
        {
            AssertTrue(AppActionIpcServer.IsAllowed(new AppActionRequest
            {
                Action = AppActionType.Manage,
                TargetPath = @"C:\Apps\App.exe"
            }), "manage target is allowed");

            AssertFalse(AppActionIpcServer.IsAllowed(new AppActionRequest
            {
                Action = AppActionType.Run,
                TargetPath = @"C:\Apps\App.exe"
            }), "run action is rejected");

            AssertFalse(AppActionIpcServer.IsAllowed(new AppActionRequest
            {
                Action = AppActionType.Manage,
                TargetPath = string.Empty
            }), "empty target is rejected");
        }

        private static void AppCatalogImportsDirectExecutable()
        {
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            AppCatalogItem item = new AppCatalogService().ResolvePathAsApp(executable);
            AssertNotNull(item, "current executable resolves");
            AssertEqual(AppIdentityService.CreateExecutableId(executable), item.Id, "direct exe identity");
            AssertEqual("Executable", item.Source, "direct exe source");
            AssertEqual(AppIdentityService.NormalizeExecutablePath(executable), item.TargetPath, "direct exe path");
        }

        private static void AppGuardBlocksSelfDestructiveActions()
        {
            var current = new AppCatalogItem
            {
                TargetPath = Process.GetCurrentProcess().MainModule.FileName
            };
            var other = new AppCatalogItem
            {
                TargetPath = @"C:\Apps\Other.exe"
            };

            AssertTrue(AppActionService.IsCurrentApplication(current), "current executable is identified");
            AssertTrue(AppActionService.IsBlockedSelfAction(current, AppActionType.Terminate),
                "self terminate is blocked");
            AssertTrue(AppActionService.IsBlockedSelfAction(current, AppActionType.Restart),
                "self restart is blocked");
            AssertFalse(AppActionService.IsBlockedSelfAction(current, AppActionType.Run),
                "non-destructive self action remains available");
            AssertFalse(AppActionService.IsBlockedSelfAction(other, AppActionType.Terminate),
                "other applications retain terminate support");
        }

        private static void AppGuardExpansionScrollsSmoothlyToViewportCenter()
        {
            AssertEqual(500d, MainWindow.CalculateAppGuardCenteredScrollOffset(
                420d, 180d, 400d, 600d, 1000d),
                "expanded card center is aligned with the viewport center");
            AssertEqual(1000d, MainWindow.CalculateAppGuardCenteredScrollOffset(
                900d, 200d, 400d, 600d, 1000d),
                "scroll target is clamped to the available extent");
            AssertEqual(0d, MainWindow.CalculateAppGuardCenteredScrollOffset(
                50d, 20d, 200d, 600d, 1000d),
                "scroll target never becomes negative");
            AssertEqual(600d, MainWindow.CalculateAppGuardCenteredScrollOffset(
                420d, 180d, 800d, 600d, 1000d),
                "cards taller than the viewport retain top alignment");
            AssertEqual(100d, MainWindow.CalculateAppGuardRequiredScrollTail(
                420d, 180d, 400d, 600d, 400d),
                "a tail spacer supplies missing scroll extent for the final cards");
            AssertEqual(0d, MainWindow.CalculateAppGuardRequiredScrollTail(
                420d, 180d, 400d, 600d, 700d),
                "no tail spacer is added when the existing extent is sufficient");
            AssertEqual(100d, MainWindow.CalculateAppGuardScrollAnimationOffset(100d, 500d, 0d),
                "animation starts at the current offset");
            AssertEqual(450d, MainWindow.CalculateAppGuardScrollAnimationOffset(100d, 500d, 0.5d),
                "animation uses cubic ease-out interpolation");
            AssertEqual(500d, MainWindow.CalculateAppGuardScrollAnimationOffset(100d, 500d, 1d),
                "animation ends exactly at the target offset");
        }

        private static void AppGuardBatchDetailsShareProcessSnapshot()
        {
            int snapshotCount = 0;
            string runningPath = AppIdentityService.NormalizeExecutablePath(@"C:\Apps\Running.exe");
            var service = new AppActionService(delegate
            {
                snapshotCount++;
                return new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
                {
                    { runningPath, new List<int> { 41, 42 } }
                };
            });
            var apps = new List<AppCatalogItem>
            {
                new AppCatalogItem { Id = "running", TargetPath = runningPath },
                new AppCatalogItem { Id = "idle", TargetPath = @"C:\Apps\Idle.exe" },
                new AppCatalogItem { Id = "other", TargetPath = @"C:\Apps\Other.exe" }
            };

            List<AppCatalogDetail> details = service.LoadDetails(apps);
            AssertEqual(1, snapshotCount, "one process snapshot serves the complete detail batch");
            AssertEqual(3, details.Count, "every batch item receives a detail record");
            AssertTrue(details[0].IsRunning, "running app is matched from the shared snapshot");
            AssertEqual(2, details[0].ProcessIds.Count, "all matching process ids are retained");
            AssertFalse(details[1].IsRunning, "non-running app remains idle");
        }

        private static void AppGuardPrefetchWarmsDetailCache()
        {
            int snapshotCount = 0;
            var service = new AppActionService(delegate
            {
                snapshotCount++;
                return new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            });
            using (var module = new AppGuardModule(new AppCatalogService(), service))
            {
                module.PrefetchDetails(new List<AppCatalogItem>
                {
                    new AppCatalogItem { Id = "one", TargetPath = @"C:\Apps\One.exe" },
                    new AppCatalogItem { Id = "two", TargetPath = @"C:\Apps\Two.exe" }
                });

                AppCatalogDetail first = null;
                AppCatalogDetail second = null;
                AssertTrue(PumpUntil(delegate
                {
                    return module.TryGetDetail("one", out first)
                        && module.TryGetDetail("two", out second);
                }, 3000), "prefetch fills the cache for every materialized app");
                AssertEqual(1, snapshotCount, "one prefetch batch uses one process snapshot");
                AssertNotNull(first, "first prefetched detail is available synchronously");
                AssertNotNull(second, "second prefetched detail is available synchronously");
            }
        }

        private static void EmptyShortcutIconLocationIsIgnored()
        {
            AssertEqual(string.Empty, AppIdentityService.NormalizeIconPath(",0"),
                "shortcut without an icon file is empty");

            string shellIcon = AppIdentityService.NormalizeIconPath(
                "\"C:\\Windows\\System32\\shell32.dll\",4");
            AssertEqual(AppIdentityService.NormalizeExecutablePath(@"C:\Windows\System32\shell32.dll"), shellIcon,
                "icon resource index is removed");
        }

        private static void AppCatalogMergesSteamIdentity()
        {
            AssertEqual("steam:1172470", AppCatalogService.GetSteamLogicalId(
                "Steam App 1172470", @"C:\Program Files (x86)\Steam\steam.exe steam://uninstall/1172470"),
                "Steam registry identity is stable");

            var candidates = new List<AppCatalogItem>
            {
                new AppCatalogItem
                {
                    Id = "exe:apex",
                    Name = "《Apex 英雄》",
                    Publisher = "Steam",
                    Source = "Steam",
                    TargetPath = @"D:\Steam\steamapps\common\Apex Legends\r5apex_dx12.exe",
                    LogicalId = "steam:1172470"
                },
                new AppCatalogItem
                {
                    Id = "exe:apex-icon",
                    Name = "《Apex 英雄》",
                    Publisher = "Respawn",
                    Source = "Installed apps",
                    TargetPath = @"C:\Program Files (x86)\Steam\steam\games\apex.ico",
                    IconPath = @"C:\Program Files (x86)\Steam\steam\games\apex.ico",
                    UninstallCommand = "steam://uninstall/1172470",
                    LogicalId = "steam:1172470"
                }
            };

            List<AppCatalogItem> merged = AppCatalogService.CollapseEquivalentApps(candidates);
            AssertEqual(1, merged.Count, "Steam shortcut and registry icon collapse");
            AssertEqual(candidates[0].TargetPath, merged[0].TargetPath, "real Steam executable is retained");
            AssertEqual("Respawn", merged[0].Publisher, "product publisher replaces the generic Steam publisher");
            AssertEqual(candidates[1].UninstallCommand, merged[0].UninstallCommand,
                "registry uninstall metadata is retained");
            AssertTrue(merged[0].Source.IndexOf("Installed apps", StringComparison.OrdinalIgnoreCase) >= 0,
                "merged source remains visible");
        }

        private static void AppCatalogPrefersLaunchTarget()
        {
            var candidates = new List<AppCatalogItem>
            {
                new AppCatalogItem
                {
                    Id = "exe:docker-launch",
                    Name = "Docker Desktop",
                    Source = "Start Menu",
                    TargetPath = @"C:\Program Files\Docker\Docker\frontend\Docker Desktop.exe"
                },
                new AppCatalogItem
                {
                    Id = "exe:docker-installer",
                    Name = "Docker Desktop",
                    Publisher = "Docker Inc.",
                    Source = "Installed apps",
                    TargetPath = @"C:\Program Files\Docker\Docker\Docker Desktop Installer.exe",
                    InstallLocation = @"C:\Program Files\Docker\Docker",
                    UninstallCommand = @"Docker Desktop Installer.exe uninstall"
                }
            };

            List<AppCatalogItem> merged = AppCatalogService.CollapseEquivalentApps(candidates);
            AssertEqual(1, merged.Count, "Start Menu and installer entries collapse within one install family");
            AssertEqual(candidates[0].TargetPath, merged[0].TargetPath, "launch executable wins over installer");
            AssertEqual("Docker Inc.", merged[0].Publisher, "installed-app publisher is retained");
            AssertEqual(candidates[1].UninstallCommand, merged[0].UninstallCommand,
                "installed-app command is retained");
        }

        private static void AppCatalogMergesLocalizedProductLauncher()
        {
            var candidates = new List<AppCatalogItem>
            {
                new AppCatalogItem
                {
                    Id = "exe:apex-launcher",
                    Name = "ApexLauncher",
                    Source = "Start Menu",
                    TargetPath = @"D:\Apex\ApexLauncher.exe",
                    ShortcutPath = @"C:\Start Menu\ApexLauncher.lnk"
                },
                new AppCatalogItem
                {
                    Id = "exe:apex-game",
                    Name = "Apex 英雄",
                    Publisher = "Electronic Arts, Inc.",
                    Source = "Installed apps",
                    TargetPath = @"D:\Apex\r5apex_dx12.exe",
                    InstallLocation = @"D:\Apex"
                }
            };

            List<AppCatalogItem> merged = AppCatalogService.CollapseEquivalentApps(candidates);
            AssertEqual(1, merged.Count, "localized product and launcher collapse within one install family");
            AssertEqual(candidates[1].TargetPath, merged[0].TargetPath,
                "the real game executable wins over the launcher wrapper");
            AssertEqual(candidates[1].Name, merged[0].Name,
                "the installed product display name is retained");
            AssertEqual(candidates[0].ShortcutPath, merged[0].ShortcutPath,
                "the launcher shortcut metadata is retained");
        }

        private static void AppCatalogKeepsUnrelatedNamesSeparate()
        {
            var candidates = new List<AppCatalogItem>
            {
                new AppCatalogItem
                {
                    Id = "exe:tool-one",
                    Name = "Shared Tool",
                    Source = "Start Menu",
                    TargetPath = @"C:\VendorA\Shared Tool.exe"
                },
                new AppCatalogItem
                {
                    Id = "exe:tool-two",
                    Name = "Shared Tool",
                    Publisher = "Vendor B",
                    Source = "Installed apps",
                    TargetPath = @"D:\VendorB\Shared Tool.exe",
                    InstallLocation = @"D:\VendorB"
                }
            };

            List<AppCatalogItem> merged = AppCatalogService.CollapseEquivalentApps(candidates);
            AssertEqual(2, merged.Count, "same display name alone never merges unrelated applications");
        }

        private static void AppCatalogPrefersExplicitInternetShortcutIcon()
        {
            var app = new AppCatalogItem
            {
                ShortcutPath = @"C:\Start Menu\Apex.url",
                IconPath = @"C:\Steam\games\apex.ico",
                TargetPath = @"D:\Steam\Apex\r5apex_dx12.exe"
            };

            List<string> candidates = MainWindow.BuildAppCatalogIconCandidates(app);
            AssertEqual(3, candidates.Count, "all unique icon candidates remain available");
            AssertEqual(app.IconPath, candidates[0], "explicit IconFile precedes the generic url shell icon");
            AssertEqual(app.TargetPath, candidates[1], "executable is the first fallback");
            AssertEqual(app.ShortcutPath, candidates[2], "url shell icon is the final fallback");
        }

        private static void ApplicationIconReplacementIsOrphanFree()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "GuardCenter-icon-test-" + Guid.NewGuid().ToString("N"));
            string appearanceRoot = Path.Combine(root, "Appearance");
            string firstSource = Path.Combine(root, "first.png");
            string secondSource = Path.Combine(root, "second.png");
            try
            {
                Directory.CreateDirectory(root);
                using (var first = new System.Drawing.Bitmap(80, 40))
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(first))
                {
                    graphics.Clear(System.Drawing.Color.Red);
                    first.Save(firstSource, System.Drawing.Imaging.ImageFormat.Png);
                }
                using (var second = new System.Drawing.Bitmap(40, 80))
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(second))
                {
                    graphics.Clear(System.Drawing.Color.Blue);
                    second.Save(secondSource, System.Drawing.Imaging.ImageFormat.Png);
                }

                string imported = ApplicationIconService.Import(firstSource, appearanceRoot);
                byte[] firstBytes = File.ReadAllBytes(imported);
                File.WriteAllText(Path.Combine(appearanceRoot, "CustomAppIcon-old.png"), "orphan");

                string replaced = ApplicationIconService.Import(secondSource, appearanceRoot);
                byte[] secondBytes = File.ReadAllBytes(replaced);
                List<string> managed = ApplicationIconService.GetManagedFiles(appearanceRoot);

                AssertEqual(imported, replaced, "each upload reuses the same managed PNG path");
                AssertFalse(Convert.ToBase64String(firstBytes) == Convert.ToBase64String(secondBytes),
                    "second upload replaces the first image contents");
                AssertEqual(2, managed.Count, "only the current PNG and ICO remain");
                AssertTrue(File.Exists(Path.Combine(appearanceRoot,
                    ApplicationIconService.CustomPngFileName)), "current PNG exists");
                AssertTrue(File.Exists(Path.Combine(appearanceRoot,
                    ApplicationIconService.CustomIcoFileName)), "current ICO exists");
                AssertFalse(File.Exists(Path.Combine(appearanceRoot, "CustomAppIcon-old.png")),
                    "obsolete managed image is removed");

                using (var output = new System.Drawing.Bitmap(replaced))
                {
                    AssertEqual(512, output.Width, "uploaded image is normalized to square width");
                    AssertEqual(512, output.Height, "uploaded image is normalized to square height");
                }

                ApplicationIconService.Reset(appearanceRoot);
                AssertFalse(Directory.Exists(appearanceRoot), "reset removes all managed icon files");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void StickyKeysHotkeyPreservesFlagsAndAvoidsLoops()
        {
            const uint original = 0x000001FE;
            var settings = new KeyboardGuardSettings();
            var api = new FakeStickyKeysSettingsApi(original);
            using (var module = new KeyboardGuardModule(settings, api))
            {
                AssertTrue(module.DisableStickyKeysHotkey(), "hotkey protection enables");
                AssertEqual(original & ~KeyboardGuardModule.StickyKeysHotkeyActive, api.Flags,
                    "enable clears only SKF_HOTKEYACTIVE");
                AssertTrue(settings.StickyKeysOriginalHotkeyActive, "original hotkey bit captured");

                int writesAfterEnable = api.WriteCount;
                module.CheckStickyKeysAfterPreferenceChange();
                AssertEqual(writesAfterEnable, api.WriteCount,
                    "self-generated preference notification does not write again");

                api.Flags ^= 0x00000008;
                uint externallyChangedWithoutHotkey = api.Flags;
                module.CheckStickyKeysAfterPreferenceChange();
                AssertEqual(writesAfterEnable, api.WriteCount,
                    "external changes that keep hotkey disabled are observed but not overwritten");

                api.Flags |= KeyboardGuardModule.StickyKeysHotkeyActive;
                module.CheckStickyKeysAfterPreferenceChange();
                AssertEqual(writesAfterEnable + 1, api.WriteCount,
                    "external hotkey re-enable is corrected once");
                AssertEqual(externallyChangedWithoutHotkey, api.Flags,
                    "reapply preserves every unrelated externally changed flag");

                api.Flags ^= 0x00000020;
                uint beforeRestore = api.Flags;
                AssertTrue(module.RestoreStickyKeysHotkey(), "hotkey protection disables");
                AssertEqual(beforeRestore | KeyboardGuardModule.StickyKeysHotkeyActive, api.Flags,
                    "disable restores only the originally enabled hotkey bit");
                AssertFalse(settings.DisableStickyKeysHotkey, "configured state clears after verified restore");
                AssertFalse(settings.StickyKeysHotkeyBackupCaptured, "next enable cycle captures a fresh baseline");
            }
        }

        private static void StickyKeysRestoreRespectsOriginalDisabledState()
        {
            const uint originalWithoutHotkey = 0x000001FA;
            var settings = new KeyboardGuardSettings();
            var api = new FakeStickyKeysSettingsApi(originalWithoutHotkey);
            using (var module = new KeyboardGuardModule(settings, api))
            {
                AssertTrue(module.DisableStickyKeysHotkey(), "already-disabled hotkey can be protected");
                api.Flags |= 0x00000040;
                AssertTrue(module.RestoreStickyKeysHotkey(), "restore succeeds");
                AssertEqual(originalWithoutHotkey | 0x00000040, api.Flags,
                    "restore keeps hotkey disabled and preserves other current flags");
            }
        }

        private static void StickyKeysFailuresDoNotPersistSuccess()
        {
            var settings = new KeyboardGuardSettings();
            var api = new FakeStickyKeysSettingsApi(0x000001FE) { WriteFails = true };
            using (var module = new KeyboardGuardModule(settings, api))
            {
                AssertFalse(module.DisableStickyKeysHotkey(), "failed write is reported");
                AssertFalse(settings.DisableStickyKeysHotkey, "failed write does not enable saved setting");
                AssertFalse(settings.StickyKeysHotkeyBackupCaptured,
                    "failed first write rolls back the provisional backup");
            }

            settings = new KeyboardGuardSettings
            {
                DisableStickyKeysHotkey = true,
                StickyKeysHotkeyBackupCaptured = true,
                StickyKeysOriginalHotkeyActive = true
            };
            api = new FakeStickyKeysSettingsApi(0x000001FA) { ReadFails = true };
            using (var module = new KeyboardGuardModule(settings, api))
            {
                AssertFalse(module.RestoreStickyKeysHotkey(), "failed read prevents restore");
                AssertTrue(settings.DisableStickyKeysHotkey,
                    "failed restore keeps protection configured for a later retry");
                AssertTrue(settings.StickyKeysHotkeyBackupCaptured,
                    "failed restore keeps the original hotkey bit backup");
            }
        }

        private static void DeviceGuardPresentationSeparatesHealthAndRepairResult()
        {
            var healthy = new CoreHardwareItem
            {
                Health = CoreHardwareHealth.Healthy,
                IsPresent = true,
                IsStarted = true,
                ProblemCode = 0,
                RepairState = DeviceRepairState.RebootRequired,
                ResultMessage = "Windows recommends restart."
            };
            AssertEqual(DeviceHealthPresentationState.Healthy, DeviceGuardPresentation.Classify(healthy),
                "a prior repair restart recommendation does not replace current health");
            AssertTrue(DeviceGuardPresentation.HasOperation(healthy.RepairState, healthy.ResultMessage),
                "prior repair remains visible as a separate operation");

            healthy.Health = CoreHardwareHealth.DriverMissing;
            healthy.ProblemCode = 28;
            AssertEqual(DeviceHealthPresentationState.DriverError, DeviceGuardPresentation.Classify(healthy),
                "problem 28 is shown as a driver error");
            healthy.Health = CoreHardwareHealth.Disabled;
            healthy.ProblemCode = 22;
            AssertEqual(DeviceHealthPresentationState.Disabled, DeviceGuardPresentation.Classify(healthy),
                "problem 22 is shown as disabled");
            healthy.Health = CoreHardwareHealth.Missing;
            healthy.IsPresent = false;
            AssertEqual(DeviceHealthPresentationState.Missing, DeviceGuardPresentation.Classify(healthy),
                "non-present core hardware is shown as missing");
            healthy.IsPresent = true;
            healthy.IsStarted = false;
            healthy.Health = CoreHardwareHealth.Healthy;
            healthy.ProblemCode = 0;
            AssertEqual(DeviceHealthPresentationState.Stopped, DeviceGuardPresentation.Classify(healthy),
                "present but non-started hardware is shown as stopped");
            healthy.IsStarted = true;
            healthy.Health = CoreHardwareHealth.RebootRequired;
            AssertEqual(DeviceHealthPresentationState.RebootRequired, DeviceGuardPresentation.Classify(healthy),
                "current reboot-required health is shown independently of operation history");
            healthy.RepairState = DeviceRepairState.Repairing;
            AssertEqual(DeviceHealthPresentationState.Repairing, DeviceGuardPresentation.Classify(healthy),
                "active repair has a transient status");
            healthy.RepairState = DeviceRepairState.Ready;
            healthy.Health = CoreHardwareHealth.Unknown;
            AssertEqual(DeviceHealthPresentationState.Unknown, DeviceGuardPresentation.Classify(healthy),
                "unverified health is explicitly unknown");
            AssertFalse(DeviceGuardPresentation.HasOperation(DeviceRepairState.Ready, string.Empty),
                "cards without an operation collapse the operation section");
        }

        private static void TrayMenuRemainsOpenOnlyAfterItemClicks()
        {
            foreach (System.Windows.Forms.ToolStripDropDownCloseReason reason in
                Enum.GetValues(typeof(System.Windows.Forms.ToolStripDropDownCloseReason)))
            {
                var eventArgs = new System.Windows.Forms.ToolStripDropDownClosingEventArgs(reason);
                GuardCenterController.KeepTrayMenuOpenOnItemClick(null, eventArgs);
                AssertEqual(reason == System.Windows.Forms.ToolStripDropDownCloseReason.ItemClicked,
                    eventArgs.Cancel, reason + " uses the expected tray-menu closing behavior");
            }
        }

        private static void TrayMenuCheckedStatesKeepOneRowHeight()
        {
            using (var menu = new System.Windows.Forms.ContextMenuStrip
            {
                ShowCheckMargin = true,
                ShowImageMargin = false
            })
            {
                var rootToggle = new GuardCenterController.StableTrayMenuItem("Toggle") { Checked = false };
                menu.Items.Add(rootToggle);
                var parent = new GuardCenterController.StableTrayMenuItem("Parent");
                var childToggle = new GuardCenterController.StableTrayMenuItem("Child") { Checked = true };
                parent.DropDownItems.Add(childToggle);
                menu.Items.Add(parent);
                var childMenu = (System.Windows.Forms.ToolStripDropDownMenu)parent.DropDown;
                childMenu.ShowCheckMargin = true;
                childMenu.ShowImageMargin = false;

                int stableHeight = GuardCenterController.StabilizeTrayMenuItemHeights(menu, childMenu);
                AssertTrue(stableHeight > 0, "tray menu resolves a positive preferred row height");
                AssertEqual(stableHeight, rootToggle.StableHeight,
                    "unchecked root items use the shared row height");
                AssertEqual(stableHeight, parent.StableHeight,
                    "parent items use the shared row height");
                AssertEqual(stableHeight, childToggle.StableHeight,
                    "checked child items use the shared row height");
                AssertEqual(0, stableHeight % 2, "shared row height uses WinForms even-pixel alignment");
                AssertEqual(stableHeight, rootToggle.Height,
                    "unchecked root item is laid out at the shared height");
                AssertEqual(stableHeight, childToggle.Height,
                    "checked child item is laid out at the shared height");

                rootToggle.Checked = true;
                childToggle.Checked = false;
                menu.PerformLayout();
                childMenu.PerformLayout();
                AssertEqual(stableHeight, rootToggle.GetPreferredSize(System.Drawing.Size.Empty).Height,
                    "checking a root item does not change its row-height floor");
                AssertEqual(stableHeight, childToggle.GetPreferredSize(System.Drawing.Size.Empty).Height,
                    "unchecking a child item does not change its row-height floor");
                AssertEqual(stableHeight, rootToggle.Height,
                    "checked root item remains at the shared laid-out height");
                AssertEqual(stableHeight, childToggle.Height,
                    "unchecked child item remains at the shared laid-out height");

                int shortWidth = rootToggle.GetPreferredSize(System.Drawing.Size.Empty).Width;
                rootToggle.Text = "A much longer dynamic tray-menu label";
                menu.PerformLayout();
                AssertTrue(rootToggle.GetPreferredSize(System.Drawing.Size.Empty).Width > shortWidth,
                    "stable row height does not prevent automatic width growth");
                AssertEqual(stableHeight, rootToggle.Height,
                    "dynamic text keeps the shared laid-out height");
            }
        }

        private static void PowerGuardDurationBoundsAndFormattingIncludeDaysAndSeconds()
        {
            AssertEqual(TimeSpan.FromMinutes(1), PowerGuardDurations.ClampCustom(TimeSpan.Zero),
                "custom duration keeps a one-minute minimum");
            AssertEqual(TimeSpan.FromDays(30), PowerGuardDurations.ClampCustom(TimeSpan.FromDays(30)),
                "custom duration accepts exactly 30 days");
            AssertEqual(TimeSpan.FromDays(30),
                PowerGuardDurations.ClampCustom(TimeSpan.FromDays(30) + TimeSpan.FromHours(23)),
                "30 days plus additional time normalizes to the 30-day maximum");

            AssertEqual("0 秒", GuardCenterController.FormatPowerGuardRemaining(TimeSpan.Zero),
                "zero remaining time uses seconds");
            AssertEqual("42 秒", GuardCenterController.FormatPowerGuardRemaining(TimeSpan.FromSeconds(42)),
                "seconds are shown without leading zero units");
            AssertEqual("4 分 33 秒",
                GuardCenterController.FormatPowerGuardRemaining(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(33)),
                "minutes and seconds are both shown");
            AssertEqual("3 小時 0 分 4 秒",
                GuardCenterController.FormatPowerGuardRemaining(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(4)),
                "zero units after the largest unit remain visible");
            AssertEqual("2 天 3 小時 0 分 4 秒",
                GuardCenterController.FormatPowerGuardRemaining(
                    TimeSpan.FromDays(2) + TimeSpan.FromHours(3) + TimeSpan.FromSeconds(4)),
                "days are the largest dynamic unit");
            AssertEqual("1 秒", GuardCenterController.FormatPowerGuardRemaining(TimeSpan.FromMilliseconds(1)),
                "positive fractional seconds round upward");
            AssertEqual("1 分 0 秒",
                GuardCenterController.FormatPowerGuardRemaining(TimeSpan.FromSeconds(59.001)),
                "ceiling prevents the countdown from showing zero early");

            AssertEqual("2 小時", GuardCenterController.FormatPowerGuardDuration(TimeSpan.FromHours(2)),
                "static duration remains concise");
            AssertEqual("1 天 2 小時 3 分鐘",
                GuardCenterController.FormatPowerGuardDuration(
                    TimeSpan.FromDays(1) + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3)),
                "static duration supports days at minute precision");
            AssertEqual("30 天", GuardCenterController.FormatPowerGuardDuration(TimeSpan.FromDays(30)),
                "slider maximum shows the 30-day limit concisely");
        }

        private static void PowerGuardRestoresPersistedPreferences()
        {
            var settings = new PowerGuardSettings
            {
                DurationKind = "Custom",
                CustomDurationMinutes = 1503,
                KeepDisplayOn = true
            };
            var factory = new FakePowerRequestLeaseFactory();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(settings, factory, clock, false, false))
            {
                PowerGuardState restored = module.GetState();
                AssertFalse(restored.IsEnabled,
                    "restored preferences do not automatically enable power guard");
                AssertEqual(PowerGuardDurationKind.Custom, restored.DurationKind,
                    "custom duration mode is restored");
                AssertEqual(TimeSpan.FromMinutes(1503), restored.CustomDuration,
                    "custom duration value is restored");
                AssertFalse(restored.KeepDisplayOn,
                    "display request remains inactive until power guard is enabled");

                string error;
                AssertTrue(module.TrySetEnabled(true, out error),
                    "power guard enables with restored preferences");
                AssertTrue(module.GetState().KeepDisplayOn,
                    "restored display preference is applied on enable");
                AssertEqual(clock.GetUtcNow() + TimeSpan.FromMinutes(1503),
                    module.GetState().EndAtUtc,
                    "restored duration establishes the new deadline");
            }
        }

        private static void PowerGuardSelectionStaysPassiveWhileOff()
        {
            var factory = new FakePowerRequestLeaseFactory();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(factory, clock, false, false))
            {
                PowerGuardState initial = module.GetState();
                AssertFalse(initial.IsEnabled, "power guard starts off");
                AssertEqual(PowerGuardDurationKind.TwoHours, initial.DurationKind, "two hours is the default");

                module.SelectDuration(PowerGuardDurationKind.ThirtyMinutes, TimeSpan.Zero);
                PowerGuardState selected = module.GetState();
                AssertFalse(selected.IsEnabled, "duration selection does not enable power guard");
                AssertEqual(TimeSpan.FromMinutes(30), selected.SelectedDuration, "selection updates slider maximum");
                AssertNull(selected.EndAtUtc, "off state has no deadline");
                AssertEqual(0, factory.CreateCount, "off selection creates no native request");

                module.SelectDuration(PowerGuardDurationKind.Custom, TimeSpan.FromHours(30));
                AssertEqual(TimeSpan.FromHours(30), module.GetState().SelectedDuration,
                    "custom duration accepts values beyond one day");

                module.SelectDuration(PowerGuardDurationKind.Custom, TimeSpan.FromDays(31));
                AssertEqual(TimeSpan.FromDays(30), module.GetState().SelectedDuration,
                    "custom duration is clamped to 30 days");

                string error;
                AssertTrue(module.TryActivate(PowerGuardDurationKind.OneHour, TimeSpan.Zero, out error),
                    "tray-style duration command activates power guard");
                AssertTrue(module.GetState().IsEnabled, "tray-style command turns the main state on");
                AssertEqual(clock.GetUtcNow() + TimeSpan.FromHours(1), module.GetState().EndAtUtc.Value,
                    "tray-style command starts its requested duration immediately");
                AssertEqual(1, factory.CreateCount, "tray-style command creates exactly one request");
            }
        }

        private static void PowerGuardRequestTransitionsAreAtomic()
        {
            var factory = new FakePowerRequestLeaseFactory();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(factory, clock, false, false))
            {
                string error;
                AssertTrue(module.TrySetEnabled(true, out error), "system request enables");
                AssertEqual(1, factory.ActiveCount, "one system lease is active");
                AssertFalse(module.GetState().KeepDisplayOn, "display starts off");
                AssertEqual(clock.GetUtcNow() + TimeSpan.FromHours(2), module.GetState().EndAtUtc,
                    "enabling starts the selected countdown");

                AssertTrue(module.TrySetKeepDisplayOn(true, out error), "display request enables");
                AssertEqual(1, factory.ActiveCount, "replacement leaves one active lease");
                AssertTrue(module.GetState().KeepDisplayOn, "confirmed display state is on");

                AssertTrue(module.TrySetKeepDisplayOn(false, out error), "display request disables");
                AssertEqual(1, factory.ActiveCount, "system-only replacement leaves one lease");
                AssertFalse(module.GetState().KeepDisplayOn, "confirmed display state is off");

                AssertTrue(module.TrySetEnabled(false, out error), "system request disables");
                AssertEqual(0, factory.ActiveCount, "turning off disposes the request");
                AssertFalse(module.GetState().KeepDisplayOn, "turning off clears the child state");
            }
        }

        private static void PowerGuardFailuresRetainConfirmedState()
        {
            var factory = new FakePowerRequestLeaseFactory { FailNext = true };
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(factory, clock, false, false))
            {
                string error;
                AssertFalse(module.TrySetEnabled(true, out error), "enable failure is reported");
                AssertFalse(module.GetState().IsEnabled, "failed enable does not show fake on");
                AssertTrue(module.GetState().LastError.Length > 0, "failed enable exposes an error");

                AssertTrue(module.TrySetEnabled(true, out error), "second enable succeeds");
                AssertEqual(1, factory.ActiveCount, "system request remains active");
                factory.FailNext = true;
                AssertFalse(module.TrySetKeepDisplayOn(true, out error), "display failure is reported");
                AssertTrue(module.GetState().IsEnabled, "display failure retains the system request");
                AssertFalse(module.GetState().KeepDisplayOn, "display failure retains confirmed off state");
                AssertEqual(1, factory.ActiveCount, "display failure does not discard the old lease");
            }
        }

        private static void PowerGuardCountdownUsesAbsoluteDeadline()
        {
            var factory = new FakePowerRequestLeaseFactory();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(factory, clock, false, false))
            {
                string error;
                AssertTrue(module.TrySetEnabled(true, out error), "timed request enables");
                DateTimeOffset originalEnd = module.GetState().EndAtUtc.Value;
                clock.Advance(TimeSpan.FromMinutes(67));
                TimeSpan remaining = module.GetState().Remaining.Value;
                AssertEqual(TimeSpan.FromMinutes(53), remaining, "remaining derives from the deadline");
                AssertEqual(originalEnd, module.GetState().EndAtUtc.Value, "ticks do not mutate the deadline");

                module.AdjustRemaining(TimeSpan.FromMinutes(90));
                AssertEqual(clock.GetUtcNow() + TimeSpan.FromMinutes(90), module.GetState().EndAtUtc.Value,
                    "drag adjustment establishes a new deadline");

                module.SelectDuration(PowerGuardDurationKind.ThirtyMinutes, TimeSpan.Zero);
                AssertEqual(clock.GetUtcNow() + TimeSpan.FromMinutes(30), module.GetState().EndAtUtc.Value,
                    "changing an active duration restarts from now");

                module.SelectDuration(PowerGuardDurationKind.UntilManual, TimeSpan.Zero);
                PowerGuardState manual = module.GetState();
                AssertTrue(manual.IsEnabled, "selecting manual mode keeps power guard on");
                AssertEqual(PowerGuardDurationKind.UntilManual, manual.DurationKind,
                    "selecting manual mode uses until manual");
                AssertNull(manual.EndAtUtc, "manual mode has no deadline");
                AssertEqual(1, factory.ActiveCount, "selecting manual mode retains the request");
            }
        }

        private static void PowerGuardExpiryAndZeroClearRequests()
        {
            var factory = new FakePowerRequestLeaseFactory();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(factory, clock, false, false))
            {
                module.SelectDuration(PowerGuardDurationKind.ThirtyMinutes, TimeSpan.Zero);
                string error;
                AssertTrue(module.TrySetEnabled(true, out error), "timed request enables");
                AssertTrue(module.TrySetKeepDisplayOn(true, out error), "display request enables");
                clock.Advance(TimeSpan.FromMinutes(31));
                module.UpdateFromClock();
                PowerGuardState expired = module.GetState();
                AssertFalse(expired.IsEnabled, "expiry turns the main state off");
                AssertFalse(expired.KeepDisplayOn, "expiry clears the display state");
                AssertEqual(0, factory.ActiveCount, "expiry clears the native lease");
                AssertTrue(expired.StatusText.Contains("已交回 Windows"), "expiry text does not claim forced sleep");

                AssertTrue(module.TrySetEnabled(true, out error), "request can restart");
                module.AdjustRemaining(TimeSpan.Zero);
                AssertFalse(module.GetState().IsEnabled, "dragging to zero turns power guard off");
                AssertEqual(0, factory.ActiveCount, "zero adjustment clears the native lease");
            }
        }

        private static void PowerGuardResumeReappliesOrExpires()
        {
            var factory = new FakePowerRequestLeaseFactory();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(factory, clock, false, false))
            {
                string error;
                AssertTrue(module.TrySetEnabled(true, out error), "request enables");
                AssertTrue(module.TrySetKeepDisplayOn(true, out error), "display enables");
                DateTimeOffset end = module.GetState().EndAtUtc.Value;
                clock.Advance(TimeSpan.FromMinutes(10));
                int createsBeforeResume = factory.CreateCount;
                module.HandleResume();
                AssertEqual(createsBeforeResume + 1, factory.CreateCount, "resume creates a fresh request");
                AssertEqual(1, factory.ActiveCount, "resume swaps rather than duplicates the lease");
                AssertTrue(module.GetState().KeepDisplayOn, "resume restores the display request");
                AssertEqual(end, module.GetState().EndAtUtc.Value, "resume preserves the absolute deadline");

                factory.FailNext = true;
                module.HandleResume();
                AssertFalse(module.GetState().IsEnabled, "failed resume reapply returns to off");
                AssertEqual(0, factory.ActiveCount, "failed resume reapply disposes the stale lease");
            }

            var expiryFactory = new FakePowerRequestLeaseFactory();
            var expiryClock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            using (var module = new PowerGuardModule(expiryFactory, expiryClock, false, false))
            {
                module.SelectDuration(PowerGuardDurationKind.ThirtyMinutes, TimeSpan.Zero);
                string error;
                module.TrySetEnabled(true, out error);
                expiryClock.Advance(TimeSpan.FromHours(1));
                int createsBeforeResume = expiryFactory.CreateCount;
                module.HandleResume();
                AssertFalse(module.GetState().IsEnabled, "expired request is not recreated on resume");
                AssertEqual(createsBeforeResume, expiryFactory.CreateCount, "expired resume makes no native request");
            }
        }

        private static void PowerGuardRapidTogglesDoNotLeak()
        {
            var factory = new FakePowerRequestLeaseFactory();
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            var module = new PowerGuardModule(factory, clock, false, false);
            string error;
            for (int i = 0; i < 20; i++)
            {
                AssertTrue(module.TrySetEnabled(true, out error), "rapid enable succeeds");
                AssertTrue(module.TrySetKeepDisplayOn((i % 2) == 0, out error), "rapid display toggle succeeds");
                AssertTrue(factory.ActiveCount <= 1, "at most one confirmed lease remains after each transition");
                AssertTrue(module.TrySetEnabled(false, out error), "rapid disable succeeds");
                AssertEqual(0, factory.ActiveCount, "rapid disable releases the lease");
            }
            module.Dispose();
            module.Dispose();
            AssertEqual(0, factory.ActiveCount, "repeated disposal leaks no lease");
        }

        private static void PowerRequestInteropLayoutMatchesAbi()
        {
            int expected = IntPtr.Size == 8 ? 32 : 24;
            AssertEqual(expected, Marshal.SizeOf(typeof(WindowsPowerRequestLeaseFactory.ReasonContext)),
                "REASON_CONTEXT size matches the native ABI");
            AssertEqual((IntPtr)8,
                Marshal.OffsetOf(typeof(WindowsPowerRequestLeaseFactory.ReasonContext), "Reason"),
                "REASON_CONTEXT union starts at offset 8");
        }

        private static List<DisplayGuardMonitorInfo> CreateMonitors()
        {
            return new List<DisplayGuardMonitorInfo>
            {
                new DisplayGuardMonitorInfo
                {
                    RuntimeId = "one",
                    StableId = "DISPLAY\\AAA",
                    StableIdReliable = true,
                    DisplayName = "Main",
                    SupportsBrightness = true,
                    BrightnessPercent = 50,
                    SupportsContrast = true,
                    ContrastPercent = 70
                },
                new DisplayGuardMonitorInfo
                {
                    RuntimeId = "two",
                    StableId = "DISPLAY\\BBB",
                    StableIdReliable = true,
                    DisplayName = "Secondary",
                    SupportsBrightness = true,
                    BrightnessPercent = 20,
                    SupportsContrast = false
                },
                new DisplayGuardMonitorInfo
                {
                    RuntimeId = "three",
                    StableId = "DISPLAY\\UNMATCHED",
                    StableIdReliable = false,
                    DisplayName = "Unmatched",
                    SupportsBrightness = true,
                    BrightnessPercent = 80,
                    SupportsContrast = true,
                    ContrastPercent = 80
                }
            };
        }

        private static DisplayGuardMonitorProfile FindProfile(DisplayGuardProfileStore store, string modeKey, string stableId)
        {
            DisplayGuardMonitorProfile profile = FindProfileOrNull(store, modeKey, stableId);
            AssertNotNull(profile, "profile " + modeKey + "/" + stableId + " exists");
            return profile;
        }

        private static DisplayGuardMonitorProfile FindProfileOrNull(DisplayGuardProfileStore store, string modeKey,
            string stableId)
        {
            DisplayGuardModeProfile mode = DisplayGuardProfileCodec.FindMode(store, modeKey);
            if (mode == null)
            {
                return null;
            }

            for (int i = 0; i < mode.Monitors.Count; i++)
            {
                if (string.Equals(mode.Monitors[i].StableId, stableId, StringComparison.OrdinalIgnoreCase))
                {
                    return mode.Monitors[i];
                }
            }

            return null;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("[FAIL] " + name + ": " + ex.Message);
            }
        }

        private static void PointerPrecisionGuardSettingPersists()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "GuardCenter-pointer-precision-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var settings = new AppSettings();
                settings.GameHelper.KeepEnhancedPointerPrecisionOff = true;
                var store = new SettingsStore(path);
                store.Save(settings);
                AppSettings loaded = store.Load();
                AssertTrue(loaded.GameHelper.KeepEnhancedPointerPrecisionOff,
                    "the global pointer acceleration guard setting reloads as enabled");
            }
            finally
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(path + ".bak"); } catch { }
            }
        }

        private static void PointerPrecisionGuardUsesThirtySecondInterval()
        {
            AssertEqual(TimeSpan.FromSeconds(30), PointerPrecisionGuard.CheckInterval,
                "production polling interval is exactly thirty seconds");
        }

        private static void PointerPrecisionGuardDisablesImmediatelyAndOnPolling()
        {
            var service = new FakePointerPrecisionService { Enhanced = true };
            using (var guard = new PointerPrecisionGuard(service, delegate { },
                TimeSpan.FromMilliseconds(40)))
            {
                guard.SetEnabled(true);
                AssertTrue(guard.IsMonitoring, "enabling starts pointer precision monitoring");
                AssertEqual(1, service.DisableCalls,
                    "enabling immediately disables Enhance pointer precision");
                AssertFalse(service.Enhanced, "the immediate correction leaves acceleration off");

                service.Enhanced = true;
                AssertTrue(SpinWait.SpinUntil(delegate { return service.DisableCalls >= 2; }, 2000),
                    "the polling check disables acceleration when another app turns it back on");
                AssertFalse(service.Enhanced, "the polling correction leaves acceleration off");

                guard.SetEnabled(false);
                AssertFalse(guard.IsMonitoring, "disabling stops pointer precision monitoring");
                Thread.Sleep(80);
                int callsAfterDisable = service.DisableCalls;
                service.Enhanced = true;
                Thread.Sleep(160);
                AssertEqual(callsAfterDisable, service.DisableCalls,
                    "no further correction runs after the guard is disabled");
                AssertTrue(service.Enhanced,
                    "disabling the guard does not silently change the setting later");
            }
        }

        private static void PointerPrecisionServiceReadsLiveSetting()
        {
            var service = new WindowsPointerPrecisionService();
            bool enabled;
            string error;
            AssertTrue(service.TryGetEnhancedPointerPrecision(out enabled, out error),
                "Windows pointer precision state is readable: " + error);
        }

        private sealed class FakePointerPrecisionService : IPointerPrecisionService
        {
            public volatile bool Enhanced;
            private int disableCalls;

            public int DisableCalls
            {
                get { return Interlocked.CompareExchange(ref disableCalls, 0, 0); }
            }

            public bool TryGetEnhancedPointerPrecision(out bool enabled, out string error)
            {
                enabled = Enhanced;
                error = string.Empty;
                return true;
            }

            public bool TryDisableEnhancedPointerPrecision(out string error)
            {
                Interlocked.Increment(ref disableCalls);
                Enhanced = false;
                error = string.Empty;
                return true;
            }
        }

        private static void LinkGuardRulesRoundTrip()
        {
            var source = new LinkGuardRule
            {
                TriggerApp = new LinkGuardApp
                {
                    Name = "Apex",
                    TargetPath = @"C:\Games\Apex\r5apex.exe",
                    Arguments = "-novid",
                    WorkingDirectory = @"C:\Games\Apex",
                    Publisher = "Respawn",
                    Source = "Start Menu",
                    IconPath = @"C:\Games\Apex\r5apex.exe"
                },
                LinkedApp = new LinkGuardApp
                {
                    Name = "Huajuan",
                    TargetPath = @"C:\Tools\Huajuan.exe",
                    Arguments = "--profile apex",
                    WorkingDirectory = @"C:\Tools",
                    Publisher = "Local",
                    Source = "Executable",
                    IconPath = @"C:\Tools\Huajuan.exe"
                },
                Mode = LinkGuardMode.Bidirectional,
                Enabled = false,
                UseGsudo = true,
                KeepLinkedAppRunning = false,
                MaintainLinkedAppRunning = false,
                LaunchDelaySeconds = 5
            };

            string encoded = LinkGuardModule.EncodeRules(new[] { source });
            List<LinkGuardRule> decoded = LinkGuardModule.DecodeRules(encoded);
            AssertEqual(1, decoded.Count, "one rule survives serialization");
            AssertEqual(LinkGuardMode.Bidirectional, decoded[0].Mode, "direction survives");
            AssertFalse(decoded[0].Enabled, "enabled option survives");
            AssertTrue(decoded[0].UseGsudo, "gsudo option survives");
            AssertFalse(decoded[0].KeepLinkedAppRunning, "exit option survives");
            AssertFalse(decoded[0].MaintainLinkedAppRunning, "keep-running option survives");
            AssertEqual(5, decoded[0].LaunchDelaySeconds, "delay survives");
            AssertEqual("Apex", decoded[0].TriggerApp.Name, "trigger name survives");
            AssertEqual("-novid", decoded[0].TriggerApp.Arguments, "trigger arguments survive");
            AssertEqual("Huajuan", decoded[0].LinkedApp.Name, "linked name survives");
            AssertEqual("--profile apex", decoded[0].LinkedApp.Arguments, "linked arguments survive");
            AssertTrue(decoded[0].Id.Length > 0, "stable rule id is rebuilt");

            string currentText = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            string[] currentFields = currentText.Split('\t');
            string legacyEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                string.Join("\t", currentFields, 0, 19)));
            List<LinkGuardRule> legacyDecoded = LinkGuardModule.DecodeRules(legacyEncoded);
            AssertEqual(1, legacyDecoded.Count, "legacy rule format still loads");
            AssertTrue(legacyDecoded[0].MaintainLinkedAppRunning,
                "legacy rules preserve the previous keep-running behavior");
        }

        private static void LinkGuardRejectsInvalidLinks()
        {
            var settings = new LinkGuardSettings();
            using (var module = new LinkGuardModule(settings))
            {
                var apex = new AppCatalogItem
                {
                    Id = "apex",
                    Name = "Apex",
                    TargetPath = @"C:\Games\Apex\r5apex.exe"
                };
                var huajuan = new AppCatalogItem
                {
                    Id = "huajuan",
                    Name = "Huajuan",
                    TargetPath = @"C:\Tools\Huajuan.exe"
                };

                AssertTrue(module.AddRule(apex, huajuan).Success, "first one-way link is accepted");
                AssertFalse(module.AddRule(apex, huajuan).Success, "duplicate link is rejected");
                AssertFalse(module.AddRule(huajuan, apex).Success, "reverse link is rejected");
                AssertFalse(module.AddRule(apex, apex).Success, "self link is rejected");

                List<LinkGuardRule> rules = module.GetRules();
                AssertEqual(1, rules.Count, "only the valid rule remains");
                AssertTrue(module.UpdateRule(rules[0].Id, LinkGuardMode.Bidirectional,
                    true, true, false, false, 10).Success, "rule options update");
                LinkGuardRule updated = module.GetRules()[0];
                AssertEqual(LinkGuardMode.Bidirectional, updated.Mode, "updated direction is retained");
                AssertTrue(updated.UseGsudo, "updated gsudo option is retained");
                AssertFalse(updated.KeepLinkedAppRunning, "updated exit option is retained");
                AssertFalse(updated.MaintainLinkedAppRunning,
                    "updated keep-running option is retained");
                AssertEqual(10, updated.LaunchDelaySeconds, "updated delay is retained");
                AssertTrue(!string.IsNullOrWhiteSpace(settings.Rules), "updated rules persist to settings");
            }
        }

        private static void LinkGuardStartsMissingLinkedProcess()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string pingPath = Path.Combine(system, "ping.exe");
            string choicePath = Path.Combine(system, "choice.exe");
            var existingChoiceIds = new HashSet<int>();
            Process[] existing = Process.GetProcessesByName("choice");
            for (int i = 0; i < existing.Length; i++)
            {
                existingChoiceIds.Add(existing[i].Id);
                existing[i].Dispose();
            }

            Process trigger = null;
            Process linked = null;
            var module = new LinkGuardModule(new LinkGuardSettings());
            try
            {
                LinkGuardActionResult added = module.AddRule(new AppCatalogItem
                {
                    Id = "link-test-trigger",
                    Name = "Link test trigger",
                    TargetPath = pingPath,
                    Arguments = "-t 127.0.0.1",
                    WorkingDirectory = system
                }, new AppCatalogItem
                {
                    Id = "link-test-target",
                    Name = "Link test target",
                    TargetPath = choicePath,
                    Arguments = "/T 30 /D Y /N",
                    WorkingDirectory = system
                });
                AssertTrue(added.Success, "live test rule is accepted");
                LinkGuardRule liveRule = module.GetRules()[0];
                AssertTrue(module.UpdateRule(liveRule.Id, LinkGuardMode.OneWay,
                    true, false, false, true, 0).Success,
                    "close-B-after-A-closes is enabled for the live test");
                module.Start();
                trigger = Process.Start(new ProcessStartInfo
                {
                    FileName = pingPath,
                    Arguments = "-t 127.0.0.1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                AssertNotNull(trigger, "trigger process starts");
                string detectedTriggerPath;
                int triggerPathError;
                AssertTrue(GameInputLanguageNative.TryGetProcessPath((uint)trigger.Id,
                    out detectedTriggerPath, out triggerPathError),
                    "trigger executable path is readable (error " + triggerPathError + ")");
                AssertTrue(string.Equals(AppIdentityService.NormalizeExecutablePath(pingPath),
                    AppIdentityService.NormalizeExecutablePath(detectedTriggerPath),
                    StringComparison.OrdinalIgnoreCase),
                    "trigger process matches the configured executable path");

                bool linkedStarted = SpinWait.SpinUntil(delegate
                {
                    Process[] candidates = Process.GetProcessesByName("choice");
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        Process candidate = candidates[i];
                        if (!existingChoiceIds.Contains(candidate.Id))
                        {
                            linked = candidate;
                            for (int j = i + 1; j < candidates.Length; j++) candidates[j].Dispose();
                            return true;
                        }
                        candidate.Dispose();
                    }
                    return false;
                }, 10000);
                AssertTrue(linkedStarted, "running A causes Link Guard to start B; status="
                    + module.GetRules()[0].RuntimeStatus);

                TryKillProcessTree(trigger);
                trigger = null;
                bool closePathEntered = SpinWait.SpinUntil(delegate
                {
                    string status = module.GetRules()[0].RuntimeStatus;
                    return status.IndexOf("Trigger app closed;", StringComparison.OrdinalIgnoreCase) >= 0;
                }, 10000);
                AssertTrue(closePathEntered,
                    "A closing moves rule-started B into the close path; status="
                    + module.GetRules()[0].RuntimeStatus);
            }
            finally
            {
                module.Dispose();
                TryKillProcessTree(trigger);
                TryKillProcessTree(linked);
            }
        }

        private static void LinkGuardLeavesClosedLinkedProcessStopped()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string pingPath = Path.Combine(system, "ping.exe");
            string choicePath = Path.Combine(system, "choice.exe");
            var existingChoiceIds = new HashSet<int>();
            Process[] existing = Process.GetProcessesByName("choice");
            for (int i = 0; i < existing.Length; i++)
            {
                existingChoiceIds.Add(existing[i].Id);
                existing[i].Dispose();
            }

            Process trigger = null;
            Process linked = null;
            var module = new LinkGuardModule(new LinkGuardSettings());
            try
            {
                AssertTrue(module.AddRule(new AppCatalogItem
                {
                    Id = "link-maintenance-trigger",
                    Name = "Link maintenance trigger",
                    TargetPath = pingPath,
                    Arguments = "-t 127.0.0.1",
                    WorkingDirectory = system
                }, new AppCatalogItem
                {
                    Id = "link-maintenance-target",
                    Name = "Link maintenance target",
                    TargetPath = choicePath,
                    Arguments = "/T 30 /D Y /N",
                    WorkingDirectory = system
                }).Success, "keep-running live test rule is accepted");
                LinkGuardRule rule = module.GetRules()[0];
                AssertTrue(module.UpdateRule(rule.Id, LinkGuardMode.OneWay,
                    true, false, true, false, 0).Success,
                    "keep-running is disabled for the live test");
                module.Start();
                trigger = Process.Start(new ProcessStartInfo
                {
                    FileName = pingPath,
                    Arguments = "-t 127.0.0.1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                AssertNotNull(trigger, "keep-running trigger starts");

                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    Process[] candidates = Process.GetProcessesByName("choice");
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        Process candidate = candidates[i];
                        if (!existingChoiceIds.Contains(candidate.Id))
                        {
                            linked = candidate;
                            for (int j = i + 1; j < candidates.Length; j++) candidates[j].Dispose();
                            return true;
                        }
                        candidate.Dispose();
                    }
                    return false;
                }, 10000), "A initially starts B even when keep-running is off");
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return module.GetRules()[0].RuntimeStatus.StartsWith("Linked",
                        StringComparison.Ordinal);
                }, 5000), "the active A/B pair is observed before B closes");

                int closedId = linked.Id;
                TryKillProcessTree(linked);
                linked = null;
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return module.GetRules()[0].RuntimeStatus.IndexOf("keep-running is off",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }, 5000), "Link Guard records that B was intentionally left stopped");
                Thread.Sleep(6500);

                Process[] after = Process.GetProcessesByName("choice");
                bool restarted = false;
                for (int i = 0; i < after.Length; i++)
                {
                    if (!existingChoiceIds.Contains(after[i].Id) && after[i].Id != closedId)
                    {
                        restarted = true;
                    }
                    after[i].Dispose();
                }
                AssertFalse(restarted, "B stays closed while A remains open");
            }
            finally
            {
                module.Dispose();
                TryKillProcessTree(trigger);
                TryKillProcessTree(linked);
                Process[] remaining = Process.GetProcessesByName("choice");
                for (int i = 0; i < remaining.Length; i++)
                {
                    if (!existingChoiceIds.Contains(remaining[i].Id))
                    {
                        TryKillProcessTree(remaining[i]);
                    }
                    else
                    {
                        remaining[i].Dispose();
                    }
                }
            }
        }

        private static void LinkGuardClosesAfterObservedTriggerExit()
        {
            AssertTrue(LinkGuardModule.ShouldCloseLinkedAppAfterTriggerExit(true, true, true),
                "B closes when A exits after the active pair was observed");
            AssertFalse(LinkGuardModule.ShouldCloseLinkedAppAfterTriggerExit(false, true, true),
                "B is not closed without an A running-to-closed transition");
            AssertFalse(LinkGuardModule.ShouldCloseLinkedAppAfterTriggerExit(true, false, true),
                "no close is requested when B is already stopped");
            AssertFalse(LinkGuardModule.ShouldCloseLinkedAppAfterTriggerExit(true, true, false),
                "B keeps running when Close B after A closes is switched off");
        }

        private static void LinkGuardDetectsClosedWindowWithBackgroundProcess()
        {
            AssertTrue(LinkGuardModule.IsAppConsideredRunning(true, true, false),
                "a newly observed GUI process is running");
            AssertTrue(LinkGuardModule.IsAppConsideredRunning(true, true, true),
                "a previously observed GUI process remains running while its window exists");
            AssertFalse(LinkGuardModule.IsAppConsideredRunning(true, false, true),
                "a Chrome-style background process is not treated as A after its last window closes");
            AssertTrue(LinkGuardModule.IsAppConsideredRunning(true, false, false),
                "a process that never exposes a window remains supported as a headless app");
            AssertFalse(LinkGuardModule.IsAppConsideredRunning(false, false, false),
                "an exited process is stopped");
        }

        private static void LinkGuardClosesRuleStartedWindowProcess()
        {
            string testRoot = Path.Combine(Path.GetTempPath(), "GuardCenter.LinkGuard.Close."
                + Guid.NewGuid().ToString("N"));
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string pingPath = Path.Combine(system, "ping.exe");
            Process trigger = null;
            Process linked = null;
            LinkGuardModule module = null;
            try
            {
                string linkedPath = CopyTestRuntime(testRoot);
                string linkedLog = Path.Combine(testRoot, "linked-window.log");
                module = new LinkGuardModule(new LinkGuardSettings());
                AssertTrue(module.AddRule(new AppCatalogItem
                {
                    Id = "link-window-trigger",
                    Name = "Link window trigger",
                    TargetPath = pingPath,
                    Arguments = "-t 127.0.0.1",
                    WorkingDirectory = system
                }, new AppCatalogItem
                {
                    Id = "link-window-target",
                    Name = "Link window target",
                    TargetPath = linkedPath,
                    Arguments = "game-helper-window --title LinkGuardCloseProbe --log \""
                        + linkedLog + "\"",
                    WorkingDirectory = testRoot
                }).Success, "window-close live rule is accepted");
                LinkGuardRule liveRule = module.GetRules()[0];
                AssertTrue(module.UpdateRule(liveRule.Id, LinkGuardMode.OneWay,
                    true, false, false, true, 0).Success,
                    "close-B-after-A-closes is enabled for the window-close test");
                module.Start();
                trigger = Process.Start(new ProcessStartInfo
                {
                    FileName = pingPath,
                    Arguments = "-t 127.0.0.1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                AssertNotNull(trigger, "window-close trigger starts");
                WaitForProbe(linkedLog, 10000);
                Process[] candidates = Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(linkedPath));
                for (int i = 0; i < candidates.Length; i++)
                {
                    string actualPath;
                    int error;
                    if (GameInputLanguageNative.TryGetProcessPath((uint)candidates[i].Id,
                        out actualPath, out error)
                        && string.Equals(AppIdentityService.NormalizeExecutablePath(linkedPath),
                            AppIdentityService.NormalizeExecutablePath(actualPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        linked = candidates[i];
                        for (int j = i + 1; j < candidates.Length; j++) candidates[j].Dispose();
                        break;
                    }
                    candidates[i].Dispose();
                }
                AssertNotNull(linked, "rule-started linked window process is identifiable");

                TryKillProcessTree(trigger);
                trigger = null;
                AssertTrue(linked.WaitForExit(10000),
                    "A closing sends WM_CLOSE and the rule-started B exits; status="
                    + module.GetRules()[0].RuntimeStatus);
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return module.GetRules()[0].RuntimeStatus.IndexOf("Waiting for",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }, 5000), "Link Guard confirms B closed instead of clearing ownership early");
            }
            finally
            {
                if (module != null) module.Dispose();
                TryKillProcessTree(trigger);
                TryKillProcessTree(linked);
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static int RunLinkGuardChromeWmpLiveTest()
        {
            string chromePath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe");
            string wmpPath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86), "Windows Media Player", "wmplayer.exe");
            string profileRoot = Path.Combine(Path.GetTempPath(), "GuardCenter.LinkGuard.Chrome."
                + Guid.NewGuid().ToString("N"));
            Process chrome = null;
            Process wmp = null;
            LinkGuardModule module = null;
            try
            {
                AssertTrue(File.Exists(chromePath), "installed Google Chrome executable exists");
                AssertTrue(File.Exists(wmpPath), "installed Windows Media Player Legacy executable exists");
                AssertEqual(0, CountProcessesAtPath(chromePath),
                    "live test starts without an existing Chrome session");
                AssertEqual(0, CountProcessesAtPath(wmpPath),
                    "live test starts without an existing WMP Legacy session");

                wmp = Process.Start(new ProcessStartInfo
                {
                    FileName = wmpPath,
                    Arguments = "/Task MediaLibrary",
                    WorkingDirectory = Path.GetDirectoryName(wmpPath),
                    UseShellExecute = true
                });
                AssertNotNull(wmp, "WMP Legacy starts before the Link Guard monitor");
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return CountProcessesAtPath(wmpPath) > 0
                        && LinkGuardModule.HasTopLevelWindow(wmpPath);
                }, 10000), "WMP Legacy remains open with a top-level library window");

                module = new LinkGuardModule(new LinkGuardSettings());
                AssertTrue(module.AddRule(new AppCatalogItem
                {
                    Id = "installed-chrome",
                    Name = "Google Chrome",
                    TargetPath = chromePath,
                    WorkingDirectory = Path.GetDirectoryName(chromePath)
                }, new AppCatalogItem
                {
                    Id = "installed-wmp",
                    Name = "Windows Media Player Legacy",
                    TargetPath = wmpPath,
                    Arguments = "/Task MediaLibrary",
                    WorkingDirectory = Path.GetDirectoryName(wmpPath)
                }).Success, "installed Chrome-to-WMP rule is accepted");
                LinkGuardRule rule = module.GetRules()[0];
                AssertTrue(module.UpdateRule(rule.Id, LinkGuardMode.OneWay,
                    true, false, false, true, 0).Success,
                    "Close B after A closes is enabled for installed apps");
                module.Start();

                Directory.CreateDirectory(profileRoot);
                chrome = Process.Start(new ProcessStartInfo
                {
                    FileName = chromePath,
                    Arguments = "--user-data-dir=\"" + profileRoot
                        + "\" --no-first-run --disable-default-apps --new-window about:blank",
                    WorkingDirectory = Path.GetDirectoryName(chromePath),
                    UseShellExecute = true
                });
                AssertNotNull(chrome, "Chrome test window starts");
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return CountProcessesAtPath(wmpPath) > 0
                        && module.GetRules()[0].RuntimeStatus.StartsWith("Linked",
                            StringComparison.Ordinal);
                }, 15000), "Link Guard observes Chrome with the pre-existing WMP Legacy; status="
                    + module.GetRules()[0].RuntimeStatus);
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return LinkGuardModule.HasTopLevelWindow(chromePath);
                }, 10000), "Chrome exposes a top-level test window before the close transition");
                Thread.Sleep(1250);

                AssertTrue(LinkGuardModule.RequestClose(chromePath) > 0,
                    "Chrome test window receives a normal close request");
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return CountProcessesAtPath(wmpPath) == 0;
                }, 15000), "WMP Legacy exits after Chrome closes; status="
                    + module.GetRules()[0].RuntimeStatus);
                Console.WriteLine("[PASS] installed Google Chrome -> Windows Media Player Legacy"
                    + " closes a pre-existing B after A closes");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] installed Chrome/WMP Link Guard live test: " + ex);
                return 1;
            }
            finally
            {
                if (module != null) module.Dispose();
                LinkGuardModule.RequestClose(chromePath);
                LinkGuardModule.RequestClose(wmpPath);
                if (chrome != null)
                {
                    try { if (!chrome.HasExited) chrome.Kill(true); } catch { }
                    chrome.Dispose();
                }
                if (wmp != null)
                {
                    try { if (!wmp.HasExited) wmp.Kill(true); } catch { }
                    wmp.Dispose();
                }
                SpinWait.SpinUntil(delegate { return CountProcessesAtPath(chromePath) == 0; }, 5000);
                try { Directory.Delete(profileRoot, true); } catch { }
            }
        }

        private static int CountProcessesAtPath(string executablePath)
        {
            int count = 0;
            Process[] processes = Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(executablePath));
            for (int i = 0; i < processes.Length; i++)
            {
                using (Process process = processes[i])
                {
                    string actualPath;
                    int error;
                    if (GameInputLanguageNative.TryGetProcessPath((uint)process.Id,
                        out actualPath, out error)
                        && string.Equals(AppIdentityService.NormalizeExecutablePath(executablePath),
                            AppIdentityService.NormalizeExecutablePath(actualPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static void LinkGuardClosesAlreadyRunningLinkedProcess()
        {
            string testRoot = Path.Combine(Path.GetTempPath(), "GuardCenter.LinkGuard.Restart."
                + Guid.NewGuid().ToString("N"));
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string pingPath = Path.Combine(system, "ping.exe");
            Process trigger = null;
            Process linked = null;
            LinkGuardModule module = null;
            try
            {
                string linkedPath = CopyTestRuntime(testRoot);
                string linkedLog = Path.Combine(testRoot, "already-running-linked.log");
                linked = StartGameHelperProbe(linkedPath, linkedLog, "Already running Link Guard B");
                WaitForProbe(linkedLog, 5000);
                AssertNotNull(linked, "B starts before the Link Guard monitor is constructed");

                module = new LinkGuardModule(new LinkGuardSettings());
                AssertTrue(module.AddRule(new AppCatalogItem
                {
                    Id = "link-restart-trigger",
                    Name = "Link restart trigger",
                    TargetPath = pingPath,
                    Arguments = "-t 127.0.0.1",
                    WorkingDirectory = system
                }, new AppCatalogItem
                {
                    Id = "link-already-running-target",
                    Name = "Link already-running target",
                    TargetPath = linkedPath,
                    Arguments = "game-helper-window --title AlreadyRunningLinkGuardB --log \""
                        + linkedLog + "\"",
                    WorkingDirectory = testRoot
                }).Success, "monitor-restart test rule is accepted");
                LinkGuardRule liveRule = module.GetRules()[0];
                AssertTrue(module.UpdateRule(liveRule.Id, LinkGuardMode.OneWay,
                    true, false, false, true, 0).Success,
                    "close-B-after-A-closes is enabled after the monitor restart");
                module.Start();
                trigger = Process.Start(new ProcessStartInfo
                {
                    FileName = pingPath,
                    Arguments = "-t 127.0.0.1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                AssertNotNull(trigger, "A starts after the already-running B");
                AssertTrue(SpinWait.SpinUntil(delegate
                {
                    return module.GetRules()[0].RuntimeStatus.StartsWith("Linked",
                        StringComparison.Ordinal);
                }, 10000), "the restarted monitor observes the already-active A/B pair");

                TryKillProcessTree(trigger);
                trigger = null;
                AssertTrue(linked.WaitForExit(10000),
                    "A closing also closes B when ownership was lost before monitor startup; status="
                    + module.GetRules()[0].RuntimeStatus);
            }
            finally
            {
                if (module != null) module.Dispose();
                TryKillProcessTree(trigger);
                TryKillProcessTree(linked);
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static void TryKillProcessTree(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private static void AssertTrue(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertFalse(bool value, string message)
        {
            if (value)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertNull(object value, string message)
        {
            if (value != null)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertNotNull(object value, string message)
        {
            if (value == null)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + " expected " + expected + " but got " + actual);
            }
        }

        private static void AssertNear(double expected, double actual, double tolerance,
            string message)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(message + " expected " + expected
                    + " but got " + actual);
            }
        }

        private delegate IntPtr NativeWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeWindowClass
        {
            public uint cbSize;
            public uint style;
            public NativeWindowProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pointX;
            public int pointY;
            public uint privateValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TestNativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(string section, string key,
            string value, string filePath);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref NativeWindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out TestNativePoint point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadKeyboardLayout(string keyboardLayoutId, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr keyboardLayout, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out NativeMessage message, IntPtr hwnd,
            uint minimumMessage, uint maximumMessage);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags,
            UIntPtr extraInfo);

    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public void Advance(TimeSpan value)
        {
            utcNow += value;
        }
    }

    internal sealed class FakePowerRequestLeaseFactory : IPowerRequestLeaseFactory
    {
        public int CreateCount;
        public int ActiveCount;
        public bool FailNext;
        public bool LastKeepDisplayOn;

        public bool TryCreate(bool keepDisplayOn, out IPowerRequestLease lease, out string error)
        {
            CreateCount++;
            LastKeepDisplayOn = keepDisplayOn;
            if (FailNext)
            {
                FailNext = false;
                lease = null;
                error = "simulated power request failure";
                return false;
            }

            ActiveCount++;
            lease = new FakePowerRequestLease(this, keepDisplayOn);
            error = string.Empty;
            return true;
        }

        private sealed class FakePowerRequestLease : IPowerRequestLease
        {
            private readonly FakePowerRequestLeaseFactory owner;
            private bool disposed;

            public FakePowerRequestLease(FakePowerRequestLeaseFactory owner, bool keepDisplayOn)
            {
                this.owner = owner;
                KeepDisplayOn = keepDisplayOn;
            }

            public bool KeepDisplayOn { get; private set; }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                owner.ActiveCount--;
            }
        }
    }

    internal sealed class BlockingDisplayGuardProvider : IDisplayGuardProvider, IDisposable
    {
        public readonly ManualResetEventSlim Started = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
        public bool LastWriteDiagnosticLog;
        private int activeCalls;

        public int ActiveCalls
        {
            get { return Volatile.Read(ref activeCalls); }
        }

        public List<DisplayGuardDevice> EnumerateMonitors(bool writeDiagnosticLog = true)
        {
            Interlocked.Increment(ref activeCalls);
            try
            {
                LastWriteDiagnosticLog = writeDiagnosticLog;
                Started.Set();
                Release.Wait();
                return new List<DisplayGuardDevice>();
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }

        public void Dispose()
        {
            Started.Dispose();
            Release.Dispose();
        }
    }

    internal sealed class FakeProvider : IDisplayGuardProvider
    {
        private readonly DisplayGuardDevice[] devices;

        public FakeProvider(params DisplayGuardDevice[] devices)
        {
            this.devices = devices;
        }

        public List<DisplayGuardDevice> EnumerateMonitors(bool writeDiagnosticLog = true)
        {
            return new List<DisplayGuardDevice>(devices);
        }
    }

    internal sealed class FakeStickyKeysSettingsApi : IStickyKeysSettingsApi
    {
        public uint Flags;
        public int WriteCount;
        public bool ReadFails;
        public bool WriteFails;

        public FakeStickyKeysSettingsApi(uint flags)
        {
            Flags = flags;
        }

        public bool TryRead(out uint flags, out string error)
        {
            flags = Flags;
            error = ReadFails ? "read failed" : string.Empty;
            return !ReadFails;
        }

        public bool TryWrite(uint flags, out string error)
        {
            WriteCount++;
            if (WriteFails)
            {
                error = "write failed";
                return false;
            }
            Flags = flags;
            error = string.Empty;
            return true;
        }
    }

    internal sealed class FakeInputMethodHotKeyApi : IInputMethodHotKeyApi
    {
        private readonly Dictionary<uint, InputMethodHotKeyState> states =
            new Dictionary<uint, InputMethodHotKeyState>();

        public FakeInputMethodHotKeyApi(InputMethodHotKeyState hotKey11,
            InputMethodHotKeyState hotKey71)
        {
            states[KeyboardGuardModule.SimplifiedChineseShapeToggleHotKey] = hotKey11;
            states[KeyboardGuardModule.TraditionalChineseShapeToggleHotKey] = hotKey71;
        }

        public InputMethodHotKeyState Read(uint hotKeyId)
        {
            InputMethodHotKeyState state;
            return states.TryGetValue(hotKeyId, out state)
                ? state
                : InputMethodHotKeyState.Disabled;
        }

        public bool TryWrite(uint hotKeyId, InputMethodHotKeyState state, out string error)
        {
            states[hotKeyId] = state;
            error = string.Empty;
            return true;
        }
    }

    internal sealed class FakeMicrosoftImeCharacterWidthApi : IMicrosoftImeCharacterWidthApi
    {
        public FakeMicrosoftImeCharacterWidthApi(RegistryTextValueState state)
        {
            State = state;
        }

        public RegistryTextValueState State;
        public int RemainingWriteFailures;
        public bool IsAvailable { get { return true; } }

        public bool TryRead(out RegistryTextValueState state, out string error)
        {
            state = State;
            error = string.Empty;
            return true;
        }

        public bool TryWrite(RegistryTextValueState state, out string error)
        {
            if (RemainingWriteFailures > 0)
            {
                RemainingWriteFailures--;
                error = "simulated Microsoft IME write failure";
                return false;
            }
            State = state;
            error = string.Empty;
            return true;
        }
    }

    internal sealed class FakeAsusImeCharacterWidthApi : IAsusImeCharacterWidthApi
    {
        public FakeAsusImeCharacterWidthApi(bool fullWidth)
        {
            FullWidth = fullWidth;
        }

        public bool FullWidth;
        public int RemainingWriteFailures;
        public bool IsAvailable { get { return true; } }
        public event EventHandler Changed;

        public bool TryRead(out bool fullWidth, out string error)
        {
            fullWidth = FullWidth;
            error = string.Empty;
            return true;
        }

        public bool TryWrite(bool fullWidth, out string error)
        {
            if (RemainingWriteFailures > 0)
            {
                RemainingWriteFailures--;
                error = "simulated ASUS IME write failure";
                return false;
            }
            FullWidth = fullWidth;
            error = string.Empty;
            return true;
        }

        public void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }

    internal sealed class FakeDeviceInventory : IDeviceGuardInventory
    {
        public List<DeviceGuardDevice> Scan()
        {
            return new List<DeviceGuardDevice>
            {
                new DeviceGuardDevice
                {
                    RuntimeId = "test-device",
                    InstanceId = "USB\\TEST",
                    DisplayName = "Test device",
                    Kind = DeviceGuardKind.Camera,
                    CanRepair = true,
                    IncludeInRepairAll = true,
                    IsPresent = true
                }
            };
        }

        public DeviceGuardDevice Resolve(DeviceRepairTarget target)
        {
            return Scan()[0];
        }

        public bool TryReadDevNodeStatus(string instanceId, out uint status, out uint problemCode, out string error)
        {
            status = 0;
            problemCode = 0;
            error = string.Empty;
            return true;
        }
    }

    internal sealed class FakeUpperFilterStore : IUpperFilterStore
    {
        public string[] Keyboard;
        public string[] Mouse;
        public int WriteFailuresRemaining;

        public FakeUpperFilterStore(string[] keyboard, string[] mouse)
        {
            Keyboard = (string[])keyboard.Clone();
            Mouse = (string[])mouse.Clone();
        }

        public string[] ReadKeyboard()
        {
            return (string[])Keyboard.Clone();
        }

        public string[] ReadMouse()
        {
            return (string[])Mouse.Clone();
        }

        public void WriteKeyboard(string[] values)
        {
            if (WriteFailuresRemaining > 0)
            {
                WriteFailuresRemaining--;
                throw new IOException("simulated registry failure");
            }
            Keyboard = values == null ? Array.Empty<string>() : (string[])values.Clone();
        }
    }

    internal sealed class FakeHuaJuanPostRebootStore : IHuaJuanPostRebootStore
    {
        public readonly Dictionary<string, string> Values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public FakeHuaJuanPostRebootStore(string name, string command)
        {
            if (!string.IsNullOrWhiteSpace(name)) Values[name] = command;
        }

        public Dictionary<string, string> ReadConflicting()
        {
            return new Dictionary<string, string>(Values,
                StringComparer.OrdinalIgnoreCase);
        }

        public void RemoveConflicting()
        {
            Values.Clear();
        }

        public void Restore(Dictionary<string, string> values)
        {
            Values.Clear();
            if (values == null) return;
            foreach (KeyValuePair<string, string> item in values)
                Values[item.Key] = item.Value;
        }
    }

    internal sealed class CountingPnPInventory : IWindowsPnPInventory
    {
        private readonly WindowsPnPSnapshot snapshot;
        public int CaptureCount;

        public CountingPnPInventory(WindowsPnPSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public WindowsPnPSnapshot Capture()
        {
            CaptureCount++;
            return snapshot;
        }
    }

    internal sealed class FakeEndpoint : IDisplayGuardControlEndpoint
    {
        private readonly string kind;
        private readonly int brightnessMinimum;
        private readonly int brightnessMaximum;
        private readonly int contrastMinimum;
        private readonly int contrastMaximum;
        private int brightnessCurrent;
        private int contrastCurrent;

        public int LastBrightnessSet = -1;

        public FakeEndpoint(string kind, int brightnessMinimum, int brightnessCurrent, int brightnessMaximum,
            int contrastMinimum, int contrastCurrent, int contrastMaximum)
        {
            this.kind = kind;
            this.brightnessMinimum = brightnessMinimum;
            this.brightnessCurrent = brightnessCurrent;
            this.brightnessMaximum = brightnessMaximum;
            this.contrastMinimum = contrastMinimum;
            this.contrastCurrent = contrastCurrent;
            this.contrastMaximum = contrastMaximum;
        }

        public string Kind
        {
            get { return kind; }
        }

        public bool TryReadBrightness(out int minimum, out int current, out int maximum, out string error)
        {
            minimum = brightnessMinimum;
            current = brightnessCurrent;
            maximum = brightnessMaximum;
            error = string.Empty;
            return brightnessMaximum > brightnessMinimum;
        }

        public bool TrySetBrightness(int value, out string error)
        {
            LastBrightnessSet = value;
            brightnessCurrent = value;
            error = string.Empty;
            return true;
        }

        public bool TryReadContrast(out int minimum, out int current, out int maximum, out string error)
        {
            minimum = contrastMinimum;
            current = contrastCurrent;
            maximum = contrastMaximum;
            error = string.Empty;
            return contrastMaximum > contrastMinimum;
        }

        public bool TrySetContrast(int value, out string error)
        {
            contrastCurrent = value;
            error = string.Empty;
            return true;
        }

        public void Dispose()
        {
        }
    }

    internal sealed class NonApplyingEndpoint : IDisplayGuardControlEndpoint
    {
        public string Kind { get { return "Test"; } }
        public bool TryReadBrightness(out int minimum, out int current, out int maximum, out string error)
        {
            minimum = 0;
            current = 20;
            maximum = 100;
            error = string.Empty;
            return true;
        }
        public bool TrySetBrightness(int value, out string error) { error = string.Empty; return true; }
        public bool TryReadContrast(out int minimum, out int current, out int maximum, out string error)
        {
            minimum = current = maximum = 0;
            error = "Unsupported";
            return false;
        }
        public bool TrySetContrast(int value, out string error) { error = "Unsupported"; return false; }
        public void Dispose() { }
    }
}
