# StreamingMesh ストリーム形式

エンコード／デコード処理、量子化式、`stream.bin` の構築方法を含む実装解説は [ENCODING_DECODING.md](ENCODING_DECODING.md) を参照してください。

## バイト順と時間単位

すべての整数値と浮動小数点値はリトルエンディアンで格納する。`ChannelInfo.timebase_hz` は.NETのtickと同じ `10,000,000` である。プロトコルv2では、従来の32ビットstampフィールドを単調増加するシーケンス番号として使用し、正確な64ビットの表示時刻（PTS）を追加している。

受信側は、プロトコルv1の21バイトヘッダーも引き続きデコードできる。

プロトコルv3ではMeshフレームヘッダー自体はv2のまま、fMP4音声ストリームを追加する。

## チャンネルのメタデータ

`stream.json` は、静的なMesh／Materialデータとストリームのプレイリストを記述する。

- `protocol_version`: 現在は `3`。
- `timebase_hz`: 1秒あたりのtick数。
- `container_size`、`package_size`: 頂点位置の量子化パラメーター。
- `frame_interval`: 公称フレーム間隔（秒）。
- `combined_frames`: 1つの `.stmv` チャンクに格納する最大フレーム数。
- `stream_info`: 改行区切りの `StreamInfo` プレイリスト。
- `data`: GZip圧縮した静的なMesh／マテリアル／テクスチャデータ。
- `audio_format`: fMP4音声では `fmp4`。
- `audio_mime_type` / `audio_codec`: 通常は `audio/mp4` / `mp4a.40.2`。
- `audio_timescale`: fMP4の音声トラックtimescale。現在は48,000。
- `audio_init`: MSEへ最初にappendする初期化セグメント。
- `audio_info`: 改行区切りの`AudioInfo`プレイリスト。

各 `StreamInfo` は、`video`、`startTicks`、`endTicks`、`firstSequence`、`lastSequence` を持つ。時刻は送信側で記録を開始した時点からの相対値である。

## fMP4音声

音声は録音セッション全体で1つのAACエンコーダーを継続使用し、`audio-init.mp4`と
`audio-NNNNNN.m4s`へfragmentする。各`.m4s`を独立したAACとして再エンコードしてはならない。

`stream.stma`はNDJSONであり、各行は次のフィールドを持つ。

```json
{"sequence":0,"audio":"audio-000000.m4s","startTicks":0,"endTicks":10240000,"startSample":0,"sampleCount":49152,"discontinuity":false}
```

- `startSample` / `sampleCount`: 48 kHz出力上の累積サンプルフレーム位置と長さ。
- `startTicks` / `endTicks`: `timebase_hz`へ変換した同じ範囲。
- `discontinuity`: エンコーダー再起動などで連続性が切れた場合にtrue。

時刻変換は累積値から毎回行い、丸め誤差を加算しない。

```text
ticks = cumulativeSample * timebase_hz / audio_sample_rate
```

## ストリームチャンク

`.stmv` ファイル全体をGZipで圧縮する。展開後のペイロード構造は次のとおり。

| オフセット | 型 | 内容 |
| --- | --- | --- |
| 0 | `int32` | フレーム数 |
| 4 | `int32[frameCount]` | 各フレームのバイト数 |
| 可変 | `byte[]` | 連結したフレームデータ |

## フレームヘッダー

プロトコルv2の各フレームは、29バイトのヘッダーで始まる。

| オフセット | サイズ | 内容 |
| --- | --- | --- |
| 0 | 1 | `0x0F`: キーフレーム、`0x0E`: 差分フレーム |
| 1 | 4 | `uint32` のシーケンス番号 |
| 5 | 3 | キーフレームのパッケージ数（`uint24`）。差分フレームでは0 |
| 8 | 1 | ヘッダー／プロトコル識別値（`2`） |
| 9 | 12 | ルート位置（`float32 x, y, z`） |
| 21 | 8 | `timebase_hz` のtick単位による表示時刻（`int64`） |
| 29 | 可変 | フレームペイロード |

表示時刻は、送信側で記録を開始した時点からの相対値である。公称固定フレームレートではなく実際のキャプチャ時刻を記録するため、GPU readbackのバックプレッシャーやUnity Editorの不規則なフレーム時間があっても、受信側の再生時間軸を維持できる。

プロトコルv1の入力では、受信側が `StreamInfo.startTicks + frameIndex * frame_interval` から表示時刻を合成する。

## キーフレームのペイロード

キーフレームは空間タイルのパッケージで構成する。各パッケージは、パック済みタイルIDの昇順に並べる。

| フィールド | サイズ | 内容 |
| --- | --- | --- |
| `tileX`, `tileY`, `tileZ` | 3 | 量子化したタイル座標 |
| `vertexCount` | 3 | このパッケージ内の頂点数（`uint24`） |
| `vertices` | `5 * vertexCount` | パック済み頂点レコード |

各頂点レコードの構造は次のとおり。

| フィールド | サイズ | 内容 |
| --- | --- | --- |
| `vertexIndex` | 2 | Mesh内の頂点インデックス |
| `meshIndex` | 1 | Meshインデックス。最大255 |
| `localXYZ` | 2 | X: ビット0～4、Y: ビット5～9、Z: ビット10～14 |

このレコード順が、次のキーフレームまでに続くすべての差分フレームの依存順序となる。

## 差分フレームのペイロード

差分フレームは、直前のキーフレームで定義した各頂点につき3バイトを持つ。

```text
deltaX:u8 deltaY:u8 deltaZ:u8
```

各成分では `q = encodedByte - 128` とし、次の式で差分値を復元する。

```text
delta = sign(q) * q * q / 16384
```

差分フレームはMeshインデックスや頂点インデックスを持たないため、シーケンス番号が連続する順序でデコードしなければならない。シーケンスの欠落を検出した場合、受信側は最後の正常なフレームを保持し、無効になった差分依存チェーンを破棄して、次のキーフレームを待つ。
