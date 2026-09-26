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
| Android | 将来追加するMediaExtractor / MediaCodec実装 | init、m4s |

Apple実装はAVPlayerの再生時刻をUnityへ返し、Receiverはその値をメッシュ再生クロックとして使う。AVPlayerがHLSを継続的に読み込むため、各`.m4s`を個別にデコーダーへ渡してAACデコーダーをリセットすることはない。

## Appleプラグイン

- 共通Objective-C++ソース: `Assets/Plugins/iOS/StreamingMeshAppleAudio.mm`
- macOS universal dylib: `Assets/Plugins/macOS/StreamingMeshAppleAudio.dylib`
- macOS再ビルド: `Tools/build_apple_audio_plugin.sh`
- Unity C#ラッパー: `Assets/StreamingMesh/Scripts/Core/Rendering/AppleFmp4AudioPlayer.cs`

macOSライブラリはarm64とx86_64を含み、最小対象はmacOS 12.0である。iOSソースはUnityのiOS Xcodeプロジェクトへ直接取り込まれ、C ABIを`DllImport("__Internal")`から呼び出す。

iOSでHTTP URLを使う場合は、配信先に応じたApp Transport Security設定が別途必要になる。HTTPS配信では追加設定は不要である。
