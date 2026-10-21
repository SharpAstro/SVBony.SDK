using TianWen.DAL;
using static SVBony.SDK.SVBCamera;

namespace SVBony.SDK;

/// <summary>Which SVBONY control each DAL control is, the unit each is carried in, and the error mapping.</summary>
internal static class SvbControls
{
    /// <summary>
    /// Maps a DAL control onto its SVBONY control, false for one this vendor does not have, or must not be given.
    /// </summary>
    /// <remarks>
    /// <para><b><c>Gamma</c> is deliberately NOT mapped, though the camera has it.</b> The DAL driver writes 50 to it
    /// on every connect, a midpoint that suits a ZWO body, and on an SVBONY body gamma is applied to the RAW frame:
    /// 100 is the identity (and what the binding holds it at), 200 took a 804 ADU pedestal to 4. Mapped, every frame
    /// would be bent.</para>
    /// <para><c>Brightness</c> is the black level (<c>SVB_BLACK_LEVEL</c>, "Offset", 0 to 100 on the SV605CC), as
    /// ZWO's <c>ASI_BRIGHTNESS</c> is. <c>HighSpeedMode</c> is the frame speed, which has THREE levels; the DAL's
    /// switch reads it as off for low and on for high, and medium reads as off (<see cref="FromSdk"/>).</para>
    /// <para>The temperatures: <c>SVB_CURRENT_TEMPERATURE</c> is in tenths, already the DAL's
    /// <see cref="CMOSControlType.TemperatureDeci"/>; <c>SVB_TARGET_TEMPERATURE</c> is in tenths too, where the DAL's
    /// <see cref="CMOSControlType.TargetTemperature"/> is whole degrees, so it is divided and multiplied here.</para>
    /// </remarks>
    internal static bool TryMap(CMOSControlType controlType, out SVB_CONTROL_TYPE control)
    {
        switch (controlType)
        {
            case CMOSControlType.Gain: control = SVB_CONTROL_TYPE.SVB_GAIN; return true;
            case CMOSControlType.Exposure: control = SVB_CONTROL_TYPE.SVB_EXPOSURE; return true;
            case CMOSControlType.WB_R: control = SVB_CONTROL_TYPE.SVB_WB_R; return true;
            case CMOSControlType.WB_G: control = SVB_CONTROL_TYPE.SVB_WB_G; return true;
            case CMOSControlType.WB_B: control = SVB_CONTROL_TYPE.SVB_WB_B; return true;
            case CMOSControlType.Brightness: control = SVB_CONTROL_TYPE.SVB_BLACK_LEVEL; return true;
            case CMOSControlType.Flip: control = SVB_CONTROL_TYPE.SVB_FLIP; return true;
            case CMOSControlType.HighSpeedMode: control = SVB_CONTROL_TYPE.SVB_FRAME_SPEED_MODE; return true;
            case CMOSControlType.AutoMaxBrightness: control = SVB_CONTROL_TYPE.SVB_AUTO_TARGET_BRIGHTNESS; return true;
            case CMOSControlType.TemperatureDeci: control = SVB_CONTROL_TYPE.SVB_CURRENT_TEMPERATURE; return true;
            case CMOSControlType.TargetTemperature: control = SVB_CONTROL_TYPE.SVB_TARGET_TEMPERATURE; return true;
            case CMOSControlType.CoolerOn: control = SVB_CONTROL_TYPE.SVB_COOLER_ENABLE; return true;
            case CMOSControlType.CoolerPowerPercent: control = SVB_CONTROL_TYPE.SVB_COOLER_POWER; return true;
            default: control = default; return false;
        }
    }

    /// <summary>The highest frame speed, which the DAL's high speed switch turns on.</summary>
    private const int HighFrameSpeed = 2;

    /// <summary>A value in the SDK's unit, as the DAL carries it.</summary>
    internal static int FromSdk(CMOSControlType controlType, int value) => controlType switch
    {
        CMOSControlType.TargetTemperature => (int)System.Math.Round(value / 10d),
        CMOSControlType.HighSpeedMode => value >= HighFrameSpeed ? 1 : 0,
        _ => value,
    };

    /// <summary>A value as the DAL carries it, in the SDK's unit.</summary>
    internal static int ToSdk(CMOSControlType controlType, int value) => controlType switch
    {
        CMOSControlType.TargetTemperature => value * 10,
        CMOSControlType.HighSpeedMode => value != 0 ? HighFrameSpeed : 0,
        _ => value,
    };

    /// <summary>
    /// The SDK's error, as the DAL's. The codes up to <c>SVB_ERROR_GENERAL_ERROR</c> are the DAL's own in the DAL's
    /// order; the three after it have no DAL counterpart.
    /// </summary>
    internal static CMOSErrorCode ToDal(SVB_ERROR_CODE error) => error switch
    {
        SVB_ERROR_CODE.SVB_SUCCESS => CMOSErrorCode.Success,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_INDEX => CMOSErrorCode.InvalidIndex,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_ID => CMOSErrorCode.InvalidId,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_CONTROL_TYPE => CMOSErrorCode.InvalidControlType,
        SVB_ERROR_CODE.SVB_ERROR_CAMERA_CLOSED => CMOSErrorCode.CameraClosed,
        SVB_ERROR_CODE.SVB_ERROR_CAMERA_REMOVED => CMOSErrorCode.CameraRemoved,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_PATH => CMOSErrorCode.InvalidPath,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_FILEFORMAT => CMOSErrorCode.InvalidFileFormat,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_SIZE => CMOSErrorCode.InvalidSize,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_IMGTYPE => CMOSErrorCode.InvalidImageFormat,
        SVB_ERROR_CODE.SVB_ERROR_OUTOF_BOUNDARY => CMOSErrorCode.OutOfBoundary,
        SVB_ERROR_CODE.SVB_ERROR_TIMEOUT => CMOSErrorCode.Timeout,
        SVB_ERROR_CODE.SVB_ERROR_INVALID_SEQUENCE => CMOSErrorCode.InvalidSequence,
        SVB_ERROR_CODE.SVB_ERROR_BUFFER_TOO_SMALL => CMOSErrorCode.BufferTooSmall,
        SVB_ERROR_CODE.SVB_ERROR_VIDEO_MODE_ACTIVE => CMOSErrorCode.VideoModeActive,
        SVB_ERROR_CODE.SVB_ERROR_EXPOSURE_IN_PROGRESS => CMOSErrorCode.ExposureInProgress,
        _ => CMOSErrorCode.GeneralError,
    };
}
