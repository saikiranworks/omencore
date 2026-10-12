using OmenCore.Linux.Hardware;

namespace OmenCore.Linux.Daemon;

/// <summary>
/// Software emulation of the HP BIOS "keyboard backlight timeout".
///
/// The hp-wmi driver only exposes color/brightness/animation — there is no hardware
/// timeout knob — so this watches keyboard and touchpad activity via evdev
/// (/dev/input/event*), dims the four-zone backlight after a configurable idle period,
/// and restores it on the next keypress or touch. External mice are deliberately ignored
/// so resting your hand on a mouse doesn't keep the keyboard lit.
///
/// Dimming is done purely through <c>fourzone_brightness</c> (SetBrightness 0), so the
/// per-zone colors are preserved and restoring is just re-applying the "on" brightness.
/// </summary>
public sealed class KeyboardIdleMonitor : IDisposable
{
    // struct input_event on 64-bit Linux: struct timeval (16) + type(2) + code(2) + value(4).
    private const int EventSize = 24;

    // EV_ABS bit inside the /proc/bus/input/devices "EV=" bitmask (absolute pointer => touchpad).
    private const int EvAbsBit = 1 << 0x03;

    private readonly LinuxKeyboardController _keyboard;
    private readonly int _timeoutSeconds;
    private readonly int _onBrightness;
    private readonly Action<string>? _log;

    private readonly List<FileStream> _devices = new();
    private readonly List<Task> _readers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private long _lastActivityTicks;
    private bool _dimmed;

    /// <summary>True while the backlight is dimmed for inactivity.</summary>
    public bool IsDimmed => Volatile.Read(ref _dimmed);
    private Task? _idleLoop;
    private bool _started;

    public KeyboardIdleMonitor(
        LinuxKeyboardController keyboard,
        int timeoutSeconds,
        int onBrightness,
        Action<string>? log = null)
    {
        _keyboard = keyboard;
        _timeoutSeconds = timeoutSeconds;
        // Never restore to 0 — that would leave the keyboard permanently dark.
        _onBrightness = Math.Clamp(onBrightness <= 0 ? 100 : onBrightness, 1, 100);
        _log = log;
        _lastActivityTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Begin monitoring. Returns false (and does nothing) when disabled, when the keyboard
    /// has no brightness control, or when no keyboard/touchpad devices could be opened.
    /// </summary>
    public bool Start()
    {
        if (_started)
            return false;

        if (_timeoutSeconds <= 0)
            return false;

        if (!_keyboard.IsAvailable || !_keyboard.SupportsBrightnessControl)
        {
            _log?.Invoke("[idle] Keyboard brightness control unavailable — backlight timeout disabled");
            return false;
        }

        var nodes = DiscoverInputDevices();
        foreach (var node in nodes)
        {
            try
            {
                var fs = new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: false);
                _devices.Add(fs);
                _readers.Add(Task.Run(() => ReadLoop(fs)));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[idle] Could not open {node}: {ex.Message}");
            }
        }

        if (_devices.Count == 0)
        {
            _log?.Invoke("[idle] No keyboard/touchpad input devices found — backlight timeout disabled");
            return false;
        }

        _started = true;
        _idleLoop = Task.Run(IdleLoopAsync);
        _log?.Invoke($"[idle] Backlight timeout active: {_timeoutSeconds}s idle across {_devices.Count} input device(s)");
        return true;
    }

    /// <summary>
    /// Parse /proc/bus/input/devices and return the /dev/input/eventN nodes for keyboards
    /// and touchpads/trackpoints, excluding plain external mice.
    /// </summary>
    private List<string> DiscoverInputDevices()
    {
        var nodes = new List<string>();
        const string devicesPath = "/proc/bus/input/devices";

        string[] lines;
        try
        {
            lines = File.ReadAllLines(devicesPath);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[idle] Could not read {devicesPath}: {ex.Message}");
            return nodes;
        }

        var name = string.Empty;
        var handlers = string.Empty;
        long evMask = 0;

        void FlushBlock()
        {
            if (string.IsNullOrEmpty(handlers))
            {
                name = string.Empty;
                handlers = string.Empty;
                evMask = 0;
                return;
            }

            var eventNode = handlers
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(t => t.StartsWith("event", StringComparison.Ordinal));

            if (eventNode is not null && ShouldWatch(name, handlers, evMask))
            {
                nodes.Add($"/dev/input/{eventNode}");
            }

            name = string.Empty;
            handlers = string.Empty;
            evMask = 0;
        }

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                FlushBlock();
                continue;
            }

            if (line.StartsWith("N: Name=", StringComparison.Ordinal))
            {
                name = line["N: Name=".Length..].Trim().Trim('"');
            }
            else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
            {
                handlers = line["H: Handlers=".Length..].Trim();
            }
            else if (line.StartsWith("B: EV=", StringComparison.Ordinal))
            {
                var hex = line["B: EV=".Length..].Trim();
                _ = long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out evMask);
            }
        }

        // The file does not end with a blank line; flush the final block.
        FlushBlock();

        return nodes;
    }

    private static bool ShouldWatch(string name, string handlers, long evMask)
    {
        // Keyboards register a "kbd" handler.
        if (handlers.Contains("kbd", StringComparison.Ordinal))
            return true;

        var lower = name.ToLowerInvariant();

        // Touchpads / trackpoints by name (covers relative-mode pointing sticks too).
        if (lower.Contains("touchpad") || lower.Contains("trackpoint") ||
            lower.Contains("synaptics") || lower.Contains("elan"))
            return true;

        // Absolute pointing device exposed as a mouse handler => touchpad/touchscreen.
        if ((evMask & EvAbsBit) != 0 && handlers.Contains("mouse", StringComparison.Ordinal))
            return true;

        // Everything else (plain external mouse: relative-only) is ignored.
        return false;
    }

    private void ReadLoop(FileStream fs)
    {
        var buffer = new byte[EventSize * 16];
        while (!_cts.IsCancellationRequested)
        {
            int read;
            try
            {
                read = fs.Read(buffer, 0, buffer.Length);
            }
            catch
            {
                // Stream closed on shutdown, or a transient device error — stop this reader.
                break;
            }

            if (read <= 0)
                break;

            OnActivity();
        }
    }

    private void OnActivity()
    {
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

        if (!Volatile.Read(ref _dimmed))
            return;

        lock (_gate)
        {
            if (_dimmed)
            {
                _keyboard.SetBrightness(_onBrightness);
                _dimmed = false;
                _log?.Invoke("[idle] Input detected — backlight restored");
            }
        }
    }

    private async Task IdleLoopAsync()
    {
        var timeoutMs = _timeoutSeconds * 1000L;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token);

                var idleMs = Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks);
                if (Volatile.Read(ref _dimmed) || idleMs < timeoutMs)
                    continue;

                lock (_gate)
                {
                    if (!_dimmed)
                    {
                        _keyboard.SetBrightness(0);
                        _dimmed = true;
                        _log?.Invoke($"[idle] Idle for {_timeoutSeconds}s — backlight off");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch { }

        foreach (var fs in _devices)
        {
            try { fs.Dispose(); } catch { }
        }

        try
        {
            _idleLoop?.Wait(TimeSpan.FromSeconds(2));
            Task.WaitAll(_readers.ToArray(), TimeSpan.FromSeconds(2));
        }
        catch { }

        // Leave the keyboard lit on exit — don't hand control back with the backlight off.
        if (_dimmed)
        {
            try { _keyboard.SetBrightness(_onBrightness); } catch { }
            _dimmed = false;
        }

        _cts.Dispose();
    }
}
