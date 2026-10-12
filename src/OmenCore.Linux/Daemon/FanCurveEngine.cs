using OmenCore.Linux.Config;
using OmenCore.Linux.Hardware;

namespace OmenCore.Linux.Daemon;

/// <summary>
/// Fan curve engine that automatically adjusts fan speed based on temperature.
/// 
/// Features:
/// - Configurable temperature/speed curve points
/// - Hysteresis to prevent fan speed oscillation
/// - Ramp-up/ramp-down delays to ignore brief temperature spikes
/// - Smooth transitions between speed levels
/// - Both CPU and GPU temperature monitoring
/// - Battery-aware profiles (quieter on battery power)
/// </summary>
public class FanCurveEngine : IDisposable
{
    private readonly LinuxEcController _ec;
    private readonly LinuxHwMonController _hwmon;
    private readonly OmenCoreConfig _config;
    private readonly CancellationTokenSource _cts = new();
    
    private const int CriticalTempC = 90;

    private int _lastTargetSpeed = -1;
    private bool _hasEffectiveTemp;
    private int _effectiveTemp;
    private int _pendingDirection;
    private DateTime _pendingSinceUtc = DateTime.MinValue;
    private bool _isRunning;
    private bool _lastBatteryState;
    private IReadOnlyList<FanCurvePoint>? _sortedCurvePoints;
    private string? _curveCacheKey;
    
    public event Action<string>? OnLog;
    public event Action<int, int, int>? OnSpeedChange; // (temp, targetSpeed, actualSpeed)
    public event Action<bool>? OnBatteryStateChange;
    
    // Battery awareness settings
    public bool BatteryAwareEnabled { get; set; } = true;
    public int BatterySpeedReduction { get; set; } = 20; // Reduce speed by this % on battery
    
    public FanCurveEngine(LinuxEcController ec, LinuxHwMonController hwmon, OmenCoreConfig config)
    {
        _ec = ec;
        _hwmon = hwmon;
        _config = config;
    }
    
    /// <summary>
    /// Start the fan curve engine.
    /// </summary>
    public Task StartAsync()
    {
        if (_isRunning) return Task.CompletedTask;
        _isRunning = true;
        
        return Task.Run(async () =>
        {
            Log("Fan curve engine started");
            
            // Disable BIOS fan control for custom curves
            if (_config.Fan.Curve.Enabled)
            {
                if (_ec.HasHwmonPwmDutyAccess)
                {
                    _ec.SetHwmonPwmEnable(1);
                    Log("hp-wmi hwmon manual PWM mode enabled");
                }
                else
                {
                    _ec.SetFanState(biosControl: false);
                    Log("BIOS fan control disabled");
                }
            }
            
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await ProcessFanCurveAsync();
                }
                catch (Exception ex)
                {
                    Log($"Error in fan curve loop: {ex.Message}");
                }
                
                await Task.Delay(_config.General.PollIntervalMs, _cts.Token);
            }
        }, _cts.Token);
    }
    
    /// <summary>
    /// Stop the fan curve engine.
    /// </summary>
    /// <summary>
    /// Reset ramp/hysteresis state after the daemon reloads curve points from disk.
    /// Without this, smooth-transition state from the previous curve can override a
    /// gentler curve applied by the GUI until the daemon is restarted.
    /// </summary>
    public void ReloadFromConfig()
    {
        _lastTargetSpeed = -1;
        _hasEffectiveTemp = false;
        _pendingDirection = 0;
        _pendingSinceUtc = DateTime.MinValue;
        _curveCacheKey = null;
        _sortedCurvePoints = null;
        Log("Curve state reset after config reload");
    }

    public void Stop(bool restoreAuto = true)
    {
        if (!_isRunning) return;
        
        _cts.Cancel();
        _isRunning = false;
        
        // Restore BIOS fan control
        if (restoreAuto && _config.Startup.RestoreOnExit)
        {
            _ec.RestoreAutoMode();
            Log("BIOS fan control restored");
        }
        
        Log("Fan curve engine stopped");
    }
    
    private async Task ProcessFanCurveAsync()
    {
        // Check battery state for battery-aware mode
        if (BatteryAwareEnabled)
        {
            var isOnBattery = await IsOnBatteryAsync();
            if (isOnBattery != _lastBatteryState)
            {
                _lastBatteryState = isOnBattery;
                Log($"Power source changed: {(isOnBattery ? "Battery" : "AC Adapter")}");
                OnBatteryStateChange?.Invoke(isOnBattery);
            }
        }
        
        // Get temperatures from multiple sources
        var cpuTemp = _ec.GetCpuTemperature() ?? _hwmon.GetCpuTemperature() ?? 0;
        var gpuTemp = _ec.GetGpuTemperature() ?? _hwmon.GetGpuTemperature() ?? 0;
        
        // Use the higher of CPU/GPU temps for fan control
        var maxTemp = Math.Max(cpuTemp, gpuTemp);
        
        // The curve is evaluated at an "effective" temperature that only follows the
        // real reading once it has moved by at least the hysteresis, in the same
        // direction, for the ramp delay. The trend is judged on the live temperature
        // each poll, so jitter in the resulting fan target cannot keep restarting
        // the timer (which used to leave the fans stuck high after a load spike).
        UpdateEffectiveTemp(maxTemp);

        var curveTarget = CalculateSpeedFromCurve(_effectiveTemp);
        
        // Apply battery-aware reduction (but keep minimum if temp is critical)
        if (BatteryAwareEnabled && _lastBatteryState && maxTemp < 85)
        {
            curveTarget = Math.Max(20, curveTarget - BatterySpeedReduction);
        }

        var targetSpeed = curveTarget;
        
        // Apply smooth transition if enabled
        if (_config.Fan.SmoothTransition && _lastTargetSpeed >= 0)
        {
            targetSpeed = SmoothTransition(_lastTargetSpeed, targetSpeed);
        }
        
        // Only apply if changed
        if (targetSpeed != _lastTargetSpeed)
        {
            var batteryInfo = BatteryAwareEnabled && _lastBatteryState ? " [Battery]" : "";
            Log($"Temp: {maxTemp}°C (CPU: {cpuTemp}°C, GPU: {gpuTemp}°C) -> Fan: {targetSpeed}%{batteryInfo}");
            
            var success = _ec.SetFanSpeedPercent(targetSpeed);
            if (success)
            {
                _lastTargetSpeed = targetSpeed;
                OnSpeedChange?.Invoke(maxTemp, targetSpeed, targetSpeed);
            }
            else
            {
                Log($"Failed to set fan speed to {targetSpeed}%");
            }
        }
        
        await Task.CompletedTask;
    }
    
    private void UpdateEffectiveTemp(int maxTemp)
    {
        if (!_hasEffectiveTemp)
        {
            _effectiveTemp = maxTemp;
            _hasEffectiveTemp = true;
            _pendingDirection = 0;
            return;
        }

        var hysteresis = Math.Max(1, _config.Fan.Curve.Hysteresis);
        var delta = maxTemp - _effectiveTemp;
        var direction = delta >= hysteresis ? 1 : delta <= -hysteresis ? -1 : 0;

        if (direction == 0)
        {
            _pendingDirection = 0;
            return;
        }

        var now = DateTime.UtcNow;
        if (_pendingDirection != direction)
        {
            _pendingDirection = direction;
            _pendingSinceUtc = now;
        }

        var delaySeconds = direction > 0
            ? _config.Fan.Curve.RampUpDelaySeconds
            : _config.Fan.Curve.RampDownDelaySeconds;

        // Never sit on a delay while the CPU is near its limit.
        if (direction > 0 && maxTemp >= CriticalTempC)
            delaySeconds = 0;

        if ((now - _pendingSinceUtc).TotalSeconds >= delaySeconds)
        {
            _effectiveTemp = maxTemp;
            _pendingDirection = 0;
        }
    }

    /// <summary>
    /// Calculate fan speed from curve based on temperature.
    /// Uses linear interpolation between curve points.
    /// </summary>
    private int CalculateSpeedFromCurve(int temp)
    {
        var points = GetSortedCurvePoints();
        
        if (points.Count == 0)
        {
            return 50; // Default to 50% if no curve defined
        }
        
        // Below minimum temp
        if (temp <= points[0].Temp)
        {
            return Math.Clamp(points[0].Speed, 0, 100);  // Safety: Prevent runaway (GitHub #49)
        }
        
        // Above maximum temp
        if (temp >= points[^1].Temp)
        {
            return Math.Clamp(points[^1].Speed, 0, 100);  // Safety: Prevent runaway (GitHub #49)
        }
        
        // Find the two points to interpolate between
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];
            
            if (temp >= p1.Temp && temp <= p2.Temp)
            {
                // Linear interpolation
                var tempRange = p2.Temp - p1.Temp;
                var speedRange = p2.Speed - p1.Speed;
                var tempOffset = temp - p1.Temp;
                
                var speed = p1.Speed + (speedRange * tempOffset / tempRange);
                return Math.Clamp(speed, 0, 100);  // Safety: Prevent runaway (GitHub #49)
            }
        }
        
        return 50; // Fallback
    }

    private IReadOnlyList<FanCurvePoint> GetSortedCurvePoints()
    {
        var key = string.Join(";", _config.Fan.Curve.Points.Select(p => $"{p.Temp}:{p.Speed}"));
        if (_sortedCurvePoints != null && string.Equals(_curveCacheKey, key, StringComparison.Ordinal))
        {
            return _sortedCurvePoints;
        }

        _curveCacheKey = key;
        _sortedCurvePoints = _config.Fan.Curve.Points
            .OrderBy(p => p.Temp)
            .ToList();

        return _sortedCurvePoints;
    }
    
    /// <summary>
    /// Smooth transition between fan speeds to reduce noise spikes.
    /// </summary>
    private int SmoothTransition(int currentSpeed, int targetSpeed)
    {
        const int maxStep = 10; // Max 10% change per cycle
        
        var diff = targetSpeed - currentSpeed;
        
        if (Math.Abs(diff) <= maxStep)
        {
            return targetSpeed;
        }
        
        return currentSpeed + (diff > 0 ? maxStep : -maxStep);
    }
    
    /// <summary>
    /// Check if the system is running on battery power.
    /// </summary>
    private static async Task<bool> IsOnBatteryAsync()
    {
        // Try multiple AC adapter paths (varies by laptop)
        string[] acPaths = 
        {
            "/sys/class/power_supply/AC0/online",
            "/sys/class/power_supply/AC/online",
            "/sys/class/power_supply/ACAD/online",
            "/sys/class/power_supply/ADP0/online",
            "/sys/class/power_supply/ADP1/online"
        };
        
        foreach (var path in acPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var content = await File.ReadAllTextAsync(path);
                    return content.Trim() == "0";
                }
            }
            catch { }
        }
        
        return false; // Assume plugged in if we can't determine
    }
    
    private void Log(string message)
    {
        OnLog?.Invoke($"[FanCurve] {message}");
    }
    
    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
