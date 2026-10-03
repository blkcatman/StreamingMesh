# Receiverの接線再構築

Receiverの表示時に、補間後の位置・法線・UV0・三角形から接線を再計算する。GPU常駐経路とCPUフォールバックに対応する。Senderや配信形式への追加は不要で、元モデルの接線を配信する機能ではない。

## 設定

Receiver Inspectorの `Tangent Mode` を選ぶ。

| 設定 | 対象 |
| --- | --- |
| `Auto`（既定） | `Tangent Material Names` に明示した配信マテリアル名を使うMesh |
| `None` | 接線を再計算しない |
| `Recalculate` | UV0（2成分以上）と三角形を持つすべてのMesh |

`Auto` のリストは既定で空。接線空間のノーマルマップや独自シェーダー等で接線が必要な場合に、その**配信マテリアル名**を登録する。大文字・小文字を区別し、Shader名からは推測しない。複数submeshのうち一つでも登録済みマテリアルを参照する場合、そのMesh全体を対象にする。UV0や三角形がないMeshは対象から外れ、空でないMeshでは警告を出す。

接線を計算するMeshには法線も必要なので、`Normal Mode=None` でも対象Meshの法線計算を有効にして警告を出す。設定は接続時に確定する。Inspectorのモードやリストを変更した後は再接続する。これにより、描画バッファの古いハンドルを破棄し、必要な頂点属性で初期化し直す。

コードから利用する場合も、初期化前に設定する。

```csharp
renderer.TangentMode = ReceiverTangentMode.Auto;
renderer.TangentMaterialNames.Add("Body");
renderer.AddMesh("character", mesh, meshMaterialNames);
renderer.CreateVertexBuffer();
renderer.CreateVertexContainer(packageSize, containerSize);
```

`AddMesh` の従来の2引数呼び出しも利用できる。その場合はマテリアル参照がないので、`Auto` では接線を要求しない。明示的に `Recalculate` を使える。旧 `STMHttpMeshReceiver` はこの設定の対象外。

## GPU処理

必要なMeshにFloat32×4のTangent属性を追加し、既存のUVチャンネルの成分数・値とsubmeshを保持する。UV差から面ごとの接線／従接線係数を初期化時に計算する。三角形と頂点→隣接面索引は法線処理と共有する。

表示ごとの順序は `Present → FaceNormals → VertexNormals → FaceTangents → VertexTangents`。同じgraphics queueで補間済みの位置から計算し、Raw頂点バッファへ直接書き込む。通常再生に全頂点readbackやCPUのMesh変更APIを追加しない。WebGPUの既存の16byte完了確認readbackは継続する。

各面で `T=(e1*d2.y-e2*d1.y)/det`、`B=(e2*d1.x-e1*d2.x)/det` を求める。頂点ごとに隣接面のT/Bを**同じ重みで加算**し、法線への直交化と正規化を行う。法線は既存の面積重み付き合成を維持する。`w` は `dot(cross(N,T),B)` の符号で決める。float atomicは使わない。

縮退した位置・UVや非有限値の面は接線へ寄与しない。UVの縮退判定は `det² <= max(1e-60, 1e-12*|d1|²*|d2|²)`。有効な候補がない頂点には、法線と最も平行でない座標軸を直交化した単位接線を使う。ゼロ法線では接線計算の内部基準を+Zとし、法線属性自体は既存のゼロ値を保つ。符号を決められない場合は `w=+1`。GPU法線集約も非有限値を除外する。

UV境界やhard edgeの頂点を統合しない。鏡像UVの両側で異なる符号が必要なら元Meshで頂点を分離する。オブジェクト空間で生成するため、負スケールTransformの扱いは使用Shaderの規約に従う。

この面集約方式はMikkTSpaceと同一ではない。CPUのUnity実装とも一般の複雑なモデルで完全一致するとは限らず、元モデルのベイク基底や調整済み法線との一致は別途評価する。

## CPUとメモリ

CPU経路は `位置更新 → 必要なRecalculateNormals → 必要なRecalculateTangents`。GPU失敗後も同じMesh別判定を保持し、既存の次キーフレームからの復帰に従う。

GPU追加量は対象頂点あたり16byte、面あたり48byte（UV係数・T・Bが各16byte）。ゼロ面バッファは最小1要素を確保する。`GpuTangentBytes` はこの追加量、`GpuModelBytes` はMesh出力・隣接情報・面scratch等の合計を返す。どちらも `GpuPoolBytes` のスナップショット予算とは別枠で、接続時にログへ表示する。ドライバーの割当粒度や内部メモリは含まない。

アプリ側の判定配列とGPU scratchは初期化時に作り、表示時に再利用する。Unity内部のネイティブ作業領域がゼロとは保証しない。

## 検証

Windowsでは次のコマンドで、サンプルに依存しない一時Unityプロジェクトを `Temp/` に作って検証する。本体のReceiverと依存コードもコピーするのでコンパイルを確認できる。Unity Editorのパスはインストール環境に合わせる。

```powershell
./Tools/Tests/verify_receiver_tangents.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe'
```

他の環境では `Tools/Tests/ReceiverTangentVerification.cs` を検証用プロジェクトのEditorフォルダへ置き、`-executeMethod ReceiverTangentVerification.Run` で実行できる。検証でのみRaw頂点バッファをreadbackする。

2026-10-04、Unity 6000.5.10f1／Windows Direct3D 11／NVIDIA GeForce RTX 4090 Laptop GPUで474項目通過。

- 通常・鏡像UV、共有頂点の集約、分離したhard edge、UV0と3/4成分の追加UV、複数submesh、空Mesh。
- PTS中間形状の接線、縮退位置・UV、孤立頂点、非有限入力からの有限接線。
- Auto/None/Recalculate、複数マテリアルのMesh別選択、Normal=Noneの必要Meshだけの上書き。
- 不正入力後のキーフレーム回復、GPU実行例外からCPU復帰、再初期化で接線属性を外すレイアウト更新。
- 単純な面のUnity CPU/GPU接線方向差は最大約8.43e-8。共有面のGPU結果は独立した集約式を基準に確認。

16頂点fixtureでCPU表示処理をウォームアップ後256回測定し、法線のみ0.2453ms、法線＋接線0.4358ms、どちらも測定区間のmanaged割当0byteだった。この小さいfixtureの結果は実モデルの性能やUnity内部のnative割当を示さない。

KAGURAサンプルはマテリアルテンプレートの複製とSenderの値の適用に対応し、GPUで法線・接線を再計算した13 MeshのToon静止描画を確認した。詳細は [Receiverのマテリアル復元](RECEIVER_MATERIALS.md) を参照。

プロジェクト指定版6000.6.3f1、Metal／Vulkan／WebGPU実行、iPhone／Android、負スケール描画、実モデルのGPU時間とCPU／GCは未検証。実機60fpsを保証しない。

Unity APIの挙動は [Mesh.RecalculateTangents](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Mesh.RecalculateTangents.html) を参照。設計時の検討は [実装ハンドオフ](RECEIVER_TANGENT_HANDOFF.md) に残している。
