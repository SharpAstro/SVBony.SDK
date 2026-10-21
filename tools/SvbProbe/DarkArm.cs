using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SVBony.SDK;
using TianWen.DAL;

namespace SvbProbe;

/// <summary>
/// One full-frame RAW16 dark through the DAL device, written as a 16-bit FITS (<c>--out</c>) with the cards a stacker
/// reads: frame type, exposure, gain, offset, sensor temperature and the Bayer pattern. The cooler is left as it is.
/// </summary>
internal static class DarkArm
{
    internal static int Run(string[] args)
    {
        var exposureS = double.Parse(Option(args, "--exposure-s") ?? "120", CultureInfo.InvariantCulture);
        var gain = int.Parse(Option(args, "--gain") ?? "120", CultureInfo.InvariantCulture);
        var offset = int.Parse(Option(args, "--offset") ?? "20", CultureInfo.InvariantCulture);
        var path = Option(args, "--out") ?? $"dark_{DateTime.UtcNow:yyyyMMdd_HHmmss}.fits";

        var camera = new DeviceIterator().FirstOrDefault();
        if (camera.Name is null)
        {
            Console.WriteLine("no camera");
            return 2;
        }

        if (!camera.Open())
        {
            Console.WriteLine("open failed");
            return 2;
        }

        var (w, h) = (camera.MaxWidth, camera.MaxHeight);
        var frame = Marshal.AllocHGlobal(w * h * 2);
        try
        {
            Expect(camera.SetROIFormat(w, h, 1, PixelDataFormat.RAW16), "SetROIFormat");
            Expect(camera.SetStartPosition(0, 0), "SetStartPosition");
            Expect(camera.SetControlValue(CMOSControlType.Gain, gain), "gain");
            Expect(camera.SetControlValue(CMOSControlType.Brightness, offset), "offset");
            Expect(camera.SetControlValue(CMOSControlType.Exposure, (int)(exposureS * 1_000_000)), "exposure");

            var temperature = ReadTemperature();
            var start = DateTime.UtcNow;
            Console.WriteLine($"{camera.Name}: {exposureS} s dark at gain {gain}, offset {offset}, sensor {temperature:F1} C");
            Expect(camera.StartLightExposure(), "StartLightExposure");

            var clock = Stopwatch.StartNew();
            ExposureStatus status;
            do
            {
                Thread.Sleep(200);
                _ = camera.GetExposureStatus(out status);
            }
            while (status is ExposureStatus.Working && clock.Elapsed.TotalSeconds < exposureS + 30);

            if (status is not ExposureStatus.Success)
            {
                Console.WriteLine($"no frame: status {status} after {clock.Elapsed.TotalSeconds:F1} s");
                _ = camera.StopExposure();
                return 1;
            }

            Expect(camera.GetDataAfterExposure(frame, w * h * 2), "GetDataAfterExposure");
            var after = ReadTemperature();
            var pixels = new ushort[w * h];
            unsafe
            {
                new ReadOnlySpan<ushort>((void*)frame, w * h).CopyTo(pixels);
            }

            WriteFits(path, pixels, w, h, [
                ("IMAGETYP", "'Dark'"), ("FRAMETYP", "'Dark'"),
                ("EXPTIME", F(exposureS)), ("EXPOSURE", F(exposureS)),
                ("DATE-OBS", $"'{start:yyyy-MM-ddTHH:mm:ss.fff}'"),
                ("GAIN", gain.ToString(CultureInfo.InvariantCulture)), ("OFFSET", offset.ToString(CultureInfo.InvariantCulture)),
                ("CCD-TEMP", F((temperature + after) / 2)),
                ("XBINNING", "1"), ("YBINNING", "1"), ("XPIXSZ", "3.76"), ("YPIXSZ", "3.76"),
                ("BAYERPAT", "'GRBG'"), ("XBAYROFF", "0"), ("YBAYROFF", "0"),
                ("INSTRUME", $"'{camera.Name}'"),
            ]);

            Array.Sort(pixels);
            Console.WriteLine($"saved {Path.GetFullPath(path)}: {clock.Elapsed.TotalSeconds:F1} s, sensor {temperature:F1} -> {after:F1} C, "
                + $"min {pixels[0]} median {pixels[pixels.Length / 2]} p99.9 {pixels[(int)(pixels.Length * 0.999)]} max {pixels[^1]}");
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(frame);
            _ = camera.Close();
        }

        double ReadTemperature() => camera.GetControlValue(CMOSControlType.TemperatureDeci, out var deci, out _) is CMOSErrorCode.Success ? deci / 10.0 : double.NaN;

        static string F(double v) => v.ToString("0.0##", CultureInfo.InvariantCulture);

        static void Expect(CMOSErrorCode code, string what)
        {
            if (code is not CMOSErrorCode.Success)
            {
                Console.WriteLine($"  {what}: {code}");
            }
        }
    }

    /// <summary>A primary HDU of unsigned 16-bit pixels: BITPIX 16 with BZERO 32768, big-endian, padded to 2880 bytes.</summary>
    private static void WriteFits(string path, ushort[] pixels, int w, int h, (string Key, string Value)[] cards)
    {
        var header = new StringBuilder();
        void Card(string key, string value) => header.Append($"{key,-8}= {value,20}".PadRight(80));
        Card("SIMPLE", "T");
        Card("BITPIX", "16");
        Card("NAXIS", "2");
        Card("NAXIS1", w.ToString(CultureInfo.InvariantCulture));
        Card("NAXIS2", h.ToString(CultureInfo.InvariantCulture));
        Card("BZERO", "32768");
        Card("BSCALE", "1");
        foreach (var (key, value) in cards)
        {
            Card(key, value);
        }

        header.Append("END".PadRight(80));
        while (header.Length % 2880 != 0)
        {
            header.Append(' ');
        }

        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes(header.ToString()));
        var data = new byte[pixels.Length * 2];
        for (var i = 0; i < pixels.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), (short)(pixels[i] - 32768));
        }

        file.Write(data);
        file.Write(new byte[(2880 - data.Length % 2880) % 2880]);
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
