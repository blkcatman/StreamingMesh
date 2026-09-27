# macOS動作確認

この文書は旧SDユニティちゃんサンプルを使った2026-09-25時点の検証記録です。記載の `Assets/_Files/` と `Assets/TestScene.unity` は現在のプロジェクトから削除済みです。現行デモとビルド手順は `Assets/Samples/UnityChanKAGURA/README.md` を参照してください。

確認日: 2026-09-25

## 判定

**Mac上で送信・受信・GPU処理は動作する。ただし、現行の標準手順をそのまま実行すると送信トークンとFFmpegの探索で止まるため、設定・手順の補正が必要。**

今回はソースコードの修正を行わず、実行中だけ接続先とFFmpegパスを変更し、Playモード内でチャンネルを再作成して確認した。

## 環境

- Apple M3 Pro、arm64、macOS 26.5.1、Metal
- Unity 6000.5.10f1
- Homebrew版Unity CLI 1.0.0-beta.11、Unity Pipeline 0.7.0-exp.1
- FFmpeg 9.0.1 (`/opt/homebrew/bin/ffmpeg`)
- HTTPサーバー: 同梱 `Tools/streamingmesh_dev_server.py`、localhost:18765
- 検証専用チャンネル: `mac_verification`

## 実測結果

| 対象 | 結果 |
| --- | --- |
| Recorderシーン | `Assets/_Files/__Recorder.unity` のUnityChan送信側をPlayモードで実行 |
| チャンネル作成 | テクスチャ・マテリアル・メッシュ情報を含む `stream.json` / `stream.bin` をHTTP経由で生成 |
| メッシュ送信 | 約78.03秒、782フレーム、16チャンクを保存。キーフレーム157、差分625 |
| データ整合性 | 全チャンクのGZip展開・フレーム境界を検査。sequenceは0から連続、PTSは単調増加 |
| メッシュ受信 | `Assets/TestScene.unity` の接続先だけを検証チャンネルへ変更してPlayモードで実行 |
| GPUデコード | Metalで実行、CPUフォールバックなし、再生状態 `Playing` |
| メッシュ更新 | 6メッシュ・合計6,511頂点を復元。表示sequenceが338→527へ進み、頂点座標も変化 |
| 描画 | Gameビューの画像でキャラクターの描画を確認 |
| 音声記録 | 一時的に追加したAudioSourceで440 Hzテスト音を再生。実際の `OnAudioFilterRead` → FFmpeg → HTTP送信を通して記録 |
| 音声ファイル | AAC fMP4初期化セグメント＋77断片、78.208秒。sequenceとサンプル位置が連続、参照ファイルすべて存在 |
| 音声デコード | FFmpegで全断片をデコード。平均音量−26.0 dB、最大−22.3 dBで無音ではないことを確認 |
| AVFoundationデコード | init＋全m4sを連結した実データをAVAudioFileで48 kHz・2ch・Float32 PCMとして読み出し成功 |
| Mac版Chrome | 同梱 `Tools/fmp4_mse_smoke.html` でMSEへの追加と再生成功。`passed: buffered=78.208s currentTime=0.465s` |
| AVFoundation音声再生 | 同じAAC fMP4をHLSとしてAVPlayerへ渡し、Unity Editor上で状態`Playing`、再生時刻18.277→28.874秒を確認。Receiverの同期クロックにも反映 |
| macOS Playerビルド | 現在のPipeline追加済み構成で成功。エラー0件、警告17件 |
| Apple音声プラグイン | arm64/x86_64 universal dylibをPlayerの`Contents/PlugIns`へ同梱。iOS向けObjective-C++ソースはiPhoneOS SDKでarm64コンパイル成功 |
| macOS Player起動 | Metal初期化後12秒間動作し、検証プロセスを終了。既定のリモートURLはDNS解決に失敗したため、このアプリでの受信は未確認 |

Chromeの音声試験はミュートされたスモークテストであり、聴感確認ではない。Unity Web Playerでのメッシュと音声の同期試験とも区別する。

## 再現した問題と回避手順

### 1. Playモードに入るとチャンネルの送信トークンが失われる

文書の「Create Channel → Play → Start Recording」の順序では、Playモードに入った後に `channelPushToken` が空になった。記録開始後に次のエラーが出て送信できなかった。

```text
Channel push token is unavailable; create the channel first.
```

`Assets/StreamingMesh/Scripts/STMHttpBaseSerializer.cs` のトークンは `[NonSerialized]` で、さらに `OnEnable()` で空文字へ初期化される。このため、今回のEditor設定ではPlayモード移行を跨いで維持されない。OS固有のAPIではなく、ライフサイクル上の問題である。

**回避:** Playモードに入ってから `Create Channel` を実行し、作成完了後に `Start Recording` を実行する。この手順で送信に成功した。

### 2. Hubから起動したEditorでは既定のFFmpegパスが解決できない

`STMAudioRecorder.ffmpegPath` の既定値 `ffmpeg` では、今回のHub起動Editorで次のエラーになった。

```text
StreamingMesh could not start FFmpeg: ... Native error= Cannot find the specified file
```

ターミナルからFFmpegを起動できても、GUI起動のEditorが同じPATHを持つとは限らない。前回のシェル起動Editorで成功した結果だけでは、この起動方法を検証できていなかった。

**回避:** `STMAudioRecorder` の `Ffmpeg Path` を `/opt/homebrew/bin/ffmpeg` に設定する。この指定で記録に成功した。

### 3. 同梱の受信シーンは別のチャンネルを参照する

`Assets/TestScene.unity` の既定URLは `https://stored.streamingmesh.net/channels/channel_TestCube/`。同梱Recorderのlocalhost上のチャンネルとは一致しない。

**回避:** ReceiverのChannel Addressを送信先に揃える。今回の検証では `http://127.0.0.1:18765/channels/mac_verification/` を使用した。

## 対応範囲と未確認事項

- macOSのfMP4/AAC音声はAVFoundationで再生できた。その後Unity 6000.6.3f1とiOS Build Supportを導入し、iPhone 12 ProでUnity iOS Playerのビルド、インストール、メッシュアニメーション受信、AVFoundationによるHLS/fMP4取得を確認した。詳細は `Docs/IOS_VERIFICATION.md` を参照。
- チャンネル作成・音声記録にはEditor向け処理があるため、Mac Playerをそのまま送信アプリとして使えるという判定ではない。
- UnityのWeb用ビルドモジュールが未導入のため、Unity Web Player全体のビルド・実行とAV同期は未確認。ChromeのMSE音声再生試験は成功した。
- Intel Mac、別GPU、Safari、長時間運転、外部ネットワーク経由の配信は未確認。
- ビルド警告17件は旧API、未使用フィールド、シリアライズ属性、PlayerでのPipeline無効化に関するもの。ビルドエラーは0件。

## ローカル成果物

gitignore対象のため、以下はこのMac上の検証記録として保存している。

- `Logs/MacCompatibility/e2e-data.json`: 送信データの検査結果
- `Logs/MacCompatibility/receiver-before.json` / `receiver-after.json`: 受信・GPU・頂点更新のスナップショット
- `Logs/MacCompatibility/receiver-game.png`: 受信キャラクターのGameビュー
- `Logs/MacCompatibility/mac-build-status.json`: 現在の構成でのMacビルド結果と警告一覧
- `Logs/MacCompatibility/native-player.log`: Macアプリの起動ログ
- `Builds/MacVerification/MacReceiver.app`: Macアプリ
- `/private/tmp/StreamingMesh-e2e-20260925/channels/mac_verification`: 検証用配信データ

検証用の接続先、FFmpegパス、テスト音、追加GameObjectはシーンに保存していない。検証中に生成されたLightingDataはLogs以下へ退避した。Editorは停止状態の新規シーンへ戻し、一時サーバーとブラウザタブは終了した。
