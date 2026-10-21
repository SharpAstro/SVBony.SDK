using System;
using System.Runtime.InteropServices;
using System.Threading;
using static SVBony.SDK.SVBCamera;

namespace SVBony.SDK;

/// <summary>
/// Keeps an SVBONY camera's cooler below <see cref="PowerCeiling"/> percent, by raising its target temperature a
/// degree at a time while the cooler runs at the ceiling, and lowering it again, more slowly, back to the target asked
/// for once the cooler has room.
/// </summary>
/// <remarks>
/// <para><b>Why a ceiling.</b> The camera regulates its own cooler to its target, and will drive it to 100 % when the
/// target is out of reach. The SDK has no power limit (<c>SVB_COOLER_POWER</c> is read only), so the only lever is the
/// target. The owner's rule for these bodies (2026-10-21): never past 90 %. An SV605CC held at 80 % for an hour ran
/// clean through a VIA VL817 hub, where harder cooling went with the stream stalls (TianWen #1749).</para>
/// <para><b>Slowly, both ways</b>, as every cooling change on this rig is made: a raise at most every
/// <see cref="RaiseInterval"/>, a lowering at most every <see cref="LowerInterval"/>, a degree each, and a lowering only
/// once the power has fallen to <see cref="PowerResume"/>, so the two cannot chase each other.</para>
/// <para><b>What a caller sees.</b> The target the governor wrote is the camera's target, and reads back as one, so a
/// frame records the setpoint the sensor was held at, never the one asked for. A caller's own write of the target is
/// the new target to come back to (<see cref="NoteAsked"/>). Nothing is done while the cooler is off.</para>
/// </remarks>
internal sealed class CoolerGovernor
{
    /// <summary>The cooler power the governor keeps the camera below, in percent.</summary>
    internal const int PowerCeiling = 90;

    /// <summary>The power the cooler must have fallen to before the target is lowered again, in percent.</summary>
    internal const int PowerResume = 80;

    /// <summary>One degree, in the SDK's tenths.</summary>
    private const int StepDeci = 10;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RaiseInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LowerInterval = TimeSpan.FromMinutes(2);

    private const int Unset = int.MinValue;

    private readonly int _id;
    private readonly int _maxTargetDeci;
    private readonly Timer _timer;
    private int _askedDeci = Unset;
    private int _busy;
    private long _lastChange;

    internal CoolerGovernor(int id, int maxTargetDeci)
    {
        _id = id;
        _maxTargetDeci = maxTargetDeci;
        _lastChange = Environment.TickCount64 - (long)LowerInterval.TotalMilliseconds;
        _timer = new Timer(static state => ((CoolerGovernor)state!).Tick(), this, TickInterval, TickInterval);
    }

    /// <summary>How far, in tenths, the target stands above the one asked for: 0 when the governor holds nothing up.</summary>
    internal int RaisedDeci { get; private set; }

    internal void NoteAsked(int targetDeci)
    {
        Volatile.Write(ref _askedDeci, targetDeci);
        RaisedDeci = 0;
    }

    /// <summary>Stops the ticks and waits for one under way, so nothing writes to a camera being closed.</summary>
    internal void Stop()
    {
        using var stopped = new ManualResetEvent(false);
        if (_timer.Dispose(stopped))
        {
            _ = stopped.WaitOne(TimeSpan.FromSeconds(2));
        }
    }

    private unsafe void Tick()
    {
        // A tick that outlasts the interval (an SDK call held up by a busy USB link) is not run over by the next.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            CLong enabled, power, target;
            int isAuto;
            if (SVBGetControlValue(_id, SVB_CONTROL_TYPE.SVB_COOLER_ENABLE, &enabled, &isAuto) is not SVB_ERROR_CODE.SVB_SUCCESS
                || enabled.Value == 0
                || SVBGetControlValue(_id, SVB_CONTROL_TYPE.SVB_COOLER_POWER, &power, &isAuto) is not SVB_ERROR_CODE.SVB_SUCCESS
                || SVBGetControlValue(_id, SVB_CONTROL_TYPE.SVB_TARGET_TEMPERATURE, &target, &isAuto) is not SVB_ERROR_CODE.SVB_SUCCESS)
            {
                return;
            }

            var current = (int)target.Value;

            // A target nobody set through this binding (the camera's own, kept from an earlier program) is the one asked for.
            _ = Interlocked.CompareExchange(ref _askedDeci, current, Unset);
            var asked = Volatile.Read(ref _askedDeci);
            var sinceChange = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastChange);

            int next;
            if (power.Value >= PowerCeiling && sinceChange >= RaiseInterval)
            {
                next = Math.Min(current + StepDeci, _maxTargetDeci);
            }
            else if (power.Value <= PowerResume && current > asked && sinceChange >= LowerInterval)
            {
                next = Math.Max(current - StepDeci, asked);
            }
            else
            {
                return;
            }

            if (next != current && SVBSetControlValue(_id, SVB_CONTROL_TYPE.SVB_TARGET_TEMPERATURE, new CLong(next), 0) is SVB_ERROR_CODE.SVB_SUCCESS)
            {
                _lastChange = Environment.TickCount64;
                RaisedDeci = Math.Max(0, next - asked);
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
