using System.Diagnostics;
using System.Runtime.InteropServices;
using SVBony.SDK;
using TianWen.DAL;

namespace SvbProbe;

/// <summary>
/// The video stream through the DAL members, as TianWen's DALCameraDriver drives it: the frame rate at full frame and
/// at a planetary window in RAW8 and RAW16, against the exposure path's; a pan; a stop and an exposure straight after.
/// The cooler is left off.
/// </summary>
internal static unsafe class VideoArm
{
    private static int _failures;

    internal static int Run(string[] args)
    {
        var exposureUs = IntOption(args, "--exposure-us", 2000);
        var seconds = IntOption(args, "--seconds", 5);
        var camera = new DeviceIterator().FirstOrDefault();
        if (camera.Name is null)
        {
            Console.WriteLine("no camera");
            return 2;
        }

        Expect(camera.Open(), "Open");
        try
        {
            Console.WriteLine($"{camera.Name}, {camera.MaxWidth} x {camera.MaxHeight}, {camera.PortType}, video {camera.CanVideoCapture}, pan while streaming {camera.CanPanRoiWhileStreaming}");
            Expect(camera.SetControlValue(CMOSControlType.Gain, 120) is CMOSErrorCode.Success, "Gain 120");
            Expect(camera.SetControlValue(CMOSControlType.BlackLevel, 20) is CMOSErrorCode.Success, "Offset 20");
            Expect(camera.SetControlValue(CMOSControlType.Exposure, exposureUs) is CMOSErrorCode.Success, $"exposure {exposureUs} us");
            _ = camera.GetControlValue(CMOSControlType.HighSpeedMode, out var highSpeed, out _);
            Console.WriteLine($"exposure {exposureUs} us, gain 120, offset 20, high speed {highSpeed}, {seconds} s a stream");

            var centreX = (camera.MaxWidth / 2 - 320) & ~1;
            var centreY = (camera.MaxHeight / 2 - 240) & ~1;
            foreach (var (w, h, x, y, format) in new (int, int, int, int, PixelDataFormat)[]
            {
                (camera.MaxWidth, camera.MaxHeight, 0, 0, PixelDataFormat.RAW16),
                (camera.MaxWidth, camera.MaxHeight, 0, 0, PixelDataFormat.RAW8),
                (640, 480, centreX, centreY, PixelDataFormat.RAW16),
                (640, 480, centreX, centreY, PixelDataFormat.RAW8),
            })
            {
                Expect(camera.SetROIFormat(w, h, 1, format) is CMOSErrorCode.Success, $"{w} x {h} {format}");
                Expect(camera.SetStartPosition(x, y) is CMOSErrorCode.Success, $"start {x}, {y}");
                Stream(camera, w, h, format, seconds, $"{w} x {h} {format}");
                Exposures(camera, w, h, format, 20, $"{w} x {h} {format}, single exposures");
            }

            // A pan at the planetary window: stop, move, start, and the time to the first frame after.
            var bytes = 640 * 480;
            var buffer = NativeMemory.Alloc((nuint)bytes);
            try
            {
                Expect(camera.StartVideoCapture() is CMOSErrorCode.Success, "start for the pan");
                _ = Read(camera, buffer, bytes, 10);
                var sw = Stopwatch.StartNew();
                Expect(camera.StopVideoCapture() is CMOSErrorCode.Success, "stop for the pan");
                var stopped = sw.ElapsedMilliseconds;
                Expect(camera.SetStartPosition(centreX + 64, centreY + 32) is CMOSErrorCode.Success, "pan by (64, 32)");
                var moved = sw.ElapsedMilliseconds;
                Expect(camera.StartVideoCapture() is CMOSErrorCode.Success, "start after the pan");
                var started = sw.ElapsedMilliseconds;
                var first = Read(camera, buffer, bytes, 1) ? sw.ElapsedMilliseconds : -1;
                Console.WriteLine($"pan: stopped {stopped} ms, moved {moved} ms, started {started} ms, first frame {first} ms");
                Expect(camera.SetStartPosition(centreX, centreY) is CMOSErrorCode.VideoModeActive, "a window move while streaming is refused");

                // Stop, and an exposure straight after: the soft trigger stream must be back.
                Expect(camera.StopVideoCapture() is CMOSErrorCode.Success, "stop");
                Expect(camera.SetControlValue(CMOSControlType.Exposure, 200_000) is CMOSErrorCode.Success, "exposure 200 ms");
                sw.Restart();
                Expect(camera.StartLightExposure() is CMOSErrorCode.Success, "an exposure after the stream");
                ExposureStatus status;
                while (camera.GetExposureStatus(out status) is CMOSErrorCode.Success && status is ExposureStatus.Working && sw.ElapsedMilliseconds < 10_000)
                {
                    Thread.Sleep(5);
                }

                var took = sw.ElapsedMilliseconds;
                Expect(status is ExposureStatus.Success && camera.GetDataAfterExposure((nint)buffer, bytes) is CMOSErrorCode.Success, $"its frame ({status})");
                Expect(took >= 200, $"it takes its exposure time ({took} ms)");
                Console.WriteLine($"exposure after the stream: {took} ms for 200 ms, unasked frames dropped {camera.UnaskedFramesDropped}");
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        finally
        {
            _ = camera.StopVideoCapture();
            Expect(camera.Close(), "Close");
        }

        Console.WriteLine(_failures == 0 ? "video arm: all passed" : $"video arm: {_failures} failed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Stream(SVBCameraDevice camera, int w, int h, PixelDataFormat format, int seconds, string label)
    {
        var bytes = w * h * (format is PixelDataFormat.RAW16 ? 2 : 1);
        var buffer = NativeMemory.Alloc((nuint)bytes);
        try
        {
            var sw = Stopwatch.StartNew();
            Expect(camera.StartVideoCapture() is CMOSErrorCode.Success, $"{label}: start");
            var startMs = sw.ElapsedMilliseconds;
            Expect(camera.StartLightExposure() is CMOSErrorCode.VideoModeActive, $"{label}: an exposure while streaming is refused");
            long first = -1;
            int frames = 0, timeouts = 0;
            var other = CMOSErrorCode.Success;
            var mean = 0d;
            sw.Restart();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                var code = camera.GetVideoData((nint)buffer, bytes, 500);
                if (code is CMOSErrorCode.Success)
                {
                    if (frames++ == 0)
                    {
                        first = sw.ElapsedMilliseconds;
                        sw.Restart();
                    }

                    mean = Mean(buffer, w, h, format);
                }
                else if (code is CMOSErrorCode.Timeout)
                {
                    timeouts++;
                }
                else
                {
                    other = code;
                    break;
                }
            }

            var elapsed = sw.Elapsed.TotalSeconds;
            _ = camera.TryGetDroppedFrames(out var dropped);
            Expect(camera.StopVideoCapture() is CMOSErrorCode.Success, $"{label}: stop");
            Expect(other is CMOSErrorCode.Success && frames > 1, $"{label}: frames ({other})");
            Console.WriteLine($"{label}: start {startMs} ms, first frame {first} ms, {(frames - 1) / elapsed:F1} fps over {elapsed:F1} s ({frames} frames, {timeouts} timeouts, {dropped} dropped), last frame mean {mean:F0}");
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static void Exposures(SVBCameraDevice camera, int w, int h, PixelDataFormat format, int count, string label)
    {
        var bytes = w * h * (format is PixelDataFormat.RAW16 ? 2 : 1);
        var buffer = NativeMemory.Alloc((nuint)bytes);
        try
        {
            var sw = Stopwatch.StartNew();
            var taken = 0;
            for (var i = 0; i < count; i++)
            {
                if (camera.StartLightExposure() is not CMOSErrorCode.Success)
                {
                    break;
                }

                ExposureStatus status;
                while (camera.GetExposureStatus(out status) is CMOSErrorCode.Success && status is ExposureStatus.Working)
                {
                    Thread.Sleep(1);
                }

                if (status is ExposureStatus.Success && camera.GetDataAfterExposure((nint)buffer, bytes) is CMOSErrorCode.Success)
                {
                    taken++;
                }
            }

            Expect(taken == count, $"{label}: {taken} of {count}");
            Console.WriteLine($"{label}: {taken / sw.Elapsed.TotalSeconds:F1} fps");
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static bool Read(SVBCameraDevice camera, void* buffer, int bytes, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            CMOSErrorCode code;
            var waits = 0;
            while ((code = camera.GetVideoData((nint)buffer, bytes, 500)) is CMOSErrorCode.Timeout && ++waits < 10)
            {
            }

            if (code is not CMOSErrorCode.Success)
            {
                return false;
            }
        }

        return true;
    }

    private static double Mean(void* buffer, int w, int h, PixelDataFormat format)
    {
        double sum = 0;
        var n = 0;
        for (var y = 0; y < h; y += 8)
        {
            for (var x = 0; x < w; x += 8)
            {
                sum += format is PixelDataFormat.RAW16 ? ((ushort*)buffer)[y * w + x] : ((byte*)buffer)[y * w + x];
                n++;
            }
        }

        return sum / n;
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine($"FAIL: {what}");
        }
    }

    private static int IntOption(string[] args, string name, int fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var value) ? value : fallback;
    }
}
