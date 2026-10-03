# TimeWire 公開前のコード出所確認

確認日: 2026-10-01。対象は隣接するTimeWire、TimeWire.Ntp、TimeWire.Osc各0.1.0のローカルファイル。分離したTimeWire.ArtNet 0.1.0に関する過去の確認も残す。GitHubへの公開は実施していない。

## 現在の利用構成

Art-NetはTimeWire本体から分離した上で、StreamingMeshの依存とテスト登録からも解除した。TimeWire.ArtNetの独立フォルダは現時点の本体・プロジェクトに取り込んでいない。以下には、分離前とプラグイン検証時の出所確認の履歴を残す。

現在は本体と、独立したNTP／OSCプラグインを参照する。NTPはRFC 5905のパケット記述、OSCは公式OSC 1.0のメッセージ記述に基づく新規実装であり、外部ライブラリ、仕様文書、RFCのコード付録を同梱しない。OSCのアプリケーションメッセージとPython標準ライブラリの検証クライアントも今回作成した。全3パッケージをMITとし、各プラグインの依存は`com.timewire.core`のみである。

追加分は本体の`ClockExchange.cs`、NTPのRuntime 3件／Tests 2件、OSCのRuntime 5件／Tests 2件とPython 1件。本体のTransportとテストは初回外部時刻の取得待ちに対応させた。公開パッケージには元プロジェクト名、顧客名、個人の絶対パスを記載しない。Unityと.NET、テストのNUnitをAPIとして参照し、それらの実装やバイナリは取り込まない。OSCプロトコルの利用条件と、公式サイトの文書コンテンツに適用されるCC BY 4.0をREADMEで区別した。

この追加後のUnity検証はEditMode 38件、PlayMode 3件が成功した。以下のArt-Net分離時の件数・ハッシュは当時の履歴であり、現在のファイル一覧・ハッシュではない。

## ユーザー確認と分離後の構成

ユーザーから「自分が権利を持つが、コード自体は第三者に提供している」との回答を受けた。この権利者の申告を前提としてMIT公開の準備を進める。参照元に関する本確認メモはStreamingMesh内に保存し、公開する両パッケージに元プロジェクト名や顧客名を含めない。コードは新規構成で実装しており、参照元のファイル名・コメントをそのまま残していない。

Art-Netは別UPMプラグイン`com.timewire.artnet`へ移した。本体`com.timewire.core`にはRuntime 10件とTests 2件のC#を残し、プラグインにはRuntime 3件とTests 1件を置く。プロトコル表記とOEM条件はプラグインのREADME／THIRD_PARTY_NOTICES.mdへ集約する。本体の外部依存は.NET／UnityのAPIとテストのNUnit参照のみで、実装を同梱しない。

分離後の検証では、プラグインを外した状態で本体がコンパイルされ、EditMode 16件が全件成功した。プラグイン追加後もコンパイルされ、PlayMode 5件が全件成功した。本体のC#にはネットワークAPIやArt-Netアセンブリ参照がない。

以下の照合内容は分離前の16件のC#に対して実施した検査の記録である。Art-Net部分は内容とGUIDを維持して別フォルダへ移した。

## 結果

第三者ライブラリのソース一式、DLL、SDK、モデル、画像、楽曲の同梱は確認されなかった。TimeWireはこのセッションで作成したC#実装であり、参照元VPSyncのファイルをそのままコピーして名称変更したものではない。ただし、VPSyncのコードを読んで同期方式とArtDmxペイロードの設計を参考にしている。クリーンルーム実装とは主張しない。

公開可否の判断では、同梱物の検査と、参照元コードの権利関係を分ける必要がある。参照元の権利者はユーザー自身との申告を受けた。過去の第三者への提供履歴や契約の内容をコード検査で確認したものではない。

## 確認範囲と方法

- 公開用表記の補完前の全62ファイルを確認した。C#は16ファイル（Runtime 13、Tests 3）、asmdefは6ファイル。その他はpackage.json、MIT、README、CHANGELOG、Unityのmetaファイルである。
- パッケージ内にバイナリやシンボリックリンクはなかった。外部ディレクトリをリンクして取り込む構成もなかった。
- package.jsonのdependenciesは空。asmdefの参照はTimeWire内部のみで、UnityのAPI参照とテストフレームワーク参照を使用する。
- 参照元`SONY_PCL_XREAL_VPSync/UnityProject/Assets/VPSync`のC#と照合した。同一内容のファイルはなく、長い一致コードブロックは確認されなかった。共通する箇所はDMXの値域・オフセットの検査、Unityコンポーネントの取得、using宣言などである。ハッシュや文字列一致の検査は、著作権上の独立性を証明するものではない。
- ローカルの参照元には権利者を示すヘッダー、LICENSE／NOTICE、Git履歴が見当たらなかった。作者、業務委託、職務著作、契約上の公開制限、元コードのさらに上流の出所は確認できない。
- パッケージのテキストには元プロジェクト名、個人の絶対パス、LANアドレス、GitHubトークン形式、秘密鍵ヘッダーがなかった。これは限定したパターン検査であり、秘密情報の不在を網羅的に証明するものではない。

## 実装と参照元の関係

| TimeWireの対象 | 作成・参照の関係 | 確認事項 |
| --- | --- | --- |
| Coreの5ファイル | 参照時計の直接読み出し、基準点からの時刻計算、観測値の検査、Transport、フレーム期限を新規実装 | HybridTimecodeProviderの自走・速度補正・大きな差の補正という方式を参考にした。処理と状態管理は異なる |
| Unityの5ファイル | 共通のClockSourceBehaviour入力、監視、Transport、Playable／AudioSourceアダプターを新規実装 | TimelineSyncControllerとAudioSyncControllerの追従方式を参考にした。Manual評価、世代番号、ループ境界、Pause/Resumeなどを追加 |
| ArtNetの3ファイル | ArtDmx解析、時刻変換、設定とUDP受信を新規実装 | ArtNetReceiverとTimecodeParserの通信方式・4チャンネル対応を参考にした。受信時刻、順序検査、最新観測のみの受け渡しを追加 |
| Testsの3ファイル | 新規の疑似クロック、実Playableの姿勢評価、UDPループバックのテスト | NUnit／UnityのAPIを参照する。フレームワーク本体のソースやDLLは同梱しない |

XREAL SDK、Input System／TMPのUIコード、UnityChanアセット、StreamingMeshのコーデック、ネイティブ音声プラグインはTimeWireに取り込んでいない。.NET／Unity／NUnitのAPIを呼び出すことと、それらの実装をコピーして再配布することは区別した。

## Art-Netの表記と製品条件

Art-Netの公式サイトはロイヤルティ不要のプロトコル利用に条件を設けており、製品のユーザーガイドに指定のクレジットを要求している。また、Art-Netを実装する製品についてOEMコードを要求している。[Art-Net公式条件](https://art-net.org.uk/)、[OEMコード案内](https://art-net.org.uk/oem-code-zone/)。

不足していたクレジットを補完し、分離後はTimeWire.ArtNetのREADMEとTHIRD_PARTY_NOTICES.mdへまとめた。SDK、仕様PDF、ロゴ、OEM一覧をコピーしたものではなく、MIT対象の実装とプロトコル所有権・利用条件を分けて記載した。OEMコードの取得や本SDK単体への条件の適用は未確認であり、取得済み・規格適合済みとは表記していない。

## 公開の前提

参照元の権利者はユーザー自身であるとの回答と、両パッケージへのMIT指定の承認を前提とする。追加の第三者ソース一式やSDKの同梱は確認されなかった。元プロジェクト名・顧客名・個人の絶対パスは公開対象パッケージに含まれない。

参照元にライセンスがないこと自体は、自由な再利用の許諾を意味しない。今回はユーザーの権利者としての申告を根拠とする。[GitHubのライセンス説明](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)。

## 監査対象のC#ハッシュ

以下はArt-Net分離前の照合時点のハッシュである。現在はNTP／OSCの追加と本体の変更があるため、現在の完全な監査一覧ではない。

```text
9787032cac6647dd136c3b9f1d046f608f277e1902dda70739fb8c072ced6163  Runtime/ArtNet/ArtDmxTimecode.cs
79c05b0f67fc7f5b74c016b1fa07483969512bbedd26af66291d15546ffecde1  Runtime/ArtNet/ArtNetClockReceiver.cs
bf5d3c2f2be319853aecf30722808097a06d6b5703c6388793dffea2eb375ea1  Runtime/ArtNet/ArtNetTimecodeConfig.cs
8a2302987c1aa13550431ae0c393befb0c02055235e1d421431507de8fedf46b  Runtime/Core/ClockContracts.cs
3ac9d7e5c6f88de86b4909767ad6d46de587e06020fe8fdd4e50695737f0262d  Runtime/Core/FrameSchedule.cs
0c9bce85d2560e3f986ddada7ab887474d0eb8aafe21a2505e843ea8775730b0  Runtime/Core/HybridClock.cs
a7253464af4000ede3996fbb349c22be66a70ec516f4f6594b8ea1fd3630357b  Runtime/Core/PositionMath.cs
89d62db8fe5746ed61b1bcabc5b7178154bb2084ab332897686d19c3ed4cd3b7  Runtime/Core/TransportClock.cs
0b7bb14804c7e12939e8cd8936836d8527b0d1aa235d4a22f64f4a5daae3cc98  Runtime/Unity/AudioClockController.cs
d8995b4a8f4adadcbd740de95ef8471fd4da011e82e6147cc636a332acee8bfa  Runtime/Unity/ClockSourceBehaviour.cs
8450e92d2a7153822cacfcd8976c61dc949ebab7ef42672d317d501d2be1db00  Runtime/Unity/HybridClockSource.cs
5835a7e4f3bb4af623c8267f000fd641ac3ee09bddc1de912c9ac806ac65de6b  Runtime/Unity/TimelineClockController.cs
37c2c287a61b71e5096a2f11491424b77d7a4c66e975632042cce99ee4468c87  Runtime/Unity/TransportClockSource.cs
39b2aa1ccc60991742f3b9fe63173db1fb9d766a42b1ae8fce7eb044c28751e8  Tests/Editor/Core/ClockTests.cs
7fe9c35b7b09b4408f4f04340ef8ce191217da7de50d548e10df1829133b4f5d  Tests/Editor/Unity/UnityAdapterTests.cs
da401f6993a875dd2c562b60d422ffd42da48fd54a10a34d06da9bc328fff474  Tests/Runtime/ArtNet/ArtNetTests.cs
```
