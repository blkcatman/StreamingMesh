# StreamingMesh セッション引継ぎ

更新日: 2026-10-04（日本時間）。設計レビューから実装へ進むための引継ぎ。
**直近の変更は設計書のみ。以下の新しい責務分離・ライブ／アーカイブ仕様は未実装。**
このファイルを読んだだけで実装・ビルド・PR作成を開始せず、次のユーザー指示に従う。

## 再開時に読むもの

1. このファイルと`git status --short`。未コミットの変更を保護する。
2. [STREAM_PIPELINE_DESIGN.md](STREAM_PIPELINE_DESIGN.md): 最新の責務・名称・圧縮stage。
3. [LIVE_ARCHIVE_DESIGN.md](LIVE_ARCHIVE_DESIGN.md): サーバー保存、完了API、FSM、境界、受入条件。
4. [STREAM_FORMAT.md](STREAM_FORMAT.md): 現行実装のv6。設計中のv7と混同しない。
5. 実装を変更する前に、対象コードとScene／Prefabの設定を再照合する。

他の古いハンドオフは当時の記録である。接線の現状は
[RECEIVER_TANGENTS.md](RECEIVER_TANGENTS.md)、マテリアルは
[RECEIVER_MATERIALS.md](RECEIVER_MATERIALS.md)を優先する。

## リポジトリとGit運用

- 作業場所: `C:/Users/RTX4090/hobby-git-root/StreamingMesh`。
- ブランチ: `feature/resource-path-identities`。
- origin: `git@github.com:blkcatman/StreamingMesh.git`。
- 引継ぎ作成前のHEAD: `8678ab2`。originへpush済み。
- 全プロジェクトで`codex/` prefixは禁止。必要に応じて`epic/`、`feature/`、`fix/`等を使う。
- **PR／Draft PRの作成時期はユーザーが指定する。明示指示まで作成しない。**
- 不要な別ブランチ・worktree・別セッションを自動作成しない。

引継ぎ作成時点の、今回の設計変更に含めなかったローカル変更:

```text
 M Assets/StreamingMesh/Editor/StreamingMeshMacBuild.cs
 M Assets/StreamingMesh/Settings/UniversalRenderPipelineGlobalSettings.asset
?? Assets/_Files.meta
```

これらをreset・削除・一括stageしない。新セッションでは状態を再確認する。

| コミット | 内容 |
| --- | --- |
| `8678ab2` | 6つの役割、Transport Output/Input、選択可能な圧縮stageを設計へ反映 |
| `beda7c5` | フレーム共通モデル、protocol／container／codec分離の初期設計 |
| `3b7a6d6` | Receiver FSM整理、頂点ファイル境界、公開404 |
| `79d0a72` | Receiverのライブ／アーカイブ状態管理を棚卸し |
| `6123e33` | サーバー完了API・ライブ窓・全区間保存の設計 |
| `16360b6` | 直近の実装変更: Web音声のファイル範囲先読み・公称長 |

## 目的と確定した責務

現在のSender／Receiverがcapture、codec、container、圧縮、HTTP、時計・描画まで
抱えているため、以下の流れがコードの別クラスとして追えるようにする。
HTTPファイル配送＋音声HLSを最初のprofileとし、将来のTCP、UDP、WebSocket、
WebRTC DataChannel、Kafka等へ同じcodec・FSM・時計を使えるようにする。
現行のstmj/stmv頂点配送は独自形式であり、HLS準拠の頂点trackではない。

```text
生頂点 → Vertex Encoder → 符号化頂点 → ContainerWriter
       → [Compression / None] → TransportOutput → 通信経路
       → TransportInput → [Decompression / None] → ContainerReader
       → 符号化頂点 → Vertex Decoder → 復元頂点 → 描画
```

| 名称 | 責務 |
| --- | --- |
| `IVertexFrameEncoder` / `StmVertexEncoder` | 生頂点の量子化・K/D符号化。通信やGZipは持たない |
| `ContainerWriter` / `ContainerReader` | 境界・集約・寿命と、serializer／parserによる配置・解析 |
| `IContainerSerializer` / `IContainerParser` | 形式固有のヘッダー・サイズ表・配置 |
| `CompressionStage` / `DecompressionStage` | 経路に選択して組み込む転送データの圧縮／展開 |
| `ITransportOutput` / `ITransportInput` | 不透明なコンテナデータの送信／受信、背圧・取消 |
| `ITransportSession` | 接続・認証・再接続・寿命を一度だけ管理 |
| `IVertexFrameDecoder` / `StmVertexDecoder` | 符号化頂点をCPU／GPUで復元 |
| `CapturePipeline` / `PlaybackPipeline` | 各stageの順序、予算、取消を接続 |
| `ISessionControl` | 公開範囲・session情報・完了等を正規化。メディア配送とは別 |

通信クラスにPush／Pull、Pusher／Pullerを使わない。Output／Inputは
アプリケーションのコンテナが流れる方向であり、通信開始側やsocketの片方向ではない。
InputもHTTP GET・購読要求を送れ、OutputもACKを受け取れる。
双方向接続のOutput／Inputは一つのTransportSessionを共有する。
管理サーバーでは受領がInput、配送がOutput。保存・公開・完了の方針は
RecordingService／ArchiveStore等へ分離し、直接peer通信でもサーバーを必須にしない。
Encoder／Decoderという名前をpipeline全体へ使わない。

### Compressionの最新の合意

- 圧縮をフローから外さず、頂点とTexture／Mesh／Material経路で選択する。
- 例: 低遅延頂点はNone、初期リソースpartはGZip、AACは追加圧縮なし。
- GPU圧縮テクスチャのBC7／ASTC等はresource codec。GZip展開後もその形式を維持する。
- 現行の`InitialDataPartWriter`は複数リソースをGZipへ逐次書き込み、
  `InitialDataParts.Read`はscratchで展開してloaderへ渡す。この利点を維持する。
- Noneではleaseをそのまま渡し、圧縮のためのコピーやscratchを追加しない。
- 圧縮方式・処理位置・単位・サイズ上限をprofileで決定する。
  受信側が展開前に読める目録／配送ヘッダーで識別する。現行はGZip前提でNone未対応。
- 一つのpart全体を圧縮する場合、内部の複数リソースで方式を混在させない。
- Container内部block圧縮が必要な形式では同じ圧縮処理をWriter／Reader内へ接続し、二重適用しない。
- 大きなリソースをstage間で全量展開した配列へコピーしない。slice／streamと所有権を定義する。
- 検証完了前のリソースを公開しない。cancel／破損時にGPUリソースとleaseを片付ける。
- 内容の同一性と圧縮後の配送ハッシュを分け、方式変更だけでGPUリソースを再確保しない。

## ライブ・アーカイブ設計の合意

- SenderのPCへアーカイブを残さない。有限の未確認送信bufferと必要な一時ファイルだけ。
- 完了時にarchive/deleteを選ぶため、サーバーは収録中の全区間を非公開の一時保管へ残す。
  公開2～3チャンクという指定はディスク上の全履歴の削除を意味しない。
- `POST /channels/{channel}/complete`を追加する。recordingId、認証、最終頂点／音声受領点、
  冪等性、再起動時の復旧を扱う。実際のAPIは未実装。
- `liveChunkCount`は初期標準3、範囲2～16。サーバーで可変。
  音声HLSは最低期間と削除猶予の制約により指定数より多くなる場合がある。
- 公開状態はLive／Finalizing／Archived。内部削除状態は公開しない。
  削除済みと未作成は同じ404 Not Found。Receiverから履歴を推測できる情報を返さない。
- 静的stream.jsonと動的stream.state.jsonを分け、recordingId・revision・実範囲を照合する。
- Receiver FSMはDisconnected／Preparing／Paused／Playing／Ended／Faultedの6種類。
  NotFoundはFaultedの理由。PlaybackIntentと未消費要求を保持する。
- Preparingは一つのOperationを持ち、Resources→Snapshot→Buffers→AudioSeek→Readyを進める。
  取消IDで古い完了を無効化し、I/O自体も止める。待機boolの追加で対応しない。
- LivePlaybackPolicyとArchivePlaybackPolicyは副作用を持たない判断役。
  遷移・I/O・時計操作はControllerが一度だけ実行する。
- ライブ新規接続／明示Reconnectは最新の共通再生範囲へ。遅れ・窓外は最新へ復帰。
- archive新規接続は先頭。Play／Pause／Stop／全区間Seekを提供する。
  同じ収録のlive→archiveでは絶対時刻・再生意図・同一リソースを維持する。
- 状態取得を重いメディア取得と分離する。Paused／Preparingでも状態監視を続ける。

### 頂点チャンクの新境界（設計中v7）

- 全`.stmv`の先頭K。通常ファイル末尾D。完了APIで確定した最終ファイルだけ末尾K可。
- `subframesPerKeyframe`を`keyframeIntervalFrames = 旧値 + 1`へ移行。最低2。
  既存Sceneの設定値を移行し、名称変更だけで周期を変えない。
- `combinedFrames`を希望値`targetChunkFrames`と容量上限`maxChunkFrames`へ分ける。
- 通常件数Cは目標Tに最も近い周期Gの倍数。同距離は小さい方、最低G。
  最終は1～C。128MiB・件数上限等も開始前に検証する。
- 境界計画・ordinal・世代をencode／非同期readback前に確定し、完了順で配列を壊さない。
- 最終末尾Kのtailは確定前に通常ライブチャンクとして公開しない。
- v6とv7はSender・サーバー・Receiverを揃えて移行。旧wire互換は不要。

## 既存実装を確認する入口

| ファイル | 分離・修正対象 |
| --- | --- |
| `Assets/StreamingMesh/STMHttpSender.cs` | capture、GPU codec、chunk集約、GZip Task、送信、Stopのdrain |
| `Assets/StreamingMesh/Scripts/STMHttpSerializer.cs` | ResourceSet、形式、URL／認証／送信 |
| `Assets/StreamingMesh/Scripts/STMHttpBaseSerializer.cs` | 送信queue、成功通知、drain |
| `Assets/StreamingMesh/Scripts/Receiver.cs` | 状態・目録取得、リソースfingerprint、先読みとFSM |
| `Assets/StreamingMesh/Scripts/Utils/HttpWrapper.cs` | 目録が文字数増加する前提の差分取得 |
| `Assets/StreamingMesh/Scripts/Core/Rendering/ReceiverFrameBuffers.cs` | chunk pool、GZip、size表、sliceの寿命 |
| `Assets/StreamingMesh/Scripts/Core/Rendering/StreamingMeshRenderer.cs` | 解析・復元・描画の分離、世代管理 |
| `Assets/StreamingMesh/Scripts/Core/Serialization/InitialDataParts.cs` | リソースpartの圧縮／逐次展開 |
| `Assets/StreamingMesh/Scripts/Net/HttpManager.cs` / `Assets/StreamingMesh/Scripts/Core/Threading/ThreadManager.cs` | 同一取得queue、stateの独立枠・取消 |
| `Tools/streamingmesh_dev_server.py` | 追記目録、原子的公開。新complete／永続状態は未実装 |
| `Assets/StreamingMesh/Scripts/STMAudioRecorder.cs` | PCM、AAC、最終出力とdrain |
| `Assets/Plugins/WebGL/StreamingMeshFmp4.jslib` | Web音声取得・MSE、範囲・取消 |
| `Assets/StreamingMesh/Android/StreamingMeshAndroidAudio.java` | Android HLS時刻と絶対PTS |

TimeWireのTransportClockは再生時計であり、ネットワークtransportの契約へ流用しない。
Coreから具体的通信SDKやSTM serializerを参照せず、composition側で注入する。

## 実装を指示された場合の進め方

1. 共通モデル・lease・型付き結果、codec/container/圧縮/通信の境界を小さく実装する。
   fake transportで差し替えと取消・所有権を確認し、外部SDKは同時導入しない。
2. HTTP＋STM＋HLSの既存経路を移し、Scene／Prefab／GUID・Inspector設定を明示移行する。
3. v7境界とSenderのreadback／圧縮／音声／送信ACKのdrainを実装する。
4. サーバーの全区間一時保管・状態snapshot・completeとReceiver FSM／policyを揃える。
   Receiver移行前にサーバーだけライブ目録を短縮しない。
5. 新規KAGURA収録で短チャンク・ライブ・archive・削除とメモリを検証する。

詳細な受入表はLIVE_ARCHIVE_DESIGN.mdとSTREAM_PIPELINE_DESIGN.mdを使う。
特に初期ロード中の窓移動、連続Seek／Pause／Stop、範囲外復帰、A/V同期、
古い完了・同名再収録・サーバー再起動・二重complete、None／GZipの混在、
リソースcache、長時間の有限bufferとlease返却を確認する。

## 環境・検証状況

- `ProjectSettings/ProjectVersion.txt`: Unity `6000.6.3f1`。
- 既存のv6・GPU圧縮テクスチャ・ファイル単位先読みが現在の実装基準。
- 直近の設計変更ではビルド・Sender再収録・Web／Androidの新仕様試験をしていない。
- 境界参照計算1,152,482ケースの確認は設計確認のみ。
  実Sender・バイト上限・FSM・実機試験の合格ではない。
- 今回の設計書はUTF-8、コードfence、ローカルリンク、`git diff --check`を確認済み。
- 以前確認したAndroidはPixel 5a。現在の接続は`adb devices`で再確認する。
  優先はネイティブAPK、その後端末Web。Apple実機は未検証扱い。
- 直近の既存ブラウザURL（今回到達確認なし）:
  `http://127.0.0.1:8006/viewer/?channel=http%3A%2F%2F127.0.0.1%3A8006%2Fchannels%2Fchannel_KAGURA_MULTI_V6%2F`。
  ポート・プロセス・実際のweb/data-rootを再確認し、既存チャンネルをresetしない。
- サーバー既定web-rootは`Builds/WebReceiver`、data-rootは`DevData/channels`。
  起動中サーバーが既定値を使っているとは断定しない。
- Webビルド入口: `StreamingMesh.Editor.StreamingMeshWebBuild.BuildKaguraReceiverCommandLine`。
  Android入口: `StreamingMesh.Editor.StreamingMeshAndroidBuild.BuildReceiverCommandLine`。
  namespaceとビルドtarget・出力設定をコードで再確認してから実行する。
- 既存試験: `Tools/Tests/test_server_publication.py`、`ReceiverBufferTests.cs`、
  `InitialDataPartsTests.cs`、`AudioPcmRingBufferTests.cs`。新仕様の試験は別途必要。
- KAGURA基準は30fps、3秒チャンク・公開3・頂点／Web音声先読み3。
  2秒／5秒、公開2への縮小も新規収録で試す。既存ファイルの単純切断は使わない。
- WASM使用量と確保済み容量、pool・scratch・GPU資源・取得数を別々に記録する。
  新設計でheapが減ったという実測結果はまだない。

## 次のセッションへ渡す文面

> StreamingMeshの`Docs/SESSION_HANDOFF.md`と、リンクされた最新設計書を読み、
> 現行v6実装と設計中v7を区別して引き継いでください。
> 未コミット変更を保護し、PRは明示指示まで作らず、codex/ブランチは使用しないでください。
> 以後の作業範囲はこのセッションでの指示に従ってください。
