# StreamingMesh ストリーム形式

エンコード／デコード処理、量子化式、分割した初期データの構築方法を含む実装解説は [ENCODING_DECODING.md](ENCODING_DECODING.md) を参照してください。

## バイト順と時間単位

すべての整数値と浮動小数点値はリトルエンディアンで格納する。`ChannelInfo.timebase_hz` は.NETのtickと同じ `10,000,000` である。プロトコルv2では、従来の32ビットstampフィールドを単調増加するシーケンス番号として使用し、正確な64ビットの表示時刻（PTS）を追加している。

Receiverのチャンネル読み込みはv5のみ受け付ける。フレームデコーダー単体にはv1ヘッダーの処理も残るが、旧チャンネルの互換性は提供しない。

プロトコルv3ではMeshフレームヘッダー自体はv2のまま、fMP4音声ストリームを追加する。

v4ではMaterial／Textureの参照を表示名からリソースIDへ変更する。`materials/textures` はID配列、`textureNames` は表示名配列。`MaterialInfo.id`、`MeshInfo.materialIds`、Textureプロパティの `textureId` を使う。詳細は [RECEIVER_MATERIALS.md](RECEIVER_MATERIALS.md) を参照。

## チャンネルのメタデータ

`stream.json` は、静的なMesh／Materialデータとストリームのプレイリストを記述する。

- `protocol_version`: 現在は `5`。
- `timebase_hz`: 1秒あたりのtick数。
- `container_size`、`package_size`: 頂点位置の量子化パラメーター。
- `frame_interval`: 公称フレーム間隔（秒）。
- `combined_frames`: 1つの `.stmv` チャンクに格納する最大フレーム数。
- `stream_info`: 改行区切りの `StreamInfo` プレイリスト。
- `initial_data`: GZip圧縮した初期データの分割ファイル目録。
- `texturePayloads`: Texture IDと同じ順序のGPUブロック形式、幅、高さ、ミップ数、linearフラグ。
- `audio_format`: fMP4音声では `fmp4`。
- `audio_mime_type` / `audio_codec`: 通常は `audio/mp4` / `mp4a.40.2`。
- `audio_timescale`: fMP4の音声トラックtimescale。現在は48,000。
- `audio_init`: MSEへ最初にappendする初期化セグメント。
- `audio_info`: 改行区切りの`AudioInfo`プレイリスト。

各 `StreamInfo` は、`video`、`startTicks`、`endTicks`、`firstSequence`、`lastSequence` を持つ。時刻は送信側で記録を開始した時点からの相対値である。

## v5の初期データ

既定のファイル名は `stream0.bin`、`stream1.bin` …。`stream0.bin` からMaterial JSON、Mesh JSONを格納し、メタデータの終わりでファイルを区切る。その後にGPU圧縮Textureを格納する。1ファイルの**展開後**サイズは最大16MiB。GZip後のサイズには最大64KiBの余裕を認める。ファイル順は `initial_data` 配列順であり、名前の辞書順や番号の解析には依存しない。

```json
{
  "texturePayloads": [{"format":"BC7","width":8192,"height":8192,"mipCount":1,"linear":false}],
  "initial_data": [{
    "file":"stream1.bin", "sha256":"<compressed-file SHA-256 hex>",
    "size":16777216, "compressedSize":3088056,
    "records":[{"kind":2,"index":0,"offset":0,"resourceOffset":0,"size":16777216}]
  }]
}
```

- `kind`: Material=0、Mesh=1、Texture=2。`index` は対応するリソース表内のインデックス。
- `offset`: 展開後のファイル内オフセット。`resourceOffset`: リソース全体内のオフセット。
- `records` はMaterial→Mesh→Textureの表順で連続し、隙間・重複・欠落を認めない。Textureは複数ファイルにまたがってよい。
- ファイル名は96文字以内のASCII英数字・`.`・`_`・`-`で、`.bin`で終わる安全なベース名。大文字・小文字を無視した重複、Windowsの予約名、ディレクトリ指定を拒否する。
- `format`: `BC7`、`DXT1`、`DXT5`、`ETC2_RGB`、`ETC2_RGBA8`、`ASTC_4x4`、`ASTC_6x6`。GPU形式はSenderで選ぶ。Receiverが非対応ならエラーとして停止し、RGBAへの暗黙展開は行わない。DXT1／ETC2_RGBはアルファを保持しない。
- `textureSizes` は全ミップのGPUブロックバイト数。1リソース最大128MiB、初期データ全体最大1GiB、最大4096ファイル。Material／Mesh JSONは1リソース最大16MiB。
- ミップ数とlinearは明示する。Senderはミップ数を保持してミップを再生成するため、元の独自ミップ画像そのものは保持しない。

Senderは空の目録でチャンネルを準備し、各分割ファイルを送信した後、`initialinfo=stream.json` で完成した目録を公開する。開発サーバーは全ファイルのサイズとSHA-256を検証してから目録を置換する。再作成中は一時的にチャンネルが未完成となる。旧目録で読み込み中のReceiverはハッシュ不一致なら停止し、再接続を必要とする。

ReceiverはファイルのSHA-256とGZipの長さを検証し、64KiBの再利用バッファで展開する。Textureは `GetRawTextureData<byte>()` が返すCPU領域へ順にコピーし、全ブロックが揃った時点で `Apply(false, true)` してGPUへ転送・CPU領域を解放する。全体を連結する配列や、展開後の16MiB配列は作らない。使用しないTextureだけを含むファイルは要求しない。

v5ではv4以前の初期データを受け付けない。MaterialInfo v3、Meshフレームv2、fMP4音声形式は維持する。

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
