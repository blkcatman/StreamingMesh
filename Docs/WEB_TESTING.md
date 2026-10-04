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
| 受信待ちフレーム | リング容量は`min(8192, VertexPrefetchChunks * CombinedFrames)`。完全な1ファイル分の空きがなければ取得を待つ |
| チャンクバッファ数 | `VertexPrefetchChunks`（標準3、1〜16）。8192フレームの上限で実効数を制限する。大きな配列は遅延確保し、変更時も引き継ぐ |
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

2026-10-04、同じKAGURA ID収録をUnity 6000.5.10f1／URP 17.5のWebGPU Receiverで測定。最初の `Body_Base` の `LoadImage` 前後で、WASM容量は228,982,784 B（218.4 MiB）から744,423,424 B（709.9 MiB）へ増加し、使用量は169,335,520 Bから169,335,208 Bへ戻った。呼出中に3回の拡張要求を確認した。最終ビルドの寸法ログでも8192×8192、Mip数1を確認。PNG入力は8,863,800 Bだが、展開画像はRGBA換算で256 MiBとなる。同サイズのPNGは5枚あり、残り15枚は2048×2048。大きな拡張は画像ロード中の一時確保に対応するが、デコード・転送・Unity内部作業領域の個別内訳と瞬間的な全使用量は未測定。

チャンクプール容量は68,222,976 B、同時保持データの最大値は68,190,306 B（約99.95%）、同時貸出最大3／4スロット。4番目のバイト配列は未確保だった。GPU転送配列は8,527,464 B、GPUプール33,331,848 B、CPU頂点配列563,736 B。GPUの保持最大18スロットと入力最大123,900 Bも記録した。これらのプール容量だけでは画像ロードによる約492 MiBの拡張を説明できない。

診断有効で音声時刻287.592秒まで同期再生し、エラー／OOMは0件。2秒間隔のWASM容量最大1,075,380,224 B（1025.6 MiB）、使用量最大428,257,368 B（408.4 MiB）。管理ヒープ容量最大328,433,664 B（313.2 MiB）。ログ生成による割り当てとGCタイミングの差を含み、前節の診断無効時の性能値と直接比較しない。診断無効ではCPU/GPU復元・表示の既存266項目で測定区間の割り当て0 Bを維持し、バッファの使用量カウンターを含む180,563項目も通過した。

詳細はローカルの `Logs/KaguraReceiver-memory-trace-snapshots.jsonl`、`Logs/KaguraReceiver-memory-trace-metrics.jsonl`、`Logs/KaguraReceiver-memory-trace-summary.json`。コンソール履歴には保持数の制限があるため、初期から中間で取得したスナップショットと終端のスナップショットを統合した。画像寸法を追加した最終ビルドの初期ログは `Logs/KaguraReceiver-memory-trace-final-console.json`。実行画面は `Logs/KaguraReceiver-memory-trace-WebGPU.png`。


## GPU圧縮テクスチャの書き出し・直接アップロード検証（2026-10-04）

独立した検証プロジェクトをWindowsのUnity 6000.5.10f1で生成し、DXT1（BC1）、DXT5（BC3）、BC7、ETC2 RGB、ETC2 RGBA8、ASTC 4×4、ASTC 6×6の7形式を `EditorUtility.CompressTexture` で書き出した。各形式にsRGB／Linearの48×48画像を用意し、RGB専用形式以外は透過アルファも変化させる。実形式・Mip数・ブロックから求めたバイト数を照合し、14個すべて成功した。出力はコンテナのヘッダーを含まない圧縮ブロックと、寸法・形式・sRGB／Linearのメタデータ。

受信テストは独立して生成したTextureへ `LoadRawTextureData` → `Apply(false, true)` でアップロードし、GPUで元画像と圧縮画像を描画してreadbackする。LinearプロジェクトでsRGB／UNormの実GraphicsFormatまで照合し、圧縮状態・寸法・Mip数1・CPU画素非保持を確認する。ロスのある圧縮なので、RGBA各成分のbyte差で平均8以下・最大64以下を許容した。GPU非対応形式をRGBAへ展開した結果は直接ロードの成功に数えない。

| 形式 | Windows Editorから書き出し | D3D11の直接描画 | ブラウザWebGPUの直接描画 |
| --- | --- | --- | --- |
| DXT1 / BC1 | 成功 | 成功 | 成功 |
| DXT5 / BC3 | 成功 | 成功 | 成功 |
| BC7 | 成功 | 成功 | 成功 |
| ETC2 RGB | 成功 | 非対応 | 非対応 |
| ETC2 RGBA8 | 成功 | 非対応 | 非対応 |
| ASTC 4×4 | 成功 | 非対応 | 非対応 |
| ASTC 6×6 | 成功 | 非対応 | 非対応 |

GPUはRTX 4090 Laptop GPU。sRGB／Linearの6ケースでD3D11・WebGPUのreadback誤差が一致し、いずれも通過した。ETC2／ASTCは `SupportsTextureFormat` と `IsFormatSupported(..., Sample)` で非対応を確認し、アップロードを実行しない。対応するモバイルGPU・WebGL 2・Unity 6000.6.3f1での実描画は未検証。

### KAGURAの8K Body_BaseとWASMメモリ

元の `Body_Base.png`（8192×8192、9,543,528 B）をBC7へ書き出し、64 MiBの圧縮ブロックをHTTPで受信した。D3D11とWebGPU双方で実形式 `RGBA_BC7_SRGB`、Mip数1、CPU画素非保持を確認。48×48へ縮小したGPU readbackは元PNGとの差が平均0.066／255、最大6だった。PNGの対照描画は差0。以下は同一Webビルドを各経路で新規ロードした測定で、ブラウザのエラー／OOMはすべて0件。

| 8Kロード経路 | 入力 | WASM確保済み容量の最大観測値 | 終了後のWASM使用量 | 管理ヒープ使用量 |
| --- | ---: | ---: | ---: | ---: |
| PNG `LoadImage` | 9.10 MiB | 677.7 MiB | 58.9 MiB | 10.1 MiB |
| BC7、`downloadHandler.data` の全量管理配列コピー | 64 MiB | 310.4 MiB | 114.2 MiB | 65.0 MiB |
| BC7、受信バッファのNativeArray参照 | 64 MiB | 179.1 MiB | 49.4 MiB | 1.0 MiB |

最終行は `downloadHandler.nativeData.CopyTo(texture.GetRawTextureData<byte>())` → `Apply(false, true)`。受信バッファの参照はDownloadHandlerが生存している間だけ借り、独自にDisposeしない。TextureのCPUバッファへ同期コピーし、Apply後はそのCPUバッファを破棄する。64 MiBの管理byte配列とPNG展開を避け、WASM容量の最大観測値はPNG比73.6%減った。GPUへのゼロコピーではなく、受信バッファとアップロード前のTexture用ネイティブ領域は必要。HTTP、Texture生成、診断ログ、検証用readbackの割り当てまでゼロにした測定ではない。

容量はチェックポイント、ヒープ拡張イベント、2秒間隔のUnityメトリクスから得た最大値。使用量はStartコルーチン終了から5フレーム後、明示GCを呼んだチェックポイントの値で、GCの回収完了や定常値の保証ではない。PNGの呼出中は一時領域が解放されるため、前後の使用量だけから瞬間的な使用量のピークは求めない。配信PNGと元PNGはエンコードされたバイト数が違い、Receiver全体のKAGURA再生結果との直接比較ではない。

### 再現と本実装の制約

```powershell
./Tools/Tests/verify_texture_compression.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe'
python Tools/Tests/instrument_web_receiver.py Builds/TextureCompressionProbe/index.html --trace-growth
python Tools/streamingmesh_dev_server.py --host 127.0.0.1 --port 8001 --web-root Builds/TextureCompressionProbe
```

ブラウザで `/viewer/?channel=probe&mode=matrix`、`mode=bc7`、`mode=bc7-native`、`mode=png` をそれぞれ新規ロードする。`STM_TEX_EXPORT`、`STM_TEX_RESULT`、`STM_TEX_SUMMARY`、`STM_TEX_MEM` と `STM_GROW` を記録する。ページの `#texture-probe-result` に結果JSONも表示する。コンソール履歴の最新の `STM_TEX_MEM` のstage=start以降を `Logs/TextureCompression-WebGPU-{mode}-console.json`、結果JSONを同名の `-results.json` へ保存した後、次を実行する。

```powershell
python Tools/Tests/summarize_texture_compression.py
```

集計は `Logs/TextureCompression-summary.json`、ネイティブ結果は `Logs/TextureCompression-native-results.json`、Editorログは `Logs/TextureCompression-verification.log`。画面は `Logs/TextureCompression-WebGPU-matrix.png` と `Logs/TextureCompression-WebGPU-bc7-native.png`。出力ファイルは `Builds/TextureCompressionProbe/payload`、48×48の各形式は検証プロジェクトの `Assets/Resources/*.bytes` にある。検証コードはToolsだけに追加し、本番のSender／Receiverとstream.binのPNG形式は変更していない。

KAGURA ID配信のPNGヘッダーを読み直すと8Kは5枚、2Kは15枚だったため、初期ロード診断節の枚数を訂正した。すべてをMipなしBC7へ置き換えるだけでも圧縮ブロックは合計380 MiBとなり、現在の初期データ展開上限128 MiBを超える。実装時は全Textureを単一stream.binへまとめる方式の見直し、個別リソースの配信、対応形式の選択とメタデータ（形式・寸法・Mip・sRGB等）の検証が必要。小さいfixtureで書き出せたことだけではETC2／ASTCの実機ロードやKAGURA全体のメモリ削減を保証しない。


## v5初期検証：GPU圧縮Texture・GZip分割配信（2026-10-04）

この節は16MiBでTextureを分割した初期実装の記録。現在のリソース単位分割・複数形式・再接続再利用は次節を参照。

SenderのCreate Channelは、`textureFormat`でGPU形式を選び、Material／Mesh JSONを先に格納し、GPUブロックを展開後最大16MiBの`stream0.bin`、`stream1.bin` …へ書き出す。各ファイルはGZip Fastestで圧縮する。`stream.json`には形式・寸法・ミップ数・linear、各ファイルのサイズ・SHA-256とセグメント表を格納し、全ファイルの送信後に公開する。Receiverは64KiBの再利用バッファからTextureのCPU領域へ展開し、`Apply(false, true)`する。PNG LoadImageや全Textureの結合配列は使わない。

検証はSource Editor 6000.6.3f1を閉じず、独立したUnity 6000.5.10f1／URP 17.5のプロジェクトで実施した。SourceプロジェクトのURP 17.6設定は変更せず、検証プロジェクトには17.5の既定Global Settingsを新規作成した。KAGURAはFBX単体とPrefabでMaterial割り当てが異なるため、収録と同じPrefabからSenderのSerializer／InitialResourceExporterを使って初期データを再生成した。Meshの順序・頂点数・indices、9 Material／20 TextureのIDの一致を検証し、既存の29頂点チャンク・8628フレームと29音声セグメントを再利用した。動作の再収録はしていない。

| 項目 | 結果 |
| --- | ---: |
| TextureのGPUブロック（BC7、ミップなし） | 398,458,880 B（380 MiB） |
| メタデータを含む初期データ | 400,455,951 B |
| GZip付き分割ファイル | 25ファイル、合計23,543,553 B（22.45 MiB） |
| Native復元 | 20 TextureすべてBC7、CPU画素非保持 |
| Nativeでの元Materialとの描画差 | RGB平均0.000562／255、差3超の画素2個 |
| WebGPU復元 | 13 Mesh／20 Texture、BC7のまま、ミップ1 |
| WebGPU全尺再生 | 音声287.585705秒、エラー／OOM 0 |
| 初期化完了時WASM容量／使用量 | 175.5／89.4 MiB |
| 全尺再生中WASM容量最大 | 364.0 MiB |
| 全尺再生中WASM使用量最大 | 304.9 MiB |
| 管理ヒープ容量最大／使用量最大 | 210.6／146.0 MiB |

過去の同環境・同収録・診断有効のPNG経路では、容量最大1025.6MiB、使用量最大408.4MiBだった。今回の初回全尺再生の観測値は容量64.5%、使用量25.3%減。ビルドとGCタイミングの差を含む比較で、GPUメモリやブラウザ全体の使用量を意味しない。GPUブロックをさらにGZipで圧縮してもGPU上では380MiBが必要。元ファイル直接変換のBody_Base単体BC7は64MiB→約12.4MiBだったが、今回のSourceインポートTextureからの再圧縮は別入力であり、圧縮率を混同しない。

WASM容量は一度拡張すると縮まらない。追加の再接続テストでは容量524.2MiBまで再拡張したため、364MiBは初回全尺再生の値として扱う。再接続時のGC・アロケーターの再利用／断片化を含む保持容量の内訳は未分離。大きな配列を全く確保しないという意味ではなく、Textureのアップロード用CPU領域、HTTP受信領域、既存のチャンクプールは必要である。初期リソース5/25の読み込み中にDisconnectし、エラーなしで中断したことも確認した。

Windows Editorの本番Exporterで7形式×sRGB／Linearの14ケースを再検証。BC7／DXT1／DXT5はD3D11の描画比較も通過し、ETC2／ASTCは書き出し成功・今回のGPUでは非対応。BC7の32×16・6ミップ・720バイトの復元も通過。対応モバイルGPU、WebGL 2での圧縮Texture実描画は未検証。初期データの172項目と、HTTPサーバーの完成目録公開・欠落／改変ファイル拒否・並行公開テストも通過した。Windowsの一時的なファイル置換／オープン競合は、完全な旧ファイルを保持したまま短く再試行する。

SenderのCreateChannelからHTTP送信までを通す追加Unityテストは、自動承認レビューが起動操作を拒否したため未実行。Senderが共用する書き出しパイプライン、実ファイルからのNative／WebGPU読み込み、サーバーの公開APIはそれぞれ検証済みだが、その一連のHTTP経路を通した成功とは区別する。

```powershell
./Tools/Tests/verify_indexed_resources.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe'
dotnet run --project Tools/Tests/InitialDataPartsTests.csproj
python -m unittest discover -s Tools/Tests -p 'test_server*.py'
python Tools/Tests/verify_resource_capture.py DevData/channels/channel_KAGURA_INDEX
python Tools/Tests/instrument_web_receiver.py Builds/IndexedReceiver/index.html --trace-growth
python Tools/streamingmesh_dev_server.py --port 8002 --web-root Builds/IndexedReceiver
```

`http://127.0.0.1:8002/viewer/?channel=http%3A%2F%2F127.0.0.1%3A8002%2Fchannels%2Fchannel_KAGURA_INDEX%2F` を開く。音声の自動再生が保留された場合は画面をクリックし、DOMのaudio.currentTimeとReceiver表示時間が進むことを確認する。容量／使用量はチェックポイント・拡張イベント・2秒間隔メトリクスの最大値であり、瞬間的な使用量の全ピークを保証しない。

実データは`DevData/channels/channel_KAGURA_INDEX`、ビルドは`Builds/IndexedReceiver`。記録は`Logs/IndexedResources-export-native.log`、`IndexedResources-encoders.log`、`IndexedResources-capture-summary.json`、`IndexedResources-WebGPU-summary.json`、`IndexedResources-WebGPU-merged-console.json`、`IndexedResources-WebGPU-metrics.json`、`IndexedResources-WebGPU-reconnect-console.json`。終端画面は`Logs/IndexedResources-WebGPU.png`。

## リソース単位分割・Reconnect再利用・複数GPU形式（2026-10-04）

Senderは既定でBC7 → ASTC 4×4 → ASTC 6×6 → DXT5 → ETC2 RGBA8の5形式を同じチャンネルへ書き出す。順序はSenderの優先順位で、ReceiverはGPUが全Textureの寸法・形式・sRGB／Linearに対応する最初のvariantを選ぶ。OS名で固定しない。選ばれた形式だけを読み込み、非対応形式の初期データはダウンロードしない。DXT1／ETC2 RGBも選択できるが、アルファを失うため既定から除外した。対応形式がなければ、Texture生成前に接続を中止する。

各ファイル名はランダムな32桁hexと`.bin`。複数の完全なリソースをまとめ、64MiBを目安に次のファイルへ進む。Texture1枚を境界で切らないため、64MiB超は許容する。次のリソースで128MiBを超える場合は先にファイルを閉じ、リソース自体が128MiBを超える場合は拒否する。容量はGZip展開後を基準にし、Senderも64KiBの再利用バッファから直接GZipへ書き込む。64MiBの展開済み配列は確保しない。ランダム名は難読化・暗号化ではなく、目録とファイルからリソースを復元できる。

独立したUnity 6000.5.10f1／URP 17.5の検証プロジェクトで、同じKAGURA Prefabを本番Exporterに渡し、20 Textureすべてを5形式へ変換した。既存29チャンク・8628フレーム・29音声セグメントを再利用し、動作の再収録はしていない。

| 形式 | 初期データファイル数 | Texture GPUブロック合計 | GZipファイル合計 |
| --- | ---: | ---: | ---: |
| BC7 | 6 | 398,458,880 B | 23,544,224 B |
| ASTC 4×4 | 6 | 398,458,880 B | 20,578,767 B |
| ASTC 6×6 | 3 | 177,347,840 B | 19,344,855 B |
| DXT5 | 6 | 398,458,880 B | 15,712,287 B |
| ETC2 RGBA8 | 6 | 398,458,880 B | 16,052,655 B |

27ファイル合計95,232,788 B。各形式の目録を検証し、Textureの分割レコードは0件。BC7のファイルは展開後65.905／64／64／68／64／56MiBで、末尾ファイルには14枚が入る。8K・BC7の1枚64MiBは自然に単独ファイルになる場合がある。形式ごとにMaterial／Meshのメタデータを含むため、その分の重複は残る。

KAGURAのConnect／ReconnectはReceiverを保持する。同じチャンネルURL・選択済み目録のID／SHA-256・Shader設定・復元設定が一致すれば、既存のTexture／Material／Mesh・CPU／GPU配列・チャンクプールを再利用し、再生状態だけをリセットする。IDが同じでも内容のハッシュが変われば再構築する。Disconnectは保持リソースを解放する。ローカルMaterialテンプレートのプロパティを実行中に直接変更した場合は参照が変わらないため、`Reconnect(..., forceReload: true)`で明示更新する。

Native検証ではCPU／GPUパイプラインと各Unityリソースの同一インスタンス維持、チャンク0の再インポート、古い非同期処理の拒否、ハッシュ／接線設定変更によるキャッシュ無効化を確認した。初期データ182項目とサーバー公開テスト2件も通過。サーバーは主目録だけでなく全variantのファイルの存在・サイズ・SHA-256を確認してからstream.jsonを公開する。

RTX 4090 Laptop GPUのブラウザWebGPUでは自動選択のBC7と、`texture_format=DXT5`による強制選択で、それぞれ13 Mesh／20 Texture・Mip1・CPU画素非保持を確認した。音声時刻は287.583176秒／287.584052秒まで進み、画面も終端まで更新された。検証中にエラー／OOMは観測しなかった。BC7で2回Reconnectし、キャッシュヒットと再生再開を確認。HTTPアクセスはBC7の6ファイル各1回、DXT5の6ファイル各1回、ASTC／ETC2は0回で、再接続による初期データの再ダウンロードはなかった。

2秒間隔の診断で、BC7（最初のwarm Reconnect後の全尺再生を含む）のWASM容量最大373.375MiB／使用量最大305.768MiB、DXT5は366.0MiB／302.809MiBを観測した。プールは68,222,976 Bを再利用するが、HTTP・音声・診断・GCタイミングを含むヒープ全体の容量一定を保証しない。Textureの再構築を避けたことと、再接続を何度繰り返しても容量が増えないことは区別する。選択されなかった形式はGPUへロードしていない。ASTC／ETC2の対応モバイルGPU、WebGL 2、Source Unity 6000.6.3f1／URP 17.6での実描画は未検証。SenderのCreateChannelからHTTP送信までの追加Unityテストは前節のとおり未実行で、共用ExporterとWeb読み込みの検証結果として扱う。

Webビルドヘルパーと検証ビルドは`PlayerSettings.runInBackground = true`を明示する。非表示タブに対するブラウザの描画／タイマー制限は別途残る。データ再公開時の旧ランダムファイルは自動削除せず、公開済み世代のファイルを保持する。更新を繰り返すとディスク使用量が増えるため、利用終了した世代の整理は別途必要。

```powershell
./Tools/Tests/verify_indexed_resources.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe' -Output Builds/MultiFormatReceiver -ChannelOutput DevData/channels/channel_KAGURA_MULTI
dotnet run --project Tools/Tests/InitialDataPartsTests.csproj
python -m unittest discover -s Tools/Tests -p 'test_server*.py'
python Tools/Tests/verify_resource_capture.py DevData/channels/channel_KAGURA_MULTI --texture-format ASTC_6x6
python Tools/Tests/instrument_web_receiver.py Builds/MultiFormatReceiver/index.html --trace-growth
python Tools/streamingmesh_dev_server.py --port 8005 --web-root Builds/MultiFormatReceiver --data-root DevData/channels
```

自動選択のURLは`http://127.0.0.1:8005/viewer/?channel=http%3A%2F%2F127.0.0.1%3A8005%2Fchannels%2Fchannel_KAGURA_MULTI%2F`。DXT5の確認には末尾へ`&texture_format=DXT5`を追加する。音声が自動再生待ちの場合は画面をクリックする。

記録は`Logs/MultiFormatReceiver-verification.log`、`MultiFormatReceiver-capture-summary.json`、`MultiFormatReceiver-requests.json`、`MultiFormatReceiver-WebGPU-summary.json`、`MultiFormatReceiver-BC7-console.json`、`MultiFormatReceiver-BC7-reconnect-console.json`、`MultiFormatReceiver-BC7-metrics.json`、`MultiFormatReceiver-DXT5-console.json`、`MultiFormatReceiver-DXT5-metrics.json`。画面は`Logs/MultiFormatReceiver-BC7.png`と`Logs/MultiFormatReceiver-DXT5.png`。

## Androidネイティブ優先・Web音声バッファの確認（2026-10-04）

接続したPixel 5a／Android 14／Adreno 620で、先にARM64 IL2CPPのAPKを検証した。VulkanではASTC 4×4を自動選択し、音声287.613秒までの再生、Reconnectのキャッシュヒットと再生再開を確認。ASTC 6×6、ETC2 RGBA8も指定して実描画した。全ケースで13 Mesh／20 Texture、Mip1・CPU画素非保持。ネイティブのビルド手順とメモリ診断は`NATIVE_AUDIO_PLAYBACK.md`を参照。

続いて同じ端末のChrome 154でWeb版を確認した。今回のグラフィックス経路はWebGPUであり、WebGL 2経路の検証とは区別する。ASTC 4×4を自動選択して20枚を直接GPUへロードし、GPU residentのメッシュ計算経路を使用した。

旧版では停止中でも音声の29セグメントをすべて読み込み、`SourceBuffer`の範囲が約0～250秒になったところで残り4セグメントの`appendBuffer`が`QuotaExceededError`になった。これはMSE音声バッファの上限超過で、WASMのヒープ上限超過とは異なる。さらに旧コードはappend前にキューから取り出していたため、失敗したデータを失っていた。

`StreamingMeshFmp4.jslib`は現在時刻の先60秒までを取得し、過去30秒より前と先90秒より後を解放する。境界のセグメント全体を取得するため、取得範囲は最大1セグメント分だけ先60秒を越える。キューは少数に制限し、append成功後にだけ消費済みとする。Quota発生時は同じバイト列を保持して再試行し、過去の保持を2秒へ縮める。後方シークは解放済みのセグメントを再取得し、古いシーク世代の非同期取得結果は破棄する。部分取得中も目録の全再生時間をMediaSource.durationへ反映し、離れた時刻へのシークを維持する。

```powershell
# 既存の独立検証プロジェクトを使い、GPU形式の再書き出しは省略する。
./Tools/Tests/verify_indexed_resources.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe' -VerificationProject '<独立プロジェクト>' -Output Builds/AndroidWebReceiver -BuildOnly
python Tools/Tests/instrument_web_receiver.py Builds/AndroidWebReceiver/index.html --trace-growth
python Tools/Tests/android_web_probe_server.py --port 8006 --web-root Builds/AndroidWebReceiver --data-root DevData/channels
adb reverse tcp:8006 tcp:8006
node Tools/Tests/test_web_audio_buffer.cjs
```

URLは`http://127.0.0.1:8006/viewer/?channel=http%3A%2F%2F127.0.0.1%3A8006%2Fchannels%2Fchannel_KAGURA_MULTI%2F`。USB reverse設定済み端末とホストPCの両方で開ける。音声の自動再生が保留された場合は画面をタップする。診断サーバーはlocalhostへだけbindし、コンソール・WASM診断を`Logs/AndroidWebReceiver-events.jsonl`に保存する。ビルド・目録・チャンクは共用するが、診断フックはこのサーバーが返すHTMLだけに追加する。

容量制限付きのMSEモックで、停止中の取得範囲、失敗バイト列の再試行、前方／後方シーク、古い非同期取得と破棄後の結果拒否を検証した。PCの修正版WebGPU／BC7でも再生と前方／後方シークを確認した。Androidの結果・観測値は`Logs/AndroidWebReceiver-summary.json`、画面は`AndroidWebReceiver-fullplay.png`に保存する。WASM診断は2秒間隔であり、ブラウザ全体・GPUメモリ・瞬間ピークを意味しない。

Androidの最終ビルドでは音声287.397737秒まで進み、旧版で失敗した250秒を越えて描画を継続した。20 TextureすべてASTC 4×4で、append例外・ページ例外・OOMは観測しなかった。音声バッファの保持時間最大は99.904秒、WASM容量最大445.313MiB／使用量最大381.643MiB。変更途中の別実行では533.875／450.447MiBも観測しており、今回の変更でWASM容量全体が一定・減少したとは判断しない。MSEのバッファ制限とWASMのプール／GCは別に評価する。

残る現象として、Android Chromeは末尾の約0.26秒でreadyState=2の待機状態になり、`ended`通知を受け取らなかった。buffered末尾287.658666秒・目録の終了時刻まで取得済みだが、今回の確認を完全な終端通知の成功とは扱わない。現行プラグインはライブ追加を想定してMediaSourceを開いたままにするため、収録完了の明示とendOfStreamを含む終了処理は別途検証が必要。ネイティブ版の終端再生と区別する。

終盤確認後、Android Webでも後方・前方シークとReconnectを実行した。解放済みの音声を再取得して再生でき、3回のリソースキャッシュヒットを観測。初期Textureのreadyログは20件のまま、ランダム名の初期binへのHTTP要求数も増えなかった。Reconnect後は0秒から再生を再開した。PCの最終ビルドはBC7で287.583269秒まで進み、音声バッファ上限例外はなかった。


## v6・頂点ファイル単位の先読みと追記再生（2026-10-04、4addc9a時点）

`stream.json`をlowerCamelCaseに統一し、`stream.stma.sequence`を削除した。現在のSender／Receiver／開発サーバーはv6を使用し、旧名・旧チャンネルの互換読み込みは行わない。[STREAM_FORMAT.md](STREAM_FORMAT.md)に全プロパティの対応と公開順序を記載した。頂点フレームのsequenceとHLS内部のsequenceは維持する。

当初の検証では、頂点保持数は標準3ファイル（現在消費中を含む）、1〜16で可変。Web音声は未来60秒／過去30秒を指定していた。この秒数APIは後述の音声ファイル単位設定に置き換えた。KAGURAの`Buffer settings`→`Apply / rebuffer`で変更する。頂点ファイルは分割せず、消費済みの展開配列を再利用する。設定縮小では余剰配列だけを解放し、スレッドが保持中の配列は返却を待つ。指定ファイル数のほかに8192フレーム・配列合計256MiBの上限を適用する。

検証用`channel_KAGURA_MULTI_V6`は既存Sender収録のGPU圧縮・頂点・AACデータを再使用し、目録の名前／バージョンと音声JSONだけをv6へ再構成した。新しい動画や音声を収録したものではない。9 Material／20 Texture／13 Mesh、29チャンク・8628フレームを検証した。

- 停止中のWebで標準3ファイルは`000000`〜`000002.stmv`だけを取得した。2ファイルへ変更すると取得は2個で止まり、モデルのキャッシュヒットを確認した。
- Pixel 5aのネイティブ版でASTC 4×4を選択し、3ファイル設定の再生は287.588秒まで進んだ。配列再利用の改修後にも3→2ファイルの変更を実機で確認し、13 Mesh／20 Textureを維持した。
- KAGURAの配列プールは3ファイルで68,222,976 byte（65.063MiB）、2ファイルで45,481,984 byte（43.375MiB）。フレームメタデータは900→600件となる。これはプールの値であり、WASM全体・GPU・ブラウザのメモリ総量ではない。標準を3ファイルにすることで、直前の暫定5秒設定より配列保持量は増える。
- 最終Web版の3→2ファイル変更では配列を引き継ぎ、WASM容量は326,303,744 byteのままで追加拡張しなかった。使用量は243,720,584→244,002,264 byteであり、プール縮小がWASM使用量全体の即時減少を意味するわけではない。`Logs/WindowReceiver-summary.json`に記録した。
- 既存録画をHTTPアップロードAPIで2→4→6チャンクと段階公開した。Web ReceiverはReconnectせず、約20秒の当初の公開範囲を越えて約40秒、約60秒まで音声と頂点を再生した。収録中のSender実行を直接検証したものとは区別する。
- C#リング／プールの180,573チェック（リング操作の確保0 byte）、初期データの182チェック、Unityでモデル／GPU／プールの再利用・旧スレッド結果の拒否・Sender音声JSONの生成、MSEモックでsequenceなしのPTS順・追記・可変範囲・Quota再試行・シーク、HTTP公開の原子性と旧目録の拒否を確認した。

検証は6000.5.10f1／URP 17.5の独立プロジェクトで行い、元の6000.6.3f1／URP 17.6の設定を変更していない。検証用Webビルドは4GiBの最大WASM容量・32MiBの初期容量。前節の音声終端の`ended`未通知は別の課題として残る。

```powershell
Tools/Tests/verify_android_receiver.ps1 -EditorPath '<Unity.exe>' -VerificationProject '<独立プロジェクト>' -Output Builds/AndroidWindowReceiver.apk
Tools/Tests/verify_indexed_resources.ps1 -EditorPath '<Unity.exe>' -VerificationProject '<独立プロジェクト>' -Output Builds/WindowReceiver -BuildOnly
python Tools/Tests/instrument_web_receiver.py Builds/WindowReceiver/index.html
python Tools/Tests/android_web_probe_server.py --port 8006 --web-root Builds/WindowReceiver --data-root DevData/channels --log Logs/WindowReceiver-events.jsonl
adb reverse tcp:8006 tcp:8006
```

URLは`http://127.0.0.1:8006/viewer/?channel=http%3A%2F%2F127.0.0.1%3A8006%2Fchannels%2Fchannel_KAGURA_MULTI_V6%2F`。音声が自動再生待ちの場合はPause→Playをクリックする。再生範囲の比較は停止中に設定を適用してHTTP要求と`STM_MEM`の`vertexPrefetchChunks`／`chunkSlots`／`chunkCapacity`／`encodedCapacity`を確認する。

追記確認は次の手順で、未使用の検証チャンネルを作成する。同じ名前の既存チャンネルは上書きしない。2ファイルの公開後、`channel_KAGURA_LIVE_V6`へ接続し、再生中または公開済みの末尾で追加を実行する。

```powershell
python Tools/Tests/publish_live_fixture.py --prepare --through 2
python Tools/Tests/publish_live_fixture.py --through 4
python Tools/Tests/publish_live_fixture.py --through 6
```

記録は`Logs/WindowReceiver-events.jsonl`／`WindowReceiver-access.log`、`WindowReceiver-live.png`、`AndroidWindowReceiver-v6-fullplay-logcat.txt`／`AndroidWindowReceiver-resize-logcat.txt`、`AndroidWindowReceiver-two-files.png`。再公開用トークンはローカルの`Logs/channel_KAGURA_LIVE_V6-publisher.json`へ保存し、配信ディレクトリへ置かない。


## Web音声のファイル単位先読みと実時刻による選択（2026-10-04）

Web音声も標準3ファイル、1〜16の`WebAudioPrefetchChunks`で指定する。現在再生中のファイルを含み、過去の保持秒数は別枠。APIは`ConfigureBuffering(vertexChunks, webAudioChunks, webAudioBackSeconds)`。ネイティブ音声はHLSプレイヤー管理のままで、この音声ファイル数設定の対象外。

Senderの`stream.json`に`audioSegmentDurationSeconds`を追加した。Recorderと同じ`max(0.25, combinedFrames * frameInterval)`が公称値。UnityのSender serializerで約10秒と最低0.25秒、JSONの往復を確認した。KAGURAの既存収録の実ファイル長は7.6586666〜10.0053334秒なので、公称値から厳密な範囲を決めない。両目録の`[startTicks, endTicks)`を使い、再生済みのファイルを除く。頂点は枠待ち後にも判定、Web音声は実範囲から選んだ先頭Nファイルまでを取得する。

停止中のブラウザで3ファイル分のMSE範囲は0〜30.015999秒。音声だけ2ファイルへ変更すると0〜20.010666秒に縮小した。モデルの再接続は不要。3ファイルへ戻して順次補充・再生を確認した。`AudioFiles-two-files.png`に設定画面を保存した。

テストは可変長／0.1秒の短いファイル、終了時刻ちょうどのシーク、空白区間、設定縮小、Quota再試行、古いダウンロード結果の破棄、追記に対応する。C#リング／プールは180,577チェック、操作の管理ヒープ確保0 byte。WebGLとAndroidのビルドも成功し、Pixel 5aで頂点・音声の同期再生を確認した。ネイティブ音声のファイル数制限を実機確認したという意味ではない。

追記は既存録画のHTTP段階公開で検証する。`channel_KAGURA_AUDIO_FILES_LIVE`を2ファイルだけ公開した状態で接続し、4ファイルへ増やすと、停止中の音声は3ファイルまで取得し、durationは20.0106666→40秒に増えた。Senderで新しく収録した実験とは区別する。記録は`Logs/WindowReceiver-events.jsonl`、`WindowReceiver-access.log`、`AndroidAudioFiles-logcat.txt`。音声はブラウザのMSEに保存するため、音声先読みの縮小をWASMヒープ容量の縮小と解釈しない。

追加公開を6ファイルまで増やすと、40秒付近の待機からReconnectせず再開し、59.932秒まで進んだ。通常データも287.599秒まで進み、公開済みの末尾まで取得した。ブラウザのエラー／未処理例外／append例外はこの2セッションで0件。ライブ用にMediaSourceを開いたままにする既知の終端待機は残る。`Logs/AudioFiles-summary.json`と`AudioFiles-live.png`に記録した。
