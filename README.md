# StreamingMesh

Unityのメッシュ・音声ストリーミング実装と、UnityChan KAGURAを使った動作確認用サンプルです。

## ディレクトリと権利の範囲

| 場所 | 内容 | ライセンス文書 |
| --- | --- | --- |
| `Assets/StreamingMesh/`、`Assets/Plugins/`、`Tools/`、`Docs/` | StreamingMesh本体、開発ツール、ドキュメント | ルートの `LICENSE`（MIT）。UnityChanのUCLはこれらに適用されません。 |
| `Assets/Samples/SyncDiagnostic/` | 1秒ビープと通常Mesh／SkinnedMeshの同期診断デモ | ルートの `LICENSE`（MIT）。UnityChan素材は含みません。 |
| `Assets/Samples/UnityChanKAGURA/` | 取り込んだキャラクター、モーション、楽曲、デモシーン | 同ディレクトリの `License/UCL2.0/` と `README.md` を参照。楽曲の追加条件は未確認です。 |
| `Packages/com.unity.springbone/` | KAGURAサンプルが使うSpringBone | 同パッケージの `LICENSE` / `LICENSE.md`（MIT）を参照。 |
| `Packages/com.unity.universaltoonshader.urp/` | KAGURAサンプルが使うToon Shader | 同パッケージの `LICENSE.md`（Unity Companion License）を参照。 |
| Androidビルド時に取得するAndroidX Media3 | fMP4 HLS音声再生 | `Docs/MEDIA3_LICENSE_APACHE_2.0.txt`（Apache License 2.0）を参照。 |

ルートのMITライセンスはStreamingMesh本体と本プロジェクトで作成したコード・ドキュメントに適用します。`Assets/Samples/UnityChanKAGURA/` に取り込んだ原版素材と `Packages/` の同梱パッケージには適用されません。各ディレクトリのライセンスは、別のディレクトリのコードや素材へ自動的に拡張されません。サンプル素材を含む成果物を公開・再配布する前に、楽曲を含めて適用条件を確認してください。

## 動作確認

送信デモは `Assets/Samples/UnityChanKAGURA/Scenes/KaguraDemo.unity`、受信デモは同じ場所の `KaguraReceiver.unity` です。ローカルサーバーとWeb・iOS・Androidビルドの手順は `Assets/Samples/UnityChanKAGURA/README.md` と `Docs/WEB_TESTING.md` を参照してください。

収録時刻の切り分けには `Assets/Samples/SyncDiagnostic/` の独立デモを使用します。60目盛の時計針が1秒で右回りに1周し、12時を通るたびにビープが鳴ります。受信画面で目視・聴取するほか、配信ファイルからも時刻差を数値比較できます。手順は同ディレクトリの `README.md` を参照してください。

旧SDユニティちゃんのサンプルシーンと素材は削除しました。過去の検証記録は `Docs/` に残しています。

受信Meshの接線はGPU／CPUで再計算できます。Receiverの `Tangent Mode` で有効化し、`Auto` では `Tangent Material IDs` に接線が必要な配信マテリアルIDを登録します。設定後は再接続してください。詳細と検証範囲は[Receiverの接線再構築](Docs/RECEIVER_TANGENTS.md)を参照してください。

KAGURA ReceiverはURP ToonでSenderのマテリアル設定を復元します。更新後はSenderでCreate Channelと録画を作り直し、Receiverで再接続してください。テンプレート設定と送信対象は[Receiverのマテリアル復元](Docs/RECEIVER_MATERIALS.md)を参照してください。

Receiverは上限付きのリングバッファとチャンク・頂点スナップショットの再利用で、フレームごとの管理ヒープ割り当てを抑えます。WebサンプルのWASMメモリ上限は4096MBです。頂点の保持数は標準3ファイルで、ReceiverのInspector／`ConfigureBuffering`／KAGURAの`Buffer settings`から変更できます。Web音声の先読みも標準3ファイル、1〜16で変更でき、過去の保持秒数は別に指定できます。バッファの所有権、制限と割り当て検証は[Web受信側のメモリ管理](Docs/WEB_TESTING.md#receiverのメモリ管理)を参照してください。

現在のチャンネル形式はv6です。JSONプロパティをlowerCamelCaseへ統一し、`stream.stma.sequence`を削除しました。旧チャンネルは再作成してください。形式と収録中の追記再生の前提は[ストリーム形式](Docs/STREAM_FORMAT.md)を参照してください。

## TimeWireモジュール

共通クロックの独立パッケージ[TimeWire](https://github.com/blkcatman/TimeWire/blob/main/README.ja.md)と、任意に追加できる[NTP入力](https://github.com/blkcatman/TimeWire/blob/main/NTP/README.ja.md)・[OSC時計ホスト／入力](https://github.com/blkcatman/TimeWire/blob/main/OSC/README.ja.md)を、GitHubからUPMで取得します。各パッケージのライセンスはMITです。隣接するTimeWireフォルダは不要です。

`Packages/manifest.json`には以下のGit参照を設定しています。解決したコミットは`Packages/packages-lock.json`に保存されるため、両ファイルを共有してください。

```json
"com.timewire.core": "https://github.com/blkcatman/TimeWire.git?path=/Core#main",
"com.timewire.ntp": "https://github.com/blkcatman/TimeWire.git?path=/NTP#main",
"com.timewire.osc": "https://github.com/blkcatman/TimeWire.git?path=/OSC#main"
```

NTPはUTC、OSCはホストの単調増加時計を取得します。OSCのUDP／TCPにはTimeWireの公開4時刻交換仕様を使用します。モジュールの追加だけでは既存Sender／Receiverの時計は切り替わりません。収録・再生への統合状況と導入手順は[共通クロック設計](Docs/CLOCK_SYNCHRONIZATION.md)と[外部通信モジュール](Docs/TIMEWIRE_OPEN_TRANSPORTS.md)を参照してください。

Senderの既定期限判定、DSP／PCMの収録原点、SyncDiagnosticの取得時刻での姿勢評価、KAGURAのDSPClock TimelineとTransportをTimeWireへ接続しています。60fpsの実収録データの比較と、残る固定差は[ローカル検証結果](Docs/TIMEWIRE_LOCAL_VALIDATION.md)を参照してください。
