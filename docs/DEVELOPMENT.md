# Development notes

## Projects

| Project | Responsibility |
| --- | --- |
| `src/AudioRouter.Core` | Float PCM mixer, bounded buffers, settings persistence |
| `src/AudioRouter.Audio` | Windows WASAPI capture/render, devices, application discovery |
| `src/AudioRouter.App` | WPF interface and setup guide |
| `tests/AudioRouter.Tests` | Automated regression tests |
| `tools/AudioRouter.Diagnostics` | Optional live device/audio checks |

The SDK version is specified in `global.json`. `build.ps1` uses a project-local SDK at `.tools/dotnet/dotnet.exe` when present, otherwise `dotnet` from PATH. No SDK is included in source or release archives. The WPF executable bundles its runtime.

```powershell
./build.ps1 -Test -Publish
./run.ps1
```

Regenerate the committed executable icon with `./tools/New-AppIcon.ps1` when changing the matching SVG/vector design.

## Audio path

- Discover visible application windows and audio sessions. Group processes by executable and application root; validate process creation times to guard against PID reuse.
- Capture each checked application's process trees using WASAPI process loopback. Keep a capture group per executable, normalize duplicate roots, and remove only that group's buffers/streams on uncheck, closure, or failure. Healthy captures and the microphone stay active. Never climb to Explorer or fall back to system-wide capture.
- Request 48 kHz stereo float in shared mode. Windows handles microphone/output format adaptation. Render drives the mix; bounded 100 ms queues, a 60 ms backlog limit, silence on underflow, gain ramps, and a soft limiter keep it stable.
- Render only to a detected VB-CABLE input endpoint. The cable's recording side is excluded from the microphone picker. No speaker monitoring path is added.
- Capture/render callbacks do not use the UI thread. Device changes stop/restart the route with retained source and control state. Device failures stop the route; source-app closure keeps the microphone active without silently choosing another app.
- Normal operation never changes Windows defaults, endpoint volume/mute, source-app volume, per-app device assignments, or destination-app configuration. Audio stays in memory; there are no cloud services or startup tasks.

## Optional live diagnostics

These are manual tools, not CI prerequisites. Use the project-local dotnet path if the SDK is not on PATH.

```powershell
dotnet run --project tools/AudioRouter.Diagnostics -c Release -- --inventory
dotnet run --project tools/AudioRouter.Diagnostics -c Release -- --verify-loopback
dotnet run --project tools/AudioRouter.Diagnostics -c Release -- --verify-multiple-sources
dotnet run --project tools/AudioRouter.Diagnostics -c Release -- --verify-volume
dotnet run --project tools/AudioRouter.Diagnostics -c Release -- --probe-microphone
dotnet run --project tools/AudioRouter.Diagnostics -c Release -- --probe-app 1234 3
```

`--verify-loopback` launches two quiet test-tone processes and verifies that only the selected process is captured. `--verify-volume` additionally lowers/mutes the active speaker endpoint temporarily and restores its previous values in a `finally` block. Run it when a brief listening-volume change is acceptable. Probes report aggregate levels without saving audio; inventory can include local paths and device IDs, so keep its output local.

`--verify-multiple-sources` launches two quiet independent app hosts, routes them through a real VB-CABLE pair with the microphone muted, and measures the cable recording endpoint. It checks both signals, duplicate normalization, independent removal, pause/resume of all app audio, source closure, and isolated capture failure. It needs a physical microphone and an installed cable; exit any normal router instance first. It does not change Windows volume or defaults and does not save audio.

## UI checks

Run the portable executable with `--smoke-test <absolute-output.png>` to exercise search/gain/pause/mute, meter enable/disable, close-to-tray/restore, manual refresh, and status alignment; render the main/setup windows; write a JSON report; and explicitly exit. This check does not start audio routing or save preferences.

`--device-switch-smoke-test <absolute-output.png>` additionally checks that closing to tray keeps a live route intact while meter calculations stop, changes both device selectors, checks retained 200% gain/source/mute/pause state, and exercises an unavailable microphone/recovery. It needs two microphones and two VB-CABLE render endpoints. Exit other router copies first so devices are available. Neither check opens Windows Settings or installs drivers.

The WPF app uses the Windows Forms NotifyIcon component for its tray icon; no extra tray library is installed. Normal startup uses a per-user/session mutex and named events to restore an existing instance instead of duplicating audio streams. `AudioRouter.exe --exit` requests graceful shutdown of that instance. Smoke runs bypass instance coordination so tests do not control a user's running app.

Application/device discovery occurs only on initial load and explicit Refresh. The separate audio-engine watch for the selected source exiting remains active while routing. Meter peak accumulation is gated in the mixer, and the 125 ms UI timer runs only while enabled, visible, not minimized, and routing. There is no periodic UI work in the tray.

Hardware-dependent behavior must also be checked in a receiving app. Added latency depends on capture/render buffers, the cable, the destination app, and any voice-chat/network processing; a fixed end-to-end latency is not promised.
