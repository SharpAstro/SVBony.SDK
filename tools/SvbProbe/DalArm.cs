using System.Diagnostics;
using System.Runtime.InteropServices;
using SVBony.SDK;
using TianWen.DAL;

namespace SvbProbe;

/// <summary>
/// Drives the camera through the DAL members only, as TianWen's DALCameraDriver does: enumeration, the controls,
/// a full frame, a binned sub-frame, an abort, a run of short exposures and a second holder.
/// </summary>
internal static class DalArm
{
    private static int _failures;

    internal static int Run()
    {
        var cameras = new DeviceIterator().ToList();
        Console.WriteLine($"DAL listing: {cameras.Count} camera(s)");
        if (cameras.Count == 0)
        {
            return 2;
        }

        var camera = cameras[0];
        Console.WriteLine($"  id {camera.ID} name '{camera.Name}' serial '{camera.SerialNumber}' usb3 {camera.IsUSB3Device} sensor {camera.SensorModel ?? "(none)"}");

        var sw = Stopwatch.StartNew();
        Expect(camera.Open(), "Open");
        Console.WriteLine($"open {sw.ElapsedMilliseconds} ms, firmware {camera.FirmwareVersion}, raw-neutral writes refused [{string.Join(", ", camera.RawNeutralRefused)}], unasked frames dropped {camera.UnaskedFramesDropped}");
        try
        {
            Console.WriteLine($"{camera.MaxWidth} x {camera.MaxHeight}, {camera.BitDepth} bit, container scaled {camera.DeliversContainerScaledPixels}, {camera.PixelSize} um, bayer {camera.BayerPattern}, bins [{string.Join(", ", camera.SupportedBins)}], formats [{string.Join(", ", camera.SupportedPixelDataFormats)}]");
            Console.WriteLine($"cooler {camera.HasCooler}, ST4 {camera.HasST4Port}, trigger {camera.IsTriggerCamera}, three-channel WB {camera.HasThreeChannelWhiteBalance}");
            Console.WriteLine(camera.TryGetWhiteBalanceRange(out var wbMin, out var wbMax, out var wbNeutral) ? $"white balance [{wbMin}, {wbMax}] neutral {wbNeutral}" : "no white balance");
            foreach (var control in Enum.GetValues<CMOSControlType>())
            {
                if (camera.TryGetControlRange(control, out var min, out var max))
                {
                    var read = camera.GetControlValue(control, out var value, out var auto);
                    Console.WriteLine($"  {control,-20} [{min}, {max}] {(read is CMOSErrorCode.Success ? $"{value}{(auto ? " (auto)" : "")}" : read.ToString())}");
                }
                else
                {
                    var refused = camera.SetControlValue(control, 0);
                    Console.WriteLine($"  {control,-20} not mapped; a write answers {refused}");
                }
            }

            Expect(camera.SetControlValue(CMOSControlType.Gain, 120) is CMOSErrorCode.Success, "Gain 120");
            Expect(camera.SetControlValue(CMOSControlType.BlackLevel, 20) is CMOSErrorCode.Success, "Offset 20");

            Expect(camera.SetROIFormat(camera.MaxWidth, camera.MaxHeight, 1, PixelDataFormat.RAW16) is CMOSErrorCode.Success, "full frame RAW16");
            Expect(camera.SetStartPosition(0, 0) is CMOSErrorCode.Success, "start 0, 0");
            Expose(camera, 1000, "full frame, 1 s");

            Expect(camera.SetROIFormat(512, 512, 2, PixelDataFormat.RAW16) is CMOSErrorCode.Success, "512 x 512 bin 2");
            Expect(camera.SetStartPosition(100, 100) is CMOSErrorCode.Success, "start 100, 100");
            ReportSdkRoi(camera.ID);
            Expose(camera, 100, "512 x 512 bin 2 at (100, 100), 100 ms");

            Expect(camera.SetROIFormat(camera.MaxWidth, camera.MaxHeight, 1, PixelDataFormat.RAW16) is CMOSErrorCode.Success, "back to full frame");
            Expect(camera.GetStartPosition(out var sx, out var sy) is CMOSErrorCode.Success && sx == 0 && sy == 0, "full frame clamps its start to 0, 0");
            ReportSdkRoi(camera.ID);

            // Abort a 5 s exposure after one second, then expose again.
            Expect(camera.SetControlValue(CMOSControlType.Exposure, 5_000_000) is CMOSErrorCode.Success, "exposure 5 s");
            Expect(camera.StartLightExposure() is CMOSErrorCode.Success, "start 5 s");
            Thread.Sleep(1000);
            sw.Restart();
            Expect(camera.StopExposure() is CMOSErrorCode.Success, "stop");
            var stopMs = sw.ElapsedMilliseconds;
            Expect(camera.GetExposureStatus(out var afterStop) is CMOSErrorCode.Success && afterStop is ExposureStatus.Idle, $"idle after stop ({afterStop})");
            Console.WriteLine($"abort of a 5 s exposure after 1 s: stop took {stopMs} ms");
            var afterAbort = Expose(camera, 200, "after an abort, 200 ms");
            Expect(afterAbort >= 200, $"the exposure after an abort takes its exposure time ({afterAbort} ms)");
            Console.WriteLine($"unasked frames dropped so far {camera.UnaskedFramesDropped}");

            // A run of short exposures: the per-frame overhead.
            sw.Restart();
            for (var i = 0; i < 10; i++)
            {
                Expose(camera, 10, null);
            }

            Console.WriteLine($"10 x 10 ms full frames: {sw.ElapsedMilliseconds} ms, {sw.ElapsedMilliseconds / 10} ms each");

            // A second holder (a listing or a connect comparing serials) must not close the first one's camera.
            Expect(camera.Open(), "second Open");
            Expect(camera.Close(), "second Close");
            Expose(camera, 50, "after a second holder came and went, 50 ms");
        }
        finally
        {
            Expect(camera.Close(), "Close");
        }

        Expect(camera.GetExposureStatus(out _) is CMOSErrorCode.CameraClosed, "closed answers CameraClosed");
        Console.WriteLine(_failures == 0 ? "PASS" : $"FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static unsafe void ReportSdkRoi(int id)
    {
        int x, y, w, h, bin;
        var result = SVBCamera.SVBGetROIFormat(id, &x, &y, &w, &h, &bin);
        Console.WriteLine($"  the SDK's own window: {result} ({x}, {y}) {w} x {h} bin {bin}");
    }

    /// <summary>One exposure through the DAL; the milliseconds until it was ready, or -1.</summary>
    private static long Expose(SVBCameraDevice camera, int exposureMs, string? label)
    {
        Expect(camera.SetControlValue(CMOSControlType.Exposure, exposureMs * 1000) is CMOSErrorCode.Success, "exposure");
        Expect(camera.GetROIFormat(out var w, out var h, out var bin, out var format) is CMOSErrorCode.Success, "GetROIFormat");
        var sw = Stopwatch.StartNew();
        Expect(camera.StartLightExposure() is CMOSErrorCode.Success, "StartLightExposure");
        ExposureStatus status;
        var polls = 0;
        do
        {
            Thread.Sleep(20);
            polls++;
            Expect(camera.GetExposureStatus(out status) is CMOSErrorCode.Success, "GetExposureStatus");
        }
        while (status is ExposureStatus.Working && sw.ElapsedMilliseconds < exposureMs + 30_000);

        if (status is not ExposureStatus.Success)
        {
            Expect(false, $"{label}: status {status} after {sw.ElapsedMilliseconds} ms");
            return -1;
        }

        var ready = sw.ElapsedMilliseconds;
        var bytes = w * h * (format is PixelDataFormat.RAW16 ? 2 : 1);
        var buffer = new ushort[w * h];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Expect(camera.GetDataAfterExposure(handle.AddrOfPinnedObject(), bytes) is CMOSErrorCode.Success, "GetDataAfterExposure");
        }
        finally
        {
            handle.Free();
        }

        Expect(camera.GetExposureStatus(out var after) is CMOSErrorCode.Success && after is ExposureStatus.Idle, "idle after the read");
        if (label is null)
        {
            return ready;
        }

        int min = int.MaxValue, max = 0, orAll = 0;
        foreach (var v in buffer)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            orAll |= v;
        }

        var sorted = buffer.ToArray();
        Array.Sort(sorted);
        var quantum = orAll == 0 ? 0 : 1 << System.Numerics.BitOperations.TrailingZeroCount(orAll);
        Console.WriteLine($"{label}: {w} x {h} bin {bin} {format}, ready after {ready} ms ({polls} polls), min {min} median {sorted[sorted.Length / 2]} max {max} quantum {quantum}");
        return ready;
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine($"  FAILED: {what}");
        }
    }
}
