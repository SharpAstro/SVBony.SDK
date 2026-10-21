using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using TianWen.DAL;
using static SVBony.SDK.SVBCamera;

namespace SVBony.SDK;

/// <summary>
/// An SVBONY camera as the DAL drives it: the enumeration's identity, and everything else asked of the
/// <see cref="SvbSession"/> its <see cref="Open"/> starts.
/// </summary>
/// <remarks>
/// Before <see cref="Open"/> (and after the last <see cref="Close"/>) every sensor and capability member answers
/// zero, false or empty, as a closed ZWO body does: the camera's properties are read at open.
/// </remarks>
public readonly struct SVBCameraDevice : ICMOSNativeInterface
{
    internal SVBCameraDevice(int cameraId, string name, string? serialNumber, string portType)
    {
        ID = cameraId;
        Name = name;
        SerialNumber = serialNumber;
        PortType = portType;
    }

    // ---- Identity ---------------------------------------------------------------------------------------

    /// <summary>The SDK's camera id (<c>SVB_CAMERA_INFO.CameraID</c>), which every call takes.</summary>
    public int ID { get; }

    /// <summary>
    /// The camera's friendly name, <c>SVBONY SV605CC</c>: the spelling N.I.N.A. writes into <c>INSTRUME</c> through
    /// the same SDK, so calibration taken here pairs with an archive's lights.
    /// </summary>
    public string Name { get; }

    /// <summary>The same as <see cref="Name"/>: the SDK has no user-set id.</summary>
    public string CustomId => Name;

    /// <summary>
    /// The factory serial, read from the enumeration with no open (<c>SVB_CAMERA_INFO.CameraSN</c>,
    /// <c>01233EB8CC0101CBEE2409250004</c> on the bench SV605CC, the same string <c>SVBGetSerialNumber</c>
    /// returns once open), or null when the body has none.
    /// </summary>
    public string? SerialNumber { get; }

    /// <summary>The USB link as the SDK names it, <c>USB3.0</c> or <c>USB2.0</c>.</summary>
    public string PortType { get; }

    public bool IsUSB3Device => PortType.StartsWith("USB3", StringComparison.OrdinalIgnoreCase);

    /// <summary>The sensor die, from the product name (the SDK reports none).</summary>
    public string? SensorModel => SensorModelNames.TryGetSensorModel(Name, out var model) ? model : null;

    /// <summary>The camera firmware, once open (<c>v2.0.0.4</c> on the bench SV605CC).</summary>
    public string? FirmwareVersion => Session?.FirmwareVersion;

    // Counted (INativeDeviceInfo.Open): the first holder opens the camera and starts its stream, every later one
    // shares the session, and only the last close ends it.
    public bool Open() => SvbSession.Acquire(ID);

    public bool Close() => SvbSession.Release(ID);

    private SvbSession? Session => SvbSession.Find(ID);

    /// <summary>The raw-neutral writes the open could not make (see <c>SvbSession.Open</c>): empty when all took.</summary>
    public IReadOnlyList<SVB_CONTROL_TYPE> RawNeutralRefused => Session?.RawNeutralRefused ?? [];

    /// <summary>How many frames the stream sent unasked on a start, and the session dropped.</summary>
    public int UnaskedFramesDropped => Session?.UnaskedFramesDropped ?? 0;

    // ---- What the sensor is -----------------------------------------------------------------------------

    public int MaxHeight => Session?.MaxHeight ?? 0;

    public int MaxWidth => Session?.MaxWidth ?? 0;

    public int BitDepth => Session?.BitDepth ?? 0;

    /// <summary>
    /// True: the SDK hands RAW16 over LEFT-ALIGNED, so the 14-bit SV605CC spans 0..65532 in steps of 4.
    /// </summary>
    /// <remarks>
    /// Measured through this binding on the bench SV605CC (2026-10-21): every pixel of a RAW16 dark is a multiple
    /// of 4, and N.I.N.A.'s frames of the same camera through the same SDK (E:/Astro/2026-10-17) carry the same
    /// quantum and peak at 65532.
    /// </remarks>
    public bool DeliversContainerScaledPixels => true;

    public double PixelSize => Session?.PixelSize ?? 0d;

    /// <remarks>
    /// The SDK's pattern names the photosite at the window's origin: <c>SVB_BAYER_GR</c> on the SV605CC, the GRBG
    /// N.I.N.A. records for it.
    /// </remarks>
    public BayerPattern BayerPattern => Session is { IsColour: true } session
        ? session.Bayer switch
        {
            SVB_BAYER_PATTERN.SVB_BAYER_RG => BayerPattern.RGGB,
            SVB_BAYER_PATTERN.SVB_BAYER_BG => BayerPattern.BGGR,
            SVB_BAYER_PATTERN.SVB_BAYER_GR => BayerPattern.GRBG,
            SVB_BAYER_PATTERN.SVB_BAYER_GB => BayerPattern.GBRG,
            _ => throw new NotSupportedException($"Unsupported Bayer pattern: {session.Bayer}"),
        }
        : BayerPattern.Monochrome;

    public IReadOnlyList<int> SupportedBins => Session?.SupportedBins ?? [];

    /// <summary>
    /// The formats the DAL can carry. RAW10, RAW12 and RAW14 are left out: their container is not stated
    /// anywhere, and no SVBONY body measured here offers them.
    /// </summary>
    public IReadOnlyList<PixelDataFormat> SupportedPixelDataFormats
    {
        get
        {
            var list = new List<PixelDataFormat>(4);
            if (Session is { } session)
            {
                foreach (var format in session.SupportedFormats)
                {
                    if (ToDal(format) is { } dal && !list.Contains(dal))
                    {
                        list.Add(dal);
                    }
                }
            }

            return list;
        }
    }

    // ---- What the body can do ---------------------------------------------------------------------------

    public bool IsTriggerCamera => Session?.IsTriggerCamera ?? false;

    public bool HasMechanicalShutter => false;

    /// <summary>The temperature control the SDK reports (<c>bSupportControlTemp</c>).</summary>
    public bool HasCooler => Session?.HasCooler ?? false;

    /// <summary>The guide port the SDK reports (<c>bSupportPulseGuide</c>; the SV605CC has none).</summary>
    public bool HasST4Port => Session?.HasST4Port ?? false;

    public bool HasThreeChannelWhiteBalance => Session is { IsColour: true } session && session.Controls.ContainsKey(SVB_CONTROL_TYPE.SVB_WB_G);

    // ---- Controls ---------------------------------------------------------------------------------------

    /// <summary>Zero: the SDK states no conversion gain.</summary>
    public double ElectronPerADU => 0d;

    public bool TryGetControlRange(CMOSControlType ctrlType, out int min, out int max)
    {
        min = max = 0;
        if (Session is not { } session || !SvbControls.TryMap(ctrlType, out var control) || !session.Controls.TryGetValue(control, out var caps))
        {
            return false;
        }

        (min, max) = (SvbControls.FromSdk(ctrlType, caps.Min), SvbControls.FromSdk(ctrlType, caps.Max));
        return true;
    }

    /// <summary>
    /// The white balance scale, with the SDK's own default as neutral.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed (a vendor default can be a daylight balance): on the bench SV605CC, range [0, 511],
    /// default 128, a dark at 128 on all three channels has the same pedestal on every photosite colour and keeps
    /// the 14-bit quantum of 4, while 200 on any one channel lifts its photosites by 1.74 and lowers the others by
    /// 0.91, breaking the quantum.
    /// </remarks>
    public bool TryGetWhiteBalanceRange(out int min, out int max, out int neutral)
    {
        min = max = neutral = 0;
        if (Session is not { IsColour: true } session || !session.Controls.TryGetValue(SVB_CONTROL_TYPE.SVB_WB_R, out var caps))
        {
            return false;
        }

        (min, max, neutral) = (caps.Min, caps.Max, caps.Default);
        return true;
    }

    public unsafe CMOSErrorCode GetControlValue(CMOSControlType controlType, out int value, out bool isAuto)
    {
        value = 0;
        isAuto = false;

        // An unmapped control is refused before anything else, open or not: the driver writes one start-up list to
        // every vendor and reads a refusal as "not this vendor's" (TianWen's VendorControlContractTests).
        if (!SvbControls.TryMap(controlType, out var control))
        {
            return CMOSErrorCode.InvalidControlType;
        }

        if (Session is not { } session)
        {
            return CMOSErrorCode.CameraClosed;
        }

        if (!session.Controls.ContainsKey(control))
        {
            return CMOSErrorCode.InvalidControlType;
        }

        CLong raw;
        int auto;
        var error = SVBGetControlValue(ID, control, &raw, &auto);
        if (error is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            return SvbControls.ToDal(error);
        }

        value = SvbControls.FromSdk(controlType, (int)raw.Value);
        isAuto = auto != 0;
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode SetControlValue(CMOSControlType controlType, int value, bool isAuto = false)
    {
        if (!SvbControls.TryMap(controlType, out var control))
        {
            return CMOSErrorCode.InvalidControlType;
        }

        if (Session is not { } session)
        {
            return CMOSErrorCode.CameraClosed;
        }

        if (!session.Controls.TryGetValue(control, out var caps) || !caps.IsWritable)
        {
            return CMOSErrorCode.InvalidControlType;
        }

        return SvbControls.ToDal(SVBSetControlValue(ID, control, new CLong(SvbControls.ToSdk(controlType, value)), isAuto ? 1 : 0));
    }

    // ---- Guiding ----------------------------------------------------------------------------------------

    /// <summary>True: <c>SVBPulseGuide</c> takes the pulse's duration and the camera times it.</summary>
    public bool CanPulseGuideForDuration => true;

    /// <summary>Refused: the SDK has no untimed pulse; <see cref="PulseGuideOn(GuideDirection, TimeSpan)"/> is the call.</summary>
    public CMOSErrorCode PulseGuideOn(GuideDirection direction) => CMOSErrorCode.GeneralError;

    /// <summary>Nothing to stop: the camera ends a pulse it timed itself.</summary>
    public CMOSErrorCode PulseGuideOff(GuideDirection direction) => CMOSErrorCode.Success;

    /// <remarks>
    /// Not verified on hardware: the SV605CC has no guide port, so whether the SDK call returns at once or after
    /// the pulse is not known.
    /// </remarks>
    public CMOSErrorCode PulseGuideOn(GuideDirection direction, TimeSpan duration)
    {
        if (!HasST4Port)
        {
            return CMOSErrorCode.GeneralError;
        }

        var sdkDirection = direction switch
        {
            GuideDirection.North => SVB_GUIDE_DIRECTION.SVB_GUIDE_NORTH,
            GuideDirection.South => SVB_GUIDE_DIRECTION.SVB_GUIDE_SOUTH,
            GuideDirection.East => SVB_GUIDE_DIRECTION.SVB_GUIDE_EAST,
            GuideDirection.West => SVB_GUIDE_DIRECTION.SVB_GUIDE_WEST,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown guide direction"),
        };

        return SvbControls.ToDal(SVBPulseGuide(ID, sdkDirection, (int)Math.Clamp(Math.Round(duration.TotalMilliseconds), 0, int.MaxValue)));
    }

    // ---- Exposure ---------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a single exposure: one soft trigger on the stream the open started (see <see cref="SvbSession"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="StartDarkExposure"/> is the same call: the DAL's distinction is about a MECHANICAL shutter, and
    /// no SVBONY body has one, so a dark is made by capping the telescope.
    /// </remarks>
    public CMOSErrorCode StartLightExposure() => Session is { } session ? SvbControls.ToDal(session.BeginExposure()) : CMOSErrorCode.CameraClosed;

    /// <inheritdoc cref="StartLightExposure"/>
    public CMOSErrorCode StartDarkExposure() => StartLightExposure();

    public CMOSErrorCode StopExposure() => Session is { } session ? SvbControls.ToDal(session.CancelExposure()) : CMOSErrorCode.CameraClosed;

    public CMOSErrorCode GetExposureStatus(out ExposureStatus exposureStatus)
    {
        if (Session is not { } session)
        {
            exposureStatus = ExposureStatus.Idle;
            return CMOSErrorCode.CameraClosed;
        }

        exposureStatus = session.ExposureStatus;
        return CMOSErrorCode.Success;
    }

    // ---- Region of interest -----------------------------------------------------------------------------

    public CMOSErrorCode GetStartPosition(out int startX, out int startY)
    {
        if (Session is not { } session)
        {
            startX = startY = 0;
            return CMOSErrorCode.CameraClosed;
        }

        (startX, startY) = (session.StartX, session.StartY);
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode SetStartPosition(int startX, int startY)
        => Session is { } session ? SvbControls.ToDal(session.SetStart(startX, startY)) : CMOSErrorCode.CameraClosed;

    public CMOSErrorCode GetROIFormat(out int width, out int height, out int bin, out PixelDataFormat pixelDataFormat)
    {
        if (Session is not { } session)
        {
            width = height = bin = 0;
            pixelDataFormat = PixelDataFormat.RAW8;
            return CMOSErrorCode.CameraClosed;
        }

        (width, height, bin) = (session.Width, session.Height, session.Bin);
        pixelDataFormat = ToDal(session.Format) ?? PixelDataFormat.RAW8;
        return CMOSErrorCode.Success;
    }

    /// <summary>Sets the window, in BINNED pixels: a width a multiple of 8 and a height of 2 (<see cref="GetRoiSteps"/>).</summary>
    public CMOSErrorCode SetROIFormat(int width, int height, int bin, PixelDataFormat pixelDataFormat)
    {
        if (Session is not { } session)
        {
            return CMOSErrorCode.CameraClosed;
        }

        var format = pixelDataFormat switch
        {
            PixelDataFormat.RAW8 => session.IsColour ? SVB_IMG_TYPE.SVB_IMG_RAW8 : SVB_IMG_TYPE.SVB_IMG_Y8,
            PixelDataFormat.RAW16 => session.IsColour ? SVB_IMG_TYPE.SVB_IMG_RAW16 : SVB_IMG_TYPE.SVB_IMG_Y16,
            PixelDataFormat.Y8 => SVB_IMG_TYPE.SVB_IMG_Y8,
            PixelDataFormat.RGB24 => SVB_IMG_TYPE.SVB_IMG_RGB24,
            _ => SVB_IMG_TYPE.SVB_IMG_END,
        };

        return format is SVB_IMG_TYPE.SVB_IMG_END || !session.SupportedFormats.Contains(format)
            ? CMOSErrorCode.InvalidImageFormat
            : SvbControls.ToDal(session.Configure(width, height, bin, format));
    }

    /// <summary>The header's rule: a window's width a multiple of 8, its height of 2.</summary>
    public void GetRoiSteps(out int widthStep, out int heightStep, out int originStepX, out int originStepY)
    {
        (widthStep, heightStep, originStepX, originStepY) = (8, 2, 1, 1);
    }

    // ---- The frame --------------------------------------------------------------------------------------

    public CMOSErrorCode GetDataAfterExposure(IntPtr buffer, int bufferSize)
        => Session is { } session ? SvbControls.ToDal(session.TakeFrame(buffer, bufferSize)) : CMOSErrorCode.CameraClosed;

    private static PixelDataFormat? ToDal(SVB_IMG_TYPE format) => format switch
    {
        SVB_IMG_TYPE.SVB_IMG_RAW8 => PixelDataFormat.RAW8,
        SVB_IMG_TYPE.SVB_IMG_RAW16 or SVB_IMG_TYPE.SVB_IMG_Y16 => PixelDataFormat.RAW16,
        SVB_IMG_TYPE.SVB_IMG_Y8 => PixelDataFormat.Y8,
        SVB_IMG_TYPE.SVB_IMG_RGB24 => PixelDataFormat.RGB24,
        _ => null,
    };
}
