using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SVBony.SDK;
using TianWen.DAL;

namespace SvbProbe;

/// <summary>
/// The cooler under load, as a night drives it: continuous full-frame RAW16 exposures while the setpoint walks down to
/// each stage's target and holds there, every telemetry read and every frame logged with any error the SDK answers,
/// then a walk back up before the cooler is switched off. The question is what fails, and when, at high cooler power.
/// </summary>
/// <remarks>
/// <b>The setpoint is never stepped.</b> It starts at the sensor's own temperature and moves one degree every
/// <c>--ramp-s</c> seconds (30 by default, two degrees a minute) toward its target, down and back up alike (the owner,
/// 2026-10-21: always ramp slowly). A stop file (<c>--stop-file</c>) ends the stages early and still walks it back up.
/// </remarks>
internal static class CoolArm
{
    internal static int Run(string[] args)
    {
        var targets = (Option(args, "--targets") ?? "10,0,-5,-10").Split(',').Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        var holdMinutes = double.Parse(Option(args, "--minutes") ?? "5", CultureInfo.InvariantCulture);
        var exposureS = double.Parse(Option(args, "--exposure-s") ?? "10", CultureInfo.InvariantCulture);
        var gain = int.Parse(Option(args, "--gain") ?? "120", CultureInfo.InvariantCulture);
        var rampStep = TimeSpan.FromSeconds(double.Parse(Option(args, "--ramp-s") ?? "30", CultureInfo.InvariantCulture));
        var warmTo = int.Parse(Option(args, "--warm-to") ?? "20", CultureInfo.InvariantCulture);
        var log = Option(args, "--csv");
        var stopFile = Option(args, "--stop-file");
        var boxPort = Option(args, "--box");
        var boxLog = Option(args, "--box-csv");
        var holdPower = Option(args, "--hold-power") is { } hp ? int.Parse(hp, CultureInfo.InvariantCulture) : (int?)null;

        var camera = new DeviceIterator().FirstOrDefault();
        if (camera.Name is null)
        {
            Console.WriteLine("no camera");
            return 2;
        }

        using var csv = log is null ? null : new StreamWriter(log) { AutoFlush = true };
        csv?.WriteLine("utc,elapsed_s,target_c,event,sensor_c,setpoint_c,power_pct,cooler_on,box_v,box_a,box_w,box_min_v,box_max_a,box_age_s,detail");
        using var boxCsv = boxLog is null ? null : new StreamWriter(boxLog) { AutoFlush = true };
        boxCsv?.WriteLine("utc,volts,amps,watts,ambient_c,humidity_pct,dc2_5,usb_a_f,lens_c");
        using var box = boxPort is null ? null : new BoxMeter(boxPort, boxCsv);
        if (box is not null)
        {
            Console.WriteLine($"metering the supply through the power box on {boxPort} (read-only)");
        }
        var clock = Stopwatch.StartNew();
        var failures = 0;
        var buffer = 0;

        Console.WriteLine($"SvbProbe cool, {DateTime.Now:yyyy-MM-dd HH:mm}, {camera.Name} {camera.SerialNumber}, targets [{string.Join(", ", targets)}] C held {holdMinutes} min, "
            + $"setpoint 1 C per {rampStep.TotalSeconds} s, {exposureS} s exposures at gain {gain}, warm to {warmTo} C");
        if (!camera.Open())
        {
            Console.WriteLine("open failed");
            return 2;
        }

        var frame = Marshal.AllocHGlobal(camera.MaxWidth * camera.MaxHeight * 2);
        var setpoint = 0;
        var lastStep = TimeSpan.Zero;
        try
        {
            Expect(camera.SetROIFormat(camera.MaxWidth, camera.MaxHeight, 1, PixelDataFormat.RAW16), "SetROIFormat");
            Expect(camera.SetStartPosition(0, 0), "SetStartPosition");
            Expect(camera.SetControlValue(CMOSControlType.Gain, gain), "gain");
            Expect(camera.SetControlValue(CMOSControlType.Brightness, 20), "offset");
            Expect(camera.SetControlValue(CMOSControlType.Exposure, (int)(exposureS * 1_000_000)), "exposure");

            // Start the walk where the sensor IS, so switching the cooler on asks for no difference at all.
            _ = camera.GetControlValue(CMOSControlType.TemperatureDeci, out var startDeci, out _);
            setpoint = (int)Math.Round(startDeci / 10.0);
            Expect(camera.SetControlValue(CMOSControlType.TargetTemperature, setpoint), $"setpoint {setpoint}");
            Expect(camera.SetControlValue(CMOSControlType.CoolerOn, 1), "cooler on");
            lastStep = clock.Elapsed;
            Line(setpoint, "start");

            if (holdPower is { } want)
            {
                HoldPower(want, targets.Min());
            }

            foreach (var target in holdPower is null ? targets : [])
            {
                TimeSpan? reached = null;
                while (!Stopped() && (reached is null || clock.Elapsed - reached.Value < TimeSpan.FromMinutes(holdMinutes)))
                {
                    Expose(target);
                    if (setpoint == target)
                    {
                        reached ??= clock.Elapsed;
                    }
                }

                if (Stopped())
                {
                    Console.WriteLine("stop file seen: walking the setpoint back up");
                    break;
                }
            }

            // Back up at the same rate, then wait for the sensor to follow before the cooler goes off.
            var settled = TimeSpan.Zero;
            while (setpoint < warmTo || ReadSensor() < warmTo - 2)
            {
                Expose(warmTo);
                if (setpoint >= warmTo && settled == TimeSpan.Zero)
                {
                    settled = clock.Elapsed;
                }

                if (settled != TimeSpan.Zero && clock.Elapsed - settled > TimeSpan.FromMinutes(5))
                {
                    break;
                }
            }
        }
        finally
        {
            _ = camera.SetControlValue(CMOSControlType.CoolerOn, 0);
            Line(setpoint, "cooler-off");
            Marshal.FreeHGlobal(frame);
            _ = camera.Close();
        }

        Console.WriteLine(failures == 0 ? $"no failures, {buffer} frames" : $"{failures} failure(s), {buffer} frames");
        return failures == 0 ? 0 : 1;

        bool Stopped() => stopFile is not null && File.Exists(stopFile);

        double ReadSensor() => camera.GetControlValue(CMOSControlType.TemperatureDeci, out var deci, out _) is CMOSErrorCode.Success ? deci / 10.0 : double.NaN;

        int ReadPower() => camera.GetControlValue(CMOSControlType.CoolerPowerPercent, out var power, out _) is CMOSErrorCode.Success ? power : -1;

        // --hold-power: walk down (never below floor) until the cooler draws the wanted power, then hold it there for the
        // stage's minutes, moving the setpoint at most one degree every two minutes when the power strays 8 points.
        void HoldPower(int want, int floor)
        {
            var target = floor;
            TimeSpan? reached = null;
            var lastNudge = TimeSpan.Zero;
            while (!Stopped() && (reached is null || clock.Elapsed - reached.Value < TimeSpan.FromMinutes(holdMinutes)))
            {
                Expose(target);
                var power = ReadPower();
                if (reached is null)
                {
                    if (power >= want || setpoint == floor)
                    {
                        reached = lastNudge = clock.Elapsed;
                        target = setpoint;
                        Console.WriteLine($"power {power}% at setpoint {setpoint}: holding {holdMinutes} min");
                    }
                }
                else if (clock.Elapsed - lastNudge >= TimeSpan.FromMinutes(2))
                {
                    lastNudge = clock.Elapsed;
                    target = power > want + 8 ? setpoint + 1 : power < want - 8 && setpoint > floor ? setpoint - 1 : setpoint;
                }
            }

            if (Stopped())
            {
                Console.WriteLine("stop file seen: walking the setpoint back up");
            }
        }

        // One degree toward the target once rampStep has passed since the last move, never more.
        void Walk(int target)
        {
            if (setpoint == target || clock.Elapsed - lastStep < rampStep)
            {
                return;
            }

            setpoint += Math.Sign(target - setpoint);
            lastStep = clock.Elapsed;
            Expect(camera.SetControlValue(CMOSControlType.TargetTemperature, setpoint), $"setpoint {setpoint}");
        }

        void Expose(int target)
        {
            Walk(target);
            var start = camera.StartLightExposure();
            if (start is not CMOSErrorCode.Success)
            {
                failures++;
                Line(target, "start-fail", start.ToString());
                Thread.Sleep(2000);
                return;
            }

            var exposureClock = Stopwatch.StartNew();
            var lastTelemetry = TimeSpan.Zero;
            ExposureStatus status;
            do
            {
                Thread.Sleep(100);
                Walk(target);
                var read = camera.GetExposureStatus(out status);
                if (read is not CMOSErrorCode.Success)
                {
                    failures++;
                    Line(target, "status-err", read.ToString());
                }

                if (exposureClock.Elapsed - lastTelemetry >= TimeSpan.FromSeconds(2))
                {
                    lastTelemetry = exposureClock.Elapsed;
                    Line(target, "exposing");
                }
            }
            while (status is ExposureStatus.Working && exposureClock.Elapsed.TotalSeconds < exposureS + 30);

            if (status is not ExposureStatus.Success)
            {
                failures++;
                Line(target, "frame-lost", $"status {status} after {exposureClock.Elapsed.TotalSeconds:F1} s");
                _ = camera.StopExposure();
                return;
            }

            var took = exposureClock.Elapsed.TotalSeconds;
            var data = camera.GetDataAfterExposure(frame, camera.MaxWidth * camera.MaxHeight * 2);
            buffer++;
            if (data is not CMOSErrorCode.Success)
            {
                failures++;
            }

            Line(target, data is CMOSErrorCode.Success ? "frame" : "read-fail", $"#{buffer} {took:F2} s, {(data is CMOSErrorCode.Success ? Median(frame, camera.MaxWidth * camera.MaxHeight) : data)}");
        }

        void Line(int target, string what, string detail = "")
        {
            var t = camera.GetControlValue(CMOSControlType.TemperatureDeci, out var deci, out _);
            var s = camera.GetControlValue(CMOSControlType.TargetTemperature, out var sp, out _);
            var p = camera.GetControlValue(CMOSControlType.CoolerPowerPercent, out var power, out _);
            var o = camera.GetControlValue(CMOSControlType.CoolerOn, out var on, out _);
            var errors = string.Join(" ", new[] { ("temp", t), ("setpoint", s), ("power", p), ("on", o) }.Where(e => e.Item2 is not CMOSErrorCode.Success).Select(e => $"{e.Item1}={e.Item2}"));
            if (errors.Length > 0)
            {
                failures++;
            }

            // An on-flag of 0, or a camera setpoint that is not the one written, while the probe holds the cooler on is
            // the camera having forgotten its state: the reset the owner saw as cooler power suddenly 0.
            var forgot = what is not "cooler-off" && o is CMOSErrorCode.Success && (on == 0 || sp != setpoint) ? " FORGOT-STATE" : "";
            if (forgot.Length > 0)
            {
                failures++;
            }

            // The supply as the box last reported it, and the lowest voltage and highest current since the line before, so
            // a sag between two lines still shows. A camera that left the USB bus is named when something went wrong.
            var reading = box?.Latest;
            var (minV, maxA, frames) = box?.TakeExtremes() ?? (double.NaN, double.NaN, 0);
            var age = box?.Age is { } a && a != TimeSpan.MaxValue ? a.TotalSeconds : double.NaN;
            var supply = box is null ? "" : reading is null
                ? " box: no frame yet"
                : $" box {reading.Volts,5:F2} V {reading.Amps,5:F2} A {reading.Watts,6:F2} W ({(frames > 0 ? $"min {minV:F2} V max {maxA:F2} A over {frames} frames" : "no new frame")}, {age:F0} s old)";
            if (frames == 0)
            {
                (minV, maxA) = (double.NaN, double.NaN);
            }
            if (forgot.Length > 0 || what is "frame-lost" or "start-fail" or "read-fail" or "status-err")
            {
                detail += $" cameras-on-usb={SVBony.SDK.SVBCamera.SVBGetNumOfConnectedCameras()}";
            }

            static string F(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "";

            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {clock.Elapsed.TotalSeconds,7:F0}s target {target,3} {what,-10} sensor {deci / 10.0,6:F1} C setpoint {sp,4} power {power,3}% on {on}{supply} {errors}{forgot} {detail}");
            csv?.WriteLine(string.Join(",", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
                target, what, (deci / 10.0).ToString("F1", CultureInfo.InvariantCulture), sp, power, on,
                F(reading?.Volts ?? double.NaN), F(reading?.Amps ?? double.NaN), F(reading?.Watts ?? double.NaN), F(minV), F(maxA), F(age),
                $"\"{errors}{forgot} {detail}".TrimEnd() + "\""));
        }

        void Expect(CMOSErrorCode code, string what)
        {
            if (code is not CMOSErrorCode.Success)
            {
                failures++;
                Console.WriteLine($"  {what}: {code}");
            }
        }
    }

    private static unsafe string Median(nint buffer, int pixels)
    {
        var sample = new ushort[pixels / 97 + 1];
        var source = new ReadOnlySpan<ushort>((void*)buffer, pixels);
        for (var i = 0; i < sample.Length; i++)
        {
            sample[i] = source[Math.Min(pixels - 1, i * 97)];
        }

        Array.Sort(sample);
        return $"median {sample[sample.Length / 2]}";
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
