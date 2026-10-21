using System.Runtime.InteropServices;

namespace SVBony.SDK;

/// <summary>
/// The SVBONY camera API, written against <c>include/SVBCameraSDK.h</c> (v1.13.4), as the Player One and
/// ToupTek bindings are written against their headers rather than adapted from a vendor file. SVBONY ships
/// no C# binding at all.
/// </summary>
/// <remarks>
/// <para><b>Every struct is blittable and every import a <c>LibraryImport</c></b>, so the binding is AOT-clean:
/// the fixed <c>char</c> arrays are <c>fixed</c> buffers rather than marshalled arrays.</para>
/// <para><b>A C <c>long</c> is <see cref="CLong"/>, never <c>int</c> or <c>nint</c>.</b> Windows is LLP64, so a
/// <c>long</c> is 4 bytes on win-x64; the 64-bit Unixes are LP64, where it is 8. It appears in three structs'
/// LAYOUT (<see cref="SVB_CAMERA_PROPERTY"/>'s first two fields, <see cref="SVB_CONTROL_CAPS"/>'s range) and in
/// the ABI of the control value calls and <c>SVBGetVideoData</c>'s buffer size, so a wrong width moves every
/// field after it on Linux, or corrupts the argument after it on a 32-bit target.</para>
/// </remarks>
public static unsafe partial class SVBCamera
{
    /// <summary>
    /// <c>SVBCameraSDK.dll</c> on Windows and <c>libSVBCameraSDK.so</c> on Linux, which the runtime prefixes
    /// and suffixes by itself.
    /// </summary>
    private const string SvbLib = "SVBCameraSDK";

    public enum SVB_BAYER_PATTERN
    {
        SVB_BAYER_RG = 0,
        SVB_BAYER_BG,
        SVB_BAYER_GR,
        SVB_BAYER_GB,
    }

    public enum SVB_IMG_TYPE
    {
        SVB_IMG_RAW8 = 0,
        SVB_IMG_RAW10,
        SVB_IMG_RAW12,
        SVB_IMG_RAW14,
        SVB_IMG_RAW16,
        SVB_IMG_Y8,
        SVB_IMG_Y10,
        SVB_IMG_Y12,
        SVB_IMG_Y14,
        SVB_IMG_Y16,
        SVB_IMG_RGB24,
        SVB_IMG_RGB32,
        SVB_IMG_END = -1,
    }

    public enum SVB_GUIDE_DIRECTION
    {
        SVB_GUIDE_NORTH = 0,
        SVB_GUIDE_SOUTH,
        SVB_GUIDE_EAST,
        SVB_GUIDE_WEST,
    }

    public enum SVB_CAMERA_MODE
    {
        SVB_MODE_NORMAL = 0,
        SVB_MODE_TRIG_SOFT,
        SVB_MODE_TRIG_RISE_EDGE,
        SVB_MODE_TRIG_FALL_EDGE,
        SVB_MODE_TRIG_DOUBLE_EDGE,
        SVB_MODE_TRIG_HIGH_LEVEL,
        SVB_MODE_TRIG_LOW_LEVEL,
        SVB_MODE_END = -1,
    }

    /// <summary>
    /// The SDK's error codes. Up to <see cref="SVB_ERROR_GENERAL_ERROR"/> they are ZWO's ASI codes in ZWO's
    /// order, and so <see cref="TianWen.DAL.CMOSErrorCode"/>'s; the binding maps them by name all the same.
    /// </summary>
    public enum SVB_ERROR_CODE
    {
        SVB_SUCCESS = 0,
        SVB_ERROR_INVALID_INDEX,
        SVB_ERROR_INVALID_ID,
        SVB_ERROR_INVALID_CONTROL_TYPE,
        SVB_ERROR_CAMERA_CLOSED,
        SVB_ERROR_CAMERA_REMOVED,
        SVB_ERROR_INVALID_PATH,
        SVB_ERROR_INVALID_FILEFORMAT,
        SVB_ERROR_INVALID_SIZE,
        SVB_ERROR_INVALID_IMGTYPE,
        SVB_ERROR_OUTOF_BOUNDARY,
        SVB_ERROR_TIMEOUT,
        SVB_ERROR_INVALID_SEQUENCE,
        SVB_ERROR_BUFFER_TOO_SMALL,
        SVB_ERROR_VIDEO_MODE_ACTIVE,
        SVB_ERROR_EXPOSURE_IN_PROGRESS,
        SVB_ERROR_GENERAL_ERROR,
        SVB_ERROR_INVALID_MODE,
        SVB_ERROR_INVALID_DIRECTION,
        SVB_ERROR_UNKNOW_SENSOR_TYPE,
        SVB_ERROR_END,
    }

    public enum SVB_CONTROL_TYPE
    {
        SVB_GAIN = 0,
        SVB_EXPOSURE,
        SVB_GAMMA,
        SVB_GAMMA_CONTRAST,
        SVB_WB_R,
        SVB_WB_G,
        SVB_WB_B,
        SVB_FLIP,
        SVB_FRAME_SPEED_MODE,
        SVB_CONTRAST,
        SVB_SHARPNESS,
        SVB_SATURATION,
        SVB_AUTO_TARGET_BRIGHTNESS,
        SVB_BLACK_LEVEL,
        SVB_COOLER_ENABLE,
        SVB_TARGET_TEMPERATURE,
        SVB_CURRENT_TEMPERATURE,
        SVB_COOLER_POWER,
        SVB_BAD_PIXEL_CORRECTION_ENABLE,
        SVB_BAD_PIXEL_CORRECTION_THRESHOLD,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVB_CAMERA_INFO
    {
        public fixed byte FriendlyName[32];
        public fixed byte CameraSN[32];
        public fixed byte PortType[32];
        public uint DeviceID;
        public int CameraID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVB_CAMERA_PROPERTY
    {
        public CLong MaxHeight;
        public CLong MaxWidth;
        public int IsColorCam;
        public SVB_BAYER_PATTERN BayerPattern;

        /// <summary>1 is bin 1 and so on; 0 ends the list.</summary>
        public fixed int SupportedBins[16];

        /// <summary><see cref="SVB_IMG_TYPE"/> values; <see cref="SVB_IMG_TYPE.SVB_IMG_END"/> ends the list.</summary>
        public fixed int SupportedVideoFormat[8];

        /// <summary>The sensor's own bit width since SDK 1.12.7 (its change log), not the container's.</summary>
        public int MaxBitDepth;
        public int IsTriggerCam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVB_CAMERA_PROPERTY_EX
    {
        public int bSupportPulseGuide;
        public int bSupportControlTemp;
        public fixed int Unused[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVB_CONTROL_CAPS
    {
        public fixed byte Name[64];
        public fixed byte Description[128];
        public CLong MaxValue;
        public CLong MinValue;
        public CLong DefaultValue;
        public int IsAutoSupported;
        public int IsWritable;
        public SVB_CONTROL_TYPE ControlType;
        public fixed byte Unused[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVB_SN
    {
        public fixed byte id[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVB_SUPPORTED_MODE
    {
        /// <summary><see cref="SVB_CAMERA_MODE"/> values; <see cref="SVB_CAMERA_MODE.SVB_MODE_END"/> ends the list.</summary>
        public fixed int SupportedCameraMode[16];
    }

    // ---- Entry points -----------------------------------------------------------------------------

    [LibraryImport(SvbLib)]
    public static partial int SVBGetNumOfConnectedCameras();

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetCameraInfo(SVB_CAMERA_INFO* info, int index);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetCameraProperty(int cameraId, SVB_CAMERA_PROPERTY* property);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetCameraPropertyEx(int cameraId, SVB_CAMERA_PROPERTY_EX* property);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBOpenCamera(int cameraId);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBCloseCamera(int cameraId);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetNumOfControls(int cameraId, int* count);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetControlCaps(int cameraId, int controlIndex, SVB_CONTROL_CAPS* caps);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetControlValue(int cameraId, SVB_CONTROL_TYPE controlType, CLong* value, int* isAuto);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBSetControlValue(int cameraId, SVB_CONTROL_TYPE controlType, CLong value, int isAuto);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetOutputImageType(int cameraId, SVB_IMG_TYPE* imageType);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBSetOutputImageType(int cameraId, SVB_IMG_TYPE imageType);

    /// <summary>Width a multiple of 8 and height of 2, both in BINNED pixels; capture must be stopped.</summary>
    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBSetROIFormat(int cameraId, int startX, int startY, int width, int height, int bin);

    /// <summary><see cref="SVBSetROIFormat"/> with the bin mode: 0 averages, 1 sums.</summary>
    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBSetROIFormatEx(int cameraId, int startX, int startY, int width, int height, int bin, int mode);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetROIFormat(int cameraId, int* startX, int* startY, int* width, int* height, int* bin);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetROIFormatEx(int cameraId, int* startX, int* startY, int* width, int* height, int* bin, int* mode);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetDroppedFrames(int cameraId, int* dropped);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBStartVideoCapture(int cameraId);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBStopVideoCapture(int cameraId);

    /// <summary>
    /// Blocks up to <paramref name="waitMs"/> (-1 for ever) for a frame. The header warns the call CRASHES on a
    /// buffer too small for one frame, rather than refusing it.
    /// </summary>
    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetVideoData(int cameraId, byte* buffer, CLong bufferSize, int waitMs);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetCameraFirmwareVersion(int cameraId, byte* version64);

    [LibraryImport(SvbLib, EntryPoint = "SVBGetSDKVersion")]
    private static partial byte* SVBGetSDKVersionPtr();

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetCameraSupportMode(int cameraId, SVB_SUPPORTED_MODE* modes);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetCameraMode(int cameraId, SVB_CAMERA_MODE* mode);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBSetCameraMode(int cameraId, SVB_CAMERA_MODE mode);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBSendSoftTrigger(int cameraId);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetSerialNumber(int cameraId, SVB_SN* serial);

    /// <summary>A guide pulse of <paramref name="durationMs"/> milliseconds, timed by the camera.</summary>
    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBPulseGuide(int cameraId, SVB_GUIDE_DIRECTION direction, int durationMs);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBGetSensorPixelSize(int cameraId, float* pixelSize);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBCanPulseGuide(int cameraId, int* canPulseGuide);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBSetAutoSaveParam(int cameraId, int enable);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBIsCameraNeedToUpgrade(int cameraId, int* needsUpgrade, byte* minVersion64);

    [LibraryImport(SvbLib)]
    public static partial SVB_ERROR_CODE SVBRestoreDefaultParam(int cameraId);

    /// <summary>The SDK version string, as the SDK writes it (<c>1, 13, 0503</c> in its header's example).</summary>
    public static string SVBGetSDKVersion() => Marshal.PtrToStringAnsi((nint)SVBGetSDKVersionPtr()) ?? string.Empty;

    /// <summary>A NUL-terminated ASCII field of at most <paramref name="size"/> bytes, trimmed.</summary>
    public static string AsciiOf(byte* field, int size)
    {
        var length = 0;
        while (length < size && field[length] != 0)
        {
            length++;
        }

        return System.Text.Encoding.ASCII.GetString(field, length).Trim();
    }
}
