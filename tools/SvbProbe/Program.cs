using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using static SVBony.SDK.SVBCamera;

namespace SvbProbe;

/// <summary>
/// Asks an SVBONY camera what it is and what its controls do to a RAW frame, through the raw SDK calls (the
/// <c>info</c> and <c>raw</c> arms). See the csproj for how to run it.
/// </summary>
internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        var arm = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (arm is not ("info" or "raw" or "dal" or "edge" or "cool" or "dark"))
        {
            Console.WriteLine("usage: SvbProbe info|raw|dal|edge|cool|dark [--exposure-ms N] [--gain N] [--offset N]");
            return 1;
        }

        if (arm is "cool")
        {
            return CoolArm.Run(args);
        }

        if (arm is "dark")
        {
            return DarkArm.Run(args);
        }

        if (arm is "dal")
        {
            Console.WriteLine($"SvbProbe dal, {DateTime.Now:yyyy-MM-dd HH:mm}, SDK {SVBGetSDKVersion()}");
            return DalArm.Run();
        }

        Console.WriteLine($"SvbProbe {arm}, {DateTime.Now:yyyy-MM-dd HH:mm}, SDK {SVBGetSDKVersion()}");
        var count = SVBGetNumOfConnectedCameras();
        Console.WriteLine($"cameras: {count}");
        if (count == 0)
        {
            return 2;
        }

        SVB_CAMERA_INFO info;
        Check(SVBGetCameraInfo(&info, 0), "GetCameraInfo");
        Console.WriteLine($"friendly '{AsciiOf(info.FriendlyName, 32)}' sn '{AsciiOf(info.CameraSN, 32)}' port '{AsciiOf(info.PortType, 32)}' device 0x{info.DeviceID:x} id {info.CameraID}");
        var id = info.CameraID;

        var sw = Stopwatch.StartNew();
        Check(SVBOpenCamera(id), "OpenCamera");
        Console.WriteLine($"open {sw.ElapsedMilliseconds} ms");
        try
        {
            return arm switch
            {
                "info" => Info(id),
                "edge" => EdgeArm.Run(id),
                _ => Raw(id, IntOption(args, "--exposure-ms", 100), IntOption(args, "--gain", 120), IntOption(args, "--offset", 20)),
            };
        }
        finally
        {
            _ = SVBStopVideoCapture(id);
            Check(SVBCloseCamera(id), "CloseCamera");
        }
    }

    private static int Info(int id)
    {
        SVB_CAMERA_PROPERTY p;
        Check(SVBGetCameraProperty(id, &p), "GetCameraProperty");
        var bins = new List<int>();
        for (var i = 0; i < 16 && p.SupportedBins[i] != 0; i++)
        {
            bins.Add(p.SupportedBins[i]);
        }

        var formats = new List<string>();
        for (var i = 0; i < 8 && p.SupportedVideoFormat[i] != (int)SVB_IMG_TYPE.SVB_IMG_END; i++)
        {
            formats.Add(((SVB_IMG_TYPE)p.SupportedVideoFormat[i]).ToString());
        }

        Console.WriteLine($"size {p.MaxWidth.Value} x {p.MaxHeight.Value}, colour {p.IsColorCam}, bayer {p.BayerPattern}, bins [{string.Join(", ", bins)}], formats [{string.Join(", ", formats)}], depth {p.MaxBitDepth}, trigger {p.IsTriggerCam}");

        SVB_CAMERA_PROPERTY_EX px;
        Console.WriteLine($"propertyEx {SVBGetCameraPropertyEx(id, &px)}: pulse guide {px.bSupportPulseGuide}, temperature control {px.bSupportControlTemp}");
        float pixel;
        Console.WriteLine($"pixel size {SVBGetSensorPixelSize(id, &pixel)}: {pixel} um");
        int canGuide;
        Console.WriteLine($"can pulse guide {SVBCanPulseGuide(id, &canGuide)}: {canGuide}");
        SVB_SN sn;
        Console.WriteLine($"serial {SVBGetSerialNumber(id, &sn)}: '{AsciiOf(sn.id, 64)}'");
        var fw = stackalloc byte[64];
        Console.WriteLine($"firmware {SVBGetCameraFirmwareVersion(id, fw)}: '{AsciiOf(fw, 64)}'");
        int upgrade;
        var min = stackalloc byte[64];
        Console.WriteLine($"needs upgrade {SVBIsCameraNeedToUpgrade(id, &upgrade, min)}: {upgrade} (min '{AsciiOf(min, 64)}')");
        SVB_CAMERA_MODE mode;
        Console.WriteLine($"mode {SVBGetCameraMode(id, &mode)}: {mode}");
        SVB_SUPPORTED_MODE modes;
        if (SVBGetCameraSupportMode(id, &modes) is SVB_ERROR_CODE.SVB_SUCCESS)
        {
            var list = new List<string>();
            for (var i = 0; i < 16 && modes.SupportedCameraMode[i] != (int)SVB_CAMERA_MODE.SVB_MODE_END; i++)
            {
                list.Add(((SVB_CAMERA_MODE)modes.SupportedCameraMode[i]).ToString());
            }

            Console.WriteLine($"modes [{string.Join(", ", list)}]");
        }

        SVB_IMG_TYPE type;
        Console.WriteLine($"output type {SVBGetOutputImageType(id, &type)}: {type}");
        int x, y, w, h, bin, binMode;
        Console.WriteLine($"roi {SVBGetROIFormatEx(id, &x, &y, &w, &h, &bin, &binMode)}: ({x}, {y}) {w} x {h} bin {bin} mode {binMode}");

        int n;
        Check(SVBGetNumOfControls(id, &n), "GetNumOfControls");
        Console.WriteLine($"controls: {n}");
        for (var i = 0; i < n; i++)
        {
            SVB_CONTROL_CAPS caps;
            Check(SVBGetControlCaps(id, i, &caps), "GetControlCaps");
            CLong value;
            int isAuto;
            var read = SVBGetControlValue(id, caps.ControlType, &value, &isAuto);
            Console.WriteLine($"  {caps.ControlType,-36} '{AsciiOf(caps.Name, 64)}' [{caps.MinValue.Value}, {caps.MaxValue.Value}] default {caps.DefaultValue.Value} auto {caps.IsAutoSupported} writable {caps.IsWritable} value {(read is SVB_ERROR_CODE.SVB_SUCCESS ? value.Value.ToString(CultureInfo.InvariantCulture) : read.ToString())}{(isAuto != 0 ? " (auto)" : "")}  -- {AsciiOf(caps.Description, 128)}");
        }

        return 0;
    }

    /// <summary>
    /// One soft-triggered RAW16 frame at the camera's settings, then one per control varied, each compared with
    /// the first per Bayer photosite: which controls touch a RAW frame, and the frame's quantum.
    /// </summary>
    private static int Raw(int id, int exposureMs, int gain, int offset)
    {
        SVB_CAMERA_PROPERTY p;
        Check(SVBGetCameraProperty(id, &p), "GetCameraProperty");
        var w = (int)p.MaxWidth.Value;
        var h = (int)p.MaxHeight.Value;
        var raw16 = p.IsColorCam != 0 ? SVB_IMG_TYPE.SVB_IMG_RAW16 : SVB_IMG_TYPE.SVB_IMG_Y16;

        Check(SVBSetAutoSaveParam(id, 0), "SetAutoSaveParam");
        Check(SVBSetOutputImageType(id, raw16), "SetOutputImageType");
        Check(SVBSetROIFormat(id, 0, 0, w, h, 1), "SetROIFormat");
        Check(SVBSetCameraMode(id, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT), "SetCameraMode");
        Check(SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_EXPOSURE, new CLong(exposureMs * 1000), 0), "SetExposure");
        Check(SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_GAIN, new CLong(gain), 0), "SetGain");
        Check(SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_BLACK_LEVEL, new CLong(offset), 0), "SetOffset");
        Check(SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_BAD_PIXEL_CORRECTION_ENABLE, new CLong(0), 0), "SetBadPixelCorrection");
        foreach (var wb in new[] { SVB_CONTROL_TYPE.SVB_WB_R, SVB_CONTROL_TYPE.SVB_WB_G, SVB_CONTROL_TYPE.SVB_WB_B })
        {
            Check(SVBSetControlValue(id, wb, new CLong(128), 0), "SetWB");
        }

        Console.WriteLine($"exposure {exposureMs} ms, gain {gain}, offset {offset}, bad pixel correction off, white balance 128");
        Check(SVBStartVideoCapture(id), "StartVideoCapture");

        var size = w * h * 2;
        var buffer = (ushort*)NativeMemory.Alloc((nuint)size);
        try
        {
            _ = Grab(id, buffer, size, w, h, exposureMs, "settle");
            var baseline = Grab(id, buffer, size, w, h, exposureMs, "baseline");
            _ = Grab(id, buffer, size, w, h, exposureMs, "baseline again");
            if (baseline is null)
            {
                return 1;
            }

            foreach (var (control, value) in new (SVB_CONTROL_TYPE, int)[]
            {
                (SVB_CONTROL_TYPE.SVB_WB_R, 200), (SVB_CONTROL_TYPE.SVB_WB_B, 200), (SVB_CONTROL_TYPE.SVB_WB_G, 200),
                (SVB_CONTROL_TYPE.SVB_GAMMA, 200), (SVB_CONTROL_TYPE.SVB_GAMMA_CONTRAST, 200), (SVB_CONTROL_TYPE.SVB_CONTRAST, 50),
                (SVB_CONTROL_TYPE.SVB_SHARPNESS, 50), (SVB_CONTROL_TYPE.SVB_SATURATION, 200),
                (SVB_CONTROL_TYPE.SVB_BAD_PIXEL_CORRECTION_ENABLE, 1), (SVB_CONTROL_TYPE.SVB_FRAME_SPEED_MODE, 0),
            })
            {
                CLong was;
                int auto;
                if (SVBGetControlValue(id, control, &was, &auto) is not SVB_ERROR_CODE.SVB_SUCCESS)
                {
                    Console.WriteLine($"{control}: not on this camera");
                    continue;
                }

                var set = SVBSetControlValue(id, control, new CLong(value), 0);
                CLong now;
                _ = SVBGetControlValue(id, control, &now, &auto);
                var first = Grab(id, buffer, size, w, h, exposureMs, $"{control} {was.Value} -> {now.Value} ({set}), first frame");
                if (first is not null)
                {
                    Console.WriteLine($"    vs baseline: {string.Join("  ", Enumerable.Range(0, 4).Select(i => $"{first.Value.Medians[i] / Math.Max(1, baseline.Value.Medians[i]):F3}"))}, pixels changed {Changed(buffer, w, h, baseline.Value.Copy):P3}");
                }

                var stats = Grab(id, buffer, size, w, h, exposureMs, "    second frame");
                if (stats is not null)
                {
                    Console.WriteLine($"    vs baseline: {string.Join("  ", Enumerable.Range(0, 4).Select(i => $"{stats.Value.Medians[i] / Math.Max(1, baseline.Value.Medians[i]):F3}"))}, pixels changed {Changed(buffer, w, h, baseline.Value.Copy):P3}");
                }

                _ = SVBSetControlValue(id, control, was, 0);
                _ = Grab(id, buffer, size, w, h, exposureMs, "    restored");
            }

            return 0;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private readonly record struct FrameStats(double[] Medians, ushort[] Copy);

    private static FrameStats? Grab(int id, ushort* buffer, int size, int w, int h, int exposureMs, string label)
    {
        // A frame still queued from an earlier trigger would answer this one; drain it first.
        var drained = 0;
        while (SVBGetVideoData(id, (byte*)buffer, new CLong(size), 0) is SVB_ERROR_CODE.SVB_SUCCESS)
        {
            drained++;
        }

        var sw = Stopwatch.StartNew();
        Check(SVBSendSoftTrigger(id), "SendSoftTrigger");
        SVB_ERROR_CODE got;
        var waits = 0;
        do
        {
            got = SVBGetVideoData(id, (byte*)buffer, new CLong(size), 500);
            waits++;
        }
        while (got is SVB_ERROR_CODE.SVB_ERROR_TIMEOUT && sw.ElapsedMilliseconds < exposureMs * 2 + 10_000);

        if (got is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            Console.WriteLine($"{label}: no frame ({got}) after {sw.ElapsedMilliseconds} ms");
            return null;
        }

        var span = new ReadOnlySpan<ushort>(buffer, w * h);
        var copy = span.ToArray();
        int min = int.MaxValue, max = 0, orAll = 0;
        foreach (var v in span)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            orAll |= v;
        }

        var quantum = orAll == 0 ? 0 : 1 << BitOperations.TrailingZeroCount(orAll);
        var medians = new double[4];
        for (var phase = 0; phase < 4; phase++)
        {
            var sample = new List<ushort>();
            for (var yy = phase / 2; yy < h; yy += 16)
            {
                for (var xx = phase % 2; xx < w; xx += 16)
                {
                    sample.Add(span[yy * w + xx]);
                }
            }

            sample.Sort();
            medians[phase] = sample[sample.Count / 2];
        }

        Console.WriteLine($"{label}: {sw.ElapsedMilliseconds} ms ({waits} waits, {drained} drained), min {min} max {max} quantum {quantum}, photosite medians (0,0) {medians[0]} (1,0) {medians[1]} (0,1) {medians[2]} (1,1) {medians[3]}");
        return new FrameStats(medians, copy);
    }

    private static double Changed(ushort* buffer, int w, int h, ushort[] baseline)
    {
        var changed = 0;
        for (var i = 0; i < w * h; i++)
        {
            if (Math.Abs(buffer[i] - baseline[i]) > 64)
            {
                changed++;
            }
        }

        return changed / (double)(w * h);
    }

    private static void Check(SVB_ERROR_CODE code, string what)
    {
        if (code is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            throw new InvalidOperationException($"{what}: {code}");
        }
    }

    private static int IntOption(string[] args, string name, int fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static class BitOperations
    {
        public static int TrailingZeroCount(int value) => System.Numerics.BitOperations.TrailingZeroCount(value);
    }
}
