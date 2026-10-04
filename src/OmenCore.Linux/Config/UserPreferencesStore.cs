using System.Text.Json;
using OmenCore.Linux.Hardware;

namespace OmenCore.Linux.Config;

/// <summary>
/// Persists GUI hardware preferences to the user home and /var/lib/omencore so
/// settings survive rebuilds and the root daemon can apply them without sudo in the GUI.
/// </summary>
public static class UserPreferencesStore
{
    public static string SharedPreferencesPath =>
        Path.Combine(OmenCorePaths.SharedConfigDir, "user-preferences.json");

    public static UserHardwarePreferences LoadBestAvailable() => LoadCanonical();

    /// <summary>
    /// Single source of truth: shared runtime prefs written by the GUI/CLI.
    /// Falls back to the user home copy, then defaults.
    /// </summary>
    public static UserHardwarePreferences LoadCanonical()
    {
        var shared = TryLoad(SharedPreferencesPath);
        if (shared != null)
            return shared;

        var home = TryLoad(OmenCorePaths.GetUserPreferencesPath());
        return home ?? new UserHardwarePreferences();
    }

    /// <summary>
    /// Overlay canonical GUI prefs onto a daemon config before startup.
    /// Returns the loaded prefs, or null when no preference file exists.
    /// </summary>
    public static UserHardwarePreferences? MergeIntoConfig(OmenCoreConfig config)
    {
        var prefs = TryLoad(SharedPreferencesPath);
        if (prefs == null)
            prefs = TryLoad(OmenCorePaths.GetUserPreferencesPath());
        if (prefs == null)
            return null;

        ApplyToOmenCoreConfig(config, prefs);
        return prefs;
    }

    /// <summary>
    /// Persist prefs to both storage locations and refresh daemon config.toml.
    /// </summary>
    public static void SyncAll(UserHardwarePreferences preferences)
    {
        SaveToAllLocations(preferences);
        SyncDaemonConfig(preferences);
    }

    public static UserHardwarePreferences? TryLoad(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, UserPreferencesJsonContext.Default.UserHardwarePreferences);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveToAllLocations(UserHardwarePreferences preferences)
    {
        var homePath = OmenCorePaths.GetUserPreferencesPath();
        Directory.CreateDirectory(Path.GetDirectoryName(homePath)!);
        WriteJson(homePath, preferences);

        OmenCorePaths.EnsureSharedConfigDirectory();
        WriteJson(SharedPreferencesPath, preferences);
    }

    private static void WriteJson(string path, UserHardwarePreferences preferences)
    {
        var json = JsonSerializer.Serialize(preferences, UserPreferencesJsonContext.Default.UserHardwarePreferences);
        File.WriteAllText(path, json);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead);
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    public static void SyncDaemonConfig(UserHardwarePreferences preferences)
    {
        var config = OmenCoreConfig.Load();
        ApplyToOmenCoreConfig(config, preferences);
        ApplyKeyboardToDaemonBootConfig(config, preferences.Keyboard);
        config.Startup.ApplyOnBoot = true;

        OmenCorePaths.EnsureSharedConfigDirectory();
        try
        {
            config.Save(OmenCoreConfig.SharedConfigPath);
        }
        catch
        {
            config.Save();
        }
    }

    /// <summary>
    /// Syncs keyboard brightness and daemon boot config from saved preferences.
    /// Zone colors are owned by the GUI/CLI — do not snapshot sysfs on daemon shutdown
    /// or a boot-time red apply will be written back over the user's saved palette.
    /// </summary>
    public static bool PersistKeyboardState(
        LinuxKeyboardController keyboard,
        bool captureZoneColors = true,
        int? animationIndex = null)
    {
        if (!keyboard.IsAvailable)
            return false;

        var preferences = LoadBestAvailable();
        if (captureZoneColors)
        {
            if (!TryCaptureKeyboardPreferences(keyboard, preferences.Keyboard))
                return false;

            preferences.Keyboard.AnimationIndex = animationIndex ?? 0;
        }
        else
        {
            if (animationIndex.HasValue)
                preferences.Keyboard.AnimationIndex = animationIndex.Value;

            if (keyboard.SupportsBrightnessControl)
                preferences.Keyboard.Brightness = Math.Clamp(keyboard.GetBrightness(), 0, 100);
        }

        var config = OmenCoreConfig.Load();
        ApplyKeyboardToDaemonBootConfig(config, preferences.Keyboard);
        config.Startup.ApplyOnBoot = true;

        OmenCorePaths.EnsureSharedConfigDirectory();
        try
        {
            config.Save(OmenCoreConfig.SharedConfigPath);
            SaveToAllLocations(preferences);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryCaptureKeyboardPreferences(
        LinuxKeyboardController keyboard,
        KeyboardLightingPreferences kb)
    {
        if (!keyboard.TryGetAllZoneColors(out var colors))
            return false;

        kb.Zone1R = colors[0].R;
        kb.Zone1G = colors[0].G;
        kb.Zone1B = colors[0].B;
        kb.Zone2R = colors[1].R;
        kb.Zone2G = colors[1].G;
        kb.Zone2B = colors[1].B;
        kb.Zone3R = colors[2].R;
        kb.Zone3G = colors[2].G;
        kb.Zone3B = colors[2].B;
        kb.Zone4R = colors[3].R;
        kb.Zone4G = colors[3].G;
        kb.Zone4B = colors[3].B;

        if (keyboard.SupportsBrightnessControl)
            kb.Brightness = Math.Clamp(keyboard.GetBrightness(), 0, 100);

        return true;
    }

    public static bool TryStartKeyboardAnimation(
        KeyboardAnimationEngine animationEngine,
        int animationIndex)
    {
        if (animationIndex <= 0)
            return false;

        var effect = animationIndex switch
        {
            1 => KeyboardAnimationEffect.Breathing,
            2 => KeyboardAnimationEffect.Wave,
            3 => KeyboardAnimationEffect.Spectrum,
            _ => (KeyboardAnimationEffect?)null,
        };

        return effect.HasValue && animationEngine.Start(effect.Value);
    }

    public static void ApplyKeyboardToConfig(OmenCoreConfig config, KeyboardLightingPreferences kb)
    {
        config.Keyboard.Enabled = true;
        config.Keyboard.Brightness = Math.Clamp(kb.Brightness, 0, 100);
        config.Keyboard.Color = $"{kb.Zone1R:X2}{kb.Zone1G:X2}{kb.Zone1B:X2}";
        config.Keyboard.Zone1Color = config.Keyboard.Color;
        config.Keyboard.Zone2Color = $"{kb.Zone2R:X2}{kb.Zone2G:X2}{kb.Zone2B:X2}";
        config.Keyboard.Zone3Color = $"{kb.Zone3R:X2}{kb.Zone3G:X2}{kb.Zone3B:X2}";
        config.Keyboard.Zone4Color = $"{kb.Zone4R:X2}{kb.Zone4G:X2}{kb.Zone4B:X2}";
        config.Keyboard.AnimationMode = kb.AnimationIndex;
        config.Keyboard.BacklightTimeoutSeconds = Math.Max(0, kb.BacklightTimeoutSeconds);
    }

    /// <summary>
    /// Static zone colors for daemon boot apply. Animations are software-rendered by the GUI;
    /// the firmware fourzone_animation node must stay off so it does not fight saved colors.
    /// </summary>
    public static void ApplyKeyboardToDaemonBootConfig(OmenCoreConfig config, KeyboardLightingPreferences kb)
    {
        ApplyKeyboardToConfig(config, kb);
        config.Keyboard.AnimationMode = 0;
    }

    public static void ApplyToOmenCoreConfig(OmenCoreConfig config, UserHardwarePreferences preferences)
    {
        var kb = preferences.Keyboard;
        var fan = preferences.Fan;

        ApplyKeyboardToConfig(config, kb);

        if (string.Equals(fan.ActiveFanProfile, "manual", StringComparison.OrdinalIgnoreCase)
            && fan.IsCustomCurveEnabled
            && fan.CpuCurve.Count >= 2)
        {
            config.Fan.Profile = "custom";
            config.Fan.Curve.Enabled = true;
            config.Fan.Curve.Hysteresis = Math.Clamp(fan.CurveHysteresis, 1, 10);
            config.Fan.Curve.RampUpDelaySeconds = Math.Clamp(fan.CurveRampUpDelay, 0, 30);
            config.Fan.Curve.RampDownDelaySeconds = Math.Clamp(fan.CurveRampDownDelay, 0, 30);
            config.Fan.Curve.Points = fan.CpuCurve
                .OrderBy(p => p.Temperature)
                .Select(p => new FanCurvePoint
                {
                    Temp = p.Temperature,
                    Speed = p.FanSpeed
                })
                .ToList();
        }
        else if (string.Equals(fan.ActiveFanProfile, "manual", StringComparison.OrdinalIgnoreCase))
        {
            // Fixed-speed manual mode: keep the fans under manual PWM instead of
            // handing them back to BIOS auto.
            config.Fan.Profile = "manual";
            config.Fan.Curve.Enabled = false;
        }
        else
        {
            config.Fan.Profile = fan.ActiveFanProfile.Trim().ToLowerInvariant() switch
            {
                "silent" => "silent",
                "balanced" => "balanced",
                "gaming" or "performance" => "gaming",
                "max" => "max",
                _ => "auto"
            };
            config.Fan.Curve.Enabled = false;
        }
    }

    public static bool ApplyKeyboard(LinuxKeyboardController keyboard, KeyboardLightingPreferences kb)
    {
        if (!keyboard.IsAvailable)
            return false;

        var factor = Math.Clamp(kb.Brightness, 0, 100) / 100.0;
        var zones = new[]
        {
            (kb.Zone1R, kb.Zone1G, kb.Zone1B),
            (kb.Zone2R, kb.Zone2G, kb.Zone2B),
            (kb.Zone3R, kb.Zone3G, kb.Zone3B),
            (kb.Zone4R, kb.Zone4G, kb.Zone4B),
        };

        var applied = false;
        if (keyboard.HasFourZoneControl)
        {
            for (var i = 0; i < zones.Length; i++)
            {
                var (r, g, b) = zones[i];
                var scaledR = (byte)(Math.Clamp(r, 0, 255) * factor);
                var scaledG = (byte)(Math.Clamp(g, 0, 255) * factor);
                var scaledB = (byte)(Math.Clamp(b, 0, 255) * factor);
                if (keyboard.SetZoneColor(i, scaledR, scaledG, scaledB))
                    applied = true;
            }
        }
        else if (TryParseHexColor(configColor: $"{kb.Zone1R:X2}{kb.Zone1G:X2}{kb.Zone1B:X2}",
                     out var r, out var g, out var b))
        {
            applied = keyboard.SetAllZonesColor(
                (byte)(r * factor), (byte)(g * factor), (byte)(b * factor));
        }

        if (keyboard.SupportsBrightnessControl)
            keyboard.SetBrightness(kb.Brightness);

        return applied;
    }

    private static bool TryParseHexColor(string configColor, out double r, out double g, out double b)
    {
        r = g = b = 0;
        var value = configColor.Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return false;

        r = (rgb >> 16) & 0xFF;
        g = (rgb >> 8) & 0xFF;
        b = rgb & 0xFF;
        return true;
    }
}
