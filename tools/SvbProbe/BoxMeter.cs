using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace SvbProbe;

/// <summary>
/// Reads a Gemini Power&amp;Data Hub Advanced 3's status frames, READ-ONLY: it sends nothing but <c>&gt;G#</c>, which starts the
/// box pushing a frame every 3 s, and keeps the latest supply reading and the extremes since the last <see cref="TakeExtremes"/>.
/// With the camera on the box's DC1 and its USB on the box's hub, the box's volts and amps are the camera's supply. The
/// frame layout is TianWen's <c>docs/architecture/gemini-powerbox-adv3-protocol.md</c>.
/// </summary>
internal sealed class BoxMeter : IDisposable
{
    private static readonly char[] Tags = ['D', 'U', 'A', 'T', 'M', 'B', 'C', 'S', 'H', 'V', 'P'];

    private readonly SerialPort _port;
    private readonly Thread _reader;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _stop;
    private Reading? _latest;
    private (double MinVolts, double MaxAmps, int Frames) _extremes = (double.PositiveInfinity, 0, 0);
    private readonly object _gate = new object();

    /// <summary>One status frame: <see cref="Ambient"/> is the box's own sensor (AHT20), <see cref="Lens"/> the probe on its jack (DS18B20).</summary>
    public sealed record Reading(double Volts, double Amps, double Watts, double Ambient, double Humidity, string DcBits, string UsbBits, TimeSpan At, double Lens);

    public BoxMeter(string portName, StreamWriter? frames)
    {
        _port = new SerialPort(portName, 19200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 6000,
            Encoding = Encoding.ASCII,
        };
        _port.Open();
        Thread.Sleep(2000);
        _port.Write(">G#\n");
        _reader = new Thread(() => Read(frames)) { IsBackground = true, Name = "box meter" };
        _reader.Start();
    }

    /// <summary>The latest frame, or null before the first.</summary>
    public Reading? Latest => Volatile.Read(ref _latest);

    /// <summary>How long since the latest frame arrived.</summary>
    public TimeSpan Age => _latest is { } r ? _clock.Elapsed - r.At : TimeSpan.MaxValue;

    /// <summary>The lowest voltage, highest current and frame count since the last call, then resets them.</summary>
    public (double MinVolts, double MaxAmps, int Frames) TakeExtremes()
    {
        lock (_gate)
        {
            var taken = _extremes;
            _extremes = (double.PositiveInfinity, 0, 0);
            return taken;
        }
    }

    private void Read(StreamWriter? frames)
    {
        var buffer = new StringBuilder();
        while (!_stop)
        {
            int ch;
            try
            {
                ch = _port.ReadChar();
            }
            catch (TimeoutException)
            {
                // Quiet for 6 s: ask again, as TianWen's driver does.
                TryWrite(">G#\n");
                continue;
            }
            catch (Exception) when (_stop)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"box meter: read failed ({ex.GetType().Name}: {ex.Message}); stopping");
                return;
            }

            if (ch != '#')
            {
                buffer.Append((char)ch);
                continue;
            }

            var raw = buffer.ToString();
            buffer.Clear();
            var star = raw.LastIndexOf('*');
            if (star < 0 || star + 1 >= raw.Length || raw[star + 1] != 'G' || Parse(raw[(star + 2)..]) is not { } reading)
            {
                continue;
            }

            Volatile.Write(ref _latest, reading);
            lock (_gate)
            {
                _extremes = (Math.Min(_extremes.MinVolts, reading.Volts), Math.Max(_extremes.MaxAmps, reading.Amps), _extremes.Frames + 1);
            }

            frames?.WriteLine(string.Join(",", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), F(reading.Volts), F(reading.Amps), F(reading.Watts),
                F(reading.Ambient), F(reading.Humidity), reading.DcBits, reading.UsbBits, F(reading.Lens)));
        }
    }

    private Reading? Parse(string payload)
    {
        var f = payload.Split(Tags);
        if (f.Length != 18 || f[0].Length != 4 || f[1].Length != 6)
        {
            return null;
        }

        return new Reading(N(f[14]), N(f[15]), N(f[16]), N(f[11]), N(f[12]), f[0], f[1], _clock.Elapsed, N(f[10]));

        static double N(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    private void TryWrite(string command)
    {
        try
        {
            _port.Write(command);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
        }
    }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _stop = true;
        _port.Close();
        _reader.Join(2000);
    }
}
