# UnityChan KAGURA sample

UnityChanKAGURA_URP-release-1.0.1 から、モデル、身体・表情の Timeline、楽曲1曲、ライセンス文書を取り込んだローカルデモです。

## シーン

- `Scenes/KaguraDemo.unity`: Unity Editor で開いて Play。Toon 表示、身体・目・口のアニメーション、SpringBone、楽曲を再生します。画面左上で Pause / Play / Restart を操作できます。
- `Scenes/KaguraReceiver.unity`: ローカルの `channel_KAGURA` を StreamingMesh で受信します。受信表示は既存の `StreamingMesh/Standard`（テクスチャ付き Unlit）です。Toon の陰影・輪郭・マテリアルキーワードの再現には別途対応が必要です。

Timeline は約287.6秒。音声開始位置は原版と同じ6.6秒です。身体、目、口、音声を残し、元のステージ・カメラ・UI演出トラックは除いています。固定カメラの簡易デモであり、原版ライブ演出の完全移植ではありません。

## Editor内の同期計測

`Tools > StreamingMesh > KAGURA > Verify Timeline Audio Sync` で `KaguraDemo` を開き、全曲を再生してTime系の時計・deltaTime積算・DSP時計・Director・身体と音声のPlayable時刻を記録します。前半・中盤・後半のAudioListener出力を元クリップの波形と比較でき、LateUpdateからEndOfFrameまでの時間も取得します。録画中は実行できません。通常計測ではシーンや再生時計の設定を変更せず、完了するとPlayモードを終了します。出力先はGit管理対象外の `Logs/KaguraClockComparison_日時/` です。

`python3 Tools/analyze_kagura_clock_comparison.py Logs/KaguraClockComparison_日時` で開始時点のオフセット、ばらつき、時刻差の変化と波形の対応を集計します（NumPyが必要）。`Verify DSP Clock With Hitch` と `Verify Game Clock With Hitch` は40秒の比較試験で、再生20秒付近に意図的な600msのメインスレッド停止を挿入します。時計モードの切り替えはPlayモード中だけで、シーンには保存しません。これはEditor内の時計の計測です。スピーカー・ディスプレイの実出力遅延や、StreamingMeshに収録・配信した後の同期は別途計測が必要です。以前の `Logs/KaguraTimelineSync/` のログは `Tools/analyze_kagura_timeline_sync.py` で解析できます。

## ローカル録画・受信

1. リポジトリ直下で `python3 Tools/streamingmesh_dev_server.py --port 8000` を起動します。
2. `KaguraDemo` の Main Camera にある `STMAudioRecorder` の FFmpeg Path を、インストール済み ffmpeg の実行パスに設定します（Apple Silicon Homebrew の例: `/opt/homebrew/bin/ffmpeg`）。
3. Play 後、画面の Create Channel を押します。初回はテクスチャの変換・送信に時間がかかります。サーバーログで `combined=stream.bin` の POST が200になったことを確認します。
4. Record from start を押すと Timeline と録画を同じフレームで開始します。Stop Recording で終了し、送信が完了してから Play を終了します。
5. `KaguraReceiver` を Play。Main Camera の送信設定と Receiver の Channel Address は同じチャンネルを指定してください。

音声エンコードと送信メタデータ生成は現状 Editor 専用です。シーン再生だけならサーバー・FFmpegは不要です。
録画済みの音声プレイリストは現在ライブ形式のため、受信開始時に末尾付近から再生される場合があります。録画完了時のVOD確定処理は未実装です。

## 取り込み構成と変更点

- Animation / Models / Prefabs: 原版のGUIDを保持。PSDテンプレートZIPは省略。
- Songs: `Unite_In_The_Sky_-hundreds_of_WA_sky_mix.wav` のみ。ほかの4音源は未導入。
- Timeline: 音声、身体、目・口のトラックを保持。元の音声オフセットを保持。
- License: 原版の UCL2.0 文書（日本語・英語）と表示用ロゴ一式を保持。
- `Packages/com.unity.springbone`: 同梱版1.2.0-previewを埋め込み。Unity 6のAPI UpdaterによるEditor API更新あり。MITライセンス文書を保持。
- `Packages/com.unity.universaltoonshader.urp`: 同梱版2.2.0を埋め込み。Documentation~/Samples~は省略。URP 17向けに描画パス名、追加ライトの影API、ライティング入力、SurfaceData初期化、重複キーワードを修正。原版のLICENSE.mdを保持。
- 元のURP設定やProjectSettingsはコピーせず、このプロジェクトのURP 17.6設定を使用。

## ライセンス文書

このディレクトリへ取り込んだキャラクター等の原版同梱条件は `License/UCL2.0/` を参照してください。両シーンに `© Unity Technologies Japan/UCL` を表示します。アセットの再配布時にはライセンス文書も一緒に扱ってください。このUCL文書は `Assets/StreamingMesh/` の実装に対するライセンスではありません。StreamingMesh本体と本プロジェクトで作成した連携コードは、ルートの `LICENSE`（MIT）を参照してください。

SpringBone と Toon Shader はキャラクターのライセンスとは別です。それぞれのパッケージ内の LICENSE / LICENSE.md を参照してください。Toon Shader の参照先は [Unity Companion License](https://unity.com/legal/licenses/unity-companion-license) です。

取り込み元内で楽曲固有の追加利用条件を特定できていません。本サンプルの作成・ローカル動作確認は、楽曲を含めた一般公開・再配布についての権利確認完了を意味しません。

## Web のリアルタイム受信

Web版は `Tools > StreamingMesh > Build Web KAGURA Receiver` で `Builds/WebReceiver/` に生成します。`Tools/streamingmesh_dev_server.py` を起動し、`http://127.0.0.1:8000/viewer/` で `channel_KAGURA` を選択してください。ブラウザの音声再生には最初の画面クリックが必要な場合があります。WebGPU対応ブラウザではWebGPUで描画・頂点復元し、GPU完了確認には各フレームの先頭16バイトだけを非同期readbackします。詳細は `Docs/WEB_TESTING.md` を参照してください。

## iOS のリアルタイム受信

`Tools > StreamingMesh > Build iOS KAGURA Receiver` で `Builds/iOSKaguraReceiver/` に Xcode プロジェクトを生成します。先に Build Profiles で iOS を選択してください。実機向け IL2CPP / Metal の Development ビルドです。Xcode で開発用署名を設定し、実機向けにビルドしてください。

受信画面の URL に Mac の LAN アドレスを指定します。`127.0.0.1` は iPhone 自身を指すため使用しません。Mac と iPhone は相互接続可能な同一 LAN に接続してください。iOS のローカルネットワーク確認では許可が必要です。

1. LAN 公開する開発サーバーと Unity Editor に、同一の `STREAMINGMESH_PROVISION_TOKEN` を設定します（`Docs/WEB_TESTING.md` の手順）。サーバーは `--host 0.0.0.0 --port 8000` で起動します。既定の保存先は `DevData/channels/` です。
2. Mac の `KaguraDemo` を Play し、Create Channel → メタデータ送信完了 → Record from start の順に操作します。
3. メッシュと音声が数秒分蓄積してから、iPhone で `http://<MacのLANアドレス>:8000/channels/channel_KAGURA/` を指定して Connect / Reconnect を押します。
4. 録画を作り直す場合は受信を Disconnect してからチャンネルを作り直し、再度接続してください。

メッシュ送信単位は `Combined Frames` 枚です。音声の分割時間も録画開始時に `Combined Frames / Frame Rate` 秒へ連動します（最短0.25秒）。60fps・300枚なら約5秒で、AACのフレーム境界により音声の実際の長さには端数が生じます。設定変更時は停止→Create Channel→録画開始→Receiverで再接続してください。実際の遅延には音声HLSのバッファリング・通信・メッシュ蓄積も含まれます。iOSの受信サンプルは60fpsを要求しますが、実際の描画性能は端末や処理負荷に依存します。

Receiverコンポーネントの `Decode Backend` は既定の `Auto` でGPU常駐経路を選択し、非対応時はCPUを使用します。`CPU` を指定すると比較用のCPU経路になります。`Normal Mode` は `Auto` / `None` / `Recalculate` を選べます。現在のUnlitマテリアルではAutoが法線更新を省略します。設定は受信インスタンス作成時に読み込むため、テンプレートを変更して再接続してください。iPhoneへ反映する場合は再ビルドが必要です。設計・制限・検証結果は [Receiver GPU設計](../../../Docs/RECEIVER_GPU_PIPELINE.md) を参照してください。

音声はモデルとメッシュの準備後、先頭メッシュの時刻から開始します。メッシュが不足すると `Buffering / audio paused` と表示して音声も待機し、蓄積後に再開します。途中接続時も先頭から再生するため、現在の送信画面とは遅延があります。Receiver画面には再生・一時停止・停止・前後5秒移動・シークバーを追加しました。シークは指定時刻の直前にあるキーフレームのチャンクからメッシュと音声を再読み込みするため、再生再開前に短いバッファリングが入ります。`Auto-play after initial buffering` は既定で有効で、従来どおりバッファリング後に自動再生します。無効にして接続すると、準備後も一時停止のまま待機します。
iOS用の上記ビルド処理は、生成した Info.plist にローカルネットワーク利用説明と `NSAllowsLocalNetworking` を追加します。Unity の HTTP 許可は DevelopmentOnly に設定します。通常のHTTPS通信に対するATSは有効です。

今回使用したローカル設定は `DevData/provision-token` に保存しています（Git管理対象外）。SenderはEditorで環境変数`STREAMINGMESH_PROVISION_TOKEN`を優先し、未設定ならこのローカルファイルを読みます。過去の検証データは `DevData/channels/kagura_verification/` と `DevData/channels/urp_verification/` に移動しています。

Create Channelは元のテクスチャを再インポートせずにチャンネル用PNGを生成します。大きなテクスチャでは初期化に時間がかかります（KAGURAの確認環境で約10秒）。通常の録画中は、メッシュの結合・圧縮とffmpeg出力ファイルの読み出しをバックグラウンドで処理します。

Receiver画面の `Camera view` からカメラのワールド座標X/Y/ZとY軸回転（Yaw）を調整できます。スライダーまたは位置0.1m・回転5度刻みのボタンを使います。位置は起動時の座標から各軸±20m、回転は起動時の向きから±180度です。Yで高さを変更でき、上下の傾きは維持します。`Reset camera` でシーンの初期位置・向きに戻ります。`Back to playback` で再生操作へ戻れます。調整は再接続でも維持され、アプリの再起動で初期状態に戻ります。同期確認用Receiverでも同じ操作を利用できます。

## Android のリアルタイム受信

Build ProfilesでAndroidを選択し、`Tools > StreamingMesh > Build Android KAGURA Receiver`を実行すると、開発用APKが`Builds/AndroidKaguraReceiver.apk`に生成されます。ARM64・IL2CPPのビルドです。AndroidX Media3のExoPlayerを使用してfMP4 HLS音声を再生し、その再生位置をメッシュの同期クロックにします。初回ビルドはMedia3のGradle依存関係を取得するため、ネットワーク接続が必要です。

ローカルHTTP配信の検証のため、Android Manifestは平文HTTPを許可しています。外部公開用ビルドではHTTPS配信に切り替え、Manifestの設定も見直してください。

USB接続の実機でMacのローカルサーバーを使う場合は、端末でUSBデバッグを許可し、`adb reverse tcp:8000 tcp:8000`を実行します。これでReceiverの既定URLである`http://127.0.0.1:8000/channels/channel_KAGURA/`をそのまま使用できます。`adb install -r Builds/AndroidKaguraReceiver.apk`でAPKを入れ、送信側でCreate Channel、Record from startを実行した後、端末のConnect / Reconnectを押します。EditorでのSender操作は`Tools > StreamingMesh > KAGURA`からも実行できます。USBを外してWi-Fiで受信する場合は、iOSと同じLAN公開・URL設定が必要です。

音と動きのずれを実機で確認するには、受信画面の`Mesh delay`を使用します。`+50 ms`はメッシュ表示を音声時計より50ミリ秒遅らせ、`-50 ms`は50ミリ秒早めます。変更時は先頭から再接続します。既定値は0で、音声・メッシュの配信時刻をそのまま対応させます。これは端末の音声出力経路などによる知覚上の一定のずれを調べるための調整値です。

Media3は[Apache License 2.0](../../../Docs/MEDIA3_LICENSE_APACHE_2.0.txt)です。StreamingMesh本体のMITライセンス、UnityChan素材のUCLとは別の依存ライブラリとして扱います。

## TimeWire local clock integration

The Sender now assigns `TimeWire.Unity.AudioDspClockSource` to its capture reference. Its first recorded PCM block establishes mesh/audio time zero, and capture deadlines no longer accumulate `Time.deltaTime`. The Timeline uses **DSPClock** with a TimeWire Transport and **Preserve Native Rate** enabled: graph speed correction is disabled for this AudioTrack. The sample's Play/Pause/Restart controls operate the Transport. Hard corrections can reschedule audio, so PCM validation is required; this does not calibrate native speaker/display delay or AAC priming. See [local validation results](../../../Docs/TIMEWIRE_LOCAL_VALIDATION.md) for the 600 ms hitch test, full-song clock/PCM measurements, and 60 fps diagnostic captures.
