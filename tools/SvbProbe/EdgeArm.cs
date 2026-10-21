using System.Diagnostics;
using System.Runtime.InteropServices;
using static SVBony.SDK.SVBCamera;

namespace SvbProbe;

/// <summary>
/// Two questions the DAL arm raised, asked of the raw SDK: does stopping and restarting the stream end a soft
/// trigger's exposure, or does its frame still arrive afterwards; and which bin mode averages.
/// </summary>
internal static unsafe class EdgeArm
{
    internal static int Run(int id)
    {
        SVB_CAMERA_PROPERTY p;
        Check(SVBGetCameraProperty(id, &p), "GetCameraProperty");
        var w = (int)p.MaxWidth.Value;
        var h = (int)p.MaxHeight.Value;
        Check(SVBSetAutoSaveParam(id, 0), "SetAutoSaveParam");
        _ = SVBStopVideoCapture(id);
        Check(SVBSetOutputImageType(id, SVB_IMG_TYPE.SVB_IMG_RAW16), "SetOutputImageType");
        Check(SVBSetCameraMode(id, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT), "SetCameraMode");
        Console.WriteLine($"gain with the stream stopped, no window set yet: {SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_GAIN, new CLong(120), 0)}");
        Console.WriteLine($"offset likewise: {SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_BLACK_LEVEL, new CLong(20), 0)}");
        Console.WriteLine($"exposure likewise: {SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_EXPOSURE, new CLong(100_000), 0)}");
        Check(SVBSetROIFormat(id, 0, 0, w, h, 1), "SetROIFormat full");
        Console.WriteLine($"gain after a window, stream stopped: {SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_GAIN, new CLong(120), 0)}");
        Check(SVBStartVideoCapture(id), "StartVideoCapture");
        Console.WriteLine($"gain with the stream running: {SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_GAIN, new CLong(120), 0)}, offset {SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_BLACK_LEVEL, new CLong(20), 0)}");

        var buffer = (byte*)NativeMemory.Alloc((nuint)(w * h * 2));
        try
        {
            foreach (var mode in new[] { 0, 1 })
            {
                _ = SVBStopVideoCapture(id);
                Check(SVBSetROIFormatEx(id, 0, 0, w / 2, h / 2, 2, mode), $"SetROIFormatEx bin 2 mode {mode}");
                int x, y, rw, rh, bin, readMode;
                _ = SVBGetROIFormatEx(id, &x, &y, &rw, &rh, &bin, &readMode);
                Check(SVBStartVideoCapture(id), "StartVideoCapture");
                Check(SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_EXPOSURE, new CLong(100_000), 0), "exposure");
                var median = Grab(id, buffer, rw * rh * 2, 5_000);
                Console.WriteLine($"bin 2 mode {mode} (reads back {readMode}, {rw} x {rh}): median {median}");
            }

            _ = SVBStopVideoCapture(id);
            Check(SVBSetROIFormat(id, 0, 0, w, h, 1), "SetROIFormat full");
            Check(SVBStartVideoCapture(id), "StartVideoCapture");
            Console.WriteLine($"bin 1: median {Grab(id, buffer, w * h * 2, 5_000)}");

            // Abort a 5 s exposure after 1 s by stopping and starting the stream, then see what arrives with no trigger.
            Check(SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_EXPOSURE, new CLong(5_000_000), 0), "exposure 5 s");
            var sw = Stopwatch.StartNew();
            Check(SVBSendSoftTrigger(id), "trigger");
            Thread.Sleep(1000);
            var stop = Stopwatch.StartNew();
            Check(SVBStopVideoCapture(id), "StopVideoCapture");
            var stopMs = stop.ElapsedMilliseconds;
            Check(SVBStartVideoCapture(id), "StartVideoCapture");
            Console.WriteLine($"stop took {stopMs} ms, stop and start {stop.ElapsedMilliseconds} ms");
            while (sw.ElapsedMilliseconds < 9_000)
            {
                var got = SVBGetVideoData(id, buffer, new CLong(w * h * 2), 500);
                if (got is SVB_ERROR_CODE.SVB_SUCCESS)
                {
                    Console.WriteLine($"  a frame arrived {sw.ElapsedMilliseconds} ms after the trigger, with no trigger since the restart");
                }
            }

            Console.WriteLine($"  waited {sw.ElapsedMilliseconds} ms after the trigger");

            // The same abort while a reader is blocked in GetVideoData: does StopVideoCapture release it?
            Check(SVBSendSoftTrigger(id), "trigger");
            var released = -1L;
            var reader = new Thread(() =>
            {
                var t = Stopwatch.StartNew();
                var got = SVBGetVideoData(id, buffer, new CLong(w * h * 2), 4_000);
                released = t.ElapsedMilliseconds;
                Console.WriteLine($"  the blocked read returned {got} after {released} ms");
            });
            reader.Start();
            Thread.Sleep(500);
            Check(SVBStopVideoCapture(id), "StopVideoCapture");
            reader.Join();
            Check(SVBStartVideoCapture(id), "StartVideoCapture");
            sw.Restart();
            while (sw.ElapsedMilliseconds < 6_000)
            {
                if (SVBGetVideoData(id, buffer, new CLong(w * h * 2), 500) is SVB_ERROR_CODE.SVB_SUCCESS)
                {
                    Console.WriteLine($"  a frame arrived {sw.ElapsedMilliseconds} ms after the second restart");
                }
            }

            // And a plain 200 ms exposure afterwards: how long it takes.
            Check(SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_EXPOSURE, new CLong(200_000), 0), "exposure 200 ms");
            sw.Restart();
            var m = Grab(id, buffer, w * h * 2, 5_000);
            Console.WriteLine($"200 ms after the aborts: median {m}, {sw.ElapsedMilliseconds} ms");
            return 0;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static int Grab(int id, byte* buffer, int bytes, int timeoutMs)
    {
        while (SVBGetVideoData(id, buffer, new CLong(bytes), 0) is SVB_ERROR_CODE.SVB_SUCCESS)
        {
        }

        Check(SVBSendSoftTrigger(id), "trigger");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (SVBGetVideoData(id, buffer, new CLong(bytes), 500) is SVB_ERROR_CODE.SVB_SUCCESS)
            {
                var pixels = new ReadOnlySpan<ushort>(buffer, bytes / 2).ToArray();
                Array.Sort(pixels);
                return pixels[pixels.Length / 2];
            }
        }

        return -1;
    }

    private static void Check(SVB_ERROR_CODE code, string what)
    {
        if (code is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            throw new InvalidOperationException($"{what}: {code}");
        }
    }
}
