# Unity Editor送信側とブラウザ受信側の動作確認

## 1. 受信側をビルドする

Unity Editorで `Tools > StreamingMesh > Build Web KAGURA Receiver` を実行する。ビルド結果は `Builds/WebReceiver` に出力される。描画APIはWebGPUを優先し、利用できない環境ではWebGL 2にフォールバックする。WebGPUでComputeShaderと少量の非同期readbackが利用できる場合はGPUで頂点を復元し、利用できない場合はCPUへ切り替える。WebAssemblyのマルチスレッドも有効になる。

KAGURAサンプルのバッチビルドでは `-executeMethod StreamingMesh.Editor.StreamingMeshWebBuild.BuildKaguraReceiverCommandLine` を指定する。ローカルサーバーの `/viewer/` で `channel_KAGURA` を選ぶと受信画面へ遷移し、URLの `channel` パラメーターで自動接続する。ブラウザが音声の自動再生を保留した場合は画面をクリックする。

同じビルドはターミナルからも実行できる。

```sh
"/Applications/Unity/Hub/Editor/6000.6.3f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -nographics -quit \
  -projectPath "/Users/tatsuromatsubara/hobby-git-root/StreamingMesh" \
  -buildTarget WebGL \
  -executeMethod StreamingMesh.Editor.StreamingMeshWebBuild.BuildKaguraReceiverCommandLine
```

## 2. ローカル開発サーバーを起動する

```sh
python3 Tools/streamingmesh_dev_server.py
```

このサーバーは `http://127.0.0.1:8000` からWebビルドとチャンネルAPIの両方を配信し、WebAssemblyのマルチスレッドに必要なクロスオリジン分離ヘッダーも付与する。

## 3. Unity Editorの送信側を設定する

`Assets/Samples/UnityChanKAGURA/Scenes/KaguraDemo.unity` の `STMHttpSerializer` を次のように設定する。

```text
address = http://127.0.0.1:8000/channels/
channel = channel_KAGURA
```

Playモードに入り、`Create Channel` のメタデータ送信完了後に `Record from start` を実行する。これでTimelineの楽曲とメッシュ記録が同時に始まる。

音声はEditor側で常駐FFmpegプロセスへPCMを渡し、AAC-LCのfMP4へ分割する。Senderから録音する場合の目標分割時間は `Combined Frames / Frame Rate` 秒になる。`ffmpeg` がPATHに存在しない場合は、`STMAudioRecorder` の `Ffmpeg Path` に実行ファイルの絶対パスを設定する。主な調整項目は次のとおり。

```text
STMHttpSender / Frame Rate = Meshの送信fps（既定10）
STMAudioRecorder / Bitrate Kbps = AACビットレート（既定128）
STMAudioRecorder / Target Segment Duration Seconds = 単独録音時のfMP4断片の目標時間。Senderから録音するとCombined Frames / Frame Rateで上書き
```

送信される音声ファイルは、初期化セグメント `audio-init.mp4`、メディアセグメント `audio-NNNNNN.m4s`、時刻とサンプル位置を持つNDJSONプレイリスト `stream.stma` である。エンコーダーは録画ごとではなく録画中に1プロセスだけ維持されるため、断片境界ごとのAAC再初期化や無音を発生させない。

Unity Editorの送信側とブラウザの受信側を使用している間は、開発サーバーを起動したままにする。チャンネルの認証トークンはサーバーのメモリ内に保持されるため、開発サーバーを再起動した場合は `Create Channel` でチャンネルを作り直す。

pushトークンはチャンネル作成時に発行され、Unity Editorの実行時メモリだけに保持される。以後の送信では `Authorization: Bearer` ヘッダーに設定され、Unityシーン、URL、通常の送信ログには保存されない。

localhost以外へ開発サーバーを公開する場合は、チャンネルの作成・再作成を保護するプロビジョニングトークンが必須である。サーバーとUnity Editorの両方に同じ環境変数を渡して起動する。

```sh
export STREAMINGMESH_PROVISION_TOKEN='<十分に長いランダム値>'
python3 Tools/streamingmesh_dev_server.py --host 0.0.0.0
```

Unity Editorがすでに起動している場合は環境変数を継承しないため、設定後に同じ環境を継承するプロセスから起動し直す。localhostのみで動作確認する場合、プロビジョニング認証は省略できる。インターネット越しに利用する場合は、この開発サーバーを直接公開せず、TLS終端を持つリバースプロキシの背後に配置する。

## 4. ブラウザで受信側を開く

次のURLを開くと、`stream.json`と初期データが揃った受信可能なチャンネルが一覧表示される。チャンネル名を選ぶと、そのチャンネルを指定したUnity Viewerへ遷移する。一覧の更新リンクで、録画開始後の状態を再取得できる。

```text
http://127.0.0.1:8000/viewer/
```

チャンネルを直接開く場合は、従来どおりチャンネルのパラメーターをURLエンコードしたURLも利用できる。

```text
http://127.0.0.1:8000/viewer/?channel=http%3A%2F%2F127.0.0.1%3A8000%2Fchannels%2Fchannel_KAGURA%2F
```

ChromeまたはEdgeのコンソールでエラーがないことを確認し、次の式も評価する。

```javascript
crossOriginIsolated === true
navigator.gpu !== undefined
```

いずれも `true` なら、クロスオリジン分離とWebGPUを利用できる。`navigator.gpu` が未定義でも、受信側はWebGL 2とCPUデコードへフォールバックする。

ブラウザの自動再生ポリシーによって音声開始が保留された場合は、画面を一度クリックする。再生開始後はブラウザのfMP4音声時刻をマスタークロックとしてMeshフレームを選択する。fMP4音声がまだ再生可能でない間はMesh時刻を進めず、先行再生によるずれを防ぐ。

受信側は、単独でデコードできるキーフレームと、デコード済みの2フレーム以上が揃うまで待機する。差分フレームのシーケンスが欠落した場合は、壊れた差分を適用せず、最後の正常なMeshを表示したまま次のキーフレームを待つ。

## KAGURA Web版の検証記録（2026-09-27）

Unity 6000.6.3f1でKAGURA受信シーンのWebGLビルドに成功し、ローカルサーバーで配信中の `channel_KAGURA` にブラウザから接続した。WebGPU Deviceが選ばれ、13メッシュと4テクスチャを読み込んだ。GPU頂点復元を自動選択した場合は、16フレームをGPUへ投入したままGraphicsFenceの完了が返らず、デコード済みフレーム0で待機した。音声は再生可能状態で、ブラウザにもセグメントが蓄積されていた。ComputeShaderの演算結果が正しいかどうかは、この停止だけでは判定できない。

一時的にWeb版のAuto設定をCPU頂点復元へ切り替えたビルドでは、音声とメッシュの同期再生を開始し、ブラウザ上で音声時刻12秒以上とモデルの動きを確認した。ブラウザの自動再生制限により、再生開始には画面クリックが必要だった。WebGPUは描画APIとして引き続き使用していた。

続く検証では、`AsyncQueueSynchronisation` フェンスはこのWebGPU環境で「async compute非対応」の例外になった。そこで復元スナップショットの先頭16バイトだけをCommandBuffer内で非同期readbackし、GPU完了を判定した。GPUのキーフレーム0の先頭頂点 `(0.003906, 1.070313, -0.066406)` と差分フレーム1の `(0.003906, 1.070313, -0.066345)` は、同じ配信データからCPUで計算した値と6桁表示で一致した。GPU経路のまま音声・メッシュが同期再生し、音声時刻26秒超とモデルの変化を確認。現行のWebビルドはこの方法を使用する。全頂点をCPUへ戻すのではなく、1フレームあたり16バイトだけ完了確認に使用する。WebGPUが使えない環境ではGPU経路の初期化に失敗し、CPU復元を使う。
