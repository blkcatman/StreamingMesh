# ComputeShader版の送信側／受信側

`stream.bin` と `.stmv` の生成・圧縮・復元手順、量子化式、バイト構造の詳細は [ENCODING_DECODING.md](ENCODING_DECODING.md) を参照してください。

## 処理範囲

- 送信側は `TilingShader.compute` でキーフレームを量子化し、`DiffShader.compute` で前フレームとの差分を量子化します。
- 受信側は `VertexDecodeShader.compute` でキーフレームと差分を頂点バッファへ復元します。
- タイルと差分のバイト形式は従来版と互換です。プロトコルv2ではフレームヘッダーに64ビットPTSを追加しています。
- 可変長データの解析、GZip 圧縮、HTTP 転送は CPU で実行します。
- 送信側／受信側ともGPU結果の取得は `AsyncGPUReadback.Request` を使い、完了順に関係なくシーケンス番号順で確定します。
- 旧 `lzip/libzipw` バイナリは参照互換のためリポジトリに残していますが、すべての `PluginImporter` ターゲットを無効化しており、Editor／Playerにはロードされません。

## 送信側

1. `STMHttpSender` の `Target Game Object` に SkinnedMeshRenderer を含むルートを指定します。
2. `STMHttpSerializer` の `address` と `channel` を設定します。
3. Inspectorの `Create Channel` を押した後、プレイモードで `Start Recording` を押します。

ComputeShader非対応環境では、送信側は記録を開始しません。現在、マテリアル情報を含むチャンネル作成と音声記録はUnity Editor内で実行してください。

## 受信側

`Receiver` を使用します。ComputeShaderとAsync GPU Readbackの対応環境ではGPUデコードが自動的に有効になり、非対応環境またはGPUエラー時はCPUデコードへフォールバックします。旧 `STMHttpMeshReceiver` は後方互換用です。

受信側は2フレームを事前バッファし、各フレームのPTSで補間します。差分のシーケンス番号が欠落した場合は最後の正常フレームを保持し、次のキーフレームまで待機します。

## フォーマット上の制限

- 1チャンネルあたりMeshは最大256個です。
- 1つのMeshあたり頂点は最大65,536個です。
- `packageSize` は 2～254、`containerSize` は 1 以上です。
