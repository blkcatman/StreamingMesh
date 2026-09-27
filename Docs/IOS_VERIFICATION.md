# iOS実機動作確認

この文書は旧SDユニティちゃんデータを使った2026-09-26時点の検証記録です。現行のKAGURAサンプルの手順と結果は `Assets/Samples/UnityChanKAGURA/README.md` と `Docs/RECEIVER_GPU_PIPELINE.md` を参照してください。

確認日: 2026-09-26

## 判定

**iPhone 12 Pro上で、StreamingMeshのメッシュ受信・復号・描画と、AVFoundationを使うfMP4/AAC再生経路が動作する。**

## 環境

- iPhone 12 Pro、iOS 26.6.1
- Unity 6000.6.3f1、IL2CPP、Metal
- macOS上の同梱 `Tools/streamingmesh_dev_server.py` からローカルネットワーク経由で配信
- 約78秒のUnityChanメッシュとAAC-LC音声

## 実測結果

| 対象 | 結果 |
| --- | --- |
| iOSビルド | UnityからXcodeプロジェクトを出力し、実機向け署名ビルドに成功 |
| インストール・起動 | iPhone 12 Proへインストールし、Unity Playerを正常起動 |
| メッシュ受信 | `stream.json`、`stream.bin`、`stream.stmj`、全16個の`.stmv`をHTTP 200で取得 |
| 描画 | iPhoneミラーリング上でUnityChanメッシュを確認 |
| アニメーション | 時刻の異なる画面フレームで姿勢変化を確認 |
| fMP4/AAC | `audio.m3u8`、`audio-init.mp4`、全77個の`.m4s`をAVFoundationからHTTP 200で取得 |
| 継続動作 | `stream.stmj`の5秒間隔ポーリングが継続し、実行時例外は発生しなかった |

Apple向け音声プレイヤーは`AVPlayer`の再生時刻をUnityへ返し、Receiverはその時刻をメッシュ再生クロックとして使用する。実機試験ではメッシュの姿勢が時間経過に伴って更新され、音声用HLSプレイリストとすべてのfMP4セグメントがAVFoundationから読み込まれた。

## 補足

- ローカルHTTP配信による試験では、検証ビルドにApp Transport Securityの許可とローカルネットワーク利用説明を設定した。本番のHTTPS配信では任意HTTP許可は不要。
- テストシーンではモデルが画面中央に小さく表示された。受信・復号の問題ではなく、カメラ距離またはモデルスケールの調整対象である。
- 音声セグメントの取得とAVFoundation再生経路は確認した。自動試験では端末スピーカーの音を測定していない。
