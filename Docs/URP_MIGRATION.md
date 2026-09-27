# 既存デモのURP対応

前半は削除済みの旧SDユニティちゃんサンプルを使った移行当時の検証記録です。現在の実行用シーンは `Assets/Samples/UnityChanKAGURA/Scenes/` にあります。以下のmacOSビルドと再生手順は現行シーンに合わせています。

Unity 6000.6.3f1 / Universal Render Pipeline 17.6.0を使用する。
Graphics設定とQuality設定の両方で `Assets/StreamingMesh/Settings/StreamingMeshURP.asset` を指定している。
シーンを開くごとのパイプライン切り替えは不要。

## 変更範囲

- `Assets/_Files/__Recorder.unity` のSDユニティちゃんが使用する5つのマテリアルをURP対応に変更。
- `StreamingMesh/Standard` をURPのUnlit描画へ移植。旧実装のテクスチャ×色、不透明、両面表示を維持する。照明・影・法線マップを使うLit/Toon描画ではない。
- シェーダー名・GUID・`_MainTex`・`_Color`を維持しているため、`Assets/TestScene.unity` と `Assets/_Files/__Player.unity` の受信シェーダー参照と過去の録画のマテリアル情報をそのまま利用できる。受信マテリアルの復元処理・配信フォーマットは変更していない。旧Playerの音声制限は後述。
- `Assets/_Files/Receiver.prefab` に残っていた解決できない既定シェーダー参照を修正。
- 同梱サンプルのStage/TargetマテリアルをUnityのStandardUpgraderでURP/Litへ変換。
- URP Asset、Renderer、Global Settings、Default Volume Profileは `Assets/StreamingMesh/Settings/` に配置。
- ComputeShader、メッシュ通信、音声処理、モデル・モーション、各シーンの接続先は変更していない。

既存デモで参照されていない `Assets/UnityChan/Models/UnityChanShader/Shader/` の旧Built-inシェーダー群は移植対象外。KAGURA、トゥーンの輪郭線、透過マテリアルの再現は別途対応する。

## 動作確認（2026-09-27）

macOS / Apple M3 Pro / MetalのUnity Editorで確認した。

- Recorderの移行前後の画像を比較し、テクスチャとキャラクター表示を確認。URP用StreamingMeshシェーダーのコンパイルメッセージは0件。
- localhostの検証チャンネルへ235フレーム（最終PTS 23.4505574秒）を記録。圧縮データの展開、フレーム境界、sequenceの連続性、PTSの単調増加を確認。
- 独立したAudioSourceでテスト音を再生し、AudioListener→FFmpeg→HTTP送信を確認。AAC-LC / 48 kHz / 2ch、23断片、23.5306666秒。全断片をデコードでき、平均音量は−29.0 dBで無音ではない。
- TestSceneで6メッシュ・6,511頂点の受信・描画とMetalのGPUデコードを確認。
- TestSceneではAVFoundationの音声時刻とメッシュ再生時刻がともに23.440756944秒となり、音声クロックによる同期を確認。聴感による同期評価ではない。
- 移行前に記録したデータでもURP表示を確認。再生状態はPlaying、sequenceは51→209、頂点座標も変化した。
- Recorder、TestScene、旧Player、同梱Humanoid/Genericの5シーンで、配置済みRendererのマテリアルがURP対応または継続利用可能なGUI/Text Shaderであることを確認。
- 旧Playerは、検証中だけ音声待ちを解除し、6メッシュの表示、IsPlayable=true、フレーム235までの進行を確認。
- macOS Apple Silicon（ARM64）＋Burst AOT有効で、ヘッドレスのDevelopmentクリーンビルドに成功。エラー0件、警告19件（既存コードの非推奨API・シリアライズ警告、Unityサービス／Pipelineの通知など）。Burstのログで `Completed library ... with result Compiled` を確認し、実行ファイルと `lib_burst_generated.bundle` がともにarm64であることを `file` で確認。ビルド対象はARM64のまま保存した。

### 残る制限

- 旧 `Assets/_Files/__Player.unity` の `STMHttpMeshReceiver` は現行のfMP4/AAC音声を扱わないため、旧サンプル一式とともに削除した。現行のメッシュ・音声同期デモには `Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity` を使用する。
- Intel向けの配布は対象外。Burst有効のUniversalビルドは、このMacのUnity付属 `llvm-lipo` の実行権限不足で失敗する。Apple Silicon向けARM64ビルドを使用する。
- WebGL/WebGPU・iOS実機および生成したmacOSアプリでのストリーム再生は今回未検証。

画像・検証用スクリプト・過去の比較結果はgitignore対象の `Logs/URP/` に保存している。現在のARM64＋Burst有効アプリは `Builds/MacReceiver/Receiver-arm64.app`、検証ログは `Logs/URP/arm64-burst-enabled-build.log`。
検証時のチャンネルURL、FFmpegの絶対パス、テスト音、カメラ調整はシーンへ保存していない。

## macOSビルド（Apple Silicon / Burst有効）

既存デモのmacOS配布対象はApple Silicon（ARM64）とする。
`ProjectSettings/BurstAotSettings_StandaloneOSX.json` の `EnableBurstCompilation` は `true`。
Editor内のBurstとmacOS PlayerのBurst AOTはどちらも有効で、ComputeShaderによるGPUデコードも維持する。

このMacの標準macOSプラットフォーム設定はARM64に変更済み。
ArchitectureはローカルのEditor設定にも依存するため、別のチェックアウトでは `Tools > StreamingMesh > Configure macOS ARM64` を実行するか、Build ProfilesのmacOSのArchitectureをApple Siliconに設定する。

`Tools > StreamingMesh > Build macOS ARM64 KAGURA Receiver` は `Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity` をビルドし、`Builds/MacReceiver/Receiver-arm64.app` を生成する。
このメニューと以下のCLIは、ビルド直前にARM64を明示するためIntel向けコードを生成しない。Burstの設定は変更せず、プロジェクトに保存したAOT設定を使用する。

### ヘッドレスビルド

同じプロジェクトを開いているEditorを終了してから実行する。

```sh
"/Applications/Unity/Hub/Editor/6000.6.3f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -nographics -quit \
  -projectPath "/Users/tatsuromatsubara/hobby-git-root/StreamingMesh" \
  -buildTarget StandaloneOSX \
  -executeMethod StreamingMesh.Editor.StreamingMeshMacBuild.BuildReceiverCommandLine \
  -logFile "/Users/tatsuromatsubara/hobby-git-root/StreamingMesh/Logs/mac-arm64-build.log"
```

`-streamingMeshDevelopment` でDevelopmentビルド、`-streamingMeshCleanBuild` でビルドキャッシュを使わないビルドを指定できる。
出力先は `-streamingMeshOutput "/absolute/path/Receiver.app"` で変更できる。
Unityの `BuildPipeline.BuildPlayer` を呼び出すビルドスクリプトは `Assets/StreamingMesh/Editor/StreamingMeshMacBuild.cs`。Unity付属ツールを直接起動する処理はない。

### Universalとの違い

UniversalはBurstが生成したIntel用とARM64用のライブラリを結合する工程を持ち、このMacではUnity付属 `llvm-lipo` の起動がAccess deniedで失敗する。ARM64単独ならこの結合は不要で、Burstを無効にする必要はない。
以前のUniversal検証ではGUI・ヘッドレスともBurst有効のクリーンビルドが失敗し、Burst無効では成功した。これは過去の比較結果で、現在はARM64＋Burst有効の方針に変更している。

ヘッドレスもUnity Editorの実行ファイルがビルド処理を担う。[Unity公式のコマンドライン引数](https://docs.unity.com/en-us/engine/6000.6/manual/unity-editor/command-line-arguments/editor)および[Burst AOT設定](https://docs.unity.com/en-us/engine/6000.6/manual/scripting/compilation-and-code-reload/script-compilation/burst/building-aot-settings)を参照。

## 再生手順

1. `Docs/WEB_TESTING.md` のローカルサーバーを起動する。
2. `KaguraDemo` シーンでPlayモードに入り、Create Channelの完了後にRecord from startを実行する。
3. `KaguraReceiver` シーンのReceiverのChannel Addressを送信先に合わせ、Playモードで再生する。

macOSのHub起動EditorでFFmpegが見つからない場合は、STMAudioRecorderのFfmpeg Pathへ実在する実行ファイルの絶対パスを設定する。
URP移行後のWebGL/WebGPU・iOS実機での表示は別途検証が必要。
