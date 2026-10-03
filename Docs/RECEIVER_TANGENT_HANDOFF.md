# Receiver 接線計算の実装ハンドオフ

作成日: 2026-10-03。2026-10-04にGPU／CPU接線生成とInspector設定を実装。現在の使い方、採用した集約方式、検証結果と未検証項目は[Receiverの接線再構築](RECEIVER_TANGENTS.md)を参照。ToonマテリアルのReceiver導入は別作業。

以下は実装前の設計・引き継ぎ記録であり、当時の状態と提案を残している。

## 目的

変形後の頂点位置・法線・UV0・三角形インデックスから接線を生成し、接線を必要とするマテリアルで受信メッシュを描画できるようにする。GPU常駐デコードとPTSに基づく補間を維持し、通常再生で全頂点をCPUへreadbackしない。Senderと配信形式は変更しない。

## Unityの接線生成と使用条件

接線はMeshの頂点属性であり、モデルインポート時の設定によってImport／Calculate／Noneを選ぶ。すべてのMeshが接線を持つとは限らない。生成した接線を描画で使うかは、シェーダーと有効な機能・バリアントによる。主な用途は接線空間のノーマルマップだが、異方性表現や独自のToon表現などにも使える。

マテリアルを割り当てるだけで、実行時の動的Meshに接線が自動生成されるものではない。CPU経路では`Mesh.RecalculateTangents()`を使える。`RecalculateNormals()`は接線を生成しない。GPUバッファへの直接更新はCPUのMeshデータへ反映されないため、GPU経路でCPU APIを呼ぶだけでは最新の形状を処理できない。

## 設計時点の実装

| 対象 | ファイル | 状態 |
| --- | --- | --- |
| Receiver設定 | `Assets/StreamingMesh/Scripts/Receiver.cs` | DecodeBackendとNormalModeをRendererへ渡す。接線設定なし |
| 補間・CPUフォールバック | `Assets/StreamingMesh/Scripts/Core/Rendering/StreamingMeshRenderer.cs` | `ApplyVertices()`で位置を更新し、必要な場合に法線を再計算。接線の更新なし |
| GPU描画バッファ | `Assets/StreamingMesh/Scripts/Core/Rendering/GpuVertexPipeline.cs` | `CreateOutput()`でPosition／Normal／UVのレイアウトを構築。Tangent属性なし |
| GPU法線 | `Assets/StreamingMesh/Resources/ReceiverVertexPipeline.compute` | `Present`→`FaceNormals`→`VertexNormals`。頂点ごとの隣接面索引を再利用できる |
| 静的メッシュ情報 | `Assets/StreamingMesh/Scripts/Core/Serialization/MeshConverter.cs` | 三角形、submesh、UV0〜UV3を復元。元の法線・接線は送られない |
| シェーダー選択 | `Assets/StreamingMesh/Scripts/Core/Serialization/MaterialConverter.cs` | マテリアル名→Shaderの対応表。マテリアルテンプレートの複製機能なし |

現在の`UsesNormals()`は既知のUnlitシェーダー名を除外する全体判定で、シェーダーの入力属性を解析していない。この判定を、そのまま「すべてのLit／Toonは接線が必要」とする接線判定へ流用しない。

## 設定と対象メッシュ

提案する接線設定は`Auto / None / Recalculate`。名称・公開APIは実装時に既存の設定体系と合わせる。

- `None`: 接線計算を省略し、既存のUnlit再生を維持する。
- `Recalculate`: 有効なUV0と三角形を持つ対象メッシュで接線を更新する。
- `Auto`: 使用マテリアルに対する明示的な能力設定・対応表で判定する。任意のシェーダーの必要性を、ノーマルマップのプロパティ名やシェーダー名だけから完全には判定できない。未知のマテリアルは明示指定で対応可能にする。

判定はメッシュごとに行い、複数submeshのうち一つでも接線を必要とするなら、そのMeshの接線属性を用意する。必要な法線も同時に有効にする。NormalMode=Noneと接線再計算が同時に指定された場合は、黙ってゼロ法線を使用せず、法線生成を有効にする方針をInspectorとログで明示する。

マテリアル能力の変更で必要な頂点属性が変わる場合は、安全なタイミングで出力レイアウトを再構築し、GPUバッファを再取得する。毎フレーム再構築しない。

## GPU経路

1. 初期化時に必要なMeshへFloat32×4の`VertexAttribute.Tangent`を追加する。strideとNormal／Tangent／UV0のoffsetを取得し、ComputeShaderへ渡す。既存のUVチャンネルとsubmeshは保持する。
2. UV0と三角形は収録中に固定される前提を利用し、面ごとのUV係数・UV縮退判定を初期化時に用意する。既存の頂点→隣接面索引を共有する。
3. 再生時は補間後の位置を使用し、`Present`→面法線・面接線／従接線→頂点法線→頂点接線の順に同じgraphics queueで実行する。先読み中の復元状態ではなく、実際に表示する姿勢から計算する。
4. 各面の接線・従接線を一時GPUバッファへ書き、各頂点のスレッドが隣接面を集約する。float atomicは使わない。法線への直交化、正規化、従接線からの符号決定を行い、`float4(tangent.xyz, handedness)`をMeshのTangent属性へ書く。
5. scratch領域・隣接情報・UV係数は初期化時に確保し再利用する。GPU完了順序を維持してDisposeする。通常経路で同期readbackやCPUのMesh変更APIを追加しない。

面の辺を`e1=p1-p0`、`e2=p2-p0`、UV差を`d1=uv1-uv0`、`d2=uv2-uv0`とすると、候補は次の式で求められる。

```text
det = d1.x * d2.y - d1.y * d2.x
T = (e1 * d2.y - e2 * d1.y) / det
B = (e2 * d1.x - e1 * d2.x) / det
Tvertex = normalize(Tsum - N * dot(N, Tsum))
w = sign(dot(cross(N, Tvertex), Bsum))
```

面の重み付けはCPU比較と見た目の検証を踏まえて決める。上記の単純な集約をMikkTSpaceと同一とは扱わない。元のノーマルマップのベイク方式と一致する必要がある場合は、互換性を別途評価する。

UV鏡像では`w`の符号を保持する。UV／位置が縮退した面、孤立頂点、ゼロ法線、非有限値について分岐を用意し、NaNを出さない。有効な法線はあるが接線候補がない場合は、法線に直交する安定したフォールバックを使う。UV境界の重複頂点は勝手に統合しない。鏡像UVで異なる符号を必要とする頂点は、元メッシュで分離されている必要があり、共有頂点一つに両方の基底を保存することはできない。

負スケールのTransformによる向きの反転と、UV鏡像由来の`tangent.w`は区別する。オブジェクト空間の接線を生成し、ワールド空間への変換は対象シェーダーの規約に従う。

接線属性は対象頂点あたり16byte。面ごとのT／Bをそれぞれfloat4で持つ案では32byte／面が追加される。UV係数等も含め、GPUプールとは別のモデル依存メモリを計測・表示する。

## CPUフォールバック

`ApplyVertices()`の順序を位置更新→必要な法線更新→必要な`RecalculateTangents()`にする。接線が不要なMeshは呼び出しを省略する。GPUからCPUへ切り替わった後も同じ必要性設定を保持する。

Unity内部の作業領域の確保量を未測定のままゼロと表記しない。アプリ側の配列や判定表は再利用し、CPU再計算の処理時間とGCを計測する。

## 検証と完了条件

| ケース | 確認内容 |
| --- | --- |
| 平面・通常UV | 接線方向、法線との直交、正規化、`w`が期待どおり |
| 鏡像UV・UV境界・hard edge | 従接線の符号、分離頂点の保持、境界での不正な平均化がない |
| 変形・PTS中間姿勢 | 前後フレームの接線補間ではなく、表示位置から計算される |
| 縮退面・縮退UV・孤立頂点・空Mesh | 非有限値や範囲外アクセスがない |
| CPU／GPU比較 | 同じ位置と法線を入力して接線基底・描画を比較。方式差の許容範囲を記録 |
| Unlit・複数マテリアル | 不要な接線dispatchなし。必要なsubmeshを含むMeshでは更新される |
| GPU初期化失敗・実行失敗・再接続・seek | 既存のキーフレーム回復とCPUフォールバックが動作し、バッファが解放される |
| KAGURA＋Toon | 動く顔・髪・身体、ノーマルマップ、負スケール時の見た目を確認 |
| Editor・iPhone・Android | GPU常駐とreadbackなしを確認し、法線のみとの差分GPU時間、メモリ、CPU／GCを計測 |

既存検証の候補は`Tools/Tests/verify_gpu_receiver.cs`、`verify_gpu_playback.cs`、`verify_mesh_playback.cs`。接線の数学検証では必要な範囲のGPU出力を検証専用にreadbackしてよいが、通常再生へ持ち込まない。60fpsは目標として測定し、接線追加だけで実機60fpsを保証しない。

接線生成が正しくても、元の調整済み法線が失われる影響、Toonマテリアルのキーワード・描画順・テクスチャ色空間・シェーダーバリアント・照明の問題は残り得る。マテリアル名から同梱マテリアルを複製する機能とReceiverシーンのToon化は、接線計算とは別の作業として管理する。

## 参照

- [現在のReceiver GPUパイプライン](RECEIVER_GPU_PIPELINE.md)
- [Unity: ModelImporterTangents](https://docs.unity3d.com/cn/6000.0/ScriptReference/ModelImporterTangents.html)
- [Unity: Mesh.RecalculateTangents](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Mesh.RecalculateTangents.html)
- [Unity: Mesh.RecalculateNormals](https://docs.unity3d.com/ja/6000.0/ScriptReference/Mesh.RecalculateNormals.html)
- [Unity: Mesh.GetVertexBuffer](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Mesh.GetVertexBuffer.html)
