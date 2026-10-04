using System.Runtime.InteropServices;
using OmenCore.Linux.Config;
using OmenCore.Linux.Hardware;

namespace OmenCore.Linux.Daemon;

/// <summary>
/// OmenCore Linux Daemon - Background service for automatic hardware control.
/// 
/// Features:
/// - Automatic fan curve application
/// - Temperature monitoring
/// - Configuration file watching
/// - Signal handling (SIGTERM, SIGHUP)
/// - PID file management
/// - Graceful shutdown with settings restoration
/// - Low-overhead mode on battery (v2.7.0)
/// </summary>
public class OmenCoreDaemon : IDisposable
{
    private const string PidFilePath = "/var/run/omencore.pid";
    private const string LogFilePath = "/var/log/omencore.log";
    
    private readonly OmenCoreConfig _config;
    private readonly LinuxEcController _ec;
    private readonly LinuxHwMonController _hwmon;
    private readonly LinuxKeyboardController _keyboard;
    private readonly LinuxBatteryController _battery;
    private FanCurveEngine? _fanCurveEngine;
    private readonly CancellationTokenSource _cts = new();
    
    private bool _isRunning;
    private bool _lowOverheadMode;
    private FileSystemWatcher? _configWatcher;
    private readonly object _reloadLock = new();
    private bool _reloadPending;
    private DateTime _reloadScheduledUtc = DateTime.MinValue;
    private KeyboardIdleMonitor? _keyboardIdleMonitor;
    private KeyboardAnimationEngine? _keyboardAnimationEngine;
    
    // Thermal watchdog: tracks whether the CPU has been at throttle temp so we can
    // re-apply the configured performance mode once it cools down (some OMEN models
    // silently reset the thermal profile to Balanced when PROCHOT fires).
    private bool _thermalThrottleDetected;
    private DateTime _thermalThrottleSince = DateTime.MinValue;
    private DateTime _lastPerformanceHoldCheck = DateTime.MinValue;
    private int _performanceHoldTick;
    private bool _thermalPowerUnsupportedLogged;

    private FanProfile _configuredFanProfile = FanProfile.Auto;
    private DateTime _lastWatchdogKickUtc = DateTime.MinValue;
    private int _watchdogConsecutiveHits;
    private int _watchdogTrips;
    private bool? _lastOnBatteryState;
    private DateTime _lastAutomationUtc = DateTime.MinValue;
    private string _lastAutomationMode = string.Empty;
    private string _lastWatchdogReason = string.Empty;
    private string _lastReliabilityError = string.Empty;
    private DateTime _lastSnapshotWriteUtc = DateTime.MinValue;
    private FileStream? _singleWriterLockHandle;

    public OmenCoreDaemon(OmenCoreConfig config)
    {
        _config = config;
        _ec = new LinuxEcController();
        _hwmon = new LinuxHwMonController();
        _keyboard = new LinuxKeyboardController();
        _battery = new LinuxBatteryController();
        
        // Initialize fan curve engine if custom curve is enabled
        if (WantsCustomCurve())
        {
            _fanCurveEngine = CreateFanCurveEngine();
        }
    }

    private bool WantsCustomCurve() =>
        _config.Fan.Profile == "custom" && _config.Fan.Curve.Enabled;

    private FanCurveEngine CreateFanCurveEngine()
    {
        var engine = new FanCurveEngine(_ec, _hwmon, _config);
        engine.OnLog += Log;
        engine.OnSpeedChange += OnFanSpeedChange;
        return engine;
    }

    /// <summary>
    /// Start or stop the curve engine after a config reload, so switching the
    /// profile to/from "custom" at runtime takes effect without a daemon restart.
    /// </summary>
    private void SyncFanCurveEngine()
    {
        var wantsCurve = WantsCustomCurve();

        if (wantsCurve && _fanCurveEngine == null)
        {
            _fanCurveEngine = CreateFanCurveEngine();
            _ = _fanCurveEngine.StartAsync();
        }
        else if (!wantsCurve && _fanCurveEngine != null)
        {
            var engine = _fanCurveEngine;
            _fanCurveEngine = null;
            // The new profile decides what the fans do next; don't hand them to BIOS auto first.
            engine.Stop(restoreAuto: false);
            engine.Dispose();
            ApplyFanProfile();
        }
    }
    
    /// <summary>
    /// Run the daemon (blocking).
    /// </summary>
    public async Task RunAsync()
    {
        if (_isRunning)
        {
            Log("Daemon is already running");
            return;
        }
        
        // Check prerequisites before flagging the daemon as running, so an
        // early exit doesn't leave _isRunning stuck at true.
        if (!LinuxEcController.CheckRootAccess())
        {
            Log("Error: Root privileges required");
            return;
        }
        
        if (!_ec.IsAvailable)
        {
            Log("Error: EC not available. Load ec_sys with write_support=1");
            return;
        }

        ReliabilityDiagnosticsStore.EnsureDiagnosticsDirectory();
        if (!TryAcquireSingleWriterLock())
        {
            return;
        }

        _isRunning = true;
        
        // Create PID file
        WritePidFile();
        
        // Setup signal handlers
        SetupSignalHandlers();
        
        // Setup config file watcher
        SetupConfigWatcher();
        
        Log("═══════════════════════════════════════════════════════════");
        Log("          OmenCore Linux Daemon v2.8.0 Started            ");
        Log("═══════════════════════════════════════════════════════════");
        Log($"Config: {(_config.Fan.Profile == "custom" ? "Custom fan curve" : $"Profile: {_config.Fan.Profile}")}");
        Log($"Poll interval: {_config.General.PollIntervalMs}ms");
        Log($"Reliability mode: {(_config.Reliability.Enabled ? "enabled" : "disabled")}");
        if (!_config.Reliability.Enabled)
        {
            ReliabilityDiagnosticsStore.WriteSnapshot(new ReliabilityStatusSnapshot
            {
                Enabled = false,
                UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }
        
        // Apply startup configuration
        if (_config.Startup.ApplyOnBoot)
        {
            await ApplyStartupConfigAsync();
        }

        // Start the keyboard backlight idle-timeout (BIOS-style) if configured
        StartKeyboardIdleMonitor();
        
        // Start fan curve engine if enabled
        if (_fanCurveEngine != null)
        {
            await _fanCurveEngine.StartAsync();
        }
        
        // Main daemon loop
        try
        {
            await RunMainLoopAsync();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            await ShutdownAsync();
        }
    }
    
    /// <summary>
    /// Signal the daemon to stop.
    /// </summary>
    public void Stop()
    {
        Log("Shutdown signal received");
        _cts.Cancel();
    }
    
    /// <summary>
    /// Get effective poll interval based on low-overhead mode.
    /// </summary>
    private int GetEffectivePollInterval()
    {
        return _lowOverheadMode 
            ? _config.General.LowOverhead.PollIntervalMs 
            : _config.General.PollIntervalMs;
    }
    
    /// <summary>
    /// Check and update low-overhead mode based on battery state.
    /// </summary>
    private void UpdateLowOverheadMode()
    {
        if (!_config.General.LowOverhead.EnableOnBattery)
            return;
            
        var onBattery = _battery.IsOnBattery();
        
        if (onBattery != _lowOverheadMode)
        {
            _lowOverheadMode = onBattery;
            
            if (!_config.General.LowOverhead.ReduceLogging)
            {
                Log(_lowOverheadMode 
                    ? "Switched to low-overhead mode (on battery)" 
                    : "Switched to normal mode (on AC)");
            }
        }
    }
    
    private async Task RunMainLoopAsync()
    {
        var logCounter = 0;
        
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Check battery state for low-overhead mode
                UpdateLowOverheadMode();

                // These safety/recovery policies must run regardless of whether
                // the custom fan-curve engine is driving the fans. Previously the
                // whole block was skipped in curve mode, disabling the thermal
                // restore, performance hold and reliability snapshots entirely.
                var cpuTemp = _ec.GetCpuTemperature() ?? _hwmon.GetCpuTemperature() ?? 0;
                var gpuTemp = _ec.GetGpuTemperature() ?? _hwmon.GetGpuTemperature() ?? 0;
                var (fan1, fan2) = _ec.GetFanSpeeds();

                // Thermal watchdog: re-apply performance mode if BIOS reset it after throttle
                if (_config.Thermal.RestorePerformanceAfterThrottle)
                {
                    CheckAndRestorePerformanceMode(cpuTemp);
                }

                CheckAndHoldPerformanceMode();
                await CheckReliabilityPoliciesAsync(cpuTemp, gpuTemp, fan1, fan2);
                WriteReliabilitySnapshot(cpuTemp, gpuTemp, fan1, fan2);

                // Log periodically (less often in low-overhead mode)
                logCounter++;
                var logInterval = _lowOverheadMode ? 60 : 30;
                var pollInterval = GetEffectivePollInterval();

                if (logCounter * pollInterval / 1000 >= logInterval)
                {
                    if (!_lowOverheadMode || !_config.General.LowOverhead.ReduceLogging)
                    {
                        var batteryStr = _lowOverheadMode ? $" [Battery {_battery.GetBatteryPercentage()}%]" : "";
                        var curveStr = _fanCurveEngine != null ? " [custom curve]" : "";
                        Log($"Status: CPU {cpuTemp}°C, GPU {gpuTemp}°C, Fans {fan1}/{fan2} RPM{batteryStr}{curveStr}");
                    }
                    logCounter = 0;
                }

                await Task.Delay(GetEffectivePollInterval(), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"Error in main loop: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Detects CPU thermal throttle events and re-applies the configured performance mode
    /// once the CPU cools back down.
    ///
    /// Background: several HP OMEN models (confirmed: Transcend 14-fb0014no with kernel hp-wmi)
    /// silently reset the sysfs thermal_profile / ACPI platform_profile to "balanced" when the
    /// CPU package temperature hits its PROCHOT threshold (~100 °C). This causes OmenCore's
    /// Performance setting to be discarded mid-session without any user action.
    /// </summary>
    private void CheckAndRestorePerformanceMode(int cpuTemp)
    {
        var throttleThreshold = _config.Thermal.ThrottleTempC;
        var restoreThreshold  = _config.Thermal.RestoreTempC;
        
        if (cpuTemp >= throttleThreshold)
        {
            if (!_thermalThrottleDetected)
            {
                _thermalThrottleDetected = true;
                _thermalThrottleSince = DateTime.UtcNow;
                Log($"[thermal] CPU {cpuTemp}°C ≥ {throttleThreshold}°C — throttle event recorded; " +
                    $"will restore '{_config.Performance.Mode}' mode on cooldown");
            }
        }
        else if (_thermalThrottleDetected && cpuTemp <= restoreThreshold)
        {
            var elapsed = (DateTime.UtcNow - _thermalThrottleSince).TotalSeconds;
            Log($"[thermal] CPU cooled to {cpuTemp}°C (throttled for {elapsed:F0}s) — " +
                $"re-applying performance mode: {_config.Performance.Mode}");
            
            var perfMode = ResolvePerformanceMode(_config.Performance.Mode);
            
            if (_ec.SetPerformanceMode(perfMode))
                Log($"[thermal] ✓ Performance mode restored to: {_config.Performance.Mode}");
            else
                Log($"[thermal] ⚠ Failed to restore performance mode to: {_config.Performance.Mode}");
            
            _thermalThrottleDetected = false;
            _thermalThrottleSince = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Keeps the configured performance state applied on systems where hp-wmi/firmware
    /// resets the active profile after a short timeout.
    /// </summary>
    private void CheckAndHoldPerformanceMode()
    {
        if (!_config.Performance.HoldEnabled)
        {
            return;
        }

        var intervalSeconds = Math.Clamp(_config.Performance.HoldIntervalSeconds, 10, 300);
        if ((DateTime.UtcNow - _lastPerformanceHoldCheck).TotalSeconds < intervalSeconds)
        {
            return;
        }

        _lastPerformanceHoldCheck = DateTime.UtcNow;
        _performanceHoldTick++;

        var desiredMode = ResolvePerformanceMode(_config.Performance.Mode);
        var currentMode = _ec.GetPerformanceMode();

        if (!ArePerformanceModesEquivalent(currentMode, desiredMode))
        {
            Log($"[hold] Performance mode drift detected: current={currentMode}, desired={desiredMode}; re-applying");
            if (_ec.SetPerformanceMode(desiredMode))
                Log($"[hold] Performance mode restored to: {_config.Performance.Mode}");
            else
                Log($"[hold] Failed to restore performance mode: {_config.Performance.Mode}");
        }
        else if (_performanceHoldTick % 10 == 1)
        {
            Log($"[hold] Performance mode confirmed: {currentMode} (desired {_config.Performance.Mode})");
        }

        if (_config.Performance.ThermalPowerLimit.HasValue)
        {
            var powerLimit = Math.Clamp(_config.Performance.ThermalPowerLimit.Value, 0, 5);
            if (!_ec.HasEcAccess)
            {
                if (!_thermalPowerUnsupportedLogged)
                {
                    Log($"[hold] Thermal power limit reapply skipped: backend '{_ec.AccessMethod}' does not support EC thermal power writes.");
                    _thermalPowerUnsupportedLogged = true;
                }
            }
            else if (!_ec.SetThermalPowerLimit(powerLimit))
            {
                Log($"[hold] Failed to reapply thermal power limit: {powerLimit}");
            }
            else if (_performanceHoldTick % 10 == 1)
            {
                Log($"[hold] Thermal power limit reasserted: {powerLimit}");
            }
        }
    }

    private bool TryAcquireSingleWriterLock()
    {
        if (!_config.Reliability.Enabled || !_config.Reliability.ForceSingleWriter)
        {
            Environment.SetEnvironmentVariable(ReliabilityDiagnosticsStore.WriterRoleEnvVar, ReliabilityDiagnosticsStore.DaemonWriterRole);
            return true;
        }

        try
        {
            _singleWriterLockHandle = new FileStream(
                ReliabilityDiagnosticsStore.SingleWriterLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _singleWriterLockHandle.Lock(0, 1);
            }

            Environment.SetEnvironmentVariable(ReliabilityDiagnosticsStore.WriterRoleEnvVar, ReliabilityDiagnosticsStore.DaemonWriterRole);
            var owner = $"{ReliabilityDiagnosticsStore.DaemonWriterRole}:{Environment.ProcessId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            _singleWriterLockHandle.SetLength(0);
            using var writer = new StreamWriter(_singleWriterLockHandle, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
            writer.Write(owner);
            writer.Flush();
            _singleWriterLockHandle.Flush(flushToDisk: true);
            _singleWriterLockHandle.Seek(0, SeekOrigin.Begin);

            LogReliability("single-writer lock acquired");
            return true;
        }
        catch (Exception ex)
        {
            _lastReliabilityError = $"single-writer lock failed: {ex.Message}";
            Log($"[reliability] {_lastReliabilityError}");
            return false;
        }
    }

    private void ReleaseSingleWriterLock()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _singleWriterLockHandle?.Unlock(0, 1);
            }
        }
        catch
        {
            // Ignore unlock errors.
        }

        try
        {
            _singleWriterLockHandle?.Dispose();
            _singleWriterLockHandle = null;
        }
        catch
        {
            // Ignore dispose errors.
        }

        try
        {
            if (File.Exists(ReliabilityDiagnosticsStore.SingleWriterLockPath))
            {
                File.Delete(ReliabilityDiagnosticsStore.SingleWriterLockPath);
            }
        }
        catch
        {
            // Ignore lock file cleanup errors.
        }
    }

    private async Task CheckReliabilityPoliciesAsync(int cpuTemp, int gpuTemp, int fan1, int fan2)
    {
        if (!_config.Reliability.Enabled)
        {
            return;
        }

        var onBattery = _battery.IsOnBattery();
        CheckAcBatteryOneShotAutomation(onBattery);
        await CheckStuckFanWatchdogAsync(cpuTemp, gpuTemp, fan1, fan2, onBattery);
    }

    private void CheckAcBatteryOneShotAutomation(bool onBattery)
    {
        if (!_config.Reliability.AcBatteryAutomationEnabled)
        {
            return;
        }

        if (!_lastOnBatteryState.HasValue)
        {
            _lastOnBatteryState = onBattery;
            return;
        }

        if (_lastOnBatteryState.Value == onBattery)
        {
            return;
        }

        _lastOnBatteryState = onBattery;
        var targetMode = onBattery ? _config.Reliability.BatteryMode : _config.Reliability.AcMode;
        var perfMode = ResolvePerformanceMode(targetMode);

        if (_ec.SetPerformanceMode(perfMode))
        {
            _lastAutomationMode = targetMode;
            _lastAutomationUtc = DateTime.UtcNow;
            LogReliability($"power source changed to {(onBattery ? "battery" : "ac")} -> set performance mode '{targetMode}'");
        }
        else
        {
            _lastReliabilityError = $"failed AC/Battery automation to '{targetMode}'";
            Log($"[reliability] {_lastReliabilityError}");
        }
    }

    private async Task CheckStuckFanWatchdogAsync(int cpuTemp, int gpuTemp, int fan1, int fan2, bool onBattery)
    {
        if (!_config.Reliability.StuckFanWatchdogEnabled)
        {
            return;
        }

        // The custom curve engine owns the fans; a max->auto kick here would
        // knock the EC out of manual mode and fight the curve writes.
        if (_fanCurveEngine != null)
        {
            _watchdogConsecutiveHits = 0;
            return;
        }

        if (_configuredFanProfile != FanProfile.Auto && _configuredFanProfile != FanProfile.Constant)
        {
            _watchdogConsecutiveHits = 0;
            return;
        }

        if (_configuredFanProfile == FanProfile.Constant)
        {
            _watchdogConsecutiveHits = 0;
            return;
        }

        var maxTemp = Math.Max(cpuTemp, gpuTemp);
        var fansLow = fan1 <= _config.Reliability.WatchdogMinFanRpm &&
                      fan2 <= _config.Reliability.WatchdogMinFanRpm;
        var thermalCondition = maxTemp >= _config.Reliability.WatchdogTempC;

        if (!thermalCondition || !fansLow)
        {
            _watchdogConsecutiveHits = 0;
            return;
        }

        _watchdogConsecutiveHits++;
        if (_watchdogConsecutiveHits < _config.Reliability.WatchdogConsecutiveHits)
        {
            return;
        }

        var cooldown = TimeSpan.FromSeconds(_config.Reliability.WatchdogCooldownSeconds);
        if (DateTime.UtcNow - _lastWatchdogKickUtc < cooldown)
        {
            return;
        }

        _watchdogConsecutiveHits = 0;
        var reason = $"auto watchdog trigger (temp={maxTemp}C, fans={fan1}/{fan2}rpm, power={(onBattery ? "battery" : "ac")})";
        _lastWatchdogReason = reason;

        var kickSucceeded = _ec.SetFanProfile(FanProfile.Max);
        await Task.Delay(400, _cts.Token);
        kickSucceeded = _ec.SetFanProfile(FanProfile.Auto) && kickSucceeded;

        _lastWatchdogKickUtc = DateTime.UtcNow;
        if (kickSucceeded)
        {
            _watchdogTrips++;
            LogReliability($"{reason}; kick=max->auto succeeded");
        }
        else
        {
            _lastReliabilityError = $"{reason}; kick=max->auto failed";
            Log($"[reliability] {_lastReliabilityError}");
        }
    }

    private void WriteReliabilitySnapshot(int cpuTemp, int gpuTemp, int fan1, int fan2)
    {
        if (!_config.Reliability.Enabled)
        {
            return;
        }

        if (DateTime.UtcNow - _lastSnapshotWriteUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastSnapshotWriteUtc = DateTime.UtcNow;

        var onBattery = _lastOnBatteryState ?? _battery.IsOnBattery();
        var snapshot = new ReliabilityStatusSnapshot
        {
            Enabled = _config.Reliability.Enabled,
            SingleWriterEnabled = _config.Reliability.ForceSingleWriter,
            SingleWriterActive = _singleWriterLockHandle != null,
            WriterOwner = ReliabilityDiagnosticsStore.ReadSingleWriterOwner() ?? string.Empty,
            FanProfile = _fanCurveEngine != null ? "custom" : _configuredFanProfile.ToString().ToLowerInvariant(),
            WatchdogEnabled = _config.Reliability.StuckFanWatchdogEnabled,
            WatchdogTrips = _watchdogTrips,
            LastWatchdogKickUnix = _lastWatchdogKickUtc == DateTime.MinValue ? 0 : new DateTimeOffset(_lastWatchdogKickUtc).ToUnixTimeSeconds(),
            LastWatchdogReason = _lastWatchdogReason,
            AcBatteryAutomationEnabled = _config.Reliability.AcBatteryAutomationEnabled,
            PowerSource = onBattery ? "battery" : "ac",
            LastAutomationMode = _lastAutomationMode,
            LastAutomationUnix = _lastAutomationUtc == DateTime.MinValue ? 0 : new DateTimeOffset(_lastAutomationUtc).ToUnixTimeSeconds(),
            CpuTempC = cpuTemp,
            GpuTempC = gpuTemp,
            CpuFanRpm = fan1,
            GpuFanRpm = fan2,
            LastError = _lastReliabilityError,
            UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        ReliabilityDiagnosticsStore.WriteSnapshot(snapshot);
    }

    private void LogReliability(string message)
    {
        Log($"[reliability] {message}");
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        ReliabilityDiagnosticsStore.AppendLogLine($"[{timestamp}] {message}");
    }
    
    private void ApplyFanProfile()
    {
        if (_config.Fan.Profile == "custom")
            return;

        if (_config.Fan.Profile == "manual")
        {
            // Fixed manual speed from the GUI. Constant keeps the stuck-fan watchdog away.
            _configuredFanProfile = FanProfile.Constant;
            var speed = Math.Clamp(UserPreferencesStore.LoadBestAvailable().Fan.ManualFanSpeed, 1, 100);

            if (_ec.SetFanSpeedPercent(speed))
            {
                Log($"  Fan profile: manual ({speed}%)");
            }
            else
            {
                Log($"  Failed to apply manual fan speed {speed}%");
            }
            return;
        }

        var profile = _config.Fan.Profile.ToLower() switch
        {
            "auto" => FanProfile.Auto,
            "silent" => FanProfile.Silent,
            "balanced" => FanProfile.Balanced,
            "gaming" => FanProfile.Gaming,
            "max" => FanProfile.Max,
            "constant" => FanProfile.Constant,
            _ => FanProfile.Auto
        };

        _configuredFanProfile = profile;

        if (_ec.SetFanProfile(profile))
        {
            Log($"  Fan profile: {_config.Fan.Profile}");
        }
    }

    private async Task ApplyStartupConfigAsync()
    {
        Log("Applying startup configuration...");

        ApplyFanProfile();
        
        // Apply fan boost
        if (_config.Fan.Boost)
        {
            _ec.SetFanBoost(true);
            Log("  Fan boost: enabled");
        }
        
        // Apply performance mode
        var perfMode = ResolvePerformanceMode(_config.Performance.Mode);
        
        if (_ec.SetPerformanceMode(perfMode))
        {
            Log($"  Performance mode: {_config.Performance.Mode}");
        }

        if (_config.Performance.ThermalPowerLimit.HasValue)
        {
            var powerLimit = Math.Clamp(_config.Performance.ThermalPowerLimit.Value, 0, 5);
            if (!_ec.HasEcAccess)
                Log($"  Thermal power limit skipped: backend '{_ec.AccessMethod}' does not support EC thermal power writes");
            else if (_ec.SetThermalPowerLimit(powerLimit))
                Log($"  Thermal power limit: {powerLimit}");
            else
                Log($"  Thermal power limit failed: {powerLimit}");
        }
        
        // Apply keyboard settings (static palette + software animation when configured).
        if (_config.Keyboard.Enabled)
        {
            var prefs = UserPreferencesStore.LoadCanonical().Keyboard;
            if (UserPreferencesStore.ApplyKeyboard(_keyboard, prefs))
            {
                Log($"  Keyboard lighting: Z1 #{prefs.Zone1R:X2}{prefs.Zone1G:X2}{prefs.Zone1B:X2}, " +
                    $"brightness {prefs.Brightness}%, animation={prefs.AnimationIndex}");
            }

            if (_keyboard.HasFourZoneAnimationControl)
                _keyboard.SetAnimationMode(0);

            if (prefs.AnimationIndex > 0)
            {
                _keyboardAnimationEngine = new KeyboardAnimationEngine(_keyboard);
                if (UserPreferencesStore.TryStartKeyboardAnimation(_keyboardAnimationEngine, prefs.AnimationIndex))
                    Log($"  Keyboard software animation started (mode {prefs.AnimationIndex})");
                else
                {
                    _keyboardAnimationEngine.Dispose();
                    _keyboardAnimationEngine = null;
                }
            }
        }
        
        Log("Startup configuration applied");
        await Task.CompletedTask;
    }

    private void StartKeyboardIdleMonitor()
    {
        if (!_config.Keyboard.Enabled || _config.Keyboard.BacklightTimeoutSeconds <= 0)
        {
            return;
        }

        try
        {
            _keyboardIdleMonitor = new KeyboardIdleMonitor(
                _keyboard,
                _config.Keyboard.BacklightTimeoutSeconds,
                _config.Keyboard.Brightness,
                Log);

            if (!_keyboardIdleMonitor.Start())
            {
                _keyboardIdleMonitor.Dispose();
                _keyboardIdleMonitor = null;
            }
        }
        catch (Exception ex)
        {
            Log($"[idle] Failed to start keyboard backlight timeout: {ex.Message}");
            _keyboardIdleMonitor = null;
        }
    }
    
    private async Task ShutdownAsync()
    {
        Log("Shutting down...");

        // Stop keyboard idle monitor (restores backlight if it was dimmed)
        _keyboardIdleMonitor?.Dispose();
        _keyboardIdleMonitor = null;

        _keyboardAnimationEngine?.Dispose();
        _keyboardAnimationEngine = null;
        
        // Stop fan curve engine
        _fanCurveEngine?.Stop();
        
        // Remember keyboard lighting for the next boot (brightness only — GUI owns colors).
        if (_config.Keyboard.Enabled)
        {
            if (UserPreferencesStore.PersistKeyboardState(_keyboard, captureZoneColors: false))
                Log("Keyboard brightness saved for next boot");
            else
                Log("Warning: Could not save keyboard settings for next boot");
        }

        // Restore settings if configured
        if (_config.Startup.RestoreOnExit)
        {
            Log("Restoring default settings...");
            _ec.SetFanState(biosControl: true);
            _ec.SetFanBoost(false);
        }
        
        // Remove PID file
        RemovePidFile();
        
        // Stop config watcher
        _configWatcher?.Dispose();

        ReleaseSingleWriterLock();
        
        Log("Daemon stopped");
        await Task.CompletedTask;
    }
    
    private void WritePidFile()
    {
        try
        {
            var pid = Environment.ProcessId;
            File.WriteAllText(PidFilePath, pid.ToString());
            Log($"PID file created: {PidFilePath} ({pid})");
        }
        catch (Exception ex)
        {
            Log($"Warning: Could not create PID file: {ex.Message}");
        }
    }
    
    private void RemovePidFile()
    {
        try
        {
            if (File.Exists(PidFilePath))
            {
                File.Delete(PidFilePath);
            }
        }
        catch { }
    }
    
    private void SetupSignalHandlers()
    {
        // Handle SIGTERM and SIGINT for graceful shutdown
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Stop();
        };
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();

            PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
            {
                ctx.Cancel = true;
                ScheduleConfigurationReload();
            });
        }
    }

    private void ScheduleConfigurationReload()
    {
        lock (_reloadLock)
        {
            _reloadPending = true;
            _reloadScheduledUtc = DateTime.UtcNow;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            lock (_reloadLock)
            {
                if (!_reloadPending || (DateTime.UtcNow - _reloadScheduledUtc).TotalMilliseconds < 250)
                    return;

                _reloadPending = false;
            }

            ReloadConfiguration();
        });
    }

    private void ReloadConfiguration()
    {
        try
        {
            UserPreferencesStore.MergeIntoConfig(_config);
            SyncFanCurveEngine();
            _fanCurveEngine?.ReloadFromConfig();

            var curvePreview = _config.Fan.Curve.Points.Count == 0
                ? "(none)"
                : string.Join(", ", _config.Fan.Curve.Points.Select(p => $"{p.Temp}°C→{p.Speed}%"));

            Log($"Configuration reloaded — profile={_config.Fan.Profile}, curve: {curvePreview}");
        }
        catch (Exception ex)
        {
            Log($"Failed to reload configuration: {ex.Message}");
        }
    }
    
    private void SetupConfigWatcher()
    {
        if (!Directory.Exists(OmenCorePaths.SharedConfigDir))
            return;

        try
        {
            _configWatcher = new FileSystemWatcher(OmenCorePaths.SharedConfigDir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };

            _configWatcher.Changed += (_, e) =>
            {
                var name = Path.GetFileName(e.Name);
                if (name is "config.toml" or "user-preferences.json")
                    ScheduleConfigurationReload();
            };

            _configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log($"Warning: Could not setup config watcher: {ex.Message}");
        }
    }

    private void OnFanSpeedChange(int temp, int targetSpeed, int actualSpeed)
    {
        // Additional logging or actions on fan speed change
    }

    private static bool TryParseColor(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return false;
        
        try
        {
            r = Convert.ToByte(hex[..2], 16);
            g = Convert.ToByte(hex[2..4], 16);
            b = Convert.ToByte(hex[4..6], 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PerformanceMode ResolvePerformanceMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "performance" => PerformanceMode.Performance,
            "balanced" => PerformanceMode.Balanced,
            "cool" => PerformanceMode.Cool,
            _ => PerformanceMode.Default
        };
    }

    private static bool ArePerformanceModesEquivalent(PerformanceMode current, PerformanceMode desired)
    {
        if (current == desired)
        {
            return true;
        }

        return (current is PerformanceMode.Default or PerformanceMode.Balanced)
            && (desired is PerformanceMode.Default or PerformanceMode.Balanced);
    }
    
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"[{timestamp}] {message}";
        
        Console.WriteLine(line);
        
        // Also write to log file if running as service
        try
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch { }
    }
    
    public void Dispose()
    {
        Stop();
        _keyboardIdleMonitor?.Dispose();
        _keyboardAnimationEngine?.Dispose();
        _fanCurveEngine?.Dispose();
        _configWatcher?.Dispose();
        _cts.Dispose();
    }
}
