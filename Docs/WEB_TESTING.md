# Unity Editor送信側とブラウザ受信側の動作確認

## 1. 受信側をビルドする

Unity Editorで `Tools > StreamingMesh > Build WebGPU Receiver` を実行する。ビルド結果は `Builds/WebReceiver` に出力される。WebGPUを優先し、利用できない環境ではWebGL 2とCPU処理へフォールバックする設定である。WebAssemblyのマルチスレッドも有効になる。

同じビルドはターミナルからも実行できる。

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.10f1\Editor\Unity.exe' `
  -batchmode -quit `
  -projectPath 'C:\Users\RTX4090\hobby-git-root\StreamingMesh' `
  -executeMethod StreamingMesh.Editor.StreamingMeshWebBuild.BuildReceiverCommandLine
```

## 2. ローカル開発サーバーを起動する

```powershell
python .\Tools\streamingmesh_dev_server.py
```

このサーバーは `http://127.0.0.1:8000` からWebビルドとチャンネルAPIの両方を配信し、WebAssemblyのマルチスレッドに必要なクロスオリジン分離ヘッダーも付与する。

## 3. Unity Editorの送信側を設定する

Recorderシーンの `STMHttpSerializer` を次のように設定する。

```text
address = http://127.0.0.1:8000/channels/
channel = channel_SDUTC
```

同梱の `Assets/_Files/__Recorder.unity` では、有効なUnityChan送信側にこの値が設定済みである。`Create Channel` を実行してプレイモードへ入り、続いて `Start Recording` を実行する。

音声はEditor側で常駐FFmpegプロセスへPCMを渡し、AAC-LCのfMP4へ約1.024秒単位で分割する。`ffmpeg` がPATHに存在しない場合は、`STMAudioRecorder` の `Ffmpeg Path` に実行ファイルの絶対パスを設定する。主な調整項目は次のとおり。

```text
STMHttpSender / Frame Rate = Meshの送信fps（既定10）
STMAudioRecorder / Bitrate Kbps = AACビットレート（既定128）
STMAudioRecorder / Segment Duration = fMP4断片長（既定1.024秒）
```

送信される音声ファイルは、初期化セグメント `audio-init.mp4`、メディアセグメント `audio-NNNNNN.m4s`、時刻とサンプル位置を持つNDJSONプレイリスト `stream.stma` である。エンコーダーは録画ごとではなく録画中に1プロセスだけ維持されるため、断片境界ごとのAAC再初期化や無音を発生させない。

Unity Editorの送信側とブラウザの受信側を使用している間は、開発サーバーを起動したままにする。チャンネルの認証トークンはサーバーのメモリ内に保持されるため、開発サーバーを再起動した場合は `Create Channel` でチャンネルを作り直す。

pushトークンはチャンネル作成時に発行され、Unity Editorの実行時メモリだけに保持される。以後の送信では `Authorization: Bearer` ヘッダーに設定され、Unityシーン、URL、通常の送信ログには保存されない。

localhost以外へ開発サーバーを公開する場合は、チャンネルの作成・再作成を保護するプロビジョニングトークンが必須である。サーバーとUnity Editorの両方に同じ環境変数を渡して起動する。

```powershell
$env:STREAMINGMESH_PROVISION_TOKEN = '<十分に長いランダム値>'
python .\Tools\streamingmesh_dev_server.py --host 0.0.0.0
```

Unity Editorがすでに起動している場合は環境変数を継承しないため、設定後に同じ環境を継承するプロセスから起動し直す。localhostのみで動作確認する場合、プロビジョニング認証は省略できる。インターネット越しに利用する場合は、この開発サーバーを直接公開せず、TLS終端を持つリバースプロキシの背後に配置する。

## 4. ブラウザで受信側を開く

次のURLを開くと、`stream.json`と初期データが揃った受信可能なチャンネルが一覧表示される。チャンネル名を選ぶと、そのチャンネルを指定したUnity Viewerへ遷移する。一覧の更新リンクで、録画開始後の状態を再取得できる。

```text
http://127.0.0.1:8000/viewer/
```

チャンネルを直接開く場合は、従来どおりチャンネルのパラメーターをURLエンコードしたURLも利用できる。

```text
http://127.0.0.1:8000/viewer/?channel=http%3A%2F%2F127.0.0.1%3A8000%2Fchannels%2Fchannel_SDUTC%2F
```

ChromeまたはEdgeのコンソールでエラーがないことを確認し、次の式も評価する。

```javascript
crossOriginIsolated === true
navigator.gpu !== undefined
```

いずれも `true` なら、クロスオリジン分離とWebGPUを利用できる。`navigator.gpu` が未定義でも、受信側はWebGL 2とCPUデコードへフォールバックする。

ブラウザの自動再生ポリシーによって音声開始が保留された場合は、画面を一度クリックする。再生開始後はブラウザのfMP4音声時刻をマスタークロックとしてMeshフレームを選択する。fMP4音声がまだ再生可能でない間はMesh時刻を進めず、先行再生によるずれを防ぐ。

受信側は、単独でデコードできるキーフレームと、デコード済みの2フレーム以上が揃うまで待機する。差分フレームのシーケンスが欠落した場合は、壊れた差分を適用せず、最後の正常なMeshを表示したまま次のキーフレームを待つ。
