# StreamingMesh エンコード／デコード仕様

この文書は、事前データ `stream.bin`、時系列頂点データ `*.stmv`、fMP4音声がどのように生成・配信され、受信側で復元されるかを、現在のプロトコルv3実装に沿って説明する。Meshフレーム形式はv2から変更しない。

バイト単位のフィールド一覧は [STREAM_FORMAT.md](STREAM_FORMAT.md) も参照すること。

## 1. ファイル構成と役割

1つのチャンネルは、主に次のファイルから構成される。

| ファイル | 更新頻度 | 内容 |
| --- | --- | --- |
| `stream.json` | チャンネル作成時 | プロトコル、量子化設定、各データの名前とサイズ |
| `stream.bin` | チャンネル作成時 | テクスチャ、マテリアル、Meshトポロジーの事前データ |
| `stream.stmj` | チャンク確定時に1行追記 | `.stmv` の名前、PTS範囲、シーケンス番号範囲 |
| `000000.stmv` など | リアルタイム | キーフレームと差分フレームをまとめた頂点チャンク |
| `audio-init.mp4` | 録音開始時 | AACトラックの初期化セグメント |
| `stream.stma` | 音声fragment確定時に1行追記 | `.m4s`の名前、サンプル範囲、PTS範囲 |
| `audio-000000.m4s` など | リアルタイム | 連続AACストリームのfMP4 fragment |

概略フローは次のとおり。

```text
送信側／チャンネル作成
  Texture -> PNG ---------+
  Material -> JSON -------+--> 連結 -> GZip -> stream.bin
  Meshトポロジー -> JSON -+          |
                                     +--> サイズ／名前 -> stream.json

送信側／記録
  SkinnedMeshRenderer.BakeMesh
    -> ComputeShaderでエンコード
    -> AsyncGPUReadback
    -> シーケンス番号順でフレーム確定
    -> 複数フレームをチャンク化
    -> GZip -> 000000.stmv
    -> StreamInfoをstream.stmjに追記

受信側
  stream.json + stream.bin
    -> 静的なMesh／Material／Textureを生成
  stream.stmj + *.stmv
    -> GZip展開
    -> フレームをシーケンス番号順に並べる
    -> キーフレーム待機
    -> ComputeShaderでデコード + AsyncGPUReadback
    -> PTSで補間表示
```

## 2. 共通規約

- 整数と浮動小数点数は little-endian とする。
- `timebase_hz` は `10,000,000` で、1 tick は100 nsである。
- 現行 `protocol_version` は `3` である。Meshフレームヘッダーの識別値は `2` のままである。
- `.bin` と `.stmv` は拡張子に関係なく、ファイル全体が GZip ストリームである。
- GZip処理には `System.IO.Compression.GZipStream` を使用する。
- GZipは通信量を減らす可逆圧縮であり、頂点の量子化による非可逆圧縮とは別工程である。

## 3. `stream.bin` のエンコード

### 3.1 収録する事前データ

`stream.bin` に毎フレームの頂点座標は格納しない。格納するのは、受信側が描画用オブジェクトを最初に構築するためのデータである。

#### テクスチャ

- Material の Texture プロパティから参照される `Texture2D` を収集する。
- Texture名をキーとして重複を除外する。
- Unity EditorでGPU Blitして読み出し可能な一時Textureへ転送し、`EncodeToPNG()` を実行する。元アセットは再インポートしない。
- sRGB／linearを保持し、NormalMapはインポート時のチャンネル配置から法線XYZを復元してRGBへ保存する。

#### マテリアル

マテリアルごとに `MaterialInfo` をUnity JSONへ変換し、UTF-8で格納する。

```text
MaterialInfo
  name: string
  version: int (= 2)
  shaderName: string
  keywords[] / renderQueue / instancing / GI flags
  tags[] / passes[]
  properties[]:
    name: string
    type: int
    value: string
    Textureの場合: scale / offset / linear / mipChain / sampler設定
```

`type` と `value` は次の対応になる。

| `type` | 内容 | `value` の表現 |
| ---: | --- | --- |
| 0 | `Color` | Unity JSONの `Color` |
| 1 | `Vector` | Unity JSONの `Vector4` |
| 2 | `Float` | InvariantCultureの数値文字列（往復形式） |
| 3 | `Range` | InvariantCultureの数値文字列（往復形式） |
| 4 | `Texture` | `stream.bin` 内のテクスチャ名。空文字は未設定 |
| 5 | `Integer` | InvariantCultureの整数文字列 |

プロパティは `name` で対応付ける。`type` は値の種類であり、プロパティのインデックスではない。Shaderコード自体は送信しない。受信側はマテリアルテンプレート、カスタムShader、送信Shader名（有効な場合）、既定Shaderの順に解決する。同じShaderではキーワード・描画状態も復元する。旧SenderのFloat/Rangeは `{}` として保存されていたため復元不能であり、旧データではテンプレート／Shaderの既定値を保つ。詳細と再収録手順は [RECEIVER_MATERIALS.md](RECEIVER_MATERIALS.md) を参照。

#### Meshトポロジー

各 `SkinnedMeshRenderer.sharedMesh` から `MeshInfo` を作成し、Unity JSONのUTF-8として格納する。

```text
MeshInfo
  name
  vertexCount
  subMeshCount
  materialNames[]
  indicesCounts[]
  indices[]
  uv[] / uv2[] / uv3[] / uv4[]
```

静的データには初期頂点座標、法線、接線、ボーンウェイトを格納しない。受信側は `vertexCount` 個のゼロ頂点と送信されたインデックス／UVからMeshを作り、座標を `.stmv` から設定する。法線は表示更新時に再計算する。

### 3.2 非圧縮payloadの並び

`stream.bin` の非圧縮payloadにはmagic、件数、サイズ表を埋め込まない。次の順番でデータ本体だけを連結する。

```text
[Texture PNG 0]
[Texture PNG 1]
...
[Material JSON 0]
[Material JSON 1]
...
[Mesh JSON 0]
[Mesh JSON 1]
...
```

境界情報は `stream.json` の次の配列が保持する。

```text
textures[]      <-> textureSizes[]
materials[]     <-> materialSizes[]
meshes[]        <-> meshSizes[]
```

名前配列とサイズ配列、および連結順は必ず一致させる必要がある。概念上のoffsetは次のように計算できる。

```text
textureOffset(i) = sum(textureSizes[0 .. i-1])
materialOffset(i) = sum(all textureSizes) + sum(materialSizes[0 .. i-1])
meshOffset(i) = sum(all textureSizes) + sum(all materialSizes)
              + sum(meshSizes[0 .. i-1])
```

最後に連結payload全体を1つの GZip ストリームへ圧縮し、`stream.bin` として送信する。

### 3.3 `stream.bin` のデコード

Receiverは次の順で復元する。

1. `stream.json` を読み、名前配列、サイズ配列、量子化設定を取得する。
2. `stream.bin` 全体をGZip展開する。
3. Material JSONを先読みして使用するShaderと必要なTexture設定を解決する。`textureSizes` に従ってPNGを切り出し、必要なものだけを `Texture2D.LoadImage()` で復元し、linear／ミップ有無を反映する。
4. `materialSizes` に従ってJSONを切り出し、Materialを生成して各プロパティを復元する。
5. `meshSizes` に従ってJSONを切り出し、サブメッシュのインデックス、UV、マテリアル参照を持つMeshを作る。
6. Meshごとの頂点数から、時系列デコード用の連続頂点バッファとMeshオフセットを構築する。

次の不変条件が崩れると正しく復元できない。

- 名前配列とサイズ配列の要素数が同じであること。
- 全サイズの合計がGZip展開後の長さと一致すること。
- Materialが参照するTexture名が `textures[]` 内で一意であること。
- `indices[]` が各Meshの `vertexCount` 範囲内であること。

## 4. `.stmv` のエンコード

### 4.1 フレーム取得

Senderは設定された `frame_interval` ごとに対象の全 `SkinnedMeshRenderer` を処理する。

1. `BakeMesh()` で現在のスキニング結果を得る。
2. 頂点配列をGPUの `StructuredBuffer<float3>` へ転送する。
3. ルート位置を除いたstream空間への変換行列を作る。

```text
modelToStream = Translate(-targetRoot.position) * renderer.localToWorldMatrix
```

ルート位置そのものはフレームヘッダーへ別途保存する。

### 4.2 キーフレームの量子化

最初のフレームと、その後 `subframesPerKeyframe + 1` フレームごとにキーフレームを生成する。

stream空間の頂点位置を `p`、`containerSize` を `range`、`packageSize / 2` の整数値を `halfPack` とする。

```text
quantizer = halfPack / range
tile = floor(p * quantizer + halfPack)
subTile = floor(frac((p + range) * quantizer) * 32)
```

- `tile.x/y/z` は各8 bitである。
- `subTile.x/y/z` は各5 bitである。
- `p <= -range` または `p >= range` の成分がある頂点は範囲外として `tileID = 0xFFFFFF` にし、キーフレームpayloadから除外する。
- ComputeShaderは現在のsource頂点を `previousBuf` にも保存し、次の差分計算の基準にする。

同じtileに属する頂点を `TilePacker` でまとめる。各頂点の識別子は次の24 bit値である。

```text
packedIndex = vertexIndex + meshIndex * 65536
```

したがって、フォーマット上の上限は1チャンネル256 Mesh、1 Meshあたり65,536頂点である。

キーフレームpayloadは次の構造を持つ。

```text
packageCount回繰り返す:
  tileX:u8
  tileY:u8
  tileZ:u8
  vertexCount:uint24_le

  vertexCount回繰り返す:
    vertexIndex:uint16_le
    meshIndex:u8
    localXYZ:uint16_le
```

`localXYZ` は3つの5 bit値を次のように格納する。最上位1 bitは未使用である。

```text
bits  0..4  = subTile.x
bits  5..9  = subTile.y
bits 10..14 = subTile.z
bit      15 = 0
```

tileを昇順に並べてpayloadへ格納したときの `packedIndex` 順序を、以後の差分フレームが依存する `linedIndices` として保持する。

### 4.3 差分フレームの量子化

同じvertexの現在位置と直前フレーム位置を、それぞれ現在と直前の行列でstream空間へ変換する。

```text
current = currentMatrix * currentVertex
previous = previousMatrix * previousVertex
delta = current - previous
```

各成分を1 byteへ非線形量子化する。

```text
encoded = clamp(floor(sign(delta) * sqrt(abs(delta)) * 128 + 128), 0, 255)
```

差分ペイロードにはインデックスや件数を入れず、直前キーフレームの `linedIndices` と同じ順でXYZを3バイトずつ格納する。

```text
linedIndices.Count回繰り返す:
  deltaX:u8
  deltaY:u8
  deltaZ:u8
```

このため差分フレームは単独ではデコードできない。対応するキーフレームと、それ以降の連続した全差分フレームが必要になる。

### 4.4 GPU readbackと順序の確定

ComputeShaderの出力は `AsyncGPUReadback.Request` で取得する。複数フレームのreadbackが同時進行して完了順が入れ替わっても、送信側は `sequence` ごとの保留テーブルに結果を保存し、`nextCommitSequence` から昇順にだけ確定する。

保留数が上限へ達した場合は新しいGPU処理の投入を一時停止し、シーケンス番号に穴を開けない。経過時間は保持されるため、再開後の各フレームには実際のキャプチャPTSが付く。GPU読み戻しエラーが発生した場合も、壊れた差分依存チェーンを送らないため記録を停止する。

### 4.5 プロトコルv2フレームヘッダー

キーフレームと差分フレームは共通の29 byteヘッダーを持つ。

| オフセット | サイズ | 型 | 内容 |
| ---: | ---: | --- | --- |
| 0 | 1 | `u8` | `0x0F`: キーフレーム、`0x0E`: 差分フレーム |
| 1 | 4 | `uint32` | 単調増加するシーケンス番号 |
| 5 | 3 | `uint24` | キーフレームのパッケージ数。差分フレームは0 |
| 8 | 1 | `u8` | プロトコル／ヘッダー識別値 = `2` |
| 9 | 12 | `float32 x3` | 対象ルートの位置 |
| 21 | 8 | `int64` | 記録開始からの表示時刻tick |
| 29 | 可変 | - | キーフレームまたは差分フレームのペイロード |

PTSは次の式で作る。

```text
ptsTicks = (Time.realtimeSinceStartupAsDouble - recordStartRealtime)
         * TimeSpan.TicksPerSecond
```

`frame_interval` からの推定値ではなく実際のキャプチャ時刻なので、Editorのフレーム変動やGPU読み戻しのバックプレッシャーがあっても受信側の時間軸を保てる。

### 4.6 `.stmv` チャンク化

確定したフレームを最大 `combined_frames` 個まとめる。GZip前のチャンク構造は次のとおり。

```text
frameCount:int32
frameSizes[frameCount]:int32
frameData[0]
frameData[1]
...
```

より厳密には、先頭に `(frameCount + 1)` 個の `int32` があり、最初の値が件数、後続値が各フレームのbyte数である。フレームデータは隙間なく連結する。

この非圧縮チャンク全体を GZip 圧縮し、`000000.stmv`、`000001.stmv` のような名前で送信する。同時に `stream.stmj` へ次の `StreamInfo` JSONを1行追記する。

```json
{
  "video": "000000.stmv",
  "startTicks": 1000000,
  "endTicks": 51000000,
  "firstSequence": 0,
  "lastSequence": 49
}
```

`stream.stmj` はJSON配列ではなく、1行に1オブジェクトを置くNDJSON形式である。

## 5. `.stmv` のデコード

### 5.1 チャンクの受信と分割

Receiverは `stream.stmj` の追加行をpollingし、未取得の `.stmv` を取得する。

1. `.stmv` 全体をGZip展開する。
2. `frameCount` と `frameSizes[]` を検証する。
3. 各フレームを切り出す。
4. プロトコルv2ならオフセット1のシーケンス番号とオフセット21のPTSを読む。
5. シーケンス番号をキーにした整列済みキューへ、重複なしで追加する。

プロトコルv1の21バイトヘッダーも後方互換で受理する。その場合のPTSは次の式で合成する。

```text
presentationTime = StreamInfo.startTicks / timebase_hz
                 + frameIndex * frame_interval
```

### 5.2 キーフレームの復元

可変長ペイロードの境界検証とインデックス解決はCPUで行い、各頂点を次の固定長コマンドへ変換する。

```text
KeyframeCommand
  globalVertexIndex:uint32
  position:float3
```

復元式は次のとおり。

```text
tileScale = containerSize / halfPack
subTileScale = tileScale / 32

position.x = (tileX - halfPack) * tileScale + localX * subTileScale
position.y = (tileY - halfPack) * tileScale + localY * subTileScale
position.z = (tileZ - halfPack) * tileScale + localZ * subTileScale
```

`globalVertexIndex` は、事前データから求めたMeshごとのoffsetを用いて計算する。

```text
globalVertexIndex = meshOffsets[meshIndex] + vertexIndex
```

同時にペイロードに現れたグローバルインデックスの順を保存し、後続差分フレームの対応表とする。

### 5.3 差分の復元

差分byteから128を引き、符号付き量子値 `q` を得る。

```text
q = encodedByte - 128
delta = q < 0 ? -(q * q) / 16384 : (q * q) / 16384
position += delta
```

差分payloadのi番目は、直前キーフレームで保存したindex順のi番目へ適用する。

### 5.4 ComputeShaderとCPUフォールバック

ReceiverのGPU常駐経路では、元のパック済みバイトとタイル索引を `ReceiverVertexPipeline.compute` へ渡す。GPU上で復元状態と再生待ちスナップショットを保持し、音声PTSで補間してMeshのGPU頂点バッファへ直接出力する。macOS/iOSでは頂点readbackを行わずGraphicsFenceで処理完了を確認する。WebGPUでは完了確認に各フレームの先頭16バイトだけを非同期readbackする。

Auto/GPUはComputeShaderと完了確認機能（macOS/iOSのGraphicsFence、WebGPUのAsyncGPUReadback）に対応する場合にGPU経路を試行する。CPU指定、非対応環境、初期化失敗ではCPU経路を使用する。GPU実行中の例外では次のキーフレームからCPUで再開し、GPU側だけに存在する差分復元状態は引き継がない。モバイルも同じ選択規則とする。旧VertexContainer単体APIには従来のreadback経路が残る。

GPU経路とCPU経路は同じ量子化復元式を使用する。法線・メモリ上限・検証については [RECEIVER_GPU_PIPELINE.md](RECEIVER_GPU_PIPELINE.md) を参照。

### 5.5 keyframe待機とsequence欠落

Receiverは次の状態を持つ。

| 状態 | 意味 |
| --- | --- |
| `WaitingForKeyframe` | 単独デコード可能なキーフレームを待っている |
| `Buffering` | デコード済みフレームが事前バッファ数に達していない |
| `Playing` | PTSに沿って補間再生中 |
| `Holding` | 次フレームがなく、最後の正常フレームを保持中 |

期待する `nextSequence` がなく、それより後のキーフレームが到着している場合は、欠落した差分チェーンを適用せず、そのキーフレームから再開する。後続キーフレームもなければ最後の正常なMeshを表示したまま待機する。

標準では2つ以上のデコード済みフレームを使い、前後のPTS間で頂点とroot位置を線形補間する。fMP4音声経路では、音声プレーヤーを停止状態で準備し、モデルと最初のメッシュフレームが揃ってから先頭メッシュのPTSへ音声をシークする。シーク完了後、その時刻から最低0.25秒（または2フレーム分）のメッシュが揃うと再生を開始する。途中接続も受信した先頭から開始し、ライブの最新時刻へ自動ジャンプしない。

再生中は音声時刻を基準に表示する。デコード済みメッシュの残りが0.05秒未満になると音声を一時停止し、取得・デコードを続けて上記の再開条件を満たすまで待つ。音声側のバッファ不足では音声時計が止まるため、メッシュもその時刻に留まる。制御はUnityのUpdate周期で行うため、サンプル精度での同期を保証するものではない。音声のないWebGL経路は従来どおり `Playing` の間だけ内部時計を進める。

バッファの充足は設定fpsから求めた枚数だけではなく、実際のPTSの時刻幅で判定する。容量上限に達しても必要な時刻幅に届かない場合は、補間の起点と新しい表示用スナップショットを残して中間のスナップショットを間引き、デコードを続ける。差分フレーム自体のデコード順序は維持する。

## 6. 精度と制限

### キーフレーム精度

1軸あたりの量子化stepは次の値である。

```text
keyframeStep = containerSize / (floor(packageSize / 2) * 32)
```

現在の既定値 `containerSize=4`、`packageSize=128` では `4 / (64 * 32) = 0.001953125` Unity単位となる。

### 差分範囲

差分はbyteへclampされるため、非常に大きい1フレーム移動は飽和する。正側の最大復元量は `127^2 / 16384`、負側は `-128^2 / 16384` であり、完全な対称範囲ではない。大きく変形する素材では `frame_interval` を短くするか、キーフレーム間隔を短くする。

### 現行制限

- Mesh数: 最大256
- 1 Meshの頂点数: 最大65,536
- keyframeの座標範囲: 各軸 `(-containerSize, +containerSize)`
- Meshトポロジー、頂点数、Material構成は原則として記録中に固定
- `stream.bin` と `.stmv` にはchecksumや暗号学的integrity検証がない
- プロトコルv2のフレームPTSは記録開始からの相対時刻であり、UTC時刻ではない

## 7. 実装対応表

| 処理 | 実装 |
| --- | --- |
| `stream.json` / `stream.bin` 生成 | `STMHttpSender.CreateInfos()` |
| metadata生成 | `STMHttpSerializer` / `InfoConverter` |
| GZip圧縮・展開 | `Lib/ExternalTools.cs` |
| キーフレームのGPUエンコード | `Resources/TilingShader.compute` |
| 差分フレームのGPUエンコード | `Resources/DiffShader.compute` |
| タイルのバイトパッキング | `TilePacker.cs` |
| `.stmv` ヘッダー／チャンク生成 | `STMHttpSender.CommitFrame()` / `FlushCombinedFrames()` |
| `.stmv` 分割・シーケンス番号／PTSキュー | `Core/Rendering/StreamingMeshRenderer.cs` |
| ペイロード検証・デコードコマンド生成 | `Core/VertexContainer.cs` |
| キーフレーム／差分フレームのGPUデコード | `Resources/VertexDecodeShader.compute` |
| テクスチャ／マテリアル／Mesh復元 | `TextureConverter` / `MaterialConverter` / `MeshConverter` |

## メインスレッドの待機とファイル公開

通常録画中のFFmpeg stdin書き込みとWaitForExitは専用writerThreadで実行する。FFmpegの標準エラーも非同期で排出する。終了処理OnDestroyには最大500msのJoinが残るが、通常再生中には呼ばない。

音声playlist・新しいfragmentの読み出しは0.1秒間隔でTask.Runへ投入し、完了済み結果だけをUpdateでイベント通知する。最初のplaylistが公開されるまでinitファイルを送らず、未読fragmentを含むplaylistも送らない。メッシュの結合リストは所有権をworkerへ渡してサイズ表作成・大容量コピー・GZip圧縮を行い、圧縮完了後に元の順序で送信する。Task.ResultはIsCompleted確認後だけ参照し、UpdateでTask.WaitやJoinは行わない。結合バッファは処理後に再利用する。

Senderのアップロードは本体→配信リストの順にHTTP応答を待つコルーチンで実行する。通信待ちはメインスレッドをブロックしない。受信側のチャンク展開もworkerへ移動する。GPU dispatch、Meshへの描画反映、送信元BakeMesh、PCMコールバック処理自体は別に負荷が残るため、非同期化だけでフレーム時間を保証するものではない。

Create ChannelのPNG生成は元TextureImporterを変更せず、GPU上の一時RenderTextureから読み出す。インポートや元テクスチャの圧縮設定変更は行わない。この初期化時のGPU読み出しとPNGエンコードは同期処理だが、通常録画中の毎フレーム処理ではない。
