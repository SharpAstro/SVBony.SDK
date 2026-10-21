using TianWen.DAL;
using static SVBony.SDK.SVBCamera;

namespace SVBony.SDK;

/// <summary>
/// Enumerates the connected SVBONY cameras with NO open: <c>SVBGetCameraInfo</c> carries the name and the serial,
/// so a listing never touches a camera a driver holds, and costs nothing (the bench SV605CC's first open took 3.2 s).
/// </summary>
public sealed class DeviceIterator : NativeDeviceIteratorBase<SVBCameraDevice>
{
    protected override int DeviceCount() => SVBGetNumOfConnectedCameras();

    protected override unsafe SVBCameraDevice? GetDeviceInfo(int index)
    {
        SVB_CAMERA_INFO info;
        if (SVBGetCameraInfo(&info, index) is not SVB_ERROR_CODE.SVB_SUCCESS)
        {
            return null;
        }

        var serial = AsciiOf(info.CameraSN, 32);
        return new SVBCameraDevice(
            info.CameraID,
            AsciiOf(info.FriendlyName, 32),
            serial.Length == 0 || serial.Trim('0').Length == 0 ? null : serial,
            AsciiOf(info.PortType, 32));
    }
}
