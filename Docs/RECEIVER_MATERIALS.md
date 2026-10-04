# Receiverのマテリアル復元

SenderのCreate Channelで、各Materialのプロパティ名と値を 初期データの分割ファイル に保存する。Receiverは同じ名前のプロパティへ値を設定する。`type` の0〜5は値の種類を表し、Shader内のプロパティの並び順やインデックスではない。

## KAGURAでの確認手順

`Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity` は9種類のKAGURAマテリアルをテンプレートとして参照する。Receiverはテンプレートを複製し、Senderから届いた値・テクスチャ・描画状態を上書きする。元の `.mat` アセットは変更しない。Senderと同じキーライト・環境光を設定し、法線と接線の両方を再計算する。

1. 受信中ならDisconnectし、Senderの録画を停止する。
2. KaguraDemoのマテリアルを設定する。
3. 更新したSenderで **Create Channel** を実行し、初期データの分割ファイル の送信完了を待つ。
4. **Record from start** で録画を開始する。
5. KaguraReceiverで **Connect / Reconnect** する。端末では更新したシーンを再ビルドする。

マテリアルはチャンネル作成時のスナップショットであり、録画中の変更は毎フレーム送信しない。設定を変えて比較する場合はこの手順を繰り返す。チャンネル形式はv5、MaterialInfoはv3。旧形式のチャンネル／MaterialInfoは拒否するため、更新したSenderで作り直す必要がある。不正な数値も拒否する。

Reconnect／Seekは、同じURL・選択したGPU形式の目録とSHA-256・Receiver設定が一致すれば既存のMaterial／Texture／Meshと再生バッファを再利用する。Senderの内容を更新して目録が変わると再取得する。ローカルテンプレートのプロパティを実行中に直接変更した場合は`Receiver.Reconnect(address, autoPlay, forceReload: true)`で再生成する。DisconnectはReceiverを破棄し、保持リソースも解放する。

## Receiver設定

| 設定 | 用途 |
| --- | --- |
| Material Templates | 配信マテリアルIDとローカルMaterialを対応させる。複製後に送信値を適用する。 |
| Stream Textures Only Templates | ビルド中のシーンコピーからテンプレートの画像参照を外し、ローカル画像と配信画像の二重保持を防ぐ。Senderが必要な全画像プロパティを提供する場合だけ有効にする。KAGURAでは有効。 |
| Custom Shaders | テンプレート未指定の配信マテリアルIDにShaderを割り当てる。 |
| Use Sender Shader | 上記の対応がなければ送信されたShader名を `Shader.Find` で探す。既定は無効。KAGURAでは有効。 |
| Default Shader | 対応するShaderが見つからない場合に使う。 |

優先順位はテンプレート → Custom Shaders → SenderのShader（有効な場合）→ Default Shader。同じ名前・互換性のある型のプロパティを適用する。送信元と同じShaderの場合だけ、キーワードと描画状態も適用する。別Shaderへの変換ではそのShader／テンプレートの描画状態を保つ。

Shaderコード自体は送信しない。Receiverのプロジェクト／ビルドにShaderと必要なバリアントを含める。KAGURAの9テンプレート参照は元のマテリアルが使うバリアントのビルド収録にも使われる。Senderで新しいキーワードの組み合わせへ変更する場合、その組み合わせがReceiverビルドにも含まれることを確認する。

## リソースIDとMaterialInfo version 3

Meshフレームヘッダー（v2）と音声形式を維持し、初期データはv5の分割ファイルへ変更する。Material JSONには `version: 3` と次の情報を格納する。

IDは `SHA256("asset\0" + 種類 + "\0" + パス + "\0" + localFileId)` の小文字16進64文字。パスは `Assets/...` または `Packages/...` のプロジェクト相対パスで、区切り文字を `/` に統一する。大文字・小文字は保持する。主アセットのlocalFileIdは0、FBX等のサブアセットはUnityのlocalFileIdを使う。これは同一リソースの識別であり、同じ画像内容の別ファイルをまとめるものではない。移動・改名するとIDが変わる。

アセットパスを持たない生成リソースは、Create ChannelごとのセッションIDとオブジェクトの登録番号をハッシュ化する。同じスナップショット内の参照は共有するが、次のスナップショットではIDを変える。ID計算と登録は初期データ作成時だけ行い、再生フレームでは行わない。

`ChannelInfo.materials/textures` はIDの配列、`textureNames` は対応する表示名。`MaterialInfo.id`、`MeshInfo.materialIds`、Textureプロパティの `textureId` で参照する。`MaterialInfo.name` とTextureプロパティの `value` は表示用で、一意性を要求しない。マテリアル枠の順序・空の枠・追加描画用の枠を保持する。文字列はUTF-8 JSONで可変長であり、固定長の名前領域はない。

テンプレートの `materialId` が空ならEditorで参照アセットから設定する。別のテンプレートを配信マテリアルへ割り当てる場合は配信IDを明示する。ビルド処理はIDを焼き込み、必要に応じて画像なしの一時Materialへ差し替える。元の `.mat` ファイルとShaderのプロパティ・キーワードは保持する。

- Shader名、キーワード、renderQueue、enableInstancing、doubleSidedGI、globalIlluminationFlags。
- Color、Vector4、Float、Range、Integer、Texture。Float/RangeはInvariantCultureの往復可能な数値文字列、Integerは整数文字列で保存する。
- Textureのscale/offset、linear/sRGB、ミップ有無、filterMode、wrap U/V、anisoLevel、mipMapBias。空の `textureId` は明示的な未設定として復元する。
- RenderType、IgnoreProjector、IgnoreProjection、DisableBatching、ForceNoShadowCastingの有効タグ値。Shaderに存在するPass名とLightModeの有効／無効状態。

ReceiverはID単位でMaterial／Textureを共有し、使用するShaderが参照するTextureだけを展開する。同じIDのlinear／ミップ／サンプラー設定の矛盾、重複ID、不足した参照を拒否する。初期データのサイズ・セグメント表と圧縮ファイルのSHA-256を検証する。GZip展開は64KiBの再利用バッファからTextureのCPU領域へ直接コピーする。全モデルや分割ファイルの展開済み配列は作らない。Material／Mesh JSONは個別の小さい配列へ復元する。

TextureはEditorでGPU Blitして正規化し、選択したBC7／DXT／ETC2／ASTC形式へ圧縮する。元アセットは再インポートしない。NormalMapはインポート時のチャンネル配置から法線XYZを復元してRGBへ保存する。Receiverは対応する圧縮形式へ直接読み込み、linearとミップ数を反映する。元の独自ミップ画像は再生成される。

対象はShaderのPropertiesに宣言された値と2D Texture。MaterialPropertyBlock、毎フレームのマテリアル変更、グローバルShader値、配列／行列／バッファ、Cubemap・TextureArray、列挙対象外の独自タグは送信対象外。照明・カメラ・ポスト処理はReceiverシーン側の設定を使う。送信元で編集された法線・接線も送信せず受信形状から再計算するため、実際の配信では元の描画と差が生じる場合がある。

## 検証

```powershell
./Tools/Tests/verify_receiver_materials.ps1 -EditorPath 'C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe'
```

一時Unityプロジェクトへ本体のSender／Receiver、KAGURAモデル・マテリアル・Receiverシーン、同梱Toon Shaderをコピーして実行する。既定のURPは17.5.0。使用するEditorに合わせて `-UrpVersion` を指定できる。TimeWireは本体のpackages-lock.jsonに記録されたCoreコミットを使う。画像とログは表示された検証プロジェクト内の `Results/` と `material-verification.log` に出力する。

2026-10-04、Unity 6000.5.10f1／URP 17.5.0／Windows Direct3D 11／RTX 4090 Laptop GPUでマテリアル関連3,569項目とリソースID関連24項目と接線関連474項目が通過。

- KAGURAの全9マテリアルと20 TextureをJSON／PNG経由で復元。テンプレートと異なるFloat／Range、キーワード、queue、Pass、Texture変換を設定して上書きを確認。
- フランス語ロケールで数値往復、Integer精度、明示的なnull Texture、旧形式の拒否、Shader優先順位、不足Texture、NormalMapのRGB出力を確認。
- 同じ形状・照明でSenderのマテリアルと復元したマテリアルを描画比較し、画像のbyte差は0。これは静止描画の比較であり、ネットワーク受信後の再計算法線との一致を示さない。
- GPUパイプラインでKAGURAの13 Mesh／23,489頂点の法線・接線を再計算し、Toon描画を確認。
- 実際のKaguraReceiverシーンのテンプレート参照と照明、既存の接線検証を確認。

プロジェクト指定のUnity 6000.6.3f1／URP 17.6、Metal／Vulkan、iPhone／Android、モバイル実機再生は今回の検証対象外。
