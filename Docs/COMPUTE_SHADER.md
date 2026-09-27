# ComputeShader版の送信側／受信側

`stream.bin` と `.stmv` の生成・圧縮・復元手順、量子化式、バイト構造の詳細は [ENCODING_DECODING.md](ENCODING_DECODING.md) を参照してください。

## 処理範囲

- 送信側は `TilingShader.compute` でキーフレームを量子化し、`DiffShader.compute` で前フレームとの差分を量子化します。
- 受信側のGPU常駐経路は `ReceiverVertexPipeline.compute` で復元・補間・必要な法線更新を実行します。旧APIの `VertexDecodeShader.compute` は互換用に残しています。
- タイルと差分のバイト形式は従来版と互換です。プロトコルv2ではフレームヘッダーに64ビットPTSを追加しています。
- 可変長データの解析、GZip 圧縮、HTTP 転送は CPU で実行します。
- 送信側は `AsyncGPUReadback.Request` で圧縮用データを取得します。受信側のGPU常駐経路はmacOS/iOSではGraphicsFenceで完了を確認し、WebGPUでは先頭16バイトだけを非同期readbackして確認します。全頂点のreadbackは行いません。
- 旧 `lzip/libzipw` バイナリは参照互換のためリポジトリに残していますが、すべての `PluginImporter` ターゲットを無効化しており、Editor／Playerにはロードされません。

## 送信側

1. `STMHttpSender` の `Target Game Object` に SkinnedMeshRenderer を含むルートを指定します。
2. `STMHttpSerializer` の `address` と `channel` を設定します。
3. Inspectorの `Create Channel` を押した後、プレイモードで `Start Recording` を押します。

ComputeShader非対応環境では、送信側は記録を開始しません。現在、マテリアル情報を含むチャンネル作成と音声記録はUnity Editor内で実行してください。

## 受信側

`Receiver` の Decode Backend は Auto / GPU / CPU、Normal Mode は Auto / None / Recalculate を選べます。Auto/GPUはComputeShaderと完了確認機能（macOS/iOSのGraphicsFence、WebGPUのAsyncGPUReadback）に対応する場合にGPU常駐経路を試行し、非対応や初期化失敗時はCPUに切り替えます。モバイルも同じ選択規則です。GPU実行例外時は復元状態を引き継がず、次のキーフレームからCPUで再開します。

GPU常駐経路はパック済み入力→復元状態→スナップショット→音声PTSで補間→MeshのGPU頂点バッファへ出力します。CPU経路とともに1回のUpdateで最大8フレーム、CPU投入時間約4msを目安に処理します。法線Autoでは既知のUnlitを省略し、それ以外を再計算します。

音声開始/再開にはmax(0.25秒, 公称2フレーム分)のGPU処理完了済みバッファを要求します。シーケンス欠落時は次のキーフレームから再開します。詳細と検証結果は [RECEIVER_GPU_PIPELINE.md](RECEIVER_GPU_PIPELINE.md) を参照してください。

## フォーマット上の制限

- 1チャンネルあたりMeshは最大256個です。
- 1つのMeshあたり頂点は最大65,536個です。
- `packageSize` は 2～254、`containerSize` は 1 以上です。
