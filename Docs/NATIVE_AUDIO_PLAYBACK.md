# ネイティブfMP4音声再生

## 構成

音声エンコーダーは全プラットフォームでFFmpegを使用し、AAC-LCをfMP4へ格納する。配信される音声データは次の4種類である。

- `audio-init.mp4`: fMP4初期化セグメント
- `audio-*.m4s`: fMP4メディアセグメント
- `stream.stma`: StreamingMesh用の時刻・sequenceインデックス
- `audio.m3u8`: OS標準プレイヤー用のHLSインデックス

`stream.stma`と`audio.m3u8`は同じ`.m4s`を参照する。音声データを二重に生成・保存する構成ではない。

Unity側は`IStreamingAudioPlayer`を境界としてプラットフォーム実装を選択する。

| プラットフォーム | 実装 | 入力 |
| --- | --- | --- |
| WebGL | Media Source Extensions | `stream.stma`、init、m4s |
| macOS / iOS | AVFoundation `AVPlayer` | `audio.m3u8`、init、m4s |
| Windows | 将来追加するMedia Foundation実装 | init、m4s |
| Android | AndroidX Media3 `ExoPlayer` | `audio.m3u8`、init、m4s |

Apple実装はAVPlayerの再生時刻をUnityへ返し、Receiverはその値をメッシュ再生クロックとして使う。AVPlayerがHLSを継続的に読み込むため、各`.m4s`を個別にデコーダーへ渡してAACデコーダーをリセットすることはない。

Android実装も同じHLSプレイリストをMedia3で再生し、再生位置をUnityへ返す。Java側のExoPlayer操作はAndroidのメインLooperで行い、Unity側はキャッシュされた再生位置と状態を読み取る。実装は`Assets/StreamingMesh/Android/StreamingMeshAndroidAudio.java`と`Assets/StreamingMesh/Scripts/Core/Rendering/AndroidFmp4AudioPlayer.cs`。Androidビルドは`Assets/Plugins/Android/mainTemplate.gradle`からMedia3の`media3-exoplayer`と`media3-exoplayer-hls`を取得する。Media3はApache License 2.0で提供され、StreamingMesh本体のMITライセンスやUnityChan素材のUCLとは別に扱う。

## Android実機確認（2026-10-04）

Pixel 5a／Android 14、Vulkan／Adreno 620で、ARM64 IL2CPPの開発用APKをADBからインストールして確認した。Unity 6000.5.10f1／URP 17.5の独立検証プロジェクトを使い、Source EditorとSourceのシーン・ビルド設定は変更していない。

`channel_KAGURA_MULTI`を自動選択のASTC 4×4で読み込み、13 Mesh／20 Texture、音声クロック287.613秒までの再生とReconnect後の再生再開を確認した。Reconnectは既存リソースへキャッシュヒットし、Textureの再生成はなかった。ASTC 6×6とETC2 RGBA8もIntentで指定し、20枚すべてのGPU形式維持・Mip1・CPU画素非保持と、20秒以上の描画／音声再生を確認した。この端末で非対応のBC7／DXTはロードしない。

APKは`Builds/AndroidMultiFormatReceiver.apk`、アプリIDは`com.blkcatman.streamingmesh.multiformatverification`。通常起動は自動選択、形式指定はテスト用Intentの`streamingMeshTextureFormat`へ`ASTC_6x6`または`ETC2_RGBA8`を渡す。`AndroidReceiverProbe`は検証プロジェクトだけに追加する診断コードで、本番シーンへは入れない。

5秒間隔の診断では、ASTC 4×4の再生・再接続を含むUnity nativeAllocated最大87.392MiB、nativeReserved最大251.949MiB、monoUsed最大110.113MiBを観測した。OSのPSSは一時点で480,241kB。Unityの値やPSSはGPUメモリ全体の測定ではなく、WASM容量との直接比較には使わない。再生中のOOM／プロセス異常終了は観測しなかったが、初期起動にUnityの`AssetPackManager`クラス不在の例外ログがある。今回のAPK再生は継続できたが、Play Asset Delivery経路は未検証。

```powershell
./Tools/Tests/verify_android_receiver.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe' -VerificationProject '<verify_indexed_resources.ps1で作成した独立プロジェクト>'
adb reverse tcp:8005 tcp:8005
adb install -r Builds/AndroidMultiFormatReceiver.apk
adb shell am start -n com.blkcatman.streamingmesh.multiformatverification/com.unity3d.player.UnityPlayerActivity
# 比較するときは同アプリをforce-stopした後、起動Intentへ次を追加する。
# --es streamingMeshTextureFormat ASTC_6x6
```

配信サーバーは`Tools/streamingmesh_dev_server.py --port 8005 --web-root Builds/MultiFormatReceiver --data-root DevData/channels`。Unityビルド終了時にADBサーバーが停止する場合は、USB reverseを再設定する。記録は`Logs/AndroidNativeReceiver-summary.json`、`AndroidNativeReceiver-continuous-logcat.txt`、`AndroidNativeReceiver-ASTC6-logcat.txt`、`AndroidNativeReceiver-ETC2-logcat.txt`、終端画面は`AndroidNativeReceiver-fullplay.png`。

## Appleプラグイン

- 共通Objective-C++ソース: `Assets/Plugins/iOS/StreamingMeshAppleAudio.mm`
- macOS universal dylib: `Assets/Plugins/macOS/StreamingMeshAppleAudio.dylib`
- macOS再ビルド: `Tools/build_apple_audio_plugin.sh`
- Unity C#ラッパー: `Assets/StreamingMesh/Scripts/Core/Rendering/AppleFmp4AudioPlayer.cs`

macOSライブラリはarm64とx86_64を含み、最小対象はmacOS 12.0である。iOSソースはUnityのiOS Xcodeプロジェクトへ直接取り込まれ、C ABIを`DllImport("__Internal")`から呼び出す。

iOSでHTTP URLを使う場合は、配信先に応じたApp Transport Security設定が別途必要になる。HTTPS配信では追加設定は不要である。
