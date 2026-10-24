# Set up Audio Router for a game or app

Audio Router mixes audio from one or more source apps with your microphone and sends it to a destination app that accepts a selectable microphone. The in-app **Setup guide** contains the same steps, plus buttons to open Windows sound settings and Volume mixer.

## 1. Install the virtual cable once

- Download [VB-CABLE from VB-Audio](https://vb-audio.com/Cable/).
- Extract the ZIP and run `VBCABLE_Setup_x64.exe` as administrator.
- Restart Windows if requested, then open Audio Router and click **Refresh**.
- VB-CABLE is a separate product and is not bundled or installed automatically.

## 2. Keep your usual Windows audio devices

- Open **Settings → System → Sound**.
- Keep your real speakers/headphones as **Output** and your normal microphone as **Input**.
- Check these after driver installation because the installer may change defaults.
- Leave the source app's output on your normal speakers/headphones. Audio Router captures a copy of that app's audio.

## 3. Start the mix

- Check one or more source applications in Audio Router, such as your browser and music player.
- Choose your normal physical or processed microphone.
- Choose **CABLE Input** under **Use in any game or app**, then click **Start routing**.
- Play audio in the source app. Optionally enable **Live meters** to see microphone, application, and combined output.
- Click **Refresh** after opening an app or connecting a device; discovery is manual after the initial load.
- Add or uncheck apps while routing is live. The microphone and remaining sources continue. Checked selections survive search and refresh and are remembered for the next launch.

## 4. Select the virtual microphone in the destination app

Use either route below. Prefer the app's own settings when available.

**Inside the game/app:**

- Open its **Audio**, **Voice**, or **Microphone** settings.
- Choose **CABLE Output (VB-Audio Virtual Cable)** as its microphone/input.
- Keep its speaker/output on your normal speakers or headphones.
- Save or apply, and enable its microphone or voice chat.

**Windows per-app settings:**

- Open **Settings → System → Sound → Volume mixer**. The in-app guide has a button for this page.
- Under **Apps**, expand the destination application.
- Where **Input device** is available, choose **CABLE Output (VB-Audio Virtual Cable)**.
- Keep **Output device** on your normal speakers/headphones.
- If the app is missing, open it and start playback or voice chat. Restart it if a device change does not take effect.
- Some apps do not expose an input selector here or manage their own devices. Use their own microphone selector in that case.

Windows supports opening Volume mixer directly through the [documented Settings URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings#sound). [Microsoft's Volume mixer help](https://support.microsoft.com/en-us/windows/hardware/audio/fix-app-audio-not-working-while-system-sounds-work-in-windows) explains per-app playback settings.

**Match the cable pair:**

| Output selected in Audio Router | Microphone selected in destination app |
| --- | --- |
| CABLE Input | CABLE Output |
| CABLE In 16ch | CABLE Out 16ch |
| CABLE-A Input | CABLE-A Output |

The app prefers the regular pair for fresh setup and shows the matching microphone name. If you change cable pairs, update the destination app too.

## 5. Test and adjust

- Use the destination app's microphone test, or confirm with someone that they hear both voice and music.
- Windows master volume controls what you hear. **Music sent to others** controls the outgoing copy from **0–200%**.
- That slider controls all checked apps together. Uncheck a source to remove only its audio. An app that closes or fails is unchecked independently; other apps and the microphone keep working. Refresh and check it again after reopening.
- 100% is the original captured level; 200% adds 2× signal gain (+6 dB). The output limiter contains peaks, so already-loud audio has less headroom. The source app's playback controls can still affect the captured copy.
- Uncheck **Share application audio** to pause music while your microphone continues.
- Change microphone or cable at any time; routing briefly restarts and retains your source, gains, and mute/pause state.
- The **× button hides Audio Router in the tray** and keeps music and voice working. Click its tray icon, choose **Open Audio Router**, or launch the executable again to restore the existing instance.
- **Stop routing** stops the mix; **Exit Audio Router** in the tray menu stops the mix and quits. Select your real microphone in the destination app to talk without the router.
- **Live meters** is off by default and saved. Turning it off hides the bars/dB number and disables measurement/UI updates. Metering also pauses in the tray, when minimized, or while routing is stopped. Slider percentages remain available.

## Troubleshooting

- **Voice missing:** allow desktop apps access under **Windows Settings → Privacy & security → Microphone**. The guide links directly to this page.
- **Music meter silent:** enable **Live meters**, start playback, and verify the selected source. Protected audio and unusual/exclusive audio pipelines may not be capturable.
- **Meters moving but destination silent:** check the matching cable pair and destination app's microphone selection and mute state.
- **Device busy:** close another application that holds that audio device exclusively, then start routing again.
- **Echo:** use headphones and leave Windows **Listen to this device** off for the cable.
- **Unexpected browser audio:** selection covers the entire browser, including audible tabs/windows. Pause unrelated tabs.
- **Music sounds processed:** the receiving app may apply voice compression or noise suppression.

Audio stays in memory on your PC and is not recorded or uploaded. Local settings and bounded error logs are under `%LOCALAPPDATA%/AudioRouter`.
