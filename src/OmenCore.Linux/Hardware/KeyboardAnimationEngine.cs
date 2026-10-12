namespace OmenCore.Linux.Hardware;

/// <summary>
/// Software keyboard animation effects for four-zone RGB keyboards.
/// </summary>
public enum KeyboardAnimationEffect
{
    Breathing,
    Wave,
    Spectrum,
}

/// <summary>
/// Software-rendered keyboard lighting animations.
///
/// HP firmware does not run animations on four-zone keyboards by itself —
/// on Windows, OMEN Gaming Hub's Light Studio streams color frames to the
/// keyboard continuously. This engine does the same through the hp-wmi
/// fourzone_color sysfs interface.
/// </summary>
public sealed class KeyboardAnimationEngine : IDisposable
{
    // Every frame is a WMI call into the kernel, so keep the rate modest. Effect
    // speeds are defined at ReferenceFps and scaled, so this does not change how
    // fast the animation looks.
    private const int DefaultFps = 10;
    private const double ReferenceFps = 20.0;

    private readonly LinuxKeyboardController _controller;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private (byte R, byte G, byte B)[] _baseColors = DefaultBaseColors();
    private (byte R, byte G, byte B)[]? _lastFrame;

    /// <summary>
    /// When set and returning true, frames are not rendered (e.g. backlight is dimmed
    /// for inactivity), so no kernel calls are made while nobody can see the effect.
    /// </summary>
    public Func<bool>? SuspendRendering { get; set; }

    /// <summary>Effect currently being rendered, or null when idle.</summary>
    public KeyboardAnimationEffect? ActiveEffect { get; private set; }

    public bool IsRunning => ActiveEffect is not null;

    public KeyboardAnimationEngine(LinuxKeyboardController controller)
    {
        _controller = controller;
    }

    private static (byte, byte, byte)[] DefaultBaseColors() => new (byte, byte, byte)[]
    {
        (0, 191, 255), (0, 191, 255), (0, 191, 255), (0, 191, 255),
    };

    /// <summary>
    /// Start rendering an effect. Captures the current zone colors as the
    /// base palette (used by Breathing and restored on Stop).
    /// </summary>
    public bool Start(KeyboardAnimationEffect effect, int fps = DefaultFps)
    {
        if (!_controller.HasFourZoneControl)
            return false;

        lock (_gate)
        {
            StopLocked(restoreBaseColors: false);
            _lastFrame = null;

            if (_controller.TryGetAllZoneColors(out var colors))
                _baseColors = colors;

            _cts = new CancellationTokenSource();
            ActiveEffect = effect;
            _loop = Task.Run(() => RunLoopAsync(effect, fps, _cts.Token));
            return true;
        }
    }

    /// <summary>
    /// Stop the animation. By default the zone colors captured at Start are
    /// restored so the keyboard returns to its pre-animation state.
    /// </summary>
    public void Stop(bool restoreBaseColors = true)
    {
        lock (_gate)
        {
            StopLocked(restoreBaseColors);
        }
    }

    private void StopLocked(bool restoreBaseColors)
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation is the expected completion path.
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
        ActiveEffect = null;

        _lastFrame = null;

        if (restoreBaseColors)
            ApplyBaseColors();
    }

    private void ApplyBaseColors()
    {
        var c = _baseColors;
        _controller.SetAllZoneColors(
            c[0].R, c[0].G, c[0].B,
            c[1].R, c[1].G, c[1].B,
            c[2].R, c[2].G, c[2].B,
            c[3].R, c[3].G, c[3].B);
    }

    private async Task RunLoopAsync(KeyboardAnimationEffect effect, int fps, CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps, 1, 60));
        using var timer = new PeriodicTimer(period);
        var stepScale = ReferenceFps / Math.Clamp(fps, 1, 60);
        int step = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (SuspendRendering?.Invoke() != true)
                    RenderFrame(effect, step * stepScale);

                step++;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void RenderFrame(KeyboardAnimationEffect effect, double step)
    {
        var frame = new (byte R, byte G, byte B)[4];

        switch (effect)
        {
        case KeyboardAnimationEffect.Spectrum:
            // All zones cycle through the hue wheel together (~6s per cycle at 20fps).
            var hue = step * 3.0 % 360.0;
            var rgb = HsvToRgb(hue, 1.0, 1.0);
            for (int z = 0; z < 4; z++)
                frame[z] = rgb;
            break;

        case KeyboardAnimationEffect.Wave:
            // Hue offset per zone, rolling across the keyboard.
            for (int z = 0; z < 4; z++)
                frame[z] = HsvToRgb((step * 3.0 + z * 90.0) % 360.0, 1.0, 1.0);
            break;

        case KeyboardAnimationEffect.Breathing:
            // Pulse the captured base colors (~4s per breath at 20fps).
            // Keep a 10% floor so the keyboard never looks fully dead mid-breath.
            var v = (1.0 - Math.Cos(step * 0.08)) / 2.0 * 0.9 + 0.1;
            for (int z = 0; z < 4; z++)
            {
                var b = _baseColors[z];
                frame[z] = ((byte)(b.R * v), (byte)(b.G * v), (byte)(b.B * v));
            }
            break;
        }

        // Skip the kernel call when the quantized frame did not change (common at
        // the ends of a breath and with 8-bit colors).
        if (_lastFrame is not null && _lastFrame.AsSpan().SequenceEqual(frame))
            return;

        if (_controller.SetAllZoneColors(
            frame[0].R, frame[0].G, frame[0].B,
            frame[1].R, frame[1].G, frame[1].B,
            frame[2].R, frame[2].G, frame[2].B,
            frame[3].R, frame[3].G, frame[3].B))
        {
            _lastFrame = frame;
        }
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = (h % 360.0 + 360.0) % 360.0 / 60.0;
        int i = (int)h;
        double f = h - i;
        double p = v * (1 - s);
        double q = v * (1 - s * f);
        double t = v * (1 - s * (1 - f));

        (double r, double g, double b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };

        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    public void Dispose()
    {
        Stop(restoreBaseColors: false);
    }
}
