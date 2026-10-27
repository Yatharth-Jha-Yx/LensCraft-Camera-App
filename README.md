# LensCraft

A lightweight native Windows 11 camera app (WinUI 3, C#, .NET 8). Built phase by phase from the LensCraft design spec.

**Current state: Phase 1 complete and running. Phase 2 (hardware info panel) C# compile-checked with two fixes (see "Phase 2 build fixes"); awaiting the full Windows build (F5) and the Phase 2 checklist.**

## Run it (first time with C#)

1. Install **Visual Studio 2022** (Community is free). In the installer, tick the **WinUI application development** workload. On older installers, tick **.NET desktop development** and, in its optional components, **Windows App SDK C# Templates**.
2. Turn on **Developer Mode**: Settings → System → For developers → Developer Mode → On. Windows needs this to install an app straight from Visual Studio.
3. Open `LensCraft.sln` in Visual Studio.
4. In the toolbar, set the platform dropdown to **x64** (or **ARM64** on a Snapdragon laptop). Leave the configuration on **Debug**.
5. Press **F5**. The first build downloads NuGet packages and takes a few minutes.
6. When Windows asks whether LensCraft can use the camera, choose **Yes**.

Command-line build check (compiles only; F5 is still the way to launch):

```
dotnet build LensCraft/LensCraft.csproj -p:Platform=x64
```

If a build error appears, paste the full error text back into the chat.

## Phase 1 checklist (spec §4, "Done when")

| Feature | How to verify |
|---|---|
| Camera picker | Gear icon → Camera dropdown lists every attached camera |
| Live preview | Preview fills the window, moves smoothly, black bars when shapes differ |
| Mirror toggle | Right button in the bottom pill flips the preview instantly; it starts ON |
| Saved photo not mirrored | Hold up text, take a photo, open it: text reads correctly. Gear → "Mirror saved photos" flips saved photos too |
| Resolution + FPS | Gear → list shows only what the camera reports; picking one changes the preview live |
| Photo capture | Shutter → white flash → file in `Pictures\LensCraft` → thumbnail in the bottom-left strip |
| Photo strip | Hover the bottom-left edge: strip slides up; click a thumbnail to open it |
| Camera in use | Open the Windows Camera app first, then LensCraft: banner with Retry |
| Camera unplugged | Unplug a USB webcam mid-preview: switches to the next camera, or shows a reconnect message and resumes when plugged back in |
| Permission denied | Settings → Privacy → Camera → turn LensCraft off: banner with Open Settings |

## Phase 2 checklist (spec §5)

| Field | How to verify |
|---|---|
| Panel toggle | Info icon (top-right pill) slides the 320 px panel in and out; X closes it. Debug builds start with it open |
| Identity | Camera name, VID:PID for USB cameras (full device ID when there is none), location only if Windows reports one |
| Active format | Matches the format picked in the gear flyout; updates right after switching |
| Capabilities | "N supported formats" expands to the full list |
| Controls | Only controls your camera supports, each with current value and range. Cover the lens: auto exposure values change within a second |
| Session | Measured FPS updates every second and sits close to the selected FPS |
| Copy | Copy icon → paste into Notepad: every group as plain text, including the full device ID and format list |
| No camera | Unplug every camera: panel shows "No camera is running." |

## Project map

```
LensCraft/
  App.xaml(.cs)              Shared styles: floating pill, round icon buttons
  Views/
    MainWindow               Full-bleed preview + floating layers + error banner
    CaptureControlsBar       Bottom pill: filters (Phase 3), shutter, mirror
    TopControlCluster        Top-right pill: hardware panel toggle, settings flyout
    HardwarePanel            Right-edge panel: identity, format, capabilities, controls, live FPS
    UiConverters             Small x:Bind helper functions
    PhotoStrip               Last 6 captures, slides up on hover
    FlashOverlay             Capture flash, 250 ms
  ViewModels/MainViewModel   All UI state; never touches MediaCapture
  Services/
    CameraService            The only MediaCapture owner: start, formats, capture, failures, plug/unplug
    CaptureService           Save folder, file naming, optional mirroring of saved photos
    HardwareInfoService      Builds panel rows and the copy-as-text report (reads through CameraService)
  Models/                    CameraDeviceInfo, CameraFormat, CapturedPhoto, HardwareInfoRow, CameraControlReading
```

## Phase 1 deviations (accepted by Fable 2026-10-28)

1. **Preview pipeline.** Spec §4 says `MediaCapture.StartPreviewAsync` into `MediaPlayerElement`. WinUI 3 has no `CaptureElement`, so that call has no preview target. The code uses the documented WinUI 3 route: `MediaCapture.FrameSources` → `MediaSource.CreateFromMediaFrameSource` → `MediaPlayerElement`. Source: Microsoft Learn, "Show the camera preview in a WinUI app" and "Camera preview access with MediaCapture".
2. **Format switch pauses the preview for a moment.** The camera rejects format changes while frames stream to a `MediaPlayerElement`, so the preview is detached, the format is set with `SetMediaStreamPropertiesAsync` (as specified), then reattached. Source: Microsoft Learn, `MediaCapture.SetEncodingPropertiesAsync` remarks.
3. **Acrylic.** Spec §3 says "in-app, DesktopAcrylicBackdrop". Those are two different things in WinUI 3: `DesktopAcrylicBackdrop` is window-level; in-app acrylic is `AcrylicBrush`. The pills use the in-app theme brush `AcrylicInAppFillColorDefaultBrush`. Source: Microsoft Learn, "Acrylic material" and "In-app acrylic".
4. **Layout calls not covered by the spec.** The camera picker lives in the settings flyout. The photo strip sits bottom-left so it never slides over the centered shutter pill.

## Phase 2 open points (for Fable)

1. **Location row.** Spec §5 lists "front/back/external". Windows reports a panel only through `DeviceInformation.EnclosureLocation`; external USB cameras usually report nothing, and so do some built-in ones. Calling that "External" would be a guess, so the row is omitted when nothing is reported.
2. **FPS meter runs only while the panel is open.** The `MediaFrameReader` costs a little CPU, so it starts and stops with the panel. If a camera cannot share its frames with a second reader, the Session group is hidden instead of the camera failing.
3. **Debug builds open the panel at launch** (spec §7 rule 5, "live debugging surface"). Release builds start with it closed.
4. **Control values are raw driver units.** Exposure in particular is driver-defined (often a log2-seconds scale such as -6), shown exactly as Windows reports it.

## Phase 2 build fixes

The C# code was compiled with the .NET 8 SDK against Windows App SDK 1.6.240923002 and CommunityToolkit.Mvvm 8.3.2. The XAML compiler only runs on Windows, so XAML was checked by reading, not compiling. Two compile errors were found and fixed:

1. **`ViewModels/MainViewModel.cs`, CS0104 "'DispatcherQueue' is an ambiguous reference".** `using Windows.System;` (needed for `Launcher`) also brings in `Windows.System.DispatcherQueue`, which clashes with `Microsoft.UI.Dispatching.DispatcherQueue`. Fix: replaced it with `using Launcher = Windows.System.Launcher;`.
2. **`Models/CapturedPhoto.cs`, CS4036 "'IAsyncOperation<...>' does not contain a definition for 'GetAwaiter'".** Awaiting a Windows async call needs the `GetAwaiter` extension in the `System` namespace, and `ImplicitUsings` is off. Fix: added `using System;`.

## Status paragraph for the Phase 2 chat (historical)

> Phase 1 is complete: WinUI 3 single-project MSIX (Windows App SDK 1.6, CommunityToolkit.Mvvm 8.3), MVVM with CameraService as sole MediaCapture owner. Working: camera enumeration and picker (settings flyout), live preview via MediaFrameSource → MediaSource → MediaPlayerElement (WinUI 3 has no CaptureElement), mirror toggle (preview ON, saved photo OFF, both toggleable), resolution/FPS picker via GetAvailableMediaStreamProperties/SetMediaStreamPropertiesAsync (preview detached during the switch), JPEG capture to Pictures\LensCraft with flash and 6-photo strip, InfoBar error handling for in-use / unplugged / permission-denied with DeviceWatcher reconnect. Not yet built: HardwarePanel (info button is present but disabled), filters, auto-hide, packaging/size tuning. [Add any build fixes or open issues here.]

## Status paragraph for the Phase 3 chat

> Phases 1 and 2 are complete. Phase 2 added HardwarePanel (320 px right-edge slide-in, toggled by the info icon, open by default in Debug), HardwareInfoService (rows + copy-as-text report), and to CameraService: GetControlReadings (Exposure/WhiteBalance/Brightness/Contrast via VideoDeviceController, Supported-only) and a MediaFrameReader FPS meter (1 s average, runs only while the panel is open, stopped during format switches). Location row is omitted when EnclosureLocation is not reported. Not yet built: filters (FilterStrip, EffectsService, Win2D), countdown, grid, burst, auto-hide, packaging/size tuning. Build fixes: MainViewModel aliases only `Windows.System.Launcher` (a full `using Windows.System` makes DispatcherQueue ambiguous, CS0104); CapturedPhoto needed `using System;` to await WinRT calls (CS4036). [Add any open issues from the Phase 2 checklist here.]
