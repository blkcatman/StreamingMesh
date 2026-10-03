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

Webサンプルの `Maximum Memory Size` は4096MBに設定する。初期サイズは32MBで、必要に応じて拡張する。4GBへの引き上げと併せて、Receiverは以下の再利用バッファで継続的な割り当てを抑える。

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

## Receiverのメモリ管理

チャンクはGZipの展開サイズを検証し、空いているチャンクバッファへ直接展開する。`MemoryStream` を拡張して `ToArray` する中間配列や、各フレームへのバイト配列コピーは作らない。フレームはチャンク内のオフセット・長さを持つ値型としてリングバッファへ保持し、CPU/GPUはその範囲だけを読む。圧縮形式とフレームのワイヤー形式は変更しない。

解析中のチャンクはワーカースレッドが所有する。全体の検証に成功してUnityスレッドへ戻るまで再生キューに追加せず、追加後は最後のフレームを復元した時点でバッファを返す。GPUへの入力コピー完了やCPUの同期復元より先に再利用しない。Receiver破棄中も、解析中のバッファはワーカーが返すまで保持する。不正データは部分追加せず、同じチャンクを再試行できる。

主な上限は次のとおり。

| 項目 | 制限 |
| --- | --- |
| 受信待ちフレーム | `max(16, Combined Frames * 2)` で取り込みを止める。1チャンク分の追加を含むリング容量はこの値に `Combined Frames` を足す |
| チャンクバッファ数 | `ceil(取り込み閾値 / Combined Frames) + 2`。300フレームのチャンクなら4スロット |
| チャンクの展開サイズ | 1チャンク128MiB、再利用バッファの合計256MiBまで。チャンク内のフレーム数は設定値以下、設定値は最大4096 |
| 復元済みスナップショット | 既存の復元先読み時間とGPUプール容量で制限。管理オブジェクトとCPU頂点配列も再利用する |

順不同のチャンクはシーケンス順に挿入し、重複フレームを除く。差分が欠落した場合のキーフレーム待機とGPUからCPUへの復帰も維持する。受信チャンクの重複確認履歴も固定長にし、配信時間に比例して増やさない。

CPU復元はコマンド・頂点配置の作業リストとスナップショットを再利用し、既存の整数による量子化・差分計算を使う。GPU復元はCommandBufferとWebの完了通知コールバックも再利用する。初期化、初めての大きなチャンクに合わせるバッファ拡張、HTTP受信、GZip/Task、UIやエラー処理には割り当てがあるため、アプリ全体をゼロアロケーションとは扱わない。

検証コマンド:

```powershell
dotnet run --project Tools/Tests/ReceiverBufferTests.csproj
./Tools/Tests/verify_receiver_memory.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe'
./Tools/Tests/verify_receiver_tangents.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe'
```

2026-10-04、Unity 6000.5.10f1・Windows D3D11・RTX 4090 Laptop GPUで、初期化後のCPU復元10,000フレーム、法線・接線を含むCPU更新230回、GPUのキーフレーム／差分復元、法線・接線を含むGPU表示1,000回の測定区間は `GC.GetAllocatedBytesForCurrentThread()` が0バイトだった。リングの折り返し・順序挿入・チャンク再利用・不正データ・遅延完了・バックプレッシャーの180,559項目とUnityでの266項目を検証し、接線の既存474項目も通過した。

同バージョン・URP 17.5でビルドしたWebGPU Receiverは、13メッシュ・9マテリアル・20テクスチャのKAGURA録画を終端の4:47.5まで同期再生した。WebのGPU完了通知と連続スロット再利用が動作し、コンソールのエラーとOOMは0件。10秒間隔の観測でチャンクバッファは68,222,976バイトのまま再利用され、WebAssemblyヒープ容量の最大値は1,294,336,000バイト（約1.21GiB）、使用量は最大約567MBだった。初期データ、通信、GZip、UIなども含む全体のヒープはこの値より小さくなるとは保証しない。プロジェクト指定のUnity 6000.6.3f1・URP 17.6での再検証は別途必要。

## KAGURA Web版の検証記録（2026-09-27）

Unity 6000.6.3f1でKAGURA受信シーンのWebGLビルドに成功し、ローカルサーバーで配信中の `channel_KAGURA` にブラウザから接続した。WebGPU Deviceが選ばれ、13メッシュと4テクスチャを読み込んだ。GPU頂点復元を自動選択した場合は、16フレームをGPUへ投入したままGraphicsFenceの完了が返らず、デコード済みフレーム0で待機した。音声は再生可能状態で、ブラウザにもセグメントが蓄積されていた。ComputeShaderの演算結果が正しいかどうかは、この停止だけでは判定できない。

一時的にWeb版のAuto設定をCPU頂点復元へ切り替えたビルドでは、音声とメッシュの同期再生を開始し、ブラウザ上で音声時刻12秒以上とモデルの動きを確認した。ブラウザの自動再生制限により、再生開始には画面クリックが必要だった。WebGPUは描画APIとして引き続き使用していた。

続く検証では、`AsyncQueueSynchronisation` フェンスはこのWebGPU環境で「async compute非対応」の例外になった。そこで復元スナップショットの先頭16バイトだけをCommandBuffer内で非同期readbackし、GPU完了を判定した。GPUのキーフレーム0の先頭頂点 `(0.003906, 1.070313, -0.066406)` と差分フレーム1の `(0.003906, 1.070313, -0.066345)` は、同じ配信データからCPUで計算した値と6桁表示で一致した。GPU経路のまま音声・メッシュが同期再生し、音声時刻26秒超とモデルの変化を確認。現行のWebビルドはこの方法を使用する。全頂点をCPUへ戻すのではなく、1フレームあたり16バイトだけ完了確認に使用する。WebGPUが使えない環境ではGPU経路の初期化に失敗し、CPU復元を使う。

## リソースID導入後のKAGURA検証（2026-10-04）

`feature/resource-path-identities` のコードでSenderを再収録し、`channel_KAGURA_ID` を生成した。v4チャンネル／v3 MaterialInfo、9マテリアル・20テクスチャ・13メッシュ・23,489頂点、29チャンク・8,628フレーム、29音声セグメント。32個のTextureプロパティ参照が20個のIDを共有し、すべてのMaterial／Texture参照とフレームのシーケンス・PTS、音声サンプル連続性を検証した。

Unity 6000.5.10f1／URP 17.5でWebGPU＋WebGL 2フォールバック、WASM上限4 GBのReceiverをビルド。WebGPUのGPU復元・法線・接線再計算で終端の約4:47.6まで同期再生し、ブラウザのエラー／OOMは0件だった。MSEは終端通知を出さないため、UIのPlaying表示とaudio.paused=falseは終端でも残る。

| 観測最大値 | 前回（名前参照） | 今回（ID参照＋初期メモリ削減） | 減少 |
| --- | ---: | ---: | ---: |
| WASM確保済み容量 | 1,294,336,000 B（1,234.4 MiB） | 998,375,424 B（952.1 MiB） | 22.9% |
| WASM使用量 | 567,275,912 B（541.0 MiB） | 388,342,648 B（370.4 MiB） | 31.5% |
| Managedヒープ容量 | 465,743,872 B（444.2 MiB） | 288,546,816 B（275.2 MiB） | 38.0% |
| チャンクプール | 68,222,976 B（65.1 MiB） | 68,222,976 B（65.1 MiB） | 同量で再利用 |

前回のWASM計測は10秒間隔、今回は2秒間隔。ProfilerのManagedヒープ計測は双方10秒間隔。値はこのKAGURA収録・環境で観測した最大値であり、瞬間的なピークの保証ではない。今回はID導入に加え、初期GZipのMemoryStream拡張／ToArrayとJSONのバイト配列コピーを除去し、ビルド時にテンプレートのローカル画像参照を外した。`.data` は21,937,308 Bから8,376,590 Bへ減少した。KAGURAの配信Texture数は前後とも20であり、ID化単独の削減効果を示す比較ではない。

再生計算の初期化後はCPU復元10,000フレーム、法線・接線付きCPU更新230回、GPU復元、法線・接線付きGPU表示1,000回で管理ヒープの割り当て0 Bを再確認した（266項目）。ネットワーク、GZip、UI、初期構築を含むReceiver全体はゼロアロケーションではない。リソースID24項目、マテリアル3,569項目、接線474項目も通過し、静止形状のSender／Receiverマテリアル描画比較はbyte差0だった。指定Editor 6000.6.3f1／URP 17.6とモバイルは未検証。

再現用の確認コマンド:

```powershell
python Tools/Tests/verify_resource_capture.py DevData/channels/channel_KAGURA_ID
python Tools/Tests/instrument_web_receiver.py Builds/WebReceiverIdentity/index.html
python Tools/streamingmesh_dev_server.py --web-root Builds/WebReceiverIdentity
```

計測用HTML変更はビルド出力だけに適用する。コンソールの `STM_METRICS` と検証用 `KaguraMemoryProbe` の `STM_PROBE` を記録する。ローカル計測結果は `Logs/KaguraReceiver-identity-summary.json`、`Logs/KaguraReceiver-identity-metrics.jsonl`、`Logs/KaguraReceiver-identity-console.json` に保存した。実行画面は `Logs/KaguraReceiver-identity-WebGPU.png`。

## 初期ロードとバッファのメモリ診断

ReceiverのInspectorで `Log Memory Diagnostics` を有効にすると、コンソールに `STM_MEM` のJSONを出力する。既定値は無効。初期GZip展開、PNG配列コピー、`Texture2D.LoadImage`、CPU/GPUバッファ生成、チャンク展開の前後、および初期化後の2秒間隔で測定する。JSONとログ文字列の生成には割り当てがあるため、通常のゼロアロケーション計測では無効にする。

`wasmCapacity` はWASM線形メモリのバイト長、`wasmAllocated` はUnityの `GetMetricsInfo` が返すWASM使用量。`monoHeap` / `monoUsed` と `nativeAllocated` / `nativeReserved` はUnity Profilerの値であり、相互に重なる領域を含むため合算しない。Web以外ではWASMの2値は0。`bytes` はその段階で処理する入力サイズであり、全体の使用量ではない。

`buffers` にはチャンクの確保容量・現在保持するデータ量・同時保持量の最大値・貸出スロット数とその最大値、フレームリング容量、頂点配列の容量、GPUプールとモデルの容量、GPU転送配列容量を記録する。GPU容量はGPUリソースの論理サイズであり、WASM使用量への加算値ではない。チャンクの最大値は貸出中の配列全体の入力サイズで、未復元フレームだけのサイズではない。GPU入力サイズ・保持スロットの最大値は診断有効時のみ追跡する。

生成された非圧縮Webビルドに次を適用すると、Emscriptenのヒープ拡張時にも `STM_GROW` を出力する。

```powershell
python Tools/Tests/instrument_web_receiver.py Builds/WebReceiverMemoryTrace/index.html --trace-growth
python Tools/streamingmesh_dev_server.py --web-root Builds/WebReceiverMemoryTrace
```

`STM_GROW` は拡張直前の容量、要求容量、拡張後の容量、直前の診断段階、呼出スタックを記録する。生成済みのframework.jsだけを変更し、想定する拡張処理が見つからなければ中止する。`seconds` はブラウザのperformance時刻で、`STM_MEM` のUnity起動後時刻とは基準が異なる。段階は直前のチェックポイントなので、処理がUnity内部へ遅延された場合は呼出スタックと併せて判断する。チャンク展開の前後のチェックポイントはWebビルドで記録する。

2026-10-04、同じKAGURA ID収録をUnity 6000.5.10f1／URP 17.5のWebGPU Receiverで測定。最初の `Body_Base` の `LoadImage` 前後で、WASM容量は228,982,784 B（218.4 MiB）から744,423,424 B（709.9 MiB）へ増加し、使用量は169,335,520 Bから169,335,208 Bへ戻った。呼出中に3回の拡張要求を確認した。最終ビルドの寸法ログでも8192×8192、Mip数1を確認。PNG入力は8,863,800 Bだが、展開画像はRGBA換算で256 MiBとなる。同サイズのPNGは6枚あり、残り14枚は2048×2048。大きな拡張は画像ロード中の一時確保に対応するが、デコード・転送・Unity内部作業領域の個別内訳と瞬間的な全使用量は未測定。

チャンクプール容量は68,222,976 B、同時保持データの最大値は68,190,306 B（約99.95%）、同時貸出最大3／4スロット。4番目のバイト配列は未確保だった。GPU転送配列は8,527,464 B、GPUプール33,331,848 B、CPU頂点配列563,736 B。GPUの保持最大18スロットと入力最大123,900 Bも記録した。これらのプール容量だけでは画像ロードによる約492 MiBの拡張を説明できない。

診断有効で音声時刻287.592秒まで同期再生し、エラー／OOMは0件。2秒間隔のWASM容量最大1,075,380,224 B（1025.6 MiB）、使用量最大428,257,368 B（408.4 MiB）。管理ヒープ容量最大328,433,664 B（313.2 MiB）。ログ生成による割り当てとGCタイミングの差を含み、前節の診断無効時の性能値と直接比較しない。診断無効ではCPU/GPU復元・表示の既存266項目で測定区間の割り当て0 Bを維持し、バッファの使用量カウンターを含む180,563項目も通過した。

詳細はローカルの `Logs/KaguraReceiver-memory-trace-snapshots.jsonl`、`Logs/KaguraReceiver-memory-trace-metrics.jsonl`、`Logs/KaguraReceiver-memory-trace-summary.json`。コンソール履歴には保持数の制限があるため、初期から中間で取得したスナップショットと終端のスナップショットを統合した。画像寸法を追加した最終ビルドの初期ログは `Logs/KaguraReceiver-memory-trace-final-console.json`。実行画面は `Logs/KaguraReceiver-memory-trace-WebGPU.png`。
