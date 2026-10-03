# Receiverのマテリアル復元

SenderのCreate Channelで、各Materialのプロパティ名と値を `stream.bin` に保存する。Receiverは同じ名前のプロパティへ値を設定する。`type` の0〜5は値の種類を表し、Shader内のプロパティの並び順やインデックスではない。

## KAGURAでの確認手順

`Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity` は9種類のKAGURAマテリアルをテンプレートとして参照する。Receiverはテンプレートを複製し、Senderから届いた値・テクスチャ・描画状態を上書きする。元の `.mat` アセットは変更しない。Senderと同じキーライト・環境光を設定し、法線と接線の両方を再計算する。

1. 受信中ならDisconnectし、Senderの録画を停止する。
2. KaguraDemoのマテリアルを設定する。
3. 更新したSenderで **Create Channel** を実行し、`stream.bin` の送信完了を待つ。
4. **Record from start** で録画を開始する。
5. KaguraReceiverで **Connect / Reconnect** する。端末では更新したシーンを再ビルドする。

マテリアルはチャンネル作成時のスナップショットであり、録画中の変更は毎フレーム送信しない。設定を変えて比較する場合はこの手順を繰り返す。旧SenderはFloat/Rangeを `JsonUtility.ToJson(float)` で保存していたため、値が `{}` になっていた。このデータから元の数値は復元できない。旧データは数値をテンプレートまたはShaderの既定値に保ち、警告を出す。Senderの設定を確認するにはチャンネルを作り直す必要がある。

## Receiver設定

| 設定 | 用途 |
| --- | --- |
| Material Templates | 配信マテリアル名とローカルMaterialを対応させる。複製後に送信値を適用する。 |
| Custom Shaders | テンプレート未指定の配信マテリアル名にShaderを割り当てる。 |
| Use Sender Shader | 上記の対応がなければ送信されたShader名を `Shader.Find` で探す。既定は無効。KAGURAでは有効。 |
| Default Shader | 対応するShaderが見つからない場合に使う。 |

優先順位はテンプレート → Custom Shaders → SenderのShader（有効な場合）→ Default Shader。同じ名前・互換性のある型のプロパティを適用する。送信元と同じShaderの場合だけ、キーワードと描画状態も適用する。別Shaderへの変換ではそのShader／テンプレートの描画状態を保つ。

Shaderコード自体は送信しない。Receiverのプロジェクト／ビルドにShaderと必要なバリアントを含める。KAGURAの9テンプレート参照は元のマテリアルが使うバリアントのビルド収録にも使われる。Senderで新しいキーワードの組み合わせへ変更する場合、その組み合わせがReceiverビルドにも含まれることを確認する。

## MaterialInfo version 2

Mesh／音声プロトコルと `stream.bin` の連結形式は変更しない。Material JSONに `version: 2` と次の情報を追加する。

- Shader名、キーワード、renderQueue、enableInstancing、doubleSidedGI、globalIlluminationFlags。
- Color、Vector4、Float、Range、Integer、Texture。Float/RangeはInvariantCultureの往復可能な数値文字列、Integerは整数文字列で保存する。
- Textureのscale/offset、linear/sRGB、ミップ有無、filterMode、wrap U/V、anisoLevel、mipMapBias。空文字のTexture値は明示的な未設定として復元する。
- RenderType、IgnoreProjector、IgnoreProjection、DisableBatching、ForceNoShadowCastingの有効タグ値。Shaderに存在するPass名とLightModeの有効／無効状態。

Textureは名前で共有するため異なるTextureには一意の名前を使う。Receiverは使用するShaderが参照するTextureだけを展開し、同名Textureのlinear／ミップ設定の矛盾を拒否する。必要なTextureが欠けた新形式データも拒否する。

PNGはEditorでGPU Blitして生成し、元アセットを再インポートしない。NormalMapとしてインポートされたTextureは、プラットフォームのチャンネル配置から法線XYZを復元してRGBへ保存する。ReceiverはRGBA32として読み込み、linear設定とミップ有無を反映する。圧縮形式・圧縮による品質・独自ミップ内容は引き継がない。

対象はShaderのPropertiesに宣言された値と2D Texture。MaterialPropertyBlock、毎フレームのマテリアル変更、グローバルShader値、配列／行列／バッファ、Cubemap・TextureArray、列挙対象外の独自タグは送信対象外。照明・カメラ・ポスト処理はReceiverシーン側の設定を使う。送信元で編集された法線・接線も送信せず受信形状から再計算するため、実際の配信では元の描画と差が生じる場合がある。

## 検証

```powershell
./Tools/Tests/verify_receiver_materials.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe'
```

一時Unityプロジェクトへ本体のSender／Receiver、KAGURAモデル・マテリアル・Receiverシーン、同梱Toon Shaderをコピーして実行する。既定のURPは17.5.0。使用するEditorに合わせて `-UrpVersion` を指定できる。TimeWireは本体のpackages-lock.jsonに記録されたCoreコミットを使う。画像とログは表示された検証プロジェクト内の `Results/` と `material-verification.log` に出力する。

2026-10-04、Unity 6000.5.10f1／URP 17.5.0／Windows Direct3D 11／RTX 4090 Laptop GPUでマテリアル関連3,388項目と接線関連474項目が通過。

- KAGURAの全9マテリアルと20 TextureをJSON／PNG経由で復元。テンプレートと異なるFloat／Range、キーワード、queue、Pass、Texture変換を設定して上書きを確認。
- フランス語ロケールで数値往復、Integer精度、明示的なnull Texture、旧 `{}` データ、Shader優先順位、不足Texture、NormalMapのRGB出力を確認。
- 同じ形状・照明でSenderのマテリアルと復元したマテリアルを描画比較し、画像のbyte差は0。これは静止描画の比較であり、ネットワーク受信後の再計算法線との一致を示さない。
- GPUパイプラインでKAGURAの13 Mesh／23,489頂点の法線・接線を再計算し、Toon描画を確認。
- 実際のKaguraReceiverシーンのテンプレート参照と照明、既存の接線検証を確認。

プロジェクト指定のUnity 6000.6.3f1／URP 17.6、Metal／Vulkan／WebGPU、iPhone／Android、通信・音声・アニメーションを含めた実機再生は今回の検証対象外。
