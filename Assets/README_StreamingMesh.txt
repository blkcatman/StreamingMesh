# StreamingMesh v0.11
## このアセットについて
**StreamingMesh**はWebRTCを経由して，リアルタイムに3Dモデルを読み込んで表示する
実験的なアセットです．
現バージョンでは受信側(**MeshReceiver**)はビルドしたアプリケーションに対応しますが，送信側(**MeshSender**)はUnityEditor上でしか動作しません．これはマテリアルのプロパティやテクスチャのアクセスがUnityEditorのコードを利用しているためです．また，現行バージョンではSkinnedMeshRendererに設定されたMeshのみ対応します．


## 依存関係
### 事前に必要なもの
アセットストアの[WebRTC Network](https://www.assetstore.unity3d.com/jp/#!/content/47846)をUnityにインポートします．これが最低限ないとコンパイルエラーで動作しません．

### あればよいもの
[7Zip, lzma, LZ4, fastLZ, zip/gzip & brotli multiplatform plugins.](https://www.assetstore.unity3d.com/jp/#!/content/12674)によるデータ圧縮をサポートしているので，このアセットと**StreamingMesh**のアセットをインポート後，Assetフォルダ直下の**smcs.rsp**と**gmcs.rsp**を削除することでデータ圧縮の恩恵を受けることができます．
**上記のアセットをインポートせずにsmcs.rspとgmcs.rspを削除すると，コンパイルエラーが起きてStreamingMeshが
利用できなくなるので注意です．**

## アセットの使い方

### 送信側
1. 任意のGameObjectに**MeshSender**のスクリプトを追加します．このとき，自動的に**WebRTCManager**と**SkinnedMeshesValidator**が追加されます．

1. 送信したいMeshを含むGameObjectを**MeshSender**の**Target Game Object**に追加します．GameObjectから対象のMeshを再帰的に探索するので，Hierarchy直下のGameObjectを追加すると良いでしょう．

1. **WebRTCManager**のChannelに任意のチャンネル名を指定します．指定しない場合，ランダムのチャンネルが設定されます．

#### オプション
* **Reimport Textures In Game**のチェックを外し，**Enable Textures Writable Flag**ボタンを押しておくと，実行時に速やかに処理が行われるようになります．テクスチャ情報を送信する際は，テクスチャが書き込み可能でないとアクセスができないため，**Reimport Textures In Game**のプロパティをオンにして実行時に変更するか，事前に**Enable Textures Writable Flag**ボタンを押して，テクスチャを書き込み可能にしておく必要があります．

* **Auto Reconnect**はサーバーへの接続に失敗した際に，自動的に再接続を行います．

##### 使用時に注意するオプション
* **Area Range**は対象となるMeshの有効範囲を入力します．サイズを大きくすると精度が粗くなるため，なるべくこの数字は小さくする必要がありますが，この範囲外をオーバーして描画されるとエラーが起きてしまうため注意が必要です．

* **Package Size**はMeshの有効範囲を指定の数された数で領域を分割します．ここの数字が128の場合，128\*128\*128の最大2,097,152パッケージを持つことになります．精度を高めるために使いやすいですが，通信サイズが大きくなりやすくなります．**Area Range**の精度と比較しながら変更します．

* **FrameInterval**は送信する秒間フレーム数です．0.1から変更することはあまりありません．

* **Subframes Per Keyframe**はキーフレームの間に差分フレームをいくつ挿入するか指定できます．**FrameInterval**が0.1の時，秒間にキーフレーム2，差分フレーム8を送る計算になります．0のときはキーフレームのみの送信になります．

### 受信側
1. 任意のGameObjectに**MeshReceiver**のスクリプトを追加します．このとき，自動的に**WebRTCManager**と**SkinnedMeshesValidator**が追加されます．

1. **WebRTCManager**のChannelに任意のチャンネル名を指定します．**MeshSender**と同じチャンネル名を指定しておく必要があります．指定しない場合，ランダムのチャンネルが設定されます．

#### オプション
* **SkinnedMeshesValidator**のShadersは特定の名前のマテリアルに指定したシェーダを当てはめることができます．Sizeを0から必要な数に変更し，Material Nameに対象のマテリアル名，Shaderに当てはめたいShaderを指定してください．

* **Interpolate Vertices**はフレーム間の線形補完を有効にします．この効果は非常に強力です．

* **Auto Reconnect**はサーバーへの接続に失敗した際に，自動的に再接続を行います．

##### 使用時に注意するオプション

* **Time Span**はフレーム間の線形補完のタイミングを指定します．デフォルトではフレームは0.1秒毎に来るため，0.01秒ごとに線形補完を行う設定になっています．現行バージョンでは変える必要は特にありません．

## ライセンスについて
一部のソースコード(Crc32.cs, SerializableDictionary.cs)は著作権者が異なりますので注意してください．自分が書いたものはすべてMIT Licenseにしています．
