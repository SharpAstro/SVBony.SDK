using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TianWen.DAL;
using static SVBony.SDK.SVBCamera;

namespace SVBony.SDK;

/// <summary>
/// One opened camera: what it reported at open, its window, and the exposure its frame reader is waiting for.
/// </summary>
/// <remarks>
/// <para><b>Why a class held in a registry, and not fields of the device struct.</b> The DAL contract is a
/// STRUCT, which <c>DALCameraDriver</c> calls on copies of. Player One and ZWO get away with that because every
/// call carries only an integer camera id. An SVBONY camera has live state an id cannot carry: the exposure in
/// flight and the frame its reader thread has taken. So the struct carries the id, and every copy reaches the
/// same session through <see cref="Find"/>.</para>
/// <para><b>This SDK has no single exposure.</b> Its only way to a frame is the video stream (start, then
/// <c>SVBGetVideoData</c>), so a single exposure is the stream in SOFT TRIGGER mode: the camera produces nothing
/// until <c>SVBSendSoftTrigger</c> asks for one frame, which is what its own <c>SVBSoftTriggerDemo</c> does and
/// what the DAL's start / poll / read shape needs. The stream is started at open and runs, idle, between
/// exposures; it is stopped only to change the window, which the SDK refuses while it runs.</para>
/// <para><b>The frame is read on a thread of its own.</b> <c>SVBGetVideoData</c> blocks for up to its wait, and
/// the header warns that its buffer is small and a frame not taken promptly is discarded, so a poll of the DAL's
/// <see cref="ICMOSNativeInterface.GetExposureStatus"/> cannot be what takes it. Each exposure starts one reader,
/// which waits in one-second steps until the frame arrives, the exposure is stopped, or the SDK answers anything
/// but a timeout. It has no deadline of its own: the driver gives a lost exposure up on ITS deadline, through
/// <see cref="SVBCameraDevice.StopExposure"/>.</para>
/// </remarks>
internal sealed unsafe class SvbSession
{
    private const int StateIdle = 0;
    private const int StateExposing = 1;
    private const int StateReady = 2;
    private const int StateFailed = 3;

    /// <summary>
    /// How long one wait of the frame reader lasts, so a stopped exposure ends its reader within it:
    /// <c>SVBStopVideoCapture</c> does NOT release a read blocked in <c>SVBGetVideoData</c> (a 4 s wait ran its
    /// full 4 s across a stop, on the bench SV605CC).
    /// </summary>
    private const int ReaderWaitMs = 250;

    private static readonly SharedSessions<int, SvbSession> Sessions = new SharedSessions<int, SvbSession>();

    private readonly byte* _frame;
    private readonly int _frameCapacity;
    private int _state = StateIdle;
    private int _generation;
    private int _frameBytes;
    private Thread? _reader;

    private SvbSession(int id, in SVB_CAMERA_PROPERTY property, in SVB_CAMERA_PROPERTY_EX propertyEx)
    {
        Id = id;
        MaxWidth = (int)property.MaxWidth.Value;
        MaxHeight = (int)property.MaxHeight.Value;
        IsColour = property.IsColorCam != 0;
        Bayer = property.BayerPattern;
        BitDepth = property.MaxBitDepth;
        IsTriggerCamera = property.IsTriggerCam != 0;
        HasCooler = propertyEx.bSupportControlTemp != 0;
        HasST4Port = propertyEx.bSupportPulseGuide != 0;

        var bins = new List<int>(16);
        for (var i = 0; i < 16 && property.SupportedBins[i] != 0; i++)
        {
            bins.Add(property.SupportedBins[i]);
        }

        SupportedBins = bins;

        var formats = new List<SVB_IMG_TYPE>(8);
        for (var i = 0; i < 8 && property.SupportedVideoFormat[i] != (int)SVB_IMG_TYPE.SVB_IMG_END; i++)
        {
            formats.Add((SVB_IMG_TYPE)property.SupportedVideoFormat[i]);
        }

        SupportedFormats = formats;

        float pixelSize;
        PixelSize = SVBGetSensorPixelSize(id, &pixelSize) is SVB_ERROR_CODE.SVB_SUCCESS ? pixelSize : 0d;

        var firmware = stackalloc byte[64];
        FirmwareVersion = SVBGetCameraFirmwareVersion(id, firmware) is SVB_ERROR_CODE.SVB_SUCCESS && AsciiOf(firmware, 64) is { Length: > 0 } fw
            ? fw
            : null;

        Controls = ReadControls(id);

        // The largest frame the body can deliver, in the widest format it offers, taken once: every window and
        // bin is smaller.
        _frameCapacity = checked(MaxWidth * MaxHeight * 3);
        _frame = (byte*)NativeMemory.Alloc((nuint)_frameCapacity);
    }

    internal int Id { get; }

    internal int MaxWidth { get; }

    internal int MaxHeight { get; }

    internal bool IsColour { get; }

    internal SVB_BAYER_PATTERN Bayer { get; }

    /// <summary>The SENSOR's bit width, which the SDK reports since 1.12.7 (14 on an SV605CC).</summary>
    internal int BitDepth { get; }

    internal bool IsTriggerCamera { get; }

    internal bool HasCooler { get; }

    internal bool HasST4Port { get; }

    internal double PixelSize { get; }

    internal string? FirmwareVersion { get; }

    internal IReadOnlyList<int> SupportedBins { get; }

    internal IReadOnlyList<SVB_IMG_TYPE> SupportedFormats { get; }

    /// <summary>The controls the camera lists, by type, with the range and default it declares.</summary>
    internal FrozenDictionary<SVB_CONTROL_TYPE, SvbControl> Controls { get; }

    internal static SvbSession? Find(int id) => Sessions.Find(id);

    internal static bool Acquire(int id) => Sessions.Acquire(id, Open) is not null;

    internal static bool Release(int id) => Sessions.Release(id, static (id, session) =>
    {
        if (session is null)
        {
            return SVBCloseCamera(id) is SVB_ERROR_CODE.SVB_SUCCESS;
        }

        session.Shutdown();
        return true;
    });

    /// <summary>
    /// Opens the camera, puts every control that touches a RAW frame where it touches nothing, and starts the
    /// stream in soft trigger mode.
    /// </summary>
    /// <remarks>
    /// <para><b>The SDK REMEMBERS settings across programs</b> (<c>SVBSetAutoSaveParam</c>, on by default, keeps
    /// a parameter file), so a camera arrives with whatever the last program left: the bench SV605CC came up with
    /// its exposure on AUTO and its white balance at red 220, blue 227 (2026-10-21). Saving is switched off, so
    /// nothing set here reaches another program, and every control below is put back on every open, so nothing
    /// another program set reaches a frame here.</para>
    /// <para><b>What touches a RAW16 frame, measured on that SV605CC</b> (a 200 ms dark at gain 120, offset 20,
    /// pedestal 804 on every photosite): the white balance (a digital gain per photosite colour; at the SDK
    /// default 128 the four colours agree and the 14-bit quantum of 4 holds, any other value breaks both), gamma
    /// (200 took the pedestal to 4; 100 is the identity), the gamma contrast the header lists but the camera does
    /// not (200 halved it) and sharpness (50 made a 1 % of pixels a kernel's). Contrast at its default, saturation,
    /// the frame speed and bad pixel correction left the frame alone; the correction is off all the same, since a
    /// dark library must see the hot pixels the lights will.</para>
    /// </remarks>
    private static SvbSession? Open(int id)
    {
        if (SVBOpenCamera(id) is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            return null;
        }

        SVB_CAMERA_PROPERTY property;
        SVB_CAMERA_PROPERTY_EX propertyEx;
        if (SVBGetCameraProperty(id, &property) is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            _ = SVBCloseCamera(id);
            return null;
        }

        if (SVBGetCameraPropertyEx(id, &propertyEx) is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            propertyEx = default;
        }

        var session = new SvbSession(id, property, propertyEx);
        if (!session.Start())
        {
            session.Shutdown();
            return null;
        }

        return session;
    }

    /// <remarks>
    /// <b>The window comes first</b>, because a control written before it may be refused: on the bench SV605CC a gain
    /// write straight after open answered <c>SVB_ERROR_GENERAL_ERROR</c>, while the offset and the exposure took, and
    /// the same gain took once a window was set (2026-10-21).
    /// </remarks>
    private bool Start()
    {
        _ = SVBSetAutoSaveParam(Id, 0);
        _ = SVBStopVideoCapture(Id);

        // The widest raw format the body offers, full frame, unbinned; the driver sets its own window before the
        // first exposure.
        var format = PreferredRawFormat();
        if (SVBSetOutputImageType(Id, format) is not SVB_ERROR_CODE.SVB_SUCCESS
            || SVBSetROIFormatEx(Id, 0, 0, MaxWidth, MaxHeight, 1, BinAverage) is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            return false;
        }

        (Format, StartX, StartY, Width, Height, Bin) = (format, 0, 0, MaxWidth, MaxHeight, 1);

        var refused = new List<SVB_CONTROL_TYPE>();
        foreach (var (control, value) in RawNeutral())
        {
            if (SVBSetControlValue(Id, control, new CLong(value), 0) is not SVB_ERROR_CODE.SVB_SUCCESS)
            {
                refused.Add(control);
            }
        }

        RawNeutralRefused = refused;

        // Auto exposure off, at the value it holds: the driver sets the exposure it wants before each frame.
        CLong exposure;
        int isAuto;
        if (SVBGetControlValue(Id, SVB_CONTROL_TYPE.SVB_EXPOSURE, &exposure, &isAuto) is SVB_ERROR_CODE.SVB_SUCCESS && isAuto != 0)
        {
            _ = SVBSetControlValue(Id, SVB_CONTROL_TYPE.SVB_EXPOSURE, exposure, 0);
        }

        return SVBSetCameraMode(Id, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT) is SVB_ERROR_CODE.SVB_SUCCESS
            && StartStream() is SVB_ERROR_CODE.SVB_SUCCESS;
    }

    /// <summary>The controls the open could not put at their raw-neutral value: empty when every write took.</summary>
    internal IReadOnlyList<SVB_CONTROL_TYPE> RawNeutralRefused { get; private set; } = [];

    /// <summary>
    /// <c>SVBSetROIFormatEx</c>'s bin mode that AVERAGES, keeping a binned frame on the unbinned ADU scale as the
    /// Player One and ToupTek bindings do.
    /// </summary>
    /// <remarks>
    /// Measured on the bench SV605CC (a 100 ms dark, 2026-10-21): bin 1 median 1184, bin 2 mode 0 1332, bin 2 mode 1
    /// 5333, so 0 averages and 1 sums, as the header says. The plain <c>SVBSetROIFormat</c> SUMS (a bin 2 window
    /// read a median of 3220 against 804 unbinned), so it is not used.
    /// </remarks>
    private const int BinAverage = 0;

    /// <summary>
    /// How long a started stream is given to send the frame it sends unasked.
    /// </summary>
    /// <remarks>
    /// <b>In soft trigger mode a stream that starts sends one frame nobody triggered.</b> Measured on the bench
    /// SV605CC (2026-10-21): 131 ms and 141 ms after two restarts, with no trigger since either. Taken by the next
    /// exposure's reader, it came back as that exposure, a 200 ms exposure "ready" in 94 ms. So every start of the
    /// stream waits for that frame and drops it.
    /// </remarks>
    private const int UnaskedFrameWaitMs = 500;

    /// <summary>Starts the stream and drops the frame it sends unasked (<see cref="UnaskedFrameWaitMs"/>).</summary>
    private SVB_ERROR_CODE StartStream()
    {
        var result = SVBStartVideoCapture(Id);
        if (result is SVB_ERROR_CODE.SVB_SUCCESS)
        {
            var bytes = FrameBytes;
            if (SVBGetVideoData(Id, _frame, new CLong(bytes), UnaskedFrameWaitMs) is SVB_ERROR_CODE.SVB_SUCCESS)
            {
                UnaskedFramesDropped++;
            }
        }

        return result;
    }

    /// <summary>How many frames a started stream sent unasked and the session dropped: for the probe.</summary>
    internal int UnaskedFramesDropped { get; private set; }

    private static readonly SVB_CONTROL_TYPE[] WhiteBalanceControls = [SVB_CONTROL_TYPE.SVB_WB_R, SVB_CONTROL_TYPE.SVB_WB_G, SVB_CONTROL_TYPE.SVB_WB_B];

    /// <summary>Each control that alters a RAW frame, at the value where it alters nothing (see <see cref="Open"/>).</summary>
    private IEnumerable<(SVB_CONTROL_TYPE Control, int Value)> RawNeutral()
    {
        foreach (var wb in WhiteBalanceControls)
        {
            if (Controls.TryGetValue(wb, out var caps))
            {
                yield return (wb, caps.Default);
            }
        }

        yield return (SVB_CONTROL_TYPE.SVB_GAMMA, 100);
        yield return (SVB_CONTROL_TYPE.SVB_GAMMA_CONTRAST, 100);
        yield return (SVB_CONTROL_TYPE.SVB_SHARPNESS, 0);
        if (Controls.TryGetValue(SVB_CONTROL_TYPE.SVB_CONTRAST, out var contrast))
        {
            yield return (SVB_CONTROL_TYPE.SVB_CONTRAST, contrast.Default);
        }

        if (Controls.TryGetValue(SVB_CONTROL_TYPE.SVB_SATURATION, out var saturation))
        {
            yield return (SVB_CONTROL_TYPE.SVB_SATURATION, saturation.Default);
        }

        yield return (SVB_CONTROL_TYPE.SVB_BAD_PIXEL_CORRECTION_ENABLE, 0);
    }

    private SVB_IMG_TYPE PreferredRawFormat()
    {
        var wide = IsColour ? SVB_IMG_TYPE.SVB_IMG_RAW16 : SVB_IMG_TYPE.SVB_IMG_Y16;
        var narrow = IsColour ? SVB_IMG_TYPE.SVB_IMG_RAW8 : SVB_IMG_TYPE.SVB_IMG_Y8;
        return SupportedFormats.Contains(wide) ? wide : narrow;
    }

    /// <summary>Stops the stream and any reader before closing, then frees the frame the reader wrote into.</summary>
    private void Shutdown()
    {
        _ = Interlocked.Increment(ref _generation);
        _ = SVBStopVideoCapture(Id);
        JoinReader();
        _ = SVBCloseCamera(Id);
        NativeMemory.Free(_frame);
    }

    // ---- The window -----------------------------------------------------------------------------------

    internal SVB_IMG_TYPE Format { get; private set; }

    internal int StartX { get; private set; }

    internal int StartY { get; private set; }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal int Bin { get; private set; }

    /// <summary>The bytes one frame of the current window and format takes.</summary>
    internal int FrameBytes => Width * Height * BytesPerPixel(Format);

    internal static int BytesPerPixel(SVB_IMG_TYPE format) => format switch
    {
        SVB_IMG_TYPE.SVB_IMG_RAW8 or SVB_IMG_TYPE.SVB_IMG_Y8 => 1,
        SVB_IMG_TYPE.SVB_IMG_RGB24 => 3,
        SVB_IMG_TYPE.SVB_IMG_RGB32 => 4,
        _ => 2,
    };

    /// <summary>
    /// Sets the window's size, bin and format, keeping its start where it still fits.
    /// </summary>
    /// <remarks>
    /// The SIZE wins and the start follows it, as in the ToupTek binding: the driver sets the format before the
    /// start, so a full-frame request arriving after a sub-frame at (100, 50) would otherwise ask for a window
    /// running off the sensor and be refused.
    /// </remarks>
    internal SVB_ERROR_CODE Configure(int width, int height, int bin, SVB_IMG_TYPE format)
    {
        var startX = Math.Clamp(StartX, 0, Math.Max(0, MaxWidth / bin - width));
        var startY = Math.Clamp(StartY, 0, Math.Max(0, MaxHeight / bin - height));
        return Apply(startX, startY, width, height, bin, format);
    }

    internal SVB_ERROR_CODE SetStart(int startX, int startY) => Apply(startX, startY, Width, Height, Bin, Format);

    /// <summary>Stops the stream (the SDK refuses a window or a format while it runs), sets both, starts it again.</summary>
    private SVB_ERROR_CODE Apply(int startX, int startY, int width, int height, int bin, SVB_IMG_TYPE format)
    {
        if (startX == StartX && startY == StartY && width == Width && height == Height && bin == Bin && format == Format)
        {
            return SVB_ERROR_CODE.SVB_SUCCESS;
        }

        if (Volatile.Read(ref _state) is StateExposing)
        {
            return SVB_ERROR_CODE.SVB_ERROR_EXPOSURE_IN_PROGRESS;
        }

        if (checked(width * height * BytesPerPixel(format)) > _frameCapacity)
        {
            return SVB_ERROR_CODE.SVB_ERROR_INVALID_SIZE;
        }

        _ = SVBStopVideoCapture(Id);
        JoinReader();

        var result = format == Format ? SVB_ERROR_CODE.SVB_SUCCESS : SVBSetOutputImageType(Id, format);
        if (result is SVB_ERROR_CODE.SVB_SUCCESS)
        {
            Format = format;
            result = SVBSetROIFormatEx(Id, startX, startY, width, height, bin, BinAverage);
            if (result is SVB_ERROR_CODE.SVB_SUCCESS)
            {
                (StartX, StartY, Width, Height, Bin) = (startX, startY, width, height, bin);
            }
        }

        var restart = StartStream();
        return result is SVB_ERROR_CODE.SVB_SUCCESS ? restart : result;
    }

    // ---- Exposure ---------------------------------------------------------------------------------------

    internal ExposureStatus ExposureStatus => Volatile.Read(ref _state) switch
    {
        StateExposing => ExposureStatus.Working,
        StateReady => ExposureStatus.Success,
        StateFailed => ExposureStatus.Failed,
        _ => ExposureStatus.Idle,
    };

    /// <summary>Asks the camera for one frame and starts the reader that takes it.</summary>
    /// <remarks>
    /// A frame still queued from an exposure nobody read is drained first, or this exposure's read would hand it
    /// back under this exposure's settings: a plausible picture with the wrong exposure time, not an error.
    /// </remarks>
    internal SVB_ERROR_CODE BeginExposure()
    {
        if (Volatile.Read(ref _state) is StateExposing)
        {
            return SVB_ERROR_CODE.SVB_ERROR_EXPOSURE_IN_PROGRESS;
        }

        JoinReader();
        var bytes = FrameBytes;
        for (var drained = 0; drained < 4 && SVBGetVideoData(Id, _frame, new CLong(bytes), 0) is SVB_ERROR_CODE.SVB_SUCCESS; drained++)
        {
        }

        var generation = Interlocked.Increment(ref _generation);
        _frameBytes = bytes;
        Volatile.Write(ref _state, StateExposing);
        var result = SVBSendSoftTrigger(Id);
        if (result is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            Volatile.Write(ref _state, StateIdle);
            return result;
        }

        var reader = new Thread(() => ReadFrame(generation, bytes))
        {
            IsBackground = true,
            Name = $"SVB frame reader {Id}",
        };
        _reader = reader;
        reader.Start();
        return SVB_ERROR_CODE.SVB_SUCCESS;
    }

    private void ReadFrame(int generation, int bytes)
    {
        while (Volatile.Read(ref _generation) == generation)
        {
            var result = SVBGetVideoData(Id, _frame, new CLong(bytes), ReaderWaitMs);
            if (result is SVB_ERROR_CODE.SVB_ERROR_TIMEOUT)
            {
                continue;
            }

            if (Volatile.Read(ref _generation) == generation)
            {
                _ = Interlocked.CompareExchange(ref _state, result is SVB_ERROR_CODE.SVB_SUCCESS ? StateReady : StateFailed, StateExposing);
            }

            return;
        }
    }

    /// <summary>
    /// Stops the exposure in flight. The SDK has no call for that, so the stream is stopped and started again:
    /// measured, the aborted trigger's frame never arrives after the restart (nothing came in the 8 s after a 5 s
    /// exposure was stopped at 1 s), only the frame a started stream sends unasked, which <see cref="StartStream"/>
    /// drops.
    /// </summary>
    internal SVB_ERROR_CODE CancelExposure()
    {
        _ = Interlocked.Increment(ref _generation);
        if (Interlocked.Exchange(ref _state, StateIdle) is not StateExposing)
        {
            return SVB_ERROR_CODE.SVB_SUCCESS;
        }

        _ = SVBStopVideoCapture(Id);
        JoinReader();
        return StartStream();
    }

    /// <summary>Copies the frame the reader took into <paramref name="buffer"/> and returns to idle.</summary>
    internal SVB_ERROR_CODE TakeFrame(nint buffer, int bufferSize)
    {
        if (Volatile.Read(ref _state) is not StateReady)
        {
            return SVB_ERROR_CODE.SVB_ERROR_INVALID_SEQUENCE;
        }

        var bytes = _frameBytes;
        if (bufferSize < bytes)
        {
            return SVB_ERROR_CODE.SVB_ERROR_BUFFER_TOO_SMALL;
        }

        new ReadOnlySpan<byte>(_frame, bytes).CopyTo(new Span<byte>((void*)buffer, bytes));
        Volatile.Write(ref _state, StateIdle);
        return SVB_ERROR_CODE.SVB_SUCCESS;
    }

    /// <summary>Waits for a reader that is ending (its generation has moved on, or the stream stopped).</summary>
    private void JoinReader()
    {
        if (Interlocked.Exchange(ref _reader, null) is { } reader && reader != Thread.CurrentThread)
        {
            _ = reader.Join(ReaderWaitMs * 3);
        }
    }

    // ---- Controls ---------------------------------------------------------------------------------------

    private static FrozenDictionary<SVB_CONTROL_TYPE, SvbControl> ReadControls(int id)
    {
        var controls = new Dictionary<SVB_CONTROL_TYPE, SvbControl>();
        int count;
        if (SVBGetNumOfControls(id, &count) is SVB_ERROR_CODE.SVB_SUCCESS)
        {
            for (var i = 0; i < count; i++)
            {
                SVB_CONTROL_CAPS caps;
                if (SVBGetControlCaps(id, i, &caps) is SVB_ERROR_CODE.SVB_SUCCESS)
                {
                    controls[caps.ControlType] = new SvbControl(
                        (int)caps.MinValue.Value, (int)caps.MaxValue.Value, (int)caps.DefaultValue.Value, caps.IsWritable != 0);
                }
            }
        }

        return controls.ToFrozenDictionary();
    }
}

/// <summary>A control's range and default as the camera declares them, in the SDK's own units.</summary>
internal readonly record struct SvbControl(int Min, int Max, int Default, bool IsWritable);
