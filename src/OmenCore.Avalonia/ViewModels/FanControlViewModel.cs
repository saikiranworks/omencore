using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmenCore.Avalonia.Services;
using OmenCore.Linux.Daemon;
using SavedFanCurvePoint = OmenCore.Linux.Config.SavedFanCurvePoint;
using System.Collections.ObjectModel;

namespace OmenCore.Avalonia.ViewModels;

/// <summary>
/// Fan control ViewModel for custom fan curves.
/// </summary>
public partial class FanControlViewModel : ObservableObject, IDisposable
{
    private readonly IHardwareService _hardwareService;
    private readonly IFanCurveService _fanCurveService;
    private readonly IUserPreferencesService _preferences;
    private bool _disposed;
    private bool _isRestoringPreferences;

    [ObservableProperty]
    private double _cpuTemperature;

    [ObservableProperty]
    private double _gpuTemperature;

    [ObservableProperty]
    private int _cpuFanRpm;

    [ObservableProperty]
    private int _gpuFanRpm;

    [ObservableProperty]
    private int _cpuFanPercent;

    [ObservableProperty]
    private int _gpuFanPercent;

    [ObservableProperty]
    private bool _isCustomCurveEnabled;

    [ObservableProperty]
    private string _selectedPreset = "Balanced";

    [ObservableProperty]
    private string _presetRenameText = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _reliabilityDiagnosticsVisible;

    [ObservableProperty]
    private string _reliabilitySummary = "";

    // Defaults to false — set true only when capabilities confirm direct PWM control
    [ObservableProperty]
    private bool _canEditFanCurve;

    [ObservableProperty]
    private bool _showCapabilityWarning;

    [ObservableProperty]
    private string _capabilityWarningMessage = "";

    [ObservableProperty]
    private bool _hasFanProfileAccess;

    [ObservableProperty]
    private string _activeFanProfile = "auto";

    [ObservableProperty]
    private int _manualFanSpeed = 50;

    [ObservableProperty]
    private bool _usesUnifiedPwm;

    [ObservableProperty]
    private int _curveHysteresis = 3;

    [ObservableProperty]
    private double _curveRampUpDelay = 1.0;

    [ObservableProperty]
    private double _curveRampDownDelay = 3.0;

    public string CurveHysteresisText
    {
        get => CurveHysteresis.ToString();
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (int.TryParse(value.Trim(), out var parsed))
            {
                CurveHysteresis = Math.Clamp(parsed, 1, 10);
            }

            OnPropertyChanged();
        }
    }

    public string CurveRampUpDelayText
    {
        get => CurveRampUpDelay.ToString("0.0");
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                CurveRampUpDelay = Math.Clamp(parsed, 0, 30);
            }

            OnPropertyChanged();
        }
    }

    public string CurveRampDownDelayText
    {
        get => CurveRampDownDelay.ToString("0.0");
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                CurveRampDownDelay = Math.Clamp(parsed, 0, 30);
            }

            OnPropertyChanged();
        }
    }

    // Smart Auto-Switch — temperature-driven profile switching for profile-only boards
    [ObservableProperty]
    private bool _isAutoSwitchEnabled;

    [ObservableProperty]
    private int _autoSwitchHysteresis = 3;

    [ObservableProperty]
    private int _thresholdBalanced = 70;   // °C: switch up to balanced above this

    [ObservableProperty]
    private int _thresholdPerformance = 85; // °C: switch up to performance above this

    private string _autoSwitchCurrentProfile = string.Empty;
    private DateTime _lastReliabilityRefreshUtc = DateTime.MinValue;
    private DateTime _lastCurveApplyUtc = DateTime.MinValue;
    private int _lastAppliedCurveSpeed = -1;
    private int _lastAppliedCurveTemp;
    private int _pendingCurveSpeed = -1;
    private DateTime _pendingCurveSinceUtc = DateTime.MinValue;

    public bool IsManualControlsVisible =>
        CanEditFanCurve && string.Equals(ActiveFanProfile, "manual", StringComparison.OrdinalIgnoreCase);

    public bool IsFixedSpeedVisible => IsManualControlsVisible && !IsCustomCurveEnabled;

    public bool IsCurveEditorVisible => IsManualControlsVisible && IsCustomCurveEnabled;

    public int ManualFanPwm => FanCurvePointViewModel.PercentToPwm(ManualFanSpeed);

    public bool CanDeleteSelectedPreset =>
        !string.IsNullOrWhiteSpace(SelectedPreset) && _fanCurveService.CanDeletePreset(SelectedPreset);

    public bool CanRenameSelectedPreset => CanDeleteSelectedPreset;

    public ObservableCollection<string> Presets { get; } = new();
    public ObservableCollection<FanCurvePointViewModel> CpuFanCurve { get; } = new();
    public ObservableCollection<FanCurvePointViewModel> GpuFanCurve { get; } = new();
    public ObservableCollection<string> ReliabilityLogLines { get; } = new();

    public FanControlViewModel(
        IHardwareService hardwareService,
        IFanCurveService fanCurveService,
        IUserPreferencesService preferences)
    {
        _hardwareService = hardwareService;
        _fanCurveService = fanCurveService;
        _preferences = preferences;
        
        _hardwareService.StatusChanged += OnStatusChanged;
        
        Initialize();
    }

    private void Initialize()
    {
        // Load presets
        foreach (var preset in _fanCurveService.GetPresetNames())
        {
            Presets.Add(preset);
        }

        // Load default curves
        LoadPreset("Balanced");

        _ = InitializeCapabilitiesAsync();
        _ = RefreshReliabilityDiagnosticsAsync(force: true);
    }

    private async Task RestoreFanPreferencesAsync()
    {
        var fan = _preferences.Current.Fan;
        var daemonActive = DaemonRuntime.IsServiceActive();
        _isRestoringPreferences = true;
        try
        {
            if (fan.CpuCurve.Count >= 2)
            {
                CpuFanCurve.Clear();
                foreach (var point in fan.CpuCurve.OrderBy(p => p.Temperature))
                    CpuFanCurve.Add(new FanCurvePointViewModel(new FanCurvePoint(point.Temperature, point.FanSpeed)));

                GpuFanCurve.Clear();
                foreach (var point in fan.CpuCurve.OrderBy(p => p.Temperature))
                    GpuFanCurve.Add(new FanCurvePointViewModel(new FanCurvePoint(point.Temperature, point.FanSpeed)));

                _fanCurveService.SetCpuFanCurve(CpuFanCurve.Select(vm => new FanCurvePoint(vm.Temperature, vm.FanSpeed)));
                _fanCurveService.SetGpuFanCurve(GpuFanCurve.Select(vm => new FanCurvePoint(vm.Temperature, vm.FanSpeed)));
            }

            if (!string.IsNullOrWhiteSpace(fan.SelectedPreset) && Presets.Contains(fan.SelectedPreset))
            {
                SelectedPreset = fan.SelectedPreset;
                PresetRenameText = fan.SelectedPreset;
                LoadPreset(fan.SelectedPreset);
            }

            CurveHysteresis = Math.Clamp(fan.CurveHysteresis, 1, 10);
            CurveRampUpDelay = Math.Clamp(fan.CurveRampUpDelay, 0, 30);
            CurveRampDownDelay = Math.Clamp(fan.CurveRampDownDelay, 0, 30);
            ManualFanSpeed = Math.Clamp(fan.ManualFanSpeed, 0, 100);

            if (CanEditFanCurve
                && string.Equals(fan.ActiveFanProfile, "manual", StringComparison.OrdinalIgnoreCase))
            {
                ActiveFanProfile = "manual";
                IsCustomCurveEnabled = fan.IsCustomCurveEnabled;
            }
            else if (HasFanProfileAccess
                     && !string.IsNullOrWhiteSpace(fan.ActiveFanProfile))
            {
                ActiveFanProfile = fan.ActiveFanProfile;
            }

            if (daemonActive)
                return;

            if (CanEditFanCurve
                && string.Equals(fan.ActiveFanProfile, "manual", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCustomCurveEnabled)
                    await ApplyCurve();
                else
                    await ApplyManualFanSpeedAsync(ManualFanSpeed);
            }
            else if (HasFanProfileAccess
                     && !string.IsNullOrWhiteSpace(fan.ActiveFanProfile)
                     && !string.Equals(fan.ActiveFanProfile, "manual", StringComparison.OrdinalIgnoreCase))
            {
                await SetFanProfile(fan.ActiveFanProfile);
            }
        }
        finally
        {
            _isRestoringPreferences = false;
        }
    }

    // Property-change handlers fire several saves at once; serialize them so
    // the daemon never reads a half-written or stale mode.
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    private async Task PersistFanStateAsync()
    {
        if (_isRestoringPreferences)
            return;

        await _persistLock.WaitAsync();
        try
        {
            await PersistFanStateCoreAsync();
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private async Task PersistFanStateCoreAsync()
    {

        var fan = _preferences.Current.Fan;
        fan.ActiveFanProfile = ActiveFanProfile;
        fan.IsCustomCurveEnabled = IsCustomCurveEnabled;
        fan.ManualFanSpeed = ManualFanSpeed;
        fan.CurveHysteresis = CurveHysteresis;
        fan.CurveRampUpDelay = CurveRampUpDelay;
        fan.CurveRampDownDelay = CurveRampDownDelay;
        fan.SelectedPreset = SelectedPreset;
        fan.CpuCurve = CpuFanCurve
            .Select(vm => new SavedFanCurvePoint
            {
                Temperature = vm.Temperature,
                FanSpeed = vm.FanSpeed
            })
            .OrderBy(p => p.Temperature)
            .ToList();

        _fanCurveService.SyncCustomPresetsToPreferences();
        await _preferences.SaveAndSyncDaemonConfigAsync();
    }

    private async Task InitializeCapabilitiesAsync()
    {
        try
        {
            var capabilities = await _hardwareService.GetCapabilitiesAsync();
            CanEditFanCurve = capabilities.SupportsFanControl;
            UsesUnifiedPwm = capabilities.SupportsHwmonPwmDuty;

            var capabilityClass = capabilities.FanControlCapabilityClass?.Trim().ToLowerInvariant() ?? "unsupported-control";
            HasFanProfileAccess = capabilityClass is "profile-only" or "full-control";
            switch (capabilityClass)
            {
                case "profile-only":
                    ShowCapabilityWarning = false;
                    CapabilityWarningMessage = string.Empty;
                    break;
                case "telemetry-only":
                    ShowCapabilityWarning = true;
                    CapabilityWarningMessage = "Fan telemetry is available, but firmware does not expose writable fan control interfaces on this board/kernel.";
                    break;
                case "unsupported-control":
                    ShowCapabilityWarning = true;
                    CapabilityWarningMessage = "No supported Linux fan control interface was detected for this board/kernel combination.";
                    break;
                default:
                    ShowCapabilityWarning = false;
                    CapabilityWarningMessage = string.Empty;
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowCapabilityWarning = true;
            CapabilityWarningMessage = "Could not detect Linux fan-control capability. Curve controls may be unavailable on this system.";
            System.Diagnostics.Debug.WriteLine($"Failed to initialize fan capability state: {ex.Message}");
        }

        _ = ScheduleRestoreFanPreferencesAsync();
    }

    private Task ScheduleRestoreFanPreferencesAsync()
    {
        return Task.Run(async () =>
        {
            try
            {
                // Let the window finish opening before touching hardware.
                await Task.Delay(750);
                await RestoreFanPreferencesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to restore fan preferences: {ex.Message}");
            }
        });
    }

    private void OnStatusChanged(object? sender, HardwareStatus status)
    {
        CpuTemperature = Math.Round(status.CpuTemperature, 1);
        GpuTemperature = Math.Round(status.GpuTemperature, 1);
        CpuFanRpm = status.CpuFanRpm;
        GpuFanRpm = status.GpuFanRpm;

        CpuFanPercent = status.CpuFanPercent;
        GpuFanPercent = status.GpuFanPercent;

        if (IsCurveEditorVisible && !DaemonRuntime.IsServiceActive())
            _ = ApplyCurveIfNeededAsync(status);

        if (IsAutoSwitchEnabled && HasFanProfileAccess)
            _ = RunAutoSwitchAsync(Math.Max(CpuTemperature, GpuTemperature));

        _ = RefreshReliabilityDiagnosticsAsync();
    }

    private async Task RunAutoSwitchAsync(double maxTemp)
    {
        // Determine target profile from temperature with hysteresis dead-band
        string target;
        if (maxTemp >= ThresholdPerformance)
            target = "gaming";
        else if (maxTemp >= ThresholdBalanced)
            target = "balanced";
        else if (maxTemp < ThresholdBalanced - AutoSwitchHysteresis &&
                 _autoSwitchCurrentProfile == "balanced")
            target = "silent";
        else if (maxTemp < ThresholdPerformance - AutoSwitchHysteresis &&
                 _autoSwitchCurrentProfile == "gaming")
            target = "balanced";
        else
            return; // Within dead-band — don't switch

        if (target == _autoSwitchCurrentProfile)
            return;

        try
        {
            await _hardwareService.SetFanProfileAsync(target);
            _autoSwitchCurrentProfile = target;
            ActiveFanProfile = target;
            StatusMessage = $"Auto-switched to {target} ({maxTemp:F0}°C)";
        }
        catch { }
    }

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(CanDeleteSelectedPreset));
        OnPropertyChanged(nameof(CanRenameSelectedPreset));
        PresetRenameText = value;

        if (!string.IsNullOrEmpty(value))
        {
            LoadPreset(value);
            if (IsCurveEditorVisible)
            {
                _ = ApplyCurve();
            }
        }

        _ = PersistFanStateAsync();
    }

    partial void OnActiveFanProfileChanged(string value)
    {
        NotifyManualVisibilityChanged();
        _ = PersistFanStateAsync();
    }

    partial void OnIsCustomCurveEnabledChanged(bool value)
    {
        NotifyManualVisibilityChanged();

        if (!IsManualControlsVisible)
        {
            _ = PersistFanStateAsync();
            return;
        }

        if (value)
        {
            _lastAppliedCurveSpeed = -1;
            _pendingCurveSpeed = -1;
            _ = ApplyCurve();
        }
        else
        {
            _ = ApplyManualFanSpeedAsync(ManualFanSpeed);
        }

        _ = PersistFanStateAsync();
    }

    partial void OnManualFanSpeedChanged(int value)
    {
        OnPropertyChanged(nameof(ManualFanPwm));

        if (!IsFixedSpeedVisible)
            return;

        _ = ApplyManualFanSpeedAsync(value);
        _ = PersistFanStateAsync();
    }

    partial void OnCurveHysteresisChanged(int value)
    {
        OnPropertyChanged(nameof(CurveHysteresisText));
        _ = PersistFanStateAsync();
    }

    partial void OnCurveRampUpDelayChanged(double value)
    {
        OnPropertyChanged(nameof(CurveRampUpDelayText));
        _ = PersistFanStateAsync();
    }

    partial void OnCurveRampDownDelayChanged(double value)
    {
        OnPropertyChanged(nameof(CurveRampDownDelayText));
        _ = PersistFanStateAsync();
    }

    private async Task ApplyManualFanSpeedAsync(int speed)
    {
        try
        {
            await _hardwareService.SetCpuFanSpeedAsync(speed);
            if (!UsesUnifiedPwm)
                await _hardwareService.SetGpuFanSpeedAsync(speed);
            StatusMessage = $"Manual fan speed set to {speed}%";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to set fan speed: {ex.Message}";
        }
    }

    private async Task ApplyCurveIfNeededAsync(HardwareStatus status)
    {
        var maxTemp = (int)Math.Max(status.CpuTemperature, status.GpuTemperature);
        var targetSpeed = ComputeTargetCurveSpeed(status);

        if (targetSpeed == _lastAppliedCurveSpeed)
        {
            _pendingCurveSpeed = -1;
            return;
        }

        if (_lastAppliedCurveSpeed >= 0 &&
            Math.Abs(maxTemp - _lastAppliedCurveTemp) < CurveHysteresis)
        {
            _pendingCurveSpeed = -1;
            return;
        }

        var isIncrease = _lastAppliedCurveSpeed < 0 || targetSpeed > _lastAppliedCurveSpeed;
        var rampDelay = isIncrease ? CurveRampUpDelay : CurveRampDownDelay;

        if (rampDelay > 0)
        {
            var now = DateTime.UtcNow;
            if (_pendingCurveSpeed != targetSpeed)
            {
                _pendingCurveSpeed = targetSpeed;
                _pendingCurveSinceUtc = now;
                return;
            }

            if ((now - _pendingCurveSinceUtc).TotalSeconds < rampDelay)
                return;
        }

        await ApplyCurve();
    }

    private int ComputeTargetCurveSpeed(HardwareStatus status)
    {
        var cpuFanSpeed = InterpolatePreview(CpuFanCurve, status.CpuTemperature);
        var gpuFanSpeed = InterpolatePreview(GpuFanCurve, status.GpuTemperature);
        return Math.Max(cpuFanSpeed, gpuFanSpeed);
    }

    partial void OnCanEditFanCurveChanged(bool value)
    {
        if (!value)
        {
            IsCustomCurveEnabled = false;
            if (string.Equals(ActiveFanProfile, "manual", StringComparison.OrdinalIgnoreCase))
                ActiveFanProfile = "auto";
        }

        NotifyManualVisibilityChanged();
    }

    private void NotifyManualVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsManualControlsVisible));
        OnPropertyChanged(nameof(IsFixedSpeedVisible));
        OnPropertyChanged(nameof(IsCurveEditorVisible));
    }

    [RelayCommand]
    private void LoadPreset(string presetName)
    {
        var (cpu, gpu) = _fanCurveService.GetPreset(presetName);
        
        CpuFanCurve.Clear();
        foreach (var point in cpu)
        {
            CpuFanCurve.Add(new FanCurvePointViewModel(point));
        }

        GpuFanCurve.Clear();
        foreach (var point in gpu)
        {
            GpuFanCurve.Add(new FanCurvePointViewModel(point));
        }

        _fanCurveService.SetCpuFanCurve(cpu);
        _fanCurveService.SetGpuFanCurve(gpu);
    }

    [RelayCommand]
    private async Task ApplyCurve()
    {
        if (!CanEditFanCurve)
        {
            StatusMessage = "Manual fan curve control is unavailable on this system.";
            return;
        }

        try
        {
            _fanCurveService.SetCpuFanCurve(CpuFanCurve.Select(vm => new FanCurvePoint(vm.Temperature, vm.FanSpeed)));
            _fanCurveService.SetGpuFanCurve(GpuFanCurve.Select(vm => new FanCurvePoint(vm.Temperature, vm.FanSpeed)));

            if (DaemonRuntime.IsServiceActive())
            {
                await PersistFanStateAsync();
                if (DaemonRuntime.RequestPreferencesReload())
                    StatusMessage = "Custom curve saved — daemon updated";
                else
                    StatusMessage = "Custom curve saved — restart omencore service to apply";
                return;
            }

            await _fanCurveService.ApplyAsync();
            var status = await _hardwareService.GetStatusAsync();
            _lastAppliedCurveTemp = (int)Math.Max(status.CpuTemperature, status.GpuTemperature);
            _lastAppliedCurveSpeed = ComputeTargetCurveSpeed(status);
            _pendingCurveSpeed = -1;
            _lastCurveApplyUtc = DateTime.UtcNow;
            StatusMessage = UsesUnifiedPwm
                ? $"Custom curve active — pwm {_lastAppliedCurveSpeed}% (max of CPU/GPU curve)"
                : "Custom curve active — speeds updated from CPU/GPU temperatures.";
            await PersistFanStateAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to apply fan curve: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Failed to apply fan curve: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        SelectedPreset = "Balanced";
        LoadPreset("Balanced");
        StatusMessage = "Reset to default fan curve";
    }

    [RelayCommand]
    private async Task SavePreset()
    {
        try
        {
            var baseName = string.IsNullOrWhiteSpace(SelectedPreset) ? "Custom" : SelectedPreset.Trim();
            var presetName = baseName;

            if (Presets.Contains(presetName) && !_fanCurveService.CanDeletePreset(presetName))
            {
                presetName = $"{baseName}-{DateTime.Now:HHmmss}";
            }

            var cpuCurve = CpuFanCurve.Select(vm => new FanCurvePoint(vm.Temperature, vm.FanSpeed)).ToList();
            var gpuCurve = GpuFanCurve.Select(vm => new FanCurvePoint(vm.Temperature, vm.FanSpeed)).ToList();

            _fanCurveService.SavePreset(presetName, cpuCurve, gpuCurve);

            if (!Presets.Contains(presetName))
            {
                Presets.Add(presetName);
            }

            SelectedPreset = presetName;
            PresetRenameText = presetName;
            OnPropertyChanged(nameof(CanDeleteSelectedPreset));
            OnPropertyChanged(nameof(CanRenameSelectedPreset));
            await ApplyCurve();
            await PersistFanStateAsync();
            StatusMessage = $"Saved preset '{presetName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save preset: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeletePreset()
    {
        var name = SelectedPreset?.Trim();
        if (string.IsNullOrEmpty(name) || !_fanCurveService.CanDeletePreset(name))
        {
            StatusMessage = "Only custom presets can be deleted.";
            return;
        }

        if (!_fanCurveService.DeletePreset(name))
        {
            StatusMessage = $"Failed to delete preset '{name}'.";
            return;
        }

        Presets.Remove(name);
        SelectedPreset = "Balanced";
        PresetRenameText = "Balanced";
        LoadPreset("Balanced");
        OnPropertyChanged(nameof(CanDeleteSelectedPreset));
        OnPropertyChanged(nameof(CanRenameSelectedPreset));
        await PersistFanStateAsync();
        StatusMessage = $"Deleted preset '{name}'.";
    }

    [RelayCommand]
    private async Task RenamePreset()
    {
        var oldName = SelectedPreset?.Trim();
        var newName = PresetRenameText?.Trim();

        if (string.IsNullOrEmpty(oldName)
            || string.IsNullOrEmpty(newName)
            || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Enter a new name for the preset.";
            return;
        }

        if (!_fanCurveService.RenamePreset(oldName, newName))
        {
            StatusMessage = $"Could not rename '{oldName}' to '{newName}'.";
            return;
        }

        Presets.Remove(oldName);
        if (!Presets.Contains(newName))
            Presets.Add(newName);

        SelectedPreset = newName;
        OnPropertyChanged(nameof(CanDeleteSelectedPreset));
        OnPropertyChanged(nameof(CanRenameSelectedPreset));
        await PersistFanStateAsync();
        StatusMessage = $"Renamed preset to '{newName}'.";
    }

    [RelayCommand]
    private async Task SetFanProfile(string profile)
    {
        profile = profile.Trim().ToLowerInvariant();

        if (profile == "manual")
        {
            if (!CanEditFanCurve)
            {
                StatusMessage = "Manual fan control is unavailable on this system.";
                return;
            }

            ActiveFanProfile = "manual";
            IsCustomCurveEnabled = false;
            StatusMessage = "Manual mode — set a fixed speed or switch to custom curve.";
            return;
        }

        IsCustomCurveEnabled = false;

        try
        {
            await _hardwareService.SetFanProfileAsync(profile);
            ActiveFanProfile = profile;
            StatusMessage = $"Fan mode set to {profile}";
            await PersistFanStateAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to set fan mode: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"SetFanProfile failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetManualControlType(string type)
    {
        if (!IsManualControlsVisible)
            return;

        if (string.Equals(type, "curve", StringComparison.OrdinalIgnoreCase))
        {
            IsCustomCurveEnabled = true;
            return;
        }

        IsCustomCurveEnabled = false;
        await ApplyManualFanSpeedAsync(ManualFanSpeed);
    }

    private Task RefreshReliabilityDiagnosticsAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastReliabilityRefreshUtc < TimeSpan.FromSeconds(3))
        {
            return Task.CompletedTask;
        }

        _lastReliabilityRefreshUtc = DateTime.UtcNow;

        var snapshot = ReliabilityDiagnosticsStore.ReadSnapshot();
        var recent = ReliabilityDiagnosticsStore.ReadRecentLogLines(8);

        ReliabilityLogLines.Clear();
        foreach (var line in recent)
        {
            ReliabilityLogLines.Add(line);
        }

        if (snapshot == null)
        {
            ReliabilityDiagnosticsVisible = ReliabilityLogLines.Count > 0;
            ReliabilitySummary = ReliabilityDiagnosticsVisible
                ? "Daemon diagnostics log found, but no active reliability snapshot."
                : "Daemon reliability snapshot unavailable.";
            return Task.CompletedTask;
        }

        ReliabilityDiagnosticsVisible = true;
        var source = string.IsNullOrWhiteSpace(snapshot.PowerSource) ? "unknown" : snapshot.PowerSource;
        var mode = string.IsNullOrWhiteSpace(snapshot.FanProfile) ? "unknown" : snapshot.FanProfile;
        var writer = snapshot.SingleWriterActive ? "daemon writer lock active" : "single-writer lock inactive";
        ReliabilitySummary =
            $"Reliability {(snapshot.Enabled ? "enabled" : "disabled")} | profile {mode} | power {source} | watchdog trips {snapshot.WatchdogTrips} | {writer}";

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task EmergencyStop()
    {
        try
        {
            await _hardwareService.SetCpuFanSpeedAsync(100);
            await _hardwareService.SetGpuFanSpeedAsync(100);
            StatusMessage = "Emergency stop activated - fans set to maximum";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Emergency stop failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddCpuPoint()
    {
        var lastPoint = CpuFanCurve.LastOrDefault();
        var newTemp = lastPoint != null ? Math.Min(100, lastPoint.Temperature + 10) : 40;
        var newSpeed = lastPoint != null ? Math.Min(100, lastPoint.FanSpeed + 10) : 30;
        CpuFanCurve.Add(new FanCurvePointViewModel(new FanCurvePoint(newTemp, newSpeed)));
    }

    [RelayCommand]
    private void AddGpuPoint()
    {
        var lastPoint = GpuFanCurve.LastOrDefault();
        var newTemp = lastPoint != null ? Math.Min(100, lastPoint.Temperature + 10) : 40;
        var newSpeed = lastPoint != null ? Math.Min(100, lastPoint.FanSpeed + 10) : 30;
        GpuFanCurve.Add(new FanCurvePointViewModel(new FanCurvePoint(newTemp, newSpeed)));
    }

    [RelayCommand]
    private void RemoveCpuPoint(FanCurvePointViewModel? point)
    {
        if (point != null && CpuFanCurve.Count > 2)
        {
            CpuFanCurve.Remove(point);
        }
    }

    [RelayCommand]
    private void RemoveGpuPoint(FanCurvePointViewModel? point)
    {
        if (point != null && GpuFanCurve.Count > 2)
        {
            GpuFanCurve.Remove(point);
        }
    }

    private static int InterpolatePreview(IEnumerable<FanCurvePointViewModel> curve, double temperature)
    {
        var points = curve.OrderBy(p => p.Temperature).ToList();
        if (points.Count == 0)
            return 50;

        var temp = (int)temperature;
        if (temp <= points[0].Temperature)
            return points[0].FanSpeed;
        if (temp >= points[^1].Temperature)
            return points[^1].FanSpeed;

        for (var i = 0; i < points.Count - 1; i++)
        {
            if (temp >= points[i].Temperature && temp <= points[i + 1].Temperature)
            {
                var range = points[i + 1].Temperature - points[i].Temperature;
                if (range <= 0)
                    return points[i].FanSpeed;
                var t = (double)(temp - points[i].Temperature) / range;
                return (int)(points[i].FanSpeed + t * (points[i + 1].FanSpeed - points[i].FanSpeed));
            }
        }

        return 50;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _hardwareService.StatusChanged -= OnStatusChanged;
            _disposed = true;
        }
    }
}

/// <summary>
/// ViewModel for a single fan curve point.
/// </summary>
public partial class FanCurvePointViewModel : ObservableObject
{
    [ObservableProperty]
    private int _temperature;

    [ObservableProperty]
    private int _fanSpeed;

    public string TemperatureText
    {
        get => Temperature.ToString();
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (int.TryParse(value.Trim(), out var parsed))
            {
                Temperature = Math.Clamp(parsed, 30, 100);
            }

            OnPropertyChanged();
        }
    }

    public string FanSpeedText
    {
        get => FanSpeed.ToString();
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (int.TryParse(value.Trim(), out var parsed))
            {
                FanSpeed = Math.Clamp(parsed, 0, 100);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(PwmText));
        }
    }

    public string PwmText
    {
        get => PercentToPwm(FanSpeed).ToString();
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (int.TryParse(value.Trim(), out var pwm))
            {
                pwm = Math.Clamp(pwm, 0, 255);
                FanSpeed = Math.Clamp(PwmToPercent(pwm), 0, 100);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(FanSpeedText));
        }
    }

    public int PwmValue => PercentToPwm(FanSpeed);

    public FanCurvePointViewModel(FanCurvePoint point)
    {
        Temperature = point.Temperature;
        FanSpeed = point.FanSpeed;
    }

    partial void OnTemperatureChanged(int value) => OnPropertyChanged(nameof(TemperatureText));

    partial void OnFanSpeedChanged(int value)
    {
        OnPropertyChanged(nameof(FanSpeedText));
        OnPropertyChanged(nameof(PwmText));
        OnPropertyChanged(nameof(PwmValue));
    }

    public static int PercentToPwm(int percent) =>
        Math.Clamp((int)Math.Round(Math.Clamp(percent, 0, 100) * 255.0 / 100.0), 0, 255);

    public static int PwmToPercent(int pwm) =>
        Math.Clamp((int)Math.Round(Math.Clamp(pwm, 0, 255) * 100.0 / 255.0), 0, 100);
}
