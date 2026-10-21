# SVBony.SDK

SVBONY cameras from .NET, implementing SharpAstro's `TianWen.DAL` device abstraction so an SVBONY body is driven by
the same code that drives a ZWO, QHY, Player One or ToupTek one.

Vendor SDK: **SVBCameraSDK v1.13.4** (2025-02-05), Windows x64 and Linux x64, arm64 and arm (`lib/SOURCE.txt` names
each file and its sha256). The package's `MAJOR.MINOR` tracks it. The vendor drop has no macOS build. SVBONY's newer
ToupTek-based line (`svbonycam`) is not this SDK: it is a ToupTek brand, through ToupTek.SDK.

## Verified on hardware

An **SVBONY SV605CC** (IMX533 colour, GRBG, 3008 x 3008, 3.76 um, USB 3, firmware v2.0.0.4) on Windows x64 with
SVBONY's driver package 1.13.4, 2026-10-21, through `tools/SvbProbe`.

- **Pixels are 14-bit LEFT-ALIGNED in 16**: every RAW16 value is a multiple of 4, and N.I.N.A.'s frames of the same
  camera through the same SDK carry the same quantum and peak at 65532 (`DeliversContainerScaledPixels`).
- **The friendly name is the instrument**: `SVBONY SV605CC`, N.I.N.A.'s `INSTRUME` for the same camera, so
  calibration taken through this binding pairs with an archive's lights. The serial comes with the enumeration, so
  a listing opens nothing (an open takes 0.5 to 3 s).
- **Several controls alter a RAW frame**, measured on a 200 ms dark at gain 120, offset 20 (pedestal 804):
  - **white balance** is a digital gain per photosite colour: at the SDK default 128 the four colours agree and the
    quantum of 4 holds; 200 on one channel lifts it 1.74x and lowers the others 0.91x;
  - **gamma** (100 is the identity; 200 took the pedestal to 4), the **gamma contrast** the header lists and the
    camera does not (200 halved it), and **sharpness** (50 rewrote 1 % of pixels).

  The SDK also REMEMBERS settings across programs (`SVBSetAutoSaveParam`): the camera arrived with auto exposure on
  and white balance at red 220, blue 227. So every open switches saving off and puts each of these back where it
  alters nothing, with bad pixel correction (on by default) off. The DAL's `Gamma` is deliberately not mapped:
  TianWen's driver writes 50 to it on connect.
- **There is no single exposure in this SDK.** A frame comes only from the video stream, so an exposure is the stream
  in SOFT TRIGGER mode, one `SVBSendSoftTrigger` per frame, read by a thread of its own (`SVBGetVideoData` blocks).
  Three things that shapes, all measured:
  - a stream that STARTS sends one frame nobody triggered, 130 to 140 ms later; taken by the next exposure it came
    back as that exposure (a 200 ms exposure "ready" in 94 ms), so every start drops it;
  - `SVBStopVideoCapture` neither ends an exposure in flight nor releases a blocked `SVBGetVideoData`, so an abort
    is a stop and start (the aborted trigger's frame never arrives) and the reader waits in 250 ms steps;
  - a window, a bin or a format can be changed only with the stream stopped, and a gain written before the first
    window is set after an open is refused (`SVB_ERROR_GENERAL_ERROR`), so an open sets the window first.
- **Binning goes through `SVBSetROIFormatEx` in mode 0, which averages** (bin 1 median 1184, bin 2 mode 0 1332,
  mode 1 5333). The plain `SVBSetROIFormat` sums, which would put a binned frame on another ADU scale than the
  other vendors' bindings give.
- **Timing**: a full frame at 10 ms takes about 160 ms through the binding; the first exposure after an open or a
  window change about a second longer.
- **No guide port** on the SV605CC, so `PulseGuideOn(direction, duration)` (`SVBPulseGuide`, timed by the camera) is
  untested.

## The probe

`tools/SvbProbe` drives an attached camera, not part of the package or CI:

| Arm | What it does |
|---|---|
| `info` | Everything the SDK reports: properties, modes, every control's range, default and value |
| `raw` | Which controls change a RAW16 frame, per photosite colour |
| `edge` | Bin modes, and what an abort and a stream restart do |
| `dal` | The DAL members only, as TianWen's driver calls them: controls, full frame, binned window, abort, a run, a second holder |
| `cool` | The cooler under continuous exposures, the setpoint WALKED (1 C per 30 s) down to each `--targets` stage and back up before it is switched off; `--stop-file` ends it early and still walks back |

```
dotnet run --project tools/SvbProbe -c Release -- dal
```

## Licence

The binding is MIT. The native libraries (`lib/`), the SDK header (`include/`) and the vendor's samples and change log
(`reference/`) are proprietary, closed source code and remain the property of SVBONY; they are redistributed
unmodified, as shipped in SVBONY's Camera SDK, and are not covered by the MIT licence.
