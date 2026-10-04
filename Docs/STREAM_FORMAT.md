# StreamingMesh ストリーム形式

エンコード／デコード処理、量子化式、分割した初期データの構築方法を含む実装解説は [ENCODING_DECODING.md](ENCODING_DECODING.md) を参照してください。

## バイト順と時間単位

すべての整数値と浮動小数点値はリトルエンディアンで格納する。`ChannelInfo.timebaseHz` は.NETのtickと同じ `10,000,000` である。プロトコルv2では、従来の32ビットstampフィールドを単調増加するシーケンス番号として使用し、正確な64ビットの表示時刻（PTS）を追加している。

Receiverのチャンネル読み込みはv6のみ受け付ける。フレームデコーダー単体にはv1ヘッダーの処理も残るが、旧チャンネルの互換性は提供しない。

プロトコルv3ではMeshフレームヘッダー自体はv2のまま、fMP4音声ストリームを追加する。

v4ではMaterial／Textureの参照を表示名からリソースIDへ変更する。`materials/textures` はID配列、`textureNames` は表示名配列。`MaterialInfo.id`、`MeshInfo.materialIds`、Textureプロパティの `textureId` を使う。詳細は [RECEIVER_MATERIALS.md](RECEIVER_MATERIALS.md) を参照。

## チャンネルのメタデータ

`stream.json` は、静的なMesh／Materialデータとストリームのプレイリストを記述する。

v6では全JSONプロパティを **lowerCamelCase** に統一する。`stream.stmj`、`stream.stma`、`textureVariants`内の目録にも同じ規則を適用する。v5の`protocol_version`、`timebase_hz`、`container_size`、`package_size`、`frame_interval`、`combined_frames`、`initial_data`、`texture_variants`、`stream_info`、`audio_info`、`audio_clip`、`audio_format`、`audio_mime_type`、`audio_codec`、`audio_timescale`、`audio_sample_rate`、`audio_channels`、`audio_init`、`audio_playlist`は、それぞれ`protocolVersion`、`timebaseHz`、`containerSize`、`packageSize`、`frameInterval`、`combinedFrames`、`initialData`、`textureVariants`、`streamInfo`、`audioInfo`、`audioClip`、`audioFormat`、`audioMimeType`、`audioCodec`、`audioTimescale`、`audioSampleRate`、`audioChannels`、`audioInit`、`audioPlaylist`となる。旧名の読み替えは行わない。URLの確認用クエリやHTTPアップロードAPIの名前はJSONプロパティ規則の対象外である。

- `protocolVersion`: 現在は `6`。
- `timebaseHz`: 1秒あたりのtick数。
- `containerSize`、`packageSize`: 頂点位置の量子化パラメーター。
- `frameInterval`: 公称フレーム間隔（秒）。
- `combinedFrames`: 1つの `.stmv` チャンクに格納する最大フレーム数。
- `streamInfo`: 改行区切りの `StreamInfo` プレイリスト。
- `initialData`: GZip圧縮した初期データの分割ファイル目録。
- `texturePayloads`: Texture IDと同じ順序のGPUブロック形式、幅、高さ、ミップ数、linearフラグ。
- `audioFormat`: fMP4音声では `fmp4`。
- `audioMimeType` / `audioCodec`: 通常は `audio/mp4` / `mp4a.40.2`。
- `audioTimescale`: fMP4の音声トラックtimescale。現在は48,000。
- `audioInit`: MSEへ最初にappendする初期化セグメント。
- `audioInfo`: 改行区切りの`AudioInfo`プレイリスト。
- `audioPlaylist`: ネイティブ音声プレーヤー用のHLSプレイリスト（通常`audio.m3u8`）。
- `audioSampleRate` / `audioChannels`: 音声出力のサンプルレート／チャンネル数。
- `audioSegmentDurationSeconds`: SenderがAAC fragment生成に指定する公称秒数。`max(0.25, combinedFrames * frameInterval)`。AACフレーム境界、収録終了時の端数などで実時間は異なる。先読み時間の概算に用い、各ファイルの正確な範囲は`stream.stma`の`startTicks`・`endTicks`を使う。v6への追加フィールドで、ファイル選択はこの値に依存しない。
- `audioClip`: 静的音声クリップを使用する場合のパス。
- `meshes` / `materials` / `textures`: リソース表。`textureNames`は表示名。
- `meshSizes` / `materialSizes` / `textureSizes`: 各リソースのバイト長。
- `textureVariants`: プラットフォームに応じて選択するGPU圧縮形式の候補。

各 `StreamInfo` は、`video`、`startTicks`、`endTicks`、`firstSequence`、`lastSequence` を持つ。時刻は送信側で記録を開始した時点からの相対値である。

## v6の初期データ

ファイル名は書き出しごとに生成するランダムな32桁の16進文字列＋`.bin`。Material JSON→Mesh JSON→GPU圧縮Textureの順に、**1リソース全体**をGZipへ書き込む。テクスチャは1枚全体（全ミップを含む）を1リソースとし、複数のTextureを同じファイルへ格納できる。Senderはリソースをファイル間で分割しない。

**展開後**64MiBを分割の目安とし、1リソースを書き終わった時点で目安以上ならファイルを閉じる。64MiBきっかりになるような切り出しはしない。次のリソースを追加すると絶対上限128MiBを超える場合は追加前に閉じる。目安はSenderの`initialPartSizeMiB`で1〜128MiBを指定できる。1リソースが128MiBを超える入力はエラーにする。GZip後のサイズには最大64KiBの余裕を認める。ファイル順は`initialData`配列順であり、ファイル名には依存しない。

ランダム名・複数リソースの混在は暗号化や解析防止を意味しない。Receiverに渡す目録には復元に必要な形式と境界情報を含む。リソースIDは元アセットのパスから生成し、配信用ファイル名とは独立している。

```json
{
  "texturePayloads": [{"format":"BC7","width":8192,"height":8192,"mipCount":1,"linear":false}],
  "initialData": [{
    "file":"ed79b3a1e56a4a4099618155e74b52c7.bin", "sha256":"<compressed-file SHA-256 hex>",
    "size":67108864, "compressedSize":6454245,
    "records":[{"kind":2,"index":0,"offset":0,"resourceOffset":0,"size":67108864}]
  }]
}
```

- `kind`: Material=0、Mesh=1、Texture=2。`index` は対応するリソース表内のインデックス。
- `offset`: 展開後のファイル内オフセット。`resourceOffset`: リソース全体内のオフセット。
- `records` はMaterial→Mesh→Textureの表順で連続し、隙間・重複・欠落を認めない。新しいSenderは1リソースを1レコードとして格納し、`resourceOffset`は0。Receiverの一般的なセグメント復元は維持する。
- ファイル名は96文字以内のASCII英数字・`.`・`_`・`-`で、`.bin`で終わる安全なベース名。大文字・小文字を無視した重複、Windowsの予約名、ディレクトリ指定を拒否する。
- `format`: `BC7`、`DXT1`、`DXT5`、`ETC2_RGB`、`ETC2_RGBA8`、`ASTC_4x4`、`ASTC_6x6`。GPU形式はSenderで選ぶ。Receiverが非対応ならエラーとして停止し、RGBAへの暗黙展開は行わない。DXT1／ETC2_RGBはアルファを保持しない。
- `textureSizes` は全ミップのGPUブロックバイト数。1リソース最大128MiB、初期データ全体最大1GiB、最大4096ファイル。Material／Mesh JSONは1リソース最大16MiB。
- ミップ数とlinearは明示する。Senderはミップ数を保持してミップを再生成するため、元の独自ミップ画像そのものは保持しない。

### GPU形式の複数候補

`textureVariants`に形式ごとの`format`、`textureSizes`、`texturePayloads`、`initialData`を格納する。Material／TextureのIDとMeshキーはチャンネルで共通で、Material／Meshの定義も各候補に同じ内容を格納する。先頭候補をトップレベルの同名フィールドにも設定する。既定はBC7→ASTC_4x4→ASTC_6x6→DXT5→ETC2_RGBA8の優先順で、すべてアルファを保持する。Senderの`textureFormats`で変更でき、`exportMultipleTextureFormats=false`では`textureFormat`のみを書き出す。DXT1／ETC2_RGBも追加可能だが、アルファを失う。

Receiverは候補のレイアウト・GPUブロックサイズを検証し、全TextureがGPUでサンプリング可能な最初の形式を選ぶ。OS名だけでは選ばない。選んだ候補だけをHTTP取得・復元し、他候補のTextureを確保しない。1GiBの初期データ上限は候補ごとに適用する。開発サーバーは全候補のファイルが揃い、サイズ・SHA-256が一致してから目録を公開する。

確認用URLの`texture_format=DXT5`等で形式を指定できる。指定形式やどの候補も非対応の場合は明示的に停止し、PNGやRGBAへの暗黙変換はしない。候補を増やすとSenderの書き出し時間・サーバーの保存量は増えるが、Receiverは1候補だけをロードする。ASTC／ETC2の書き出し成功はモバイル実機上での描画成功を保証しない。

Senderは空の目録でチャンネルを準備し、各分割ファイルを送信した後、`initialinfo=stream.json` で完成した目録を公開する。開発サーバーは全ファイルのサイズとSHA-256を検証してから目録を置換する。再作成中は一時的にチャンネルが未完成となる。旧目録で読み込み中のReceiverはハッシュ不一致なら停止し、再接続を必要とする。

ランダム名の旧世代ファイルは自動削除しない。再書き出し後は新しい目録のファイルのみを使用するが、サーバーのディスク使用量は世代数に応じて増える。保持／清掃は別途サーバー側で管理する。

ReceiverはファイルのSHA-256とGZipの長さを検証し、64KiBの再利用バッファで展開する。Textureは `GetRawTextureData<byte>()` が返すCPU領域へ順にコピーし、全ブロックが揃った時点で `Apply(false, true)` してGPUへ転送・CPU領域を解放する。全体を連結する配列や、展開後の64／128MiB配列は作らない。HTTP受信時にはGZip後のファイルを保持するため、圧縮率が低い入力では分割サイズを小さくする。使用しないTextureだけを含むファイルは要求しない。

KAGURAのReconnectとReceiverのSeekでは、完成したモデルを1つだけ保持する。再取得した目録全体（ID、各ファイルのSHA-256、サイズ、セグメント、Texture形式を含む）、チャンネルURL、Shader／テンプレート参照、頂点出力設定が一致すると、Texture／Material／MeshとCPU／GPU再生バッファを再利用する。再生状態は空にして新しいキーフレームを待ち、古い非同期インポートは世代番号で拒否する。変更時は古いモデルを解放して再取得し、Disconnect／Receiver破棄で保持リソースも解放する。実行中にローカルテンプレートMaterialのプロパティ自体を書き換えた場合は`Reconnect(address, autoPlay, forceReload: true)`で明示的に再生成する。

v6ではv5以前のチャンネルを受け付けない。初期データのバイナリ配置とGPUブロック形式はv5と同じで、変更はJSONのプロパティ名と音声目録のフィールドである。MaterialInfo v3、Meshフレームv2、fMP4音声形式は維持する。

## fMP4音声

音声は録音セッション全体で1つのAACエンコーダーを継続使用し、`audio-init.mp4`と
`audio-NNNNNN.m4s`へfragmentする。各`.m4s`を独立したAACとして再エンコードしてはならない。

`stream.stma`はNDJSONであり、各行は次のフィールドを持つ。

```json
{"audio":"audio-000000.m4s","startTicks":0,"endTicks":10240000,"startSample":0,"sampleCount":49152,"discontinuity":false}
```

- `startSample` / `sampleCount`: 48 kHz出力上の累積サンプルフレーム位置と長さ。
- `startTicks` / `endTicks`: `timebaseHz`へ変換した同じ範囲。
- `discontinuity`: エンコーダー再起動などで連続性が切れた場合にtrue。

v6では`sequence`を持たない。Web Receiverは`startTicks`順に並べ、ファイル名を取得済み判定に使う。ネイティブReceiverは`audioPlaylist`のHLSを使用する。Sender内部のfragment番号、HTTPアップロードのインデックス、HLSの`EXT-X-MEDIA-SEQUENCE`は別の用途なので維持する。頂点ストリームのsequenceは差分復元に必要であり維持する。

時刻変換は累積値から毎回行い、丸め誤差を加算しない。

```text
ticks = cumulativeSample * timebaseHz / audioSampleRate
```

## 収録中の公開とReceiverの先読み

この節は実装済みv6の動作を説明する。最新範囲だけを公開する方式とサーバーの
archive/delete完了APIは、[ライブ公開範囲とサーバーアーカイブの設計](LIVE_ARCHIVE_DESIGN.md)
にまとめている。そちらは設計仕様であり、現行v6では未実装。

Senderは初期リソースの完成した目録を公開した後、完成した`.stmv`／`.m4s`を先に送信し、そのファイルを参照する`stream.stmj`／`stream.stma`の行を追加する。音声の`audio.m3u8`も更新する。Receiverは同じ接続で追加行をpollingし、収録完了前から再生できる。メッシュは独自の追記型プレイリスト、ネイティブ音声はHLS、Web音声はMSE経路である。

参照先ファイルのアップロード完了後に、完全なJSON行＋改行を追記する必要がある。開発サーバーはファイルを原子的に置換し、行追記をロックする。同一接続中の頂点目録は追記のみを前提とする。再収録などで目録を切り詰めたり置き換えたりする場合はReconnectする。途中接続は公開済みの先頭から開始し、ライブ端へ自動移動しない。チャンクの完成待ちとpolling間隔が遅延になるため、低遅延HLS相当の保証はない。

頂点の先読みはReceiverの`VertexPrefetchChunks`で **ファイル単位** に指定する。標準は3、範囲は1〜16で、現在消費中のファイルを含む最大保持数である。KAGURAの通常300フレーム／30fpsでは、3ファイルで最大約30秒分に相当し、現在ファイルの残りにより将来分は変わる。秒数を固定してファイルを切り分けることはしない。消費が終わったファイルの展開配列をプールへ返し、空いた枠で次を取得する。展開ペイロードは1ファイル最大128MiB、配列プールは合計256MiB、フレームメタデータは8192件が上限であり、大きな入力では指定数より少なくなるか、バイト上限超過で入力を拒否する。配列は必要になった時点で確保し再利用する。設定変更でも既存配列を引き継ぎ、縮小時は余剰の配列だけを解放する。処理中の旧世代スレッドが保持する配列は、その返却後に解放する。

Web音声は`WebAudioPrefetchChunks`（標準3ファイル、1〜16、現在再生中を含む）と`WebAudioBackBufferSeconds`（標準30秒、0〜120秒）で指定する。`stma`を`startTicks`順に並べ、`endTicks <= 再生時刻`のファイルを除いた先頭N個を取得する。シーク先が空白区間の場合は次のファイルから選ぶ。取得済み・取得中のファイルもN個に数え、先読み上限の終了時刻は選択した実範囲から求める。縮小／後方シークでは不要な未来データを破棄する。過去の保持分はN個と別枠である。ファイル数はバイト数の上限ではなく、大きなfragmentにはMSEの容量制限も適用される。ネイティブ音声のバッファは各OSプレーヤーが管理し、このファイル数設定の対象外である。デコード済み頂点の先読み0.5秒とは別の設定である。

頂点も`stmj`の実範囲で再生済みのファイルを除外する。バッファ枠待ちの後にも判定し、ダウンロード遅延やライブの追記で不要になったファイルを取得しない。両目録の範囲は`[startTicks, endTicks)`とする。公称秒数は先読み時間の概算用であり、最終ファイルや可変長ファイルを公称値だけで切り捨てない。

```csharp
receiver.ConfigureBuffering(vertexChunks: 3, webAudioChunks: 3, webAudioBackSeconds: 5);
```

KAGURA Receiverの`Buffer settings`からも変更できる。頂点保持数の変更は現在時刻で再バッファリングし、Texture／Material／Meshを再利用する。Web音声のみの変更は再接続せず反映する。

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
| 21 | 8 | `timebaseHz` のtick単位による表示時刻（`int64`） |
| 29 | 可変 | フレームペイロード |

表示時刻は、送信側で記録を開始した時点からの相対値である。公称固定フレームレートではなく実際のキャプチャ時刻を記録するため、GPU readbackのバックプレッシャーやUnity Editorの不規則なフレーム時間があっても、受信側の再生時間軸を維持できる。

プロトコルv1の入力では、受信側が `StreamInfo.startTicks + frameIndex * frameInterval` から表示時刻を合成する。

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
