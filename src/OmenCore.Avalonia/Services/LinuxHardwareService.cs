using System.Runtime.InteropServices;
using OmenCore.Linux.Daemon;
using OmenCore.Linux.Hardware;

namespace OmenCore.Avalonia.Services;

/// <summary>
/// Linux implementation of hardware service using sysfs and ACPI interfaces.
/// </summary>
public class LinuxHardwareService : IHardwareService, IDisposable
{
    private readonly System.Timers.Timer _pollingTimer;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly SemaphoreSlim _capabilitiesLock = new(1, 1);
    private HardwareStatus _lastStatus = new();
    private SystemCapabilities? _capabilities;
    private bool _disposed;
    private bool _pollingInProgress;
    private PerformanceMode? _lastFanFallbackMode;
    private OmenCore.Linux.Hardware.FanProfile _activeFanProfile = OmenCore.Linux.Hardware.FanProfile.Auto;

    private readonly Lazy<LinuxHwMonController> _hwmon = new(() => new LinuxHwMonController());
    private readonly Lazy<LinuxEcController> _ec = new(() => new LinuxEcController());

    // Long-lived keyboard controller + software animation engine.
    // Firmware has no native four-zone animations; effects are rendered by
    // streaming frames to fourzone_color, like OGH Light Studio on Windows.
    private readonly Lazy<OmenCore.Linux.Hardware.LinuxKeyboardController> _keyboardController =
        new(() => new OmenCore.Linux.Hardware.LinuxKeyboardController());
    private OmenCore.Linux.Hardware.KeyboardAnimationEngine? _animationEngine;

    private OmenCore.Linux.Hardware.KeyboardAnimationEngine GetAnimationEngine() =>
        _animationEngine ??= new OmenCore.Linux.Hardware.KeyboardAnimationEngine(_keyboardController.Value);

    // HP OMEN specific paths
    private const string HP_WMI_PATH = LinuxSysfsPathMap.HpWmiRoot;
    private const string HWMON_BASE = "/sys/class/hwmon";
    private const string POWER_SUPPLY = "/sys/class/power_supply";
    private const string BACKLIGHT_PATH = LinuxSysfsPathMap.KeyboardBacklightPath;
    private const string HP_WMI_FAN1_OUTPUT = "/sys/devices/platform/hp-wmi/fan1_output";
    private const string HP_WMI_FAN2_OUTPUT = "/sys/devices/platform/hp-wmi/fan2_output";
    private const string HP_WMI_FAN_ALWAYS_ON = "/sys/devices/platform/hp-wmi/fan_always_on";
    private const string RaplPl1Path = "/sys/class/powercap/intel-rapl/intel-rapl:0/constraint_0_power_limit_uw";

    /// <summary>Assumed full-speed RPM used only when pwm duty is unreadable.</summary>
    private const double FallbackMaxFanRpm = 5500.0;
    private static readonly string[] PowerProfilesCtlCandidates =
    {
        "/usr/bin/powerprofilesctl",
        "/bin/powerprofilesctl"
    };
    
    private string? _resolvedThermalPath; // Cached resolved path

    // RAPL power tracking — store last reading to compute delta without sleeping
    private long _raplLastEnergyUj;
    private DateTime _raplLastReadTime = DateTime.MinValue;
    
    public event EventHandler<HardwareStatus>? StatusChanged;

    public LinuxHardwareService()
    {
        _pollingTimer = new System.Timers.Timer(2500)
        {
            AutoReset = false
        };
        _pollingTimer.Elapsed += async (s, e) =>
        {
            await PollHardwareAsync();
            if (!_disposed)
            {
                _pollingTimer.Start();
            }
        };
        _pollingTimer.Start();

        // The daemon owns fan/keyboard hardware while it is running.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !DaemonRuntime.IsServiceActive())
            _ = Task.Run(() => ReapplyFanProfile(_activeFanProfile));
    }

    private static void ReapplyFanProfile(OmenCore.Linux.Hardware.FanProfile profile)
    {
        try
        {
            var ec = new OmenCore.Linux.Hardware.LinuxEcController();
            ec.SetFanProfile(profile);
        }
        catch { }
    }

    private async Task PollHardwareAsync()
    {
        if (_pollingInProgress || _disposed)
        {
            return;
        }

        _pollingInProgress = true;
        try
        {
            var status = await GetStatusAsync();
            if (HasStatusChanged(_lastStatus, status))
            {
                _lastStatus = status;
                StatusChanged?.Invoke(this, status);
            }
        }
        catch
        {
            // Ignore polling errors
        }
        finally
        {
            _pollingInProgress = false;
        }
    }

    private static bool HasStatusChanged(HardwareStatus old, HardwareStatus current)
    {
        return Math.Abs(old.CpuTemperature - current.CpuTemperature) > 1 ||
               Math.Abs(old.GpuTemperature - current.GpuTemperature) > 1 ||
               old.CpuFanRpm != current.CpuFanRpm ||
               old.GpuFanRpm != current.GpuFanRpm;
    }

    public async Task<HardwareStatus> GetStatusAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Return mock data for testing on Windows
            return GetMockStatus();
        }

        return await ExecuteWithIoLockAsync(async () =>
        {
            var status = new HardwareStatus();

            // Use the same package sensor priority as CLI status and daemon fan control.
            status.CpuTemperature = LinuxTelemetryResolver.GetCpuTemperature(_ec.Value, _hwmon.Value)?.Temperature ?? 0;

            // Read GPU temperature (NVIDIA or AMD)
            status.GpuTemperature = await ReadGpuTemperatureAsync() / 1000.0;

            // Read fan speeds
            status.CpuFanRpm = await ReadFanRpmAsync("cpu");
            status.GpuFanRpm = await ReadFanRpmAsync("gpu");

            // Fan duty: hp-wmi exposes a single pwm channel for both fans.
            // Fall back to an RPM-based estimate when pwm1 is unavailable.
            var fanDuty = await ReadFanDutyPercentAsync();
            status.CpuFanPercent = fanDuty >= 0
                ? fanDuty
                : Math.Clamp((int)Math.Round(status.CpuFanRpm * 100.0 / FallbackMaxFanRpm), 0, 100);
            status.GpuFanPercent = fanDuty >= 0
                ? fanDuty
                : Math.Clamp((int)Math.Round(status.GpuFanRpm * 100.0 / FallbackMaxFanRpm), 0, 100);

            // Read CPU/memory usage from /proc
            status.CpuUsage = await ReadCpuUsageAsync();
            var (memPercentage, memUsedGb, memTotalGb) = await ReadMemoryUsageAsync();
            status.MemoryUsage = memPercentage;
            status.MemoryUsedGb = memUsedGb;
            status.MemoryTotalGb = memTotalGb;

            // Read battery status
            (status.BatteryPercentage, status.IsOnBattery) = await ReadBatteryStatusAsync();

            // Read GPU utilization
            status.GpuUsage = await ReadGpuUsageAsync();

            // Read power consumption (RAPL on Intel, power_now fallback)
            status.PowerConsumption = await ReadPowerConsumptionAsync();

            return status;
        });
    }

    private static HardwareStatus GetMockStatus()
    {
        var rng = new Random();
        var cpuFanPercent = 30 + rng.Next(0, 40);
        var gpuFanPercent = 25 + rng.Next(0, 45);
        var memUsed = 8.0 + rng.NextDouble() * 12;
        var memTotal = 32.0;
        return new HardwareStatus
        {
            CpuTemperature = 45 + rng.Next(0, 20),
            GpuTemperature = 40 + rng.Next(0, 25),
            CpuFanRpm = 2000 + rng.Next(0, 1000),
            GpuFanRpm = 2500 + rng.Next(0, 1500),
            CpuFanPercent = cpuFanPercent,
            GpuFanPercent = gpuFanPercent,
            CpuUsage = 10 + rng.Next(0, 50),
            GpuUsage = 5 + rng.Next(0, 60),
            MemoryUsage = (memUsed / memTotal) * 100,
            MemoryUsedGb = memUsed,
            MemoryTotalGb = memTotal,
            PowerConsumption = 25 + rng.Next(0, 50),
            BatteryPercentage = 75 + rng.Next(-20, 25),
            IsOnBattery = false,
            IsThrottling = false,
            ThrottlingReason = null
        };
    }

    public async Task<SystemCapabilities> GetCapabilitiesAsync()
    {
        if (_capabilities != null)
            return _capabilities;

        await _capabilitiesLock.WaitAsync();
        try
        {
            if (_capabilities != null)
                return _capabilities;

            var pending = new SystemCapabilities();
            try
            {
                await PopulateCapabilitiesAsync(pending);
                _capabilities = pending;
            }
            catch
            {
                // Don't cache a half-initialised object — let the next call retry.
            }

            return _capabilities ?? pending;
        }
        finally
        {
            _capabilitiesLock.Release();
        }
    }

    private async Task PopulateCapabilitiesAsync(SystemCapabilities cap)
    {
        var _capabilities = cap; // local alias so the body below compiles unchanged

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Mock capabilities for testing on non-Linux
            cap.HasKeyboardBacklight = true;
            cap.HasFourZoneRgb = true;
            cap.HasDiscreteGpu = true;
            cap.HasGpuMuxSwitch = true;
            cap.SupportsFanControl = true;
            cap.SupportsFanSurface = true;
            cap.SupportsPerformanceProfiles = true;
            cap.SupportsKeyboardBrightness = true;
            cap.SupportsKeyboardAnimation = true;
            cap.FanControlCapabilityClass = "full-control";
            cap.FanControlCapabilityReason = "Mock environment reports full control.";
            cap.ModelName = "HP OMEN 16 (Mock)";
            cap.CpuName = "AMD Ryzen 9 7945HX";
            cap.GpuName = "NVIDIA GeForce RTX 4070";
            cap.HasNvidiaSettings = true;
            return;
        }

        // Check for HP OMEN thermal/profile interfaces using centralized path normalization.
        _resolvedThermalPath = ResolveThermalProfilePath();
        bool hasDirectFanControl = File.Exists(HP_WMI_FAN1_OUTPUT) ||
                       File.Exists(HP_WMI_FAN2_OUTPUT) ||
                       ResolveHwmonFanTargetPath(1) != null ||
                       ResolveHwmonFanTargetPath(2) != null;
        var hasHpWmiThermalProfilePath = LinuxSysfsPathMap.AnyPathExists(LinuxSysfsPathMap.HpWmiThermalProfilePaths);
        var hasHpWmiPlatformProfilePath = LinuxSysfsPathMap.AnyPathExists(LinuxSysfsPathMap.PlatformProfilePaths);

        var capabilityAssessment = LinuxCapabilityClassifier.Assess(
            CheckRootAccess(),
            File.Exists(LinuxSysfsPathMap.EcIoPath),
            Directory.Exists(HP_WMI_PATH),
            hasHpWmiThermalProfilePath,
            hasHpWmiPlatformProfilePath,
            File.Exists(LinuxSysfsPathMap.AcpiPlatformProfilePath),
            File.Exists(HP_WMI_FAN1_OUTPUT),
            File.Exists(HP_WMI_FAN2_OUTPUT),
            ResolveHwmonFanTargetPath(1) != null,
            ResolveHwmonFanTargetPath(2) != null,
            ResolveHwmonPwmEnablePath(1) != null || ResolveHwmonPwmEnablePath(2) != null,
            LinuxSysfsPathMap.HasHpWmiPwmDutyAccess(),
            Directory.Exists(HWMON_BASE) || Directory.Exists(HP_WMI_PATH),
            IsUnsafeEcModel(),
            await ReadDmiStringAsync("product_name"),
            await ReadDmiStringAsync("board_name"));
        _capabilities.SupportsFanControl = capabilityAssessment.SupportsManualFanControl;
        _capabilities.SupportsFanSurface = capabilityAssessment.SupportsManualFanControl || capabilityAssessment.SupportsProfileControl || capabilityAssessment.SupportsTelemetry;
        _capabilities.SupportsHwmonPwmDuty = LinuxSysfsPathMap.HasHpWmiPwmDutyAccess();
        _capabilities.SupportsPerformanceProfiles = capabilityAssessment.SupportsProfileControl || ResolvePowerProfilesCtlPath() != null;
        _capabilities.PerformanceProfileReason = _capabilities.SupportsPerformanceProfiles
            ? string.Empty
            : "No writable platform_profile/thermal_profile interface was detected.";
        _capabilities.FanControlCapabilityClass = capabilityAssessment.CapabilityKey;
        _capabilities.FanControlCapabilityReason = capabilityAssessment.Reason;
        
        // Keyboard: check both legacy hp::kbd_backlight and the fourzone_color interface
        var kbCtrl = new OmenCore.Linux.Hardware.LinuxKeyboardController();
        _capabilities.HasKeyboardBacklight = kbCtrl.IsAvailable;
        _capabilities.SupportsKeyboardBrightness = kbCtrl.SupportsBrightnessControl;
        _capabilities.KeyboardBrightnessReason = _capabilities.SupportsKeyboardBrightness
            ? string.Empty
            : "Keyboard brightness sysfs path was not detected on this kernel/board.";
        // Animations are software-rendered through fourzone_color; the
        // firmware fourzone_animation node alone is not sufficient (no known
        // board animates natively), but color streaming always works.
        _capabilities.SupportsKeyboardAnimation = kbCtrl.HasFourZoneControl;
        _capabilities.KeyboardAnimationReason = _capabilities.SupportsKeyboardAnimation
            ? string.Empty
            : "fourzone_color sysfs path was not detected on this kernel/board.";

        // Detect RGB capabilities — include fourzone_color for newer OMEN models
        _capabilities.HasFourZoneRgb = kbCtrl.HasFourZoneControl || DetectFourZoneRgbSupport();
        _capabilities.HasPerKeyRgb = kbCtrl.IsPerKeyRgb || DetectPerKeyRgbSupport();

        // Check for discrete GPU
        _capabilities.HasDiscreteGpu = await HasDiscreteGpuAsync();

        // Read model name from DMI
        _capabilities.ModelName = await ReadDmiStringAsync("product_name") ?? "Unknown HP OMEN";

        // Read CPU name from /proc/cpuinfo
        _capabilities.CpuName = await ReadCpuNameAsync();

        // Read GPU name
        _capabilities.GpuName = await ReadGpuNameAsync();

        // Longevity: CPU RAPL power limit
        _capabilities.SupportsCpuPowerLimit = File.Exists(RaplPl1Path);

        // Longevity: battery charge limit (sysfs)
        _capabilities.SupportsBatteryChargeLimit = await FindChargeThresholdPathAsync() != null;

        // Detect nvidia-settings for the GPU settings launcher button
        _capabilities.HasNvidiaSettings = ResolveNvidiaSettingsPath() != null;
    }

    private static bool DetectFourZoneRgbSupport()
    {
        if (File.Exists("/sys/class/leds/hp::kbd_backlight/color") ||
            File.Exists("/sys/class/leds/hp::kbd_backlight/multi_intensity"))
        {
            return true;
        }

        try
        {
            if (!Directory.Exists("/sys/class/leds"))
            {
                return false;
            }

            foreach (var ledPath in Directory.EnumerateDirectories("/sys/class/leds", "*", SearchOption.TopDirectoryOnly))
            {
                var ledName = Path.GetFileName(ledPath);
                if (string.IsNullOrWhiteSpace(ledName))
                {
                    continue;
                }

                if (ledName.Contains("zone", StringComparison.OrdinalIgnoreCase) ||
                    ledName.Contains("multicolor", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (File.Exists(Path.Combine(ledPath, "multi_intensity")) ||
                    File.Exists(Path.Combine(ledPath, "multi_index")))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool DetectPerKeyRgbSupport()
    {
        try
        {
            if (!Directory.Exists("/sys/class/leds"))
            {
                return false;
            }

            return Directory.EnumerateDirectories("/sys/class/leds", "hp::*key*", SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateDirectories("/sys/class/leds", "*kbd*perkey*", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Find the first existing thermal profile sysfs path.
    /// </summary>
    private static string? ResolveThermalProfilePath()
    {
        return LinuxSysfsPathMap.ResolveThermalProfilePath();
    }

    private static string? ResolveHwmonPwmEnablePath(int index)
    {
        return LinuxSysfsPathMap.ResolveHpWmiPwmEnablePath(index);
    }

    private static bool CheckRootAccess()
    {
        try
        {
            return geteuid() == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnsafeEcModel()
    {
        try
        {
            var modelName = ReadDmiString("product_name");
            var boardName = ReadDmiString("board_name");
            if (modelName?.Contains("transcend 14", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            if (boardName != null &&
                (string.Equals(boardName, "8C58", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(boardName, "8E41", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string? ReadDmiString(string name)
    {
        var paths = new[]
        {
            $"/sys/devices/virtual/dmi/id/{name}",
            $"/sys/class/dmi/id/{name}"
        };

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path).Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    /// <summary>
    /// Convert kernel platform_profile string to OmenCore PerformanceMode.
    /// The kernel interface uses: "low-power", "cool", "quiet", "balanced", "balanced-performance", "performance".
    /// </summary>
    private static PerformanceMode ParsePlatformProfile(string profile)
    {
        return profile.Trim().ToLower() switch
        {
            "low-power" or "cool" or "quiet" => PerformanceMode.Cool,
            "balanced" => PerformanceMode.Balanced,
            "balanced-performance" => PerformanceMode.Performance,
            "performance" => PerformanceMode.Performance,
            "default" => PerformanceMode.Default,
            _ => PerformanceMode.Default
        };
    }

    /// <summary>
    /// Convert OmenCore PerformanceMode to kernel platform_profile string.
    /// Uses the standard kernel values from /sys/firmware/acpi/platform_profile_choices.
    /// </summary>
    private async Task<string> GetKernelProfileStringAsync(PerformanceMode mode)
    {
        // Read available choices from the kernel to use exact supported values
        var choices = await ReadAvailableProfileChoicesAsync();
        
        return mode switch
        {
            PerformanceMode.Cool => 
                choices.Contains("low-power") ? "low-power" :
                choices.Contains("quiet") ? "quiet" :
                choices.Contains("cool") ? "cool" : "low-power",
            PerformanceMode.Default => "balanced",
            PerformanceMode.Balanced => "balanced",
            PerformanceMode.Performance =>
                choices.Contains("performance") ? "performance" :
                choices.Contains("balanced-performance") ? "balanced-performance" :
                "performance",
            _ => "balanced"
        };
    }

    /// <summary>
    /// Read available profile choices from the kernel.
    /// </summary>
    private async Task<HashSet<string>> ReadAvailableProfileChoicesAsync()
    {
        var choices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var choicesPath in LinuxSysfsPathMap.ThermalProfileChoicePaths)
            {
                if (!File.Exists(choicesPath))
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(choicesPath);
                foreach (var choice in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    choices.Add(choice.Trim());

                if (choices.Count > 0)
                {
                    break;
                }
            }
        }
        catch { }
        
        // Fallback defaults if we couldn't read choices
        if (choices.Count == 0)
        {
            choices.Add("low-power");
            choices.Add("balanced");
            choices.Add("performance");
        }
        
        return choices;
    }

    public async Task<PerformanceMode> GetPerformanceModeAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PerformanceMode.Balanced;

        try
        {
            var thermalPath = _resolvedThermalPath ?? ResolveThermalProfilePath();
            if (thermalPath != null)
            {
                var profile = await File.ReadAllTextAsync(thermalPath);
                return ParsePlatformProfile(profile);
            }
        }
        catch { }

        var fallbackMode = await TryGetPowerProfilesCtlModeAsync();
        if (fallbackMode.HasValue)
        {
            return fallbackMode.Value;
        }

        return PerformanceMode.Balanced;
    }

    public async Task SetPerformanceModeAsync(PerformanceMode mode)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(() => SetPerformanceModeCoreAsync(mode));
    }

    private async Task SetPerformanceModeCoreAsync(PerformanceMode mode)
    {
        var thermalPath = _resolvedThermalPath ?? ResolveThermalProfilePath();
        if (thermalPath == null)
        {
            if (await TrySetPerformanceModeViaPowerProfilesCtlAsync(mode))
            {
                return;
            }

            var boardId = await ReadFirstExistingTextAsync(new[]
            {
                "/sys/class/dmi/id/board_name",
                "/sys/devices/virtual/dmi/id/board_name"
            }) ?? "unknown";

            throw new InvalidOperationException(
                $"No thermal profile interface found (board {boardId}). If hp-wmi is loaded but platform_profile/thermal_profile are missing, run 'omencore-cli diagnose --report' to capture model-specific sysfs capabilities.");
        }

        var profile = await GetKernelProfileStringAsync(mode);

        // Strategy 1: Direct sysfs write (works when running as root)
        try
        {
            // Use File.Open with FileMode.Open to avoid Create semantics that sysfs rejects
            await using var fs = new FileStream(thermalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(profile);
            await fs.WriteAsync(bytes);
            return; // Success
        }
        catch (UnauthorizedAccessException)
        {
            // Not running with permission to write this sysfs control.
        }
        catch (IOException)
        {
            // sysfs write failed.
        }

        if (await TrySetPerformanceModeViaPowerProfilesCtlAsync(mode))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Could not write to {thermalPath}. Start OmenCore with the required permissions or configure a distro policy rule for this sysfs path.");
    }

    private static string? ResolvePowerProfilesCtlPath()
    {
        foreach (var candidate in PowerProfilesCtlCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static PerformanceMode ParsePowerProfilesCtlMode(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "power-saver" => PerformanceMode.Cool,
            "performance" => PerformanceMode.Performance,
            _ => PerformanceMode.Balanced
        };
    }

    private static string ToPowerProfilesCtlMode(PerformanceMode mode)
    {
        return mode switch
        {
            PerformanceMode.Cool => "power-saver",
            PerformanceMode.Performance => "performance",
            _ => "balanced"
        };
    }

    private static async Task<(int exitCode, string stdout)> RunPowerProfilesCtlAsync(string arguments)
    {
        var binary = ResolvePowerProfilesCtlPath();
        if (binary == null)
        {
            return (-1, string.Empty);
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return (-1, string.Empty);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, waitTask);

            return (process.ExitCode, (await stdoutTask).Trim());
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    private static async Task<PerformanceMode?> TryGetPowerProfilesCtlModeAsync()
    {
        var (exitCode, stdout) = await RunPowerProfilesCtlAsync("get");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        return ParsePowerProfilesCtlMode(stdout);
    }

    private static async Task<bool> TrySetPerformanceModeViaPowerProfilesCtlAsync(PerformanceMode mode)
    {
        var profile = ToPowerProfilesCtlMode(mode);
        var (exitCode, _) = await RunPowerProfilesCtlAsync($"set {profile}");
        return exitCode == 0;
    }

    public async Task SetFanProfileAsync(string profile)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(() =>
        {
            var ec = new OmenCore.Linux.Hardware.LinuxEcController();
            var fanProfile = profile.Trim().ToLowerInvariant() switch
            {
                "silent"   => OmenCore.Linux.Hardware.FanProfile.Silent,
                "balanced" => OmenCore.Linux.Hardware.FanProfile.Balanced,
                "gaming"   => OmenCore.Linux.Hardware.FanProfile.Gaming,
                "max"      => OmenCore.Linux.Hardware.FanProfile.Max,
                "constant" => OmenCore.Linux.Hardware.FanProfile.Constant,
                _          => OmenCore.Linux.Hardware.FanProfile.Auto
            };
            ec.SetFanProfile(fanProfile);
            _activeFanProfile = fanProfile;
            return Task.CompletedTask;
        });
    }

    public async Task SetCpuFanSpeedAsync(int percentage)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(async () =>
        {
            int clamped = Math.Clamp(percentage, 0, 100);

            var ec = new OmenCore.Linux.Hardware.LinuxEcController();
            if (ec.HasHwmonPwmDutyAccess)
            {
                ec.SetHwmonPwmDutyPercent(clamped);
                return;
            }

            await TryEnableManualFanOverrideAsync();

            // Prefer direct hp-wmi fan output when exposed by kernel/firmware.
            try
            {
                if (File.Exists(HP_WMI_FAN1_OUTPUT))
                {
                    await File.WriteAllTextAsync(HP_WMI_FAN1_OUTPUT, clamped.ToString());
                    return;
                }

                var fanTargetPath = ResolveHwmonFanTargetPath(1);
                if (fanTargetPath != null)
                {
                    await File.WriteAllTextAsync(fanTargetPath, clamped.ToString());
                    return;
                }
            }
            catch
            {
                // Fall through to profile-based fallback.
            }

            // Fallback for hp_wmi-only boards that expose only thermal_profile:
            // approximate requested fan intensity by switching platform performance profile.
            var mode = clamped switch
            {
                <= 35 => PerformanceMode.Cool,
                <= 70 => PerformanceMode.Balanced,
                _ => PerformanceMode.Performance
            };

            await ApplyFanFallbackProfileAsync(mode);
        });
    }

    public async Task SetGpuFanSpeedAsync(int percentage)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(async () =>
        {
            int clamped = Math.Clamp(percentage, 0, 100);

            var ec = new OmenCore.Linux.Hardware.LinuxEcController();
            if (ec.HasHwmonPwmDutyAccess)
            {
                // Single pwm1 on many OMEN boards drives both fans together.
                ec.SetHwmonPwmDutyPercent(clamped);
                return;
            }

            await TryEnableManualFanOverrideAsync();

            try
            {
                if (File.Exists(HP_WMI_FAN2_OUTPUT))
                {
                    await File.WriteAllTextAsync(HP_WMI_FAN2_OUTPUT, clamped.ToString());
                    return;
                }

                var fanTargetPath = ResolveHwmonFanTargetPath(2);
                if (fanTargetPath != null)
                {
                    await File.WriteAllTextAsync(fanTargetPath, clamped.ToString());
                    return;
                }
            }
            catch
            {
                // Fall through to profile-based fallback.
            }

            var mode = clamped switch
            {
                <= 35 => PerformanceMode.Cool,
                <= 70 => PerformanceMode.Balanced,
                _ => PerformanceMode.Performance
            };

            await ApplyFanFallbackProfileAsync(mode);
        });
    }

    private async Task ApplyFanFallbackProfileAsync(PerformanceMode mode)
    {
        if (_lastFanFallbackMode == mode)
            return;

        try
        {
            await SetPerformanceModeCoreAsync(mode);
            _lastFanFallbackMode = mode;
        }
        catch
        {
            // No profile interface available on this model/kernel. Keep best-effort behavior.
        }
    }

    public async Task<string> GetGpuModeAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "hybrid";

        var gpuVendors = await ReadDrmGpuVendorsAsync();
        var hasDiscrete = gpuVendors.Any(v => IsDiscreteGpuVendor(v.vendorId));
        var hasIntegrated = gpuVendors.Any(v => IsIntegratedGpuVendor(v.vendorId));

        if (hasDiscrete && hasIntegrated)
        {
            return "hybrid";
        }

        return hasDiscrete ? "discrete" : "integrated";
    }

    public async Task SetGpuModeAsync(string mode)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await Task.CompletedTask;
        throw new NotSupportedException("GPU mode switching is distro-specific and is not invoked through external tools by OmenCore. Use BIOS or your distro's GPU profile manager.");
    }

    public async Task SetKeyboardBrightnessAsync(int brightness)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(() =>
        {
            var ctrl = new OmenCore.Linux.Hardware.LinuxKeyboardController();
            if (!ctrl.SupportsBrightnessControl)
                throw new NotSupportedException("Keyboard brightness control is not available on this kernel/board.");
            if (!ctrl.SetBrightness(brightness))
                throw new InvalidOperationException("Failed to set keyboard brightness.");
            return Task.CompletedTask;
        });
    }

    public async Task SetKeyboardColorAsync(byte r, byte g, byte b)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(() =>
        {
            // Manual color selection cancels any running animation, otherwise
            // the next frame would immediately overwrite the user's color.
            _animationEngine?.Stop(restoreBaseColors: false);

            var ctrl = _keyboardController.Value;
            if (!ctrl.IsAvailable)
                throw new NotSupportedException("Keyboard RGB interface is not available on this kernel/board.");
            if (!ctrl.SetAllZonesColor(r, g, b))
                throw new InvalidOperationException("Failed to set keyboard color.");
            return Task.CompletedTask;
        });
    }

    public async Task SetKeyboardZoneColorAsync(int zone, byte r, byte g, byte b)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(() =>
        {
            // Manual color selection cancels any running animation, otherwise
            // the next frame would immediately overwrite the user's color.
            _animationEngine?.Stop(restoreBaseColors: false);

            var ctrl = _keyboardController.Value;
            if (!ctrl.IsAvailable)
                throw new NotSupportedException("Keyboard RGB interface is not available on this kernel/board.");
            if (!ctrl.SetZoneColor(zone, r, g, b))
                throw new InvalidOperationException($"Failed to set keyboard zone {zone} color.");
            return Task.CompletedTask;
        });
    }

    public async Task<int> GetKeyboardAnimationModeAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return 0;

        return await ExecuteWithIoLockAsync(() =>
        {
            // Software engine state takes precedence; the firmware node has
            // no reliable readback for animations.
            var mode = _animationEngine?.ActiveEffect switch
            {
                OmenCore.Linux.Hardware.KeyboardAnimationEffect.Breathing => 1,
                OmenCore.Linux.Hardware.KeyboardAnimationEffect.Wave => 2,
                OmenCore.Linux.Hardware.KeyboardAnimationEffect.Spectrum => 3,
                _ => 0,
            };
            return Task.FromResult(mode);
        });
    }

    public async Task SetKeyboardAnimationModeAsync(int mode)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        await ExecuteWithIoLockAsync(() =>
        {
            var ctrl = _keyboardController.Value;
            if (!ctrl.HasFourZoneControl)
                throw new NotSupportedException("Keyboard animation requires the fourzone_color interface, which is not available on this kernel/board.");

            switch (Math.Clamp(mode, 0, 255))
            {
                case 0: // Static — stop animating, restore pre-animation colors
                    _animationEngine?.Stop(restoreBaseColors: true);
                    break;
                case 1:
                    StartEffect(OmenCore.Linux.Hardware.KeyboardAnimationEffect.Breathing);
                    break;
                case 2:
                    StartEffect(OmenCore.Linux.Hardware.KeyboardAnimationEffect.Wave);
                    break;
                case 3:
                    StartEffect(OmenCore.Linux.Hardware.KeyboardAnimationEffect.Spectrum);
                    break;
                case 255: // Off — stop animating and blank the keyboard
                    _animationEngine?.Stop(restoreBaseColors: false);
                    if (!ctrl.SetAllZonesColor(0, 0, 0))
                        throw new InvalidOperationException("Failed to turn keyboard lighting off.");
                    break;
                default:
                    throw new NotSupportedException($"Unknown keyboard animation mode {mode}.");
            }

            return Task.CompletedTask;
        });
    }

    private void StartEffect(OmenCore.Linux.Hardware.KeyboardAnimationEffect effect)
    {
        if (!GetAnimationEngine().Start(effect))
            throw new InvalidOperationException($"Failed to start {effect} keyboard animation.");
    }

    #region Private Helpers

    private async Task ExecuteWithIoLockAsync(Func<Task> operation)
    {
        await _ioLock.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<T> ExecuteWithIoLockAsync<T>(Func<Task<T>> operation)
    {
        await _ioLock.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static async Task TryEnableManualFanOverrideAsync()
    {
        try
        {
            if (File.Exists(HP_WMI_FAN_ALWAYS_ON))
            {
                await File.WriteAllTextAsync(HP_WMI_FAN_ALWAYS_ON, "1");
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static async Task<string?> ReadFirstExistingTextAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    return (await File.ReadAllTextAsync(path)).Trim();
                }
            }
            catch
            {
                // Ignore and continue to next candidate.
            }
        }

        return null;
    }

    private static string? ResolveHwmonFanTargetPath(int fanIndex)
    {
        try
        {
            foreach (var hwmonDir in LinuxSysfsPathMap.EnumerateHpWmiHwmonDirectories())
            {
                var targetPath = Path.Combine(hwmonDir, $"fan{fanIndex}_target");
                if (File.Exists(targetPath))
                    return targetPath;
            }
        }
        catch
        {
            // Best-effort resolution.
        }

        return null;
    }

    private async Task<int> ReadGpuTemperatureAsync()
    {
        var reading = LinuxTelemetryResolver.GetGpuTemperature(_ec.Value, _hwmon.Value);
        if (reading?.Temperature is > 0 and var celsius)
            return celsius * 1000;

        var nvidiaSmiTemp = await ReadNvidiaSmiTemperatureMillidegreesAsync();
        if (nvidiaSmiTemp > 0)
            return nvidiaSmiTemp;

        var daemonTemp = ReadDaemonSnapshotGpuTemperatureC();
        if (daemonTemp > 0)
            return daemonTemp * 1000;

        return 0;
    }

    private static int ReadDaemonSnapshotGpuTemperatureC()
    {
        try
        {
            var snapshot = ReliabilityDiagnosticsStore.ReadSnapshot();
            if (snapshot?.GpuTempC is not > 0)
                return 0;

            var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - snapshot.UpdatedAtUnix;
            if (ageSeconds is < 0 or > 90)
                return 0;

            return snapshot.GpuTempC;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<int> ReadNvidiaSmiTemperatureMillidegreesAsync()
    {
        var nvidiaSmi = ResolveNvidiaSmiPath();
        if (nvidiaSmi == null)
            return 0;

        // Never wake or hold an idle dGPU awake just to poll temperature — that would keep it
        // powered on a hybrid laptop. EC/hwmon and the daemon snapshot cover the idle case;
        // nvidia-smi is only used once the GPU is known to be busy (see AllowNvidiaProbe).
        if (!AllowNvidiaProbe())
            return 0;

        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = nvidiaSmi,
                    Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return 0;

            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (line == null)
                return 0;

            if (double.TryParse(line, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var celsius)
                && celsius is > 0 and < 150)
            {
                return (int)Math.Round(celsius * 1000.0);
            }
        }
        catch
        {
            // Fall through to other sources.
        }

        return 0;
    }

    private static string? ResolveNvidiaSmiPath() =>
        new[] { "/usr/bin/nvidia-smi", "/usr/local/bin/nvidia-smi" }.FirstOrDefault(File.Exists);

    public async Task<GpuPowerInfo?> GetGpuPowerAsync()
    {
        var nvidiaSmi = ResolveNvidiaSmiPath();
        if (nvidiaSmi == null)
            return null;

        // Do NOT wake or hold an idle dGPU awake just to read power — that would keep the GPU
        // powered and defeat Optimus battery saving. The governor returns false while the GPU
        // is suspended or in an idle cooldown; in that case report it as suspended (hides the
        // live power card) and skip nvidia-smi entirely.
        if (!AllowNvidiaProbe())
        {
            return new GpuPowerInfo(Suspended: true, 0, 0, 0, 0, DynamicBoostActive: false);
        }

        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = nvidiaSmi,
                    // enforced.power.limit is the authoritative current ceiling; power.limit
                    // can report N/A on laptop GPUs, so we don't rely on it.
                    Arguments = "--query-gpu=power.draw,enforced.power.limit,power.default_limit,power.max_limit --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return null;

            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
                return null;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
                return null;

            static double ParseWatts(string s) =>
                double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

            var draw = ParseWatts(parts[0]);
            var limit = ParseWatts(parts[1]);
            var defaultLimit = ParseWatts(parts[2]);
            var maxLimit = ParseWatts(parts[3]);

            if (double.IsNaN(limit) || double.IsNaN(defaultLimit) || double.IsNaN(maxLimit))
                return null;
            if (double.IsNaN(draw))
                draw = 0;

            // Dynamic Boost is doing something whenever the enforced ceiling is above base TGP.
            var boostActive = limit > defaultLimit + 0.5;
            return new GpuPowerInfo(Suspended: false, draw, limit, defaultLimit, maxLimit, boostActive);
        }
        catch
        {
            return null;
        }
    }

    // --- NVIDIA dGPU probe governor (RTD3 power-saving safe) --------------------------------
    // nvidia-smi wakes the dGPU. Polling it on a fixed timer resets the RTD3 idle timer every
    // cycle and would keep the GPU powered forever — including the case where OmenCore starts
    // while the GPU is still awake (right after boot, or seconds after a game closes, before it
    // has had a chance to suspend). This governor only allows probes when the GPU is genuinely
    // busy (a real client holds it), plus a single utilization probe on startup. Whenever the GPU
    // is idle it stops probing for a cooldown window so the driver can runtime-suspend it, and
    // once suspended it keeps quiet until a real client wakes the GPU again.
    private enum NvProbeState { Startup, Cooldown, Busy, Asleep }

    private static readonly object _nvProbeLock = new();
    private static NvProbeState _nvState = NvProbeState.Startup;
    private static DateTime _nvCooldownUntilUtc = DateTime.MinValue;
    private static DateTime _nvLastBusyWorkUtc = DateTime.MinValue;

    // Cooldown must exceed the driver's RTD3 autosuspend delay so an idle GPU actually powers
    // down during the window instead of being kept awake by our next probe.
    private static readonly TimeSpan NvIdleCooldown = TimeSpan.FromSeconds(20);
    // How long the GPU may sit at ~0% (while active) before we back off to re-test for suspend.
    private static readonly TimeSpan NvBusyIdleGrace = TimeSpan.FromSeconds(10);
    private const double NvBusyUtilThreshold = 1.0; // percent

    /// <summary>
    /// Decides whether nvidia-smi may run this cycle without defeating Optimus/RTD3 power
    /// saving. Returns true only when the dGPU is genuinely busy, or for a single utilization
    /// probe on startup. While the GPU is idle it returns false for a cooldown window so the
    /// driver can runtime-suspend it; once suspended it keeps returning false until a real
    /// client wakes the GPU again.
    /// </summary>
    private static bool AllowNvidiaProbe(bool allowStartupProbe = false)
    {
        // No NVIDIA dGPU with runtime PM (e.g. desktop / single-GPU) — polling is harmless.
        if (!TryGetNvidiaRuntimeStatus(out var status))
            return true;

        var active = string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        lock (_nvProbeLock)
        {
            if (!active)
            {
                // Suspended (or transitioning): never probe. The next active edge that we see
                // must therefore be caused by a real client, since we didn't wake it.
                _nvState = NvProbeState.Asleep;
                return false;
            }

            switch (_nvState)
            {
                case NvProbeState.Asleep:
                    // Active again after we stayed quiet — a real GPU client woke it. Fast-poll.
                    _nvState = NvProbeState.Busy;
                    _nvLastBusyWorkUtc = now;
                    return true;

                case NvProbeState.Startup:
                    // GPU already awake when OmenCore launched; we can't yet tell whether a real
                    // client holds it. Only the utilization path may spend this ambiguous probe,
                    // because its result feeds RecordNvidiaUtilization. Auxiliary probes would
                    // otherwise consume the startup probe and leave the governor blind.
                    if (!allowStartupProbe)
                        return false;
                    _nvState = NvProbeState.Cooldown;
                    _nvCooldownUntilUtc = now + NvIdleCooldown;
                    return true;

                case NvProbeState.Cooldown:
                    // Hold off so an idle GPU can auto-suspend. If it's still active after the
                    // whole window (and we never touched it), a real client must be holding it —
                    // promote to Busy and resume fast polling.
                    if (now < _nvCooldownUntilUtc)
                        return false;
                    _nvState = NvProbeState.Busy;
                    _nvLastBusyWorkUtc = now;
                    return true;

                case NvProbeState.Busy:
                default:
                    // A real client is (was) holding the GPU, so probing is harmless — it stays
                    // active regardless of us. But if it goes idle for a while (e.g. the game
                    // exited), back off to re-test, otherwise our own polling would keep it awake.
                    if (now - _nvLastBusyWorkUtc >= NvBusyIdleGrace)
                    {
                        _nvState = NvProbeState.Cooldown;
                        _nvCooldownUntilUtc = now + NvIdleCooldown;
                        return false;
                    }
                    return true;
            }
        }
    }

    /// <summary>
    /// Feeds observed GPU utilization back into the probe governor so it can tell a genuinely
    /// busy GPU (keep fast-polling) from one that only looks awake because we just probed it
    /// (back off so it can suspend). Call this only after a successful nvidia-smi utilization read.
    /// </summary>
    private static void RecordNvidiaUtilization(double utilPercent)
    {
        if (utilPercent < NvBusyUtilThreshold)
            return;
        lock (_nvProbeLock)
        {
            _nvLastBusyWorkUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Reads the NVIDIA dGPU's PCI runtime power state from sysfs ("active"/"suspended"/…)
    /// without touching the driver. Returns false when no NVIDIA card is present.
    /// </summary>
    private static bool TryGetNvidiaRuntimeStatus(out string status)
    {
        status = string.Empty;
        foreach (var card in SafeEnumerateDirectories("/sys/class/drm"))
        {
            var cardName = Path.GetFileName(card);
            if (string.IsNullOrWhiteSpace(cardName) ||
                !cardName.StartsWith("card", StringComparison.Ordinal) ||
                cardName.Contains('-', StringComparison.Ordinal))
            {
                continue;
            }

            var devicePath = Path.Combine(card, "device");
            var vendorPath = Path.Combine(devicePath, "vendor");
            if (!File.Exists(vendorPath))
                continue;

            try
            {
                var vendor = File.ReadAllText(vendorPath).Trim();
                if (!string.Equals(vendor, "0x10de", StringComparison.OrdinalIgnoreCase))
                    continue;

                var statusPath = Path.Combine(devicePath, "power", "runtime_status");
                if (!File.Exists(statusPath))
                    return false;

                status = File.ReadAllText(statusPath).Trim();
                return true;
            }
            catch
            {
                // Keep scanning other cards.
            }
        }

        return false;
    }

    private static async Task<double> ReadGpuUsageAsync()
    {
        // NVIDIA: query via nvidia-smi — but only when the governor says the dGPU is genuinely
        // busy. This path is allowed to spend the startup probe because the utilization result
        // feeds the governor; auxiliary power/temp reads must not consume it first.
        var nvidiaSmi = ResolveNvidiaSmiPath();
        if (nvidiaSmi != null && AllowNvidiaProbe(allowStartupProbe: true))
        {
            try
            {
                using var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = nvidiaSmi,
                        Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 &&
                    double.TryParse(stdout.Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    RecordNvidiaUtilization(pct);
                    return pct;
                }
            }
            catch { }
        }

        // AMD / Intel: gpu_busy_percent in DRM sysfs
        foreach (var card in SafeEnumerateDirectories("/sys/class/drm"))
        {
            var busyPath = Path.Combine(card, "device", "gpu_busy_percent");
            if (!File.Exists(busyPath)) continue;
            try
            {
                var raw = await File.ReadAllTextAsync(busyPath);
                if (double.TryParse(raw.Trim(), out var busy))
                    return busy;
            }
            catch { }
        }

        return 0;
    }

    private static async Task<int> ReadFanRpmAsync(string type)
    {
        // fan1 = CPU, fan2 = GPU on hp-wmi (and the common convention elsewhere).
        var fanIndex = type.Equals("gpu", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

        try
        {
            // Prefer the hp-wmi hwmon device — its readings are authoritative
            // and 0 RPM is a real value there (fan-stop), not a missing sensor.
            foreach (var hwmonDir in LinuxSysfsPathMap.EnumerateHpWmiHwmonDirectories())
            {
                var rpm = await TryReadRpmAsync(Path.Combine(hwmonDir, $"fan{fanIndex}_input"));
                if (rpm >= 0)
                    return rpm;
            }

            // Fallback: any hwmon that exposes the requested fan index.
            // Note: pwm* files are 0-255 duty cycle, never valid as RPM.
            if (Directory.Exists(HWMON_BASE))
            {
                foreach (var hwmon in Directory.GetDirectories(HWMON_BASE))
                {
                    var rpm = await TryReadRpmAsync(Path.Combine(hwmon, $"fan{fanIndex}_input"));
                    if (rpm > 0)
                        return rpm;
                }
            }
        }
        catch { }
        return 0;
    }

    private static async Task<int> TryReadRpmAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return -1;
            var raw = await File.ReadAllTextAsync(path);
            return int.TryParse(raw.Trim(), out var rpm) && rpm >= 0 ? rpm : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Reads the current fan duty cycle from the hp-wmi pwm1 node (0-255) as a
    /// percentage. The driver reports the duty derived from live RPM, so this is
    /// valid in auto, manual and max modes. Returns -1 when unavailable.
    /// </summary>
    private static async Task<int> ReadFanDutyPercentAsync()
    {
        try
        {
            foreach (var hwmonDir in LinuxSysfsPathMap.EnumerateHpWmiHwmonDirectories())
            {
                var pwmPath = Path.Combine(hwmonDir, "pwm1");
                if (!File.Exists(pwmPath))
                    continue;
                var raw = await File.ReadAllTextAsync(pwmPath);
                if (int.TryParse(raw.Trim(), out var duty) && duty >= 0)
                    return Math.Clamp((int)Math.Round(duty * 100.0 / 255.0), 0, 100);
            }
        }
        catch { }
        return -1;
    }

    private static async Task<double> ReadCpuUsageAsync()
    {
        try
        {
            var stat1 = await File.ReadAllLinesAsync("/proc/stat");
            await Task.Delay(100);
            var stat2 = await File.ReadAllLinesAsync("/proc/stat");

            var cpu1 = ParseCpuLine(stat1[0]);
            var cpu2 = ParseCpuLine(stat2[0]);

            var total1 = cpu1.Sum();
            var total2 = cpu2.Sum();
            var idle1 = cpu1[3];
            var idle2 = cpu2[3];

            var totalDiff = total2 - total1;
            var idleDiff = idle2 - idle1;

            return totalDiff > 0 ? (1.0 - (double)idleDiff / totalDiff) * 100 : 0;
        }
        catch { }
        return 0;
    }

    private static long[] ParseCpuLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Skip(1).Select(long.Parse).ToArray();
    }

    private static async Task<(double percentage, double usedGb, double totalGb)> ReadMemoryUsageAsync()
    {
        try
        {
            var meminfo = await File.ReadAllLinesAsync("/proc/meminfo");
            long total = 0, available = 0;

            foreach (var line in meminfo)
            {
                if (line.StartsWith("MemTotal:"))
                    total = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                else if (line.StartsWith("MemAvailable:"))
                    available = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            }

            if (total > 0)
            {
                var totalGb = total / 1024.0 / 1024.0; // Convert KB to GB
                var usedGb = (total - available) / 1024.0 / 1024.0;
                var percentage = (1.0 - (double)available / total) * 100;
                return (percentage, usedGb, totalGb);
            }
        }
        catch { }
        return (0, 0, 0);
    }

    private static async Task<(int percentage, bool onBattery)> ReadBatteryStatusAsync()
    {
        try
        {
            var batteries = Directory.GetDirectories(POWER_SUPPLY);
            foreach (var battery in batteries)
            {
                var type = await File.ReadAllTextAsync(Path.Combine(battery, "type"));
                if (type.Trim() == "Battery")
                {
                    var capacity = await File.ReadAllTextAsync(Path.Combine(battery, "capacity"));
                    var status = await File.ReadAllTextAsync(Path.Combine(battery, "status"));
                    var onBattery = status.Trim() == "Discharging";
                    return (int.Parse(capacity.Trim()), onBattery);
                }
            }
        }
        catch { }
        return (100, false);
    }

    private async Task<double> ReadPowerConsumptionAsync()
    {
        // Intel RAPL: delta between successive calls — no sleep needed since we poll every ~2.5s
        const string raplPath = "/sys/class/powercap/intel-rapl/intel-rapl:0/energy_uj";
        try
        {
            if (File.Exists(raplPath))
            {
                var raw = await File.ReadAllTextAsync(raplPath);
                if (long.TryParse(raw.Trim(), out var energyUj))
                {
                    var now = DateTime.UtcNow;
                    if (_raplLastReadTime != DateTime.MinValue && energyUj >= _raplLastEnergyUj)
                    {
                        var elapsedUs = (now - _raplLastReadTime).TotalSeconds * 1_000_000.0;
                        if (elapsedUs > 0)
                        {
                            var watts = Math.Round((energyUj - _raplLastEnergyUj) / elapsedUs, 1);
                            _raplLastEnergyUj = energyUj;
                            _raplLastReadTime = now;
                            return watts;
                        }
                    }
                    _raplLastEnergyUj = energyUj;
                    _raplLastReadTime = now;
                }
            }
        }
        catch { }

        // Fallback: battery power_now (µW → W) when on battery
        try
        {
            foreach (var bat in Directory.GetDirectories(POWER_SUPPLY))
            {
                var typeFile = Path.Combine(bat, "type");
                var powerFile = Path.Combine(bat, "power_now");
                if (!File.Exists(typeFile) || !File.Exists(powerFile)) continue;
                var type = (await File.ReadAllTextAsync(typeFile)).Trim();
                if (type != "Battery") continue;
                var powerRaw = (await File.ReadAllTextAsync(powerFile)).Trim();
                if (long.TryParse(powerRaw, out var uw))
                    return Math.Round(uw / 1_000_000.0, 1);
            }
        }
        catch { }
        return 0;
    }

    private static async Task<bool> HasDiscreteGpuAsync()
    {
        var gpuVendors = await ReadDrmGpuVendorsAsync();
        return gpuVendors.Any(v => IsDiscreteGpuVendor(v.vendorId));
    }

    private static async Task<string?> ReadDmiStringAsync(string field)
    {
        var path = $"/sys/class/dmi/id/{field}";
        try
        {
            if (File.Exists(path))
                return (await File.ReadAllTextAsync(path)).Trim();
        }
        catch { }
        return null;
    }

    private static async Task<string> ReadCpuNameAsync()
    {
        try
        {
            var cpuinfo = await File.ReadAllLinesAsync("/proc/cpuinfo");
            var modelLine = cpuinfo.FirstOrDefault(l => l.StartsWith("model name"));
            if (modelLine != null)
            {
                return modelLine.Split(':')[1].Trim();
            }
        }
        catch { }
        return "Unknown CPU";
    }

    private static async Task<string> ReadGpuNameAsync()
    {
        var gpuVendors = await ReadDrmGpuVendorsAsync();
        var selected = gpuVendors.FirstOrDefault(v => IsDiscreteGpuVendor(v.vendorId));
        if (string.IsNullOrWhiteSpace(selected.vendorId))
        {
            selected = gpuVendors.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(selected.vendorId))
        {
            return FormatGpuName(selected.vendorId, selected.deviceId);
        }

        return "Unknown GPU";
    }

    private static async Task<IReadOnlyList<(string vendorId, string deviceId)>> ReadDrmGpuVendorsAsync()
    {
        var result = new List<(string vendorId, string deviceId)>();
        const string drmPath = "/sys/class/drm";

        foreach (var card in SafeEnumerateDirectories(drmPath))
        {
            var cardName = Path.GetFileName(card);
            if (string.IsNullOrWhiteSpace(cardName) ||
                !cardName.StartsWith("card", StringComparison.Ordinal) ||
                cardName.Contains("-", StringComparison.Ordinal))
            {
                continue;
            }

            var devicePath = Path.Combine(card, "device");
            var vendorPath = Path.Combine(devicePath, "vendor");
            if (!File.Exists(vendorPath))
            {
                continue;
            }

            try
            {
                var vendor = (await File.ReadAllTextAsync(vendorPath)).Trim().ToLowerInvariant();
                var deviceFile = Path.Combine(devicePath, "device");
                var device = File.Exists(deviceFile)
                    ? (await File.ReadAllTextAsync(deviceFile)).Trim().ToLowerInvariant()
                    : string.Empty;

                result.Add((vendor, device));
            }
            catch
            {
                // Best-effort sysfs probing.
            }
        }

        return result;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).ToArray()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsDiscreteGpuVendor(string vendorId)
    {
        return string.Equals(vendorId, "0x10de", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(vendorId, "0x1002", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIntegratedGpuVendor(string vendorId)
    {
        return string.Equals(vendorId, "0x8086", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatGpuName(string vendorId, string deviceId)
    {
        var suffix = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : $" ({deviceId})";
        return vendorId.ToLowerInvariant() switch
        {
            "0x10de" => $"NVIDIA GPU{suffix}",
            "0x1002" => $"AMD Radeon GPU{suffix}",
            "0x8086" => $"Intel Integrated Graphics{suffix}",
            _ => $"GPU {vendorId}{suffix}"
        };
    }

    #endregion

    #region Longevity / Battery Care

    public async Task<(double healthPercent, int cycleCount)> GetBatteryHealthAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return (100.0, 0);

        double health = 100.0;
        int cycles = 0;
        try
        {
            var batteries = Directory.GetDirectories(POWER_SUPPLY);
            foreach (var bat in batteries)
            {
                var typePath = Path.Combine(bat, "type");
                if (!File.Exists(typePath)) continue;
                if ((await File.ReadAllTextAsync(typePath)).Trim() != "Battery") continue;

                var fullDesignPath = Path.Combine(bat, "energy_full_design");
                var fullPath = Path.Combine(bat, "energy_full");
                var cyclePath = Path.Combine(bat, "cycle_count");

                if (File.Exists(fullDesignPath) && File.Exists(fullPath))
                {
                    if (long.TryParse((await File.ReadAllTextAsync(fullDesignPath)).Trim(), out var design) &&
                        long.TryParse((await File.ReadAllTextAsync(fullPath)).Trim(), out var full) &&
                        design > 0)
                    {
                        health = Math.Round(100.0 * full / design, 1);
                    }
                }
                if (File.Exists(cyclePath) &&
                    int.TryParse((await File.ReadAllTextAsync(cyclePath)).Trim(), out var c))
                {
                    cycles = c;
                }
                break;
            }
        }
        catch { }
        return (health, cycles);
    }

    public async Task<int?> GetCpuPowerLimitAsync()
    {
        if (!File.Exists(RaplPl1Path)) return null;
        try
        {
            var raw = await File.ReadAllTextAsync(RaplPl1Path);
            if (long.TryParse(raw.Trim(), out var uw))
                return (int)(uw / 1_000_000);
        }
        catch { }
        return null;
    }

    public async Task SetCpuPowerLimitAsync(int watts)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        var uw = (long)watts * 1_000_000;
        await WriteSysfsAsync(RaplPl1Path, uw.ToString());
    }

    private static async Task<string?> FindChargeThresholdPathAsync()
    {
        if (!Directory.Exists(POWER_SUPPLY)) return null;
        foreach (var bat in Directory.GetDirectories(POWER_SUPPLY))
        {
            var typePath = Path.Combine(bat, "type");
            var threshPath = Path.Combine(bat, "charge_control_end_threshold");
            if (!File.Exists(typePath) || !File.Exists(threshPath)) continue;
            try
            {
                if ((await File.ReadAllTextAsync(typePath)).Trim() == "Battery")
                    return threshPath;
            }
            catch { }
        }
        return null;
    }

    public async Task<int> GetChargeEndThresholdAsync()
    {
        var path = await FindChargeThresholdPathAsync();
        if (path == null) return 0;
        try
        {
            var raw = await File.ReadAllTextAsync(path);
            if (int.TryParse(raw.Trim(), out var pct))
                return pct;
        }
        catch { }
        return 0;
    }

    public async Task SetChargeEndThresholdAsync(int percent)
    {
        var path = await FindChargeThresholdPathAsync();
        if (path == null)
            throw new NotSupportedException("Battery charge limit not available via sysfs on this hardware. Set it in BIOS: F10 → Advanced → Battery Care Mode.");
        await WriteSysfsAsync(path, percent.ToString());
    }

    private static async Task WriteSysfsAsync(string path, string value)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            await fs.WriteAsync(bytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"Cannot write to {path}. Run OmenCore as root or ensure the udev rule is applied.", ex);
        }
    }

    #endregion

    #region NVIDIA Settings

    private static readonly string[] NvidiaSettingsCandidates =
    {
        "/usr/bin/nvidia-settings",
        "/usr/local/bin/nvidia-settings"
    };

    private static string? ResolveNvidiaSettingsPath()
    {
        foreach (var candidate in NvidiaSettingsCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _pollingTimer.Stop();
            _pollingTimer.Dispose();
            _animationEngine?.Dispose();
            _ioLock.Dispose();
            _disposed = true;
        }
    }
}
