# adbView

Lightweight Android screen mirroring and control for Windows over ADB. No phone app to install, and no third-party runtime or native libraries to bundle.

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6) ![.NET](https://img.shields.io/badge/.NET-10-512BD4) ![license](https://img.shields.io/badge/license-MIT-green)

## Features

- **Live mirroring.** H.264 from the device's built-in `screenrecord`, decoded by the Windows Media Foundation decoder.
- **Low-latency control.** Touch and hardware keys are written directly to the kernel input device (`/dev/input/eventX`) through one persistent `adb shell` that keeps the device open. Each event takes under 1 ms, compared with about 1 s for `input tap` on older phones. Real-time drag and swipe work.
- **Smart quality.** *Auto* picks the smallest stream that is still sharp at the current window size. Only the levels your device supports are listed (480p → native).
- **Automatic device discovery.** Uses `adb track-devices`: plug the phone in and it shows up, with no polling.
- **Phone-like UI.** The screen fills the window edge to edge and the window sizes itself to the device aspect ratio. A Back / Home / Recents bar appears only on devices without on-screen navigation.
- **Toolbar.** Power, volume, and screenshots saved to `Pictures\adbView`.
- **Light / Dark / System** theme. **English / Tiếng Việt** UI.
- **Lightweight.** About 100 MB RAM. Native resources are released when you stop viewing, and child processes are cleaned up even if the app crashes.

## Requirements

- Windows 10 1809+ or Windows 11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- `adb` in `PATH`, in the Android SDK `platform-tools`, or next to `adbView.exe`
- Android 5.0+ with **USB debugging** enabled

## Usage

1. Enable *Developer options → USB debugging* on the phone and connect it with a USB cable.
2. Run `adbView.exe` and accept the RSA prompt on the phone.
3. The screen appears automatically. Use ⚙ to switch device, quality, language or theme.

| Input | Action on the phone |
|---|---|
| Left click / hold / drag | Tap / long press / swipe |
| Mouse wheel | Scroll |
| Right click | Back |
| Keyboard | Text, Enter, Backspace, arrows, Esc (Back) |

## Build

```bash
git clone https://github.com/vitvu/adbView.git
cd adbView
dotnet build -c Release
# single-file, framework-dependent:
dotnet publish -c Release -p:PublishSingleFile=true
```

## How it works

```
phone: screenrecord --output-format=h264 ──adb exec-out──▶ NAL splitter ──▶ MF H.264 decoder (NV12)
                                                                                   │
WPF WriteableBitmap ◀── latest-frame handoff (drops stale frames) ◀── NV12→BGRA ◀──┘

mouse/keys ──▶ struct input_event ──▶ persistent `adb shell` (print -u<fd>) ──▶ /dev/input/eventX
```

| File | Role |
|---|---|
| `ScreenStream.cs` | Runs `screenrecord`, splits the H.264 stream into NAL units, converts colors, hands off frames |
| `H264Decoder.cs` | Media Foundation decoder MFT called through raw COM vtables (no allocations per call) |
| `InputInjector.cs` | Finds the touch and key devices via `getevent -pl` and writes input events directly |
| `MainWindow.xaml(.cs)` | UI, device tracking, quality selection, touch mapping |

## Limitations

- `screenrecord` restarts every 3 minutes, which causes a brief pause of about 1 s.
- Decoding runs on the CPU. At 1080p and 30 fps expect about 1 core; choose *Medium (720p)* for lower usage.
- Text input supports ASCII only. Touch mapping assumes portrait orientation.
- Devices where `shell` cannot write `/dev/input` fall back to `input` commands, which are slower.

## Roadmap

- [ ] ADB over Wi-Fi (connect / pair)
- [ ] Multi-device grid view
- [ ] GPU (D3D11) decoding and rendering
- [ ] Landscape touch mapping

## Support

If adbView is useful to you, consider supporting development:

[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-00457C?logo=paypal&logoColor=white)](https://paypal.me/vudinhviet)

## License

[MIT](LICENSE)
