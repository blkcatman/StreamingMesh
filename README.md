# StreamingMesh for Unity v0.1 (HTTP version)
## このアセットについて
**StreamingMesh**はHTTPを経由して、リアルタイムに3Dモデルを読み込んで表示する実験的なアセットです。   
現バージョンでは受信側(**STMHttpReceiver**)はビルドしたアプリケーションに対応しますが、送信側(**STMHttpSender**)はUnityEditor上でしか動作しません。これはマテリアルのプロパティやテクスチャのアクセスがUnityEditorのコードを利用しているためです。また、現行バージョンではSkinnedMeshRendererに設定されたMeshのみ対応します。MeshFilterなどの通常のモデルは表示できません。UnityChanSDのモデルは全てSkinnedMeshRendererになっています。

## アセットの使い方

* まずは事前の設定を行います。**以下の設定が終わった後に、再生する**と、受信が開始されます。

### テスト用サーバー追加

1. 任意のGameObjectに**Examples/Scripts/SampleHttpServer**のスクリプトを追加します。このスクリプトは非再生中でも動作し、自動的にローカルホスト(127.0.0.1:8080)でサーバーを構築します。追加後は**[ResetServer]**のボタンを押してサーバーを再起動させてください。

### 送信側

1. 任意のGameObjectに**STMHttpSender**のスクリプトを追加します。このとき、自動的に**STMHttpSerializer**が追加されます。

2. 送信したいMeshを含むGameObjectを**MeshSender**の**Target Game Object**に追加します。GameObjectから対象のMeshを再帰的に探索するので、Hierarchy直下のGameObjectを追加すると良いでしょう。

3. **STMHttpSerializer**のAddressにサーバーのアドレスを指定します。初期値はローカルホストのテスト用サーバーが設定されます。その場合、"http://127.0.0.1:8080/channels/"までがサーバーのアドレスになります。

4. **STMHttpSerializer**のChannelに任意のチャンネル名を指定します。指定しない場合、ランダムのチャンネルが設定されます。

5. **[Create Channel]**を押してチャンネル生成、静的なファイルを生成します。テスト用サーバーでは、ファイルをAssetフォルダよりも上に、channels/[チャンネル名]のフォルダにファイルを生成します。**この処理を行わないと、受信側はデータを受け取ることができません。**

##### オプション
* **Area Range**は対象となるMeshの有効範囲を入力します。サイズを大きくすると精度が粗くなるため、なるべくこの数字は小さくする必要がありますが、この範囲外をオーバーして描画されるとエラーが起きてしまうため注意が必要です。

* **Package Size**はMeshの有効範囲を指定の数された数で領域を分割します。ここの数字が128の場合、128\*128\*128の最大2,097,152パッケージを持つことになります。精度を高めるために使いやすいですが、通信サイズが大きくなりやすくなります。最大値は255になります。**Area Range**の精度と比較しながら変更すると良いでしょう。

* **FrameInterval**は送信する秒間フレーム数です。0.1から変更することはあまりありません。

* **Subframes Per Keyframe**はキーフレームの間に差分フレームをいくつ挿入するか指定できます。ここが4で**FrameInterval**が0.1のとき、秒間にキーフレームを2ずつ、差分フレームを8ずつ送る計算になります。0のときはキーフレームのみの送信になります。

* **Combined Frames**は保存するフレームデータのサイズになります。**FrameInterval**が0.1、**Combined Frames**が100の時、10秒ずつのデータをファイルとして保存します。

### 受信側
1. 任意のGameObjectに**MeshReceiver**のスクリプトを追加します。このとき、自動的に**STMHttpSerializer**が追加されます。

2. **STMHttpSerializer**のAddressにサーバーのアドレスを指定します。初期値はローカルホストのテスト用サーバーが設定されます。その場合、"http://127.0.0.1:8080/channels/"までがサーバーのアドレスになります。

3. **STMHttpSerializer**のChannelに任意のチャンネル名を指定します。**MeshSender**と同じチャンネル名を指定しておく必要があります。指定しない場合、ランダムのチャンネルが設定されます。

#### オプション

* **Refer From Serializer**にチェックが入っている場合、STMHttpSerializerで設定したサーバー、チャンネルの設定が利用されます。もしSTMHttpSerializerの設定を利用しない場合は、ここのチェックボックスを外します。

* **Stream File**はサーバー側に設置されている、StreamingMeshを表示するために必要なファイル名になります。**Refer From Serializer**にチェックが入っている場合、STMHttpSerializerに設定されたサーバーとチャンネル内のファイルを探します。**Refer From Serializer**のチェックが外れている場合は、"http://..."から絶対パスを指定する必要があります。

* **Stream Refresh Interval**はストリームファイルを読み込む感覚になります。この数値の目安は、**STMHttpSender**に設定された**FrameInterval**に**Combined Frames**を掛けた秒数となります。(FrameInterval = 0.1, Combined Frames = 100のとき、 0.1 * 100 = 10)

* **Interpolate Frames**はフレーム間の線形補完を有効にします。この効果は非常に強力です。増やすほどフレームごとに線形補完される数が増えますが、処理が重くなりますので、2~10ぐらいの間で調整するのが良いでしょう。0の時はフレーム保管を行いません。

* **Default Shader**は指定のないすべてのマテリアルに対して当てはめるシェーダーです。デフォルトはテクスチャのみ、陰影なしのシェーダーが設定されています。

* **Custom Shaders**には設定した名前にマッチしたマテリアルに対して、特定のシェーダを当てはめることができます。Sizeを0から必要な数に変更し、Material Nameに対象のマテリアル名、Shaderに当てはめたいShaderを指定してください。

## ライセンスについて
StreamingMeshで使用されている一部のソースコード(SerializableDictionary.cs)は著作権者が異なりますので注意してください。自分が書いたものはすべてクリエイティブコモンズ 表示 - 継承 4.0 国際 (CC BY-SA 4.0) にしています。   
https://creativecommons.org/licenses/by-sa/4.0/deed.ja

また、テスト用のシーンとして、Examplesフォルダ内のLocalHTTPTestにはSDユニティちゃん(© Unity Technologies Japan/UCL)を使用しています。   
詳しくはAssets/UnityChan/License/UCL2.0フォルダ内のライセンス条項をお読みください。
