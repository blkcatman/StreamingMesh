# StreamingMesh

Unityのメッシュ・音声ストリーミング実装と、UnityChan KAGURAを使った動作確認用サンプルです。

## ディレクトリと権利の範囲

| 場所 | 内容 | ライセンス文書 |
| --- | --- | --- |
| `Assets/StreamingMesh/`、`Assets/Plugins/`、`Tools/` | StreamingMesh本体と開発ツール | このリポジトリには本体を包括的に許諾するLICENSEファイルはありません。UnityChanのUCLはこれらに適用されません。 |
| `Assets/Samples/UnityChanKAGURA/` | 取り込んだキャラクター、モーション、楽曲、デモシーン | 同ディレクトリの `License/UCL2.0/` と `README.md` を参照。楽曲の追加条件は未確認です。 |
| `Packages/com.unity.springbone/` | KAGURAサンプルが使うSpringBone | 同パッケージの `LICENSE` / `LICENSE.md`（MIT）を参照。 |
| `Packages/com.unity.universaltoonshader.urp/` | KAGURAサンプルが使うToon Shader | 同パッケージの `LICENSE.md`（Unity Companion License）を参照。 |

各ディレクトリのライセンスは、別のディレクトリのコードや素材へ自動的に拡張されません。サンプル素材を含む成果物を公開・再配布する前に、楽曲を含めて適用条件を確認してください。

## 動作確認

送信デモは `Assets/Samples/UnityChanKAGURA/Scenes/KaguraDemo.unity`、受信デモは同じ場所の `KaguraReceiver.unity` です。ローカルサーバーとWeb・iOSビルドの手順は `Assets/Samples/UnityChanKAGURA/README.md` と `Docs/WEB_TESTING.md` を参照してください。

旧SDユニティちゃんのサンプルシーンと素材は削除しました。過去の検証記録は `Docs/` に残しています。
