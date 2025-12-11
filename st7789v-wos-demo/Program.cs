using System.Runtime.Versioning;
using System.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Devices.Gpio;
using Windows.Devices.Spi;

var options = DisplayOptions.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("Cancellation requested, stopping animation...");
};

await using var display = new St7789vDisplay(options);

try
{
    await display.InitializeAsync();

    if (options.Pattern.Equals("ball", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Starting moving circle demo (Ctrl+C to stop)...");
        await display.RunMovingBallAsync(cts.Token);
    }
    else
    {
        display.DrawDemoPattern();
        Console.WriteLine("Demo complete; the screen should show red/green/blue stripes with a white square centered.");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Canceled by user.");
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to run the program: {ex.Message}");
    Environment.ExitCode = 1;
}

sealed class DisplayOptions
{
    public string SpiBusId { get; set; } = "SPI12";
    public int ChipSelectLine { get; set; } = 0;
    public int DcPin { get; set; } = 0;
    public int ResetPin { get; set; } = 97;
    public int ClockFrequency { get; set; } = 30000000;
    public SpiMode Mode { get; set; } = SpiMode.Mode0;
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 240;
    public string Pattern { get; set; } = "static";
    public int BallDelayMs { get; set; } = 16;
    public bool MaxFps { get; set; } = false;

    public static DisplayOptions Parse(string[] args)
    {
        var opts = new DisplayOptions();
        foreach (var arg in args)
        {
            if (!arg.Contains('='))
                continue;

            var pair = arg.Split('=', 2, StringSplitOptions.TrimEntries);
            var key = pair[0].TrimStart('-').ToLowerInvariant();
            var value = pair[1];

            try
            {
                switch (key)
                {
                    case "bus":
                        opts.SpiBusId = value;
                        break;
                    case "cs":
                        opts.ChipSelectLine = int.Parse(value);
                        break;
                    case "dc":
                        opts.DcPin = int.Parse(value);
                        break;
                    case "reset":
                        opts.ResetPin = int.Parse(value);
                        break;
                    case "freq":
                    case "clock":
                        opts.ClockFrequency = int.Parse(value);
                        break;
                    case "mode":
                        opts.Mode = (SpiMode)Enum.Parse(typeof(SpiMode), value, true);
                        break;
                    case "width":
                        opts.Width = int.Parse(value);
                        break;
                    case "height":
                        opts.Height = int.Parse(value);
                        break;
                    case "pattern":
                    case "demo":
                        opts.Pattern = value.ToLowerInvariant();
                        break;
                    case "balldelay":
                    case "delay":
                    case "fps":
                        if (key == "fps")
                            opts.BallDelayMs = (int)Math.Max(1, 1000.0 / double.Parse(value));
                        else
                            opts.BallDelayMs = Math.Max(1, int.Parse(value));
                        break;
                    case "maxfps":
                    case "nodelay":
                    case "unlimited":
                        opts.MaxFps = true;
                        opts.BallDelayMs = 0;
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse parameter {arg}; continuing with defaults: {ex.Message}");
            }
        }

        Console.WriteLine($"Configuration: SPI bus={opts.SpiBusId}, CS={opts.ChipSelectLine}, DC={opts.DcPin}, RESET={opts.ResetPin}, freq={opts.ClockFrequency}Hz, mode={opts.Mode}, resolution={opts.Width}x{opts.Height}, pattern={opts.Pattern}, ballDelay={opts.BallDelayMs}ms, maxFps={opts.MaxFps}");

        return opts;
    }
}

[SupportedOSPlatform("windows10.0.26100.0")]
sealed class St7789vDisplay : IAsyncDisposable
{
    private readonly DisplayOptions _options;
    private SpiDevice? _spi;
    private GpioPin? _dcPin;
    private GpioPin? _resetPin;
    private readonly byte[] _commandBuffer = new byte[1];
    private readonly ushort _red = Color565(255, 0, 0);
    private readonly ushort _green = Color565(0, 255, 0);
    private readonly ushort _blue = Color565(0, 0, 255);
    private readonly ushort _white = Color565(255, 255, 255);
    private readonly ushort _black = Color565(0, 0, 0);
    private byte[] _scratchBuffer = Array.Empty<byte>();

    public St7789vDisplay(DisplayOptions options)
    {
        _options = options;
    }

    public async Task InitializeAsync()
    {
        Console.WriteLine("Starting ST7789V display initialization");

        var gpio = GpioController.GetDefault() ?? throw new InvalidOperationException("GPIO is not available (missing driver or permissions)");

        _dcPin = gpio.OpenPin(_options.DcPin);
        _dcPin.SetDriveMode(GpioPinDriveMode.Output);
        _dcPin.Write(GpioPinValue.Low);

        _resetPin = gpio.OpenPin(_options.ResetPin);
        _resetPin.SetDriveMode(GpioPinDriveMode.Output);
        _resetPin.Write(GpioPinValue.High);

        await HardwareResetAsync();

        var selector = SpiDevice.GetDeviceSelector(_options.SpiBusId);
        var infos = await DeviceInformation.FindAllAsync(selector);
        if (infos.Count == 0)
            throw new InvalidOperationException($"SPI bus {_options.SpiBusId} not found.");

        var settings = new SpiConnectionSettings(_options.ChipSelectLine)
        {
            ClockFrequency = _options.ClockFrequency,
            DataBitLength = 8,
            Mode = _options.Mode
        };

        _spi = await SpiDevice.FromIdAsync(infos[0].Id, settings) ?? throw new InvalidOperationException("Failed to open SPI device");
        Console.WriteLine($"SPI opened: {infos[0].Id}, frequency {settings.ClockFrequency}Hz, mode {settings.Mode}");

        await RunInitSequenceAsync();
    }

    private async Task HardwareResetAsync()
    {
        EnsureGpio();

        Console.WriteLine("Performing hardware reset");
        _resetPin!.Write(GpioPinValue.High);
        await Task.Delay(10);
        _resetPin.Write(GpioPinValue.Low);
        await Task.Delay(10);
        _resetPin.Write(GpioPinValue.High);
        await Task.Delay(120);
    }

    private async Task RunInitSequenceAsync()
    {
        Console.WriteLine("Sending initialization command sequence");
        foreach (var step in InitSequence)
        {
            if (step.IsDelay)
            {
                Console.WriteLine($"Delay {step.DelayMs}ms");
                await Task.Delay(step.DelayMs);
                continue;
            }

            SendCommand(step.Command, step.Data);
            if (step.DelayMs > 0)
                await Task.Delay(step.DelayMs);
        }
    }

    public void DrawDemoPattern()
    {
        EnsureReady();
        Console.WriteLine("Starting demo pattern rendering");

        DrawBackgroundBands();

        var boxSize = 40;
        FillRect((_options.Width - boxSize) / 2, (_options.Height - boxSize) / 2, boxSize, boxSize, _white);
    }

    public async Task RunMovingBallAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        Console.WriteLine("Rendering background for moving circle");
        DrawBackgroundBands();

        var fpsWatch = Stopwatch.StartNew();
        var secondWatch = Stopwatch.StartNew();
        int frameCount = 0;

        int radius = 18;
        int x = radius + 4;
        int y = radius + 4;
        int vx = 2;
        int vy = 2;

        var prevRect = GetBallBounds(x, y, radius, margin: 2);
        DrawBallFrame(x, y, radius, prevRect);

        while (!cancellationToken.IsCancellationRequested)
        {
            int nextX = x + vx;
            int nextY = y + vy;

            if (nextX - radius < 0)
            {
                nextX = radius;
                vx = -vx;
            }
            else if (nextX + radius >= _options.Width)
            {
                nextX = _options.Width - radius - 1;
                vx = -vx;
            }

            if (nextY - radius < 0)
            {
                nextY = radius;
                vy = -vy;
            }
            else if (nextY + radius >= _options.Height)
            {
                nextY = _options.Height - radius - 1;
                vy = -vy;
            }

            var newRect = GetBallBounds(nextX, nextY, radius, margin: 2);
            var union = Union(prevRect, newRect);
            DrawBallFrame(nextX, nextY, radius, union);

            x = nextX;
            y = nextY;
            prevRect = newRect;

            frameCount++;
            if (secondWatch.ElapsedMilliseconds >= 1000)
            {
                double fps = frameCount / (secondWatch.Elapsed.TotalSeconds);
                Console.WriteLine($"Current FPS: {fps:F1}{(_options.MaxFps ? " (max)" : "")}");
                frameCount = 0;
                secondWatch.Restart();
            }

            if (_options.MaxFps)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                continue;
            }

            try
            {
                await Task.Delay(_options.BallDelayMs, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        Console.WriteLine("Moving circle animation stopped.");
    }

    private void DrawBackgroundBands()
    {
        FillRect(0, 0, _options.Width, _options.Height, _black);

        var band = _options.Height / 3;
        FillRect(0, 0, _options.Width, band, _red);
        FillRect(0, band, _options.Width, band, _green);
        FillRect(0, band * 2, _options.Width, _options.Height - band * 2, _blue);
    }

    private void DrawBallFrame(int cx, int cy, int radius, RectInt area)
    {
        int width = area.X1 - area.X0 + 1;
        int height = area.Y1 - area.Y0 + 1;
        int pixels = width * height;
        EnsureScratchCapacity(pixels * 2);

        var band = _options.Height / 3;
        int idx = 0;
        int radiusSq = radius * radius;
        for (int y = area.Y0; y <= area.Y1; y++)
        {
            int dy = y - cy;
            int dy2 = dy * dy;
            ushort bg = y < band ? _red : (y < band * 2 ? _green : _blue);

            for (int x = area.X0; x <= area.X1; x++)
            {
                int dx = x - cx;
                bool inside = (dx * dx + dy2) <= radiusSq;
                ushort color = inside ? _white : bg;
                _scratchBuffer[idx++] = (byte)(color >> 8);
                _scratchBuffer[idx++] = (byte)(color & 0xFF);
            }
        }

        BeginMemoryWrite(area.X0, area.Y0, area.X1, area.Y1);
        _dcPin!.Write(GpioPinValue.High);
        _spi!.Write(_scratchBuffer.AsSpan(0, pixels * 2).ToArray());
    }

    private void EnsureScratchCapacity(int sizeBytes)
    {
        if (_scratchBuffer.Length < sizeBytes)
            Array.Resize(ref _scratchBuffer, sizeBytes);
    }

    private static RectInt GetBallBounds(int cx, int cy, int radius, int margin)
    {
        return new RectInt(cx - radius - margin, cy - radius - margin, cx + radius + margin, cy + radius + margin);
    }

    private RectInt Union(RectInt a, RectInt b)
    {
        int x0 = Math.Clamp(Math.Min(a.X0, b.X0), 0, _options.Width - 1);
        int y0 = Math.Clamp(Math.Min(a.Y0, b.Y0), 0, _options.Height - 1);
        int x1 = Math.Clamp(Math.Max(a.X1, b.X1), 0, _options.Width - 1);
        int y1 = Math.Clamp(Math.Max(a.Y1, b.Y1), 0, _options.Height - 1);
        return new RectInt(x0, y0, x1, y1);
    }

    private readonly struct RectInt
    {
        public RectInt(int x0, int y0, int x1, int y1)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }

        public int X0 { get; }
        public int Y0 { get; }
        public int X1 { get; }
        public int Y1 { get; }
    }

    private void FillRect(int x, int y, int width, int height, ushort color)
    {
        EnsureReady();

        var x1 = x + width - 1;
        var y1 = y + height - 1;

        BeginMemoryWrite(x, y, x1, y1);

        var totalPixels = width * height;
        const int chunkPixels = 512;
        var chunkBuffer = new byte[chunkPixels * 2];
        FillColorBuffer(chunkBuffer, color, chunkPixels);

        _dcPin!.Write(GpioPinValue.High);

        while (totalPixels > 0)
        {
            var batch = Math.Min(chunkPixels, totalPixels);
            var bytes = batch * 2;

            if (batch == chunkPixels)
            {
                _spi!.Write(chunkBuffer);
            }
            else
            {
                var lastBuffer = new byte[bytes];
                FillColorBuffer(lastBuffer, color, batch);
                _spi!.Write(lastBuffer);
            }

            totalPixels -= batch;
        }
    }

    private void BeginMemoryWrite(int x0, int y0, int x1, int y1)
    {
        Span<byte> buf = stackalloc byte[4];

        buf[0] = (byte)(x0 >> 8);
        buf[1] = (byte)(x0 & 0xFF);
        buf[2] = (byte)(x1 >> 8);
        buf[3] = (byte)(x1 & 0xFF);
        SendCommand(0x2A, buf.ToArray()); // Column address set

        buf[0] = (byte)(y0 >> 8);
        buf[1] = (byte)(y0 & 0xFF);
        buf[2] = (byte)(y1 >> 8);
        buf[3] = (byte)(y1 & 0xFF);
        SendCommand(0x2B, buf.ToArray()); // Row address set

        SendCommand(0x2C); // Memory write
    }

    private void SendCommand(byte command, params byte[] data)
    {
        EnsureReady();

        _commandBuffer[0] = command;
        _dcPin!.Write(GpioPinValue.Low);
        _spi!.Write(_commandBuffer);

        if (data.Length > 0)
        {
            _dcPin.Write(GpioPinValue.High);
            _spi.Write(data);
        }
    }

    private void EnsureReady()
    {
        if (_spi is null || _dcPin is null || _resetPin is null)
            throw new InvalidOperationException("Device is not initialized");
    }

    private void EnsureGpio()
    {
        if (_dcPin is null || _resetPin is null)
            throw new InvalidOperationException("GPIO is not initialized");
    }

    private static void FillColorBuffer(byte[] buffer, ushort color, int pixels)
    {
        var hi = (byte)(color >> 8);
        var lo = (byte)(color & 0xFF);

        for (int i = 0; i < pixels; i++)
        {
            var idx = i * 2;
            buffer[idx] = hi;
            buffer[idx + 1] = lo;
        }
    }

    private static ushort Color565(int r, int g, int b)
    {
        var r5 = (r & 0xF8) << 8;
        var g6 = (g & 0xFC) << 3;
        var b5 = (b >> 3);
        return (ushort)(r5 | g6 | b5);
    }

    public ValueTask DisposeAsync()
    {
        _spi?.Dispose();
        _dcPin?.Dispose();
        _resetPin?.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly struct InitStep
    {
        public InitStep(byte command, byte[] data, int delayMs, bool isDelay)
        {
            Command = command;
            Data = data;
            DelayMs = delayMs;
            IsDelay = isDelay;
        }

        public byte Command { get; }
        public byte[] Data { get; }
        public int DelayMs { get; }
        public bool IsDelay { get; }
    }

    private static InitStep Cmd(byte cmd, params byte[] data) => new(cmd, data, 0, false);
    private static InitStep Delay(int ms) => new(0, Array.Empty<byte>(), ms, true);

    private static readonly IReadOnlyList<InitStep> InitSequence = new List<InitStep>
    {
        Cmd(0x3A, 0x05),
        Cmd(0xB2, 0x0C, 0x0C, 0x00, 0x33, 0x33),
        Cmd(0xB7, 0x35),
        Cmd(0xBB, 0x19),
        Cmd(0xC0, 0x2C),
        Cmd(0xC2, 0x01),
        Cmd(0xC3, 0x12),
        Cmd(0xC4, 0x20),
        Cmd(0xC6, 0x0F),
        Cmd(0xD0, 0xA4, 0xA1),
        Cmd(0xE0, 0xD0, 0x04, 0x0D, 0x11, 0x13, 0x2B, 0x3F, 0x54, 0x4C, 0x18, 0x0D, 0x0B, 0x1F, 0x23),
        Cmd(0xE1, 0xD0, 0x04, 0x0C, 0x11, 0x13, 0x2C, 0x3F, 0x44, 0x51, 0x2F, 0x1F, 0x1F, 0x20, 0x23),
        Cmd(0x21),
        Cmd(0x36, 0x00),
        Cmd(0x11),
        Delay(20),
        Cmd(0x29)
    };
}
