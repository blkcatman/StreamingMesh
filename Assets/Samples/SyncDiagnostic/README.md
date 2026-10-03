# 1 Hz audio / mesh sync diagnostic

This MIT-licensed sample contains no UnityChan assets. `SyncDiagnosticSender.unity` uses a regular `MeshFilter` / `MeshRenderer` hand, while `SyncDiagnosticSkinnedSender.unity` uses a single-bone `SkinnedMeshRenderer` hand. Both stream a stationary clock face with 60 marks and a hand that makes one clockwise revolution per second. The intended cue is a 100 ms, 880 Hz beep beginning at 12 o'clock: the scheduled audio and hand rotation reference the same DSP clock. The beep is a generated one-second looping `AudioClip`, so no external music file is involved. At one revolution per second, the hand travels 36 degrees during the beep; compare its onset rather than the entire sound.

Open either Sender scene in the Unity Editor and enter Play mode. Start `Tools/streamingmesh_dev_server.py --port 8000`, then click **Create Channel**. After `stream.bin` has been sent, click **Record from Start**. Recording stops automatically after 30 seconds. The first beep is scheduled 0.5 seconds after `sender.Record()` returns; the hand remains parked at 12 until then. This is not an assertion of zero offset in the encoded stream; see the measurement below. The regular Mesh uses `channel_sync_mesh`; the SkinnedMesh uses `channel_sync_skinned`. Both are separate from `channel_KAGURA`.

Open `SyncDiagnosticReceiver.unity` in Play mode and click **Mesh sample** or **Skinned sample**. The beep should coincide with the hand crossing 12 o'clock. The Receiver has Play, Pause, Stop and a seek bar. Seeking reloads the stream from a nearby keyframe chunk, so it buffers briefly before resuming. **Auto-play after initial buffering** defaults to on, preserving the earlier behavior; turn it off before connecting to start paused. For Android, select the Android build target and run **Tools > StreamingMesh > Build Android Sync Diagnostic Receiver**. Install `Builds/AndroidSyncDiagnosticReceiver.apk`; it has its own app ID and contains only the diagnostic scene. Use `adb reverse tcp:8000 tcp:8000` over USB or the Mac's LAN address over Wi-Fi. Edit the URL before choosing a sample if you need to change the server address; the buttons preserve that address and replace only the channel name.

For iPhone, select the iOS build target and run **Tools > StreamingMesh > Build iOS Sync Diagnostic Receiver**. Sign and build the exported `Builds/iOSSyncDiagnosticReceiver/Unity-iPhone.xcodeproj` with Xcode, then install **STM Sync**. Its app ID is `com.blkcatman.streamingmesh.syncdiagnostic`, separate from the KAGURA Receiver. Enter the Mac's LAN URL (for example, `http://192.168.3.7:8000/channels/channel_sync_skinned/`) and allow local network access when prompted. The iOS sample remembers the last connected URL. **Mesh sample** and **Skinned sample** preserve the server address; **Connect / Reconnect** starts another playback after the 30-second stream ends. The Mac and iPhone must be able to reach each other on the local network. For a device-tool launch, set the process environment variable `STREAMINGMESH_CHANNEL` to the URL to connect automatically. On iOS, IL2CPP did not expose the launch arguments through `Environment.GetCommandLineArgs`; other targets can also use `-streamingMeshChannel URL`.

The iOS Receiver reads `AVPlayer.currentTime` on every update; it does not use Android's 20 ms position cache. Matching audio and mesh timestamps in its logs verifies the playback clock, but does not measure the sound reaching the speaker or the image reaching the display. Compare the audible beep onset with the hand crossing 12 on each device.

For a numerical check of the **recorded data**, run:

```sh
python3 Tools/analyze_sync_diagnostic.py --channel DevData/channels/channel_sync_mesh
python3 Tools/analyze_sync_diagnostic.py --channel DevData/channels/channel_sync_skinned
```

The script checks that the clock root stays fixed, decodes the hand-tip vertices from `.stmv`, and linearly interpolates the X=0 crossing near the top using the actual presentation timestamps, matching the Receiver's vertex interpolation. It decodes the AAC segments with FFmpeg and measures beep onset in 1 ms blocks. The first parked beep cannot be timed from a crossing and is excluded; every subsequent crossing must have a corresponding audio beep. A positive `visual minus audio` value means the hand crosses 12 after the encoded beep begins. The output also reports the hand's interpolated angle at each beep onset. No offset is reduced modulo the frame interval: doing so conceals real whole-frame errors. This measurement isolates encoded stream timing from native-player clocks, display timing and acoustic output latency.

Local verification (Unity 6000.6.3f1, 30 fps, 30 seconds):

| Sender | Compared crossings / beeps | Interpolated offset median (range) | Hand angle at audio onset, median | First-to-last offset change |
| --- | ---: | ---: | ---: | ---: |
| MeshRenderer | 29 / 29 (first beep excluded) | +13.6 ms (+3.1 to +19.8 ms) | -5.0 degrees | -8.9 ms over 28 s |
| SkinnedMeshRenderer | 29 / 29 (first beep excluded) | +22.7 ms (+15.6 to +29.7 ms) | -8.2 degrees | -0.3 ms over 28 s |

Both channels were recorded again with a stationary clock, but the encoded beep starts before the hand reaches 12. The earlier phase-adjusted measurements and claim of synchronization were incorrect; the table above replaces them. The Sender timestamps poses with a realtime clock after `LateUpdate`, while the hand uses DSP time and the recorder's sample timeline begins at its first audio callback. Those clocks are not explicitly aligned; their contributions to this offset still need separate measurement.

The Android Receiver additionally samples Media3's position every 20 ms and returns that cached value without extrapolation. A stale clock can delay the mesh relative to audio, and the Editor/native output paths have their own presentation latency. The new SkinnedMesh recording played in the Unity Receiver. Playback, pause, seek, resume, stop, and connecting with auto-play disabled were checked in the Editor; an earlier recording was checked for playback controls on a Pixel 5a. These functional checks do not establish acoustic synchronization on the device.

On an iPhone 12 Pro (iOS 26.6.1, development build), both recordings played with GPU decoding. Six playback-clock samples over each recording reported 59.8 fps and matching audio/mesh timestamps at the log's 1 ms precision. The worst frame during playback was 18.6 ms for Mesh and 19.6 ms for SkinnedMesh; the decoded buffer stayed approximately 0.47–0.49 seconds ahead, with no mesh-buffer pause events or application errors logged. This does not eliminate the recorded offsets above or measure speaker/display latency. The current HLS playlists remain live (no `EXT-X-ENDLIST`): after the available audio finishes, the clock stops near 30.44 seconds while the Receiver's ready/playing label remains set. Use **Connect / Reconnect** to repeat the recording.

Analysis regression checks:

```sh
python3 -m unittest discover -s Tools/Tests -p test_sync_diagnostic.py
```

Use **Camera view** on either Receiver to adjust world X/Y/Z position and Y-axis yaw while the stream continues playing. Sliders cover ±20 metres per axis and ±180 degrees from the starting view; buttons move 0.1 metres or turn 5 degrees. Y adjusts camera height; the initial tilt stays unchanged. **Reset camera** restores the scene's starting pose and **Back to playback** returns to the player controls. Reconnecting preserves the view; restarting the app resets it. The panel respects the device's screen safe area.

The Receiver camera uses perspective projection so changing Z distance changes the apparent model size. Its initial field of view (approximately 36.53 degrees) matches the former orthographic framing at the diagnostic marker's Z=0 plane. Sender cameras retain their orthographic diagnostic view.

## TimeWire local clock validation

The Sender scenes now assign `TimeWire.Unity.AudioDspClockSource` to **Capture Clock Source**, with **Use TimeWire Clock** enabled. DSP observations arrive on the audio thread and are interpolated by a monotonic reference. Capture deadlines use `FrameSchedule`; unavailable GPU capacity skips capture slots without stopping the clock or manufacturing past poses. The encoded sequence and delta dependencies remain continuous. The sample evaluates its hand again at the precise `BeforeCapture` time used for the packet PTS.

The first accepted PCM block defines audio time zero. With the DSP source, the Sender maps that same block timestamp to mesh time zero. This aligns the unencoded streams; AAC priming and output-device delay still require separate accounting. A different source, such as a local stopwatch or network clock, does not automatically establish this PCM mapping. The clock monitor in the Inspector exposes state, observation age/drift, last capture time and missed capture slots.

Use **Tools > StreamingMesh > TimeWire > Configure Local Sample Clocks** outside Play mode to apply the sample configuration. It also configures KAGURA's transport and DSP-clocked Timeline, keeping its native audio rate. The test commands **Verify TimeWire Clock With Hitch** and **Verify TimeWire Full Song** measure both clocks and captured listener PCM. See [the local validation report](../../../Docs/TIMEWIRE_LOCAL_VALIDATION.md) for measured results and limits. To reproduce the legacy capture comparison, disable **Use TimeWire Clock** before recording into a separate test channel.
