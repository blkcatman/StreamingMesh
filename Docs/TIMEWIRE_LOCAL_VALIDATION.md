# TimeWire ローカル導入と同期検証

2026-10-03、macOS / Unity 6000.6.3f1で実施した。検証時はTimeWireを`Packages/manifest.json`から`file:../../TimeWire/Core`で参照した。現在はCore／NTP／OSCをGitHub参照へ切り替えている。NTP／OSCは従来からmanifestに登録されているが、この検証では通信クロックを使わない。StreamingMeshのコードはCoreとUnityラッパーだけを参照する。

## 導入した接続

- Senderの既定経路はTimeWireの`FrameSchedule`でキャプチャ期限を判定する。`Time.deltaTime`を積算しない。Source未指定の場合はStopwatchに基づくローカル時計を使う。
- サンプル3シーンのSenderは`AudioDspClockSource`を使う。AudioListenerの音声スレッドでDSP時刻と単調増加時計の受領時刻を観測し、その間をメインスレッドのHybridClockで補間する。初回観測まではAcquiring、途絶・リセット時には停止または世代変更を通知する。
- 最初に録音キューへ受け付けたPCMブロックのDSP時刻を保持し、メッシュの時刻ゼロにも対応付ける。FFmpegのプロセス起動時刻を音声時刻ゼロにしない。
- SyncDiagnosticはSenderの`BeforeCapture`で針の姿勢を評価し、同じ時刻をPTSにする。通常MeshとSkinnedMeshで共通の経路を使う。任意の既存アニメーションの評価時刻まで自動で変える機能ではない。
- KAGURAはTransportでPlay／Pause／Restartを管理し、TimelineはDSPClockを使う。AudioTrackのネイティブ再生速度を維持するため`Preserve Native Rate`を有効にし、位相差による速度補正を使わない。600ms程度の一時的な停止を即時シークと誤認しないよう、大きな位置補正の閾値を1秒にする。

GPUやパケット化を待っている間も時計は進む。待ち明けには現在の姿勢を取得し、欠けたキャプチャ枠を数える。取得済みフレームのシーケンスと差分依存は連続したままで、過去の姿勢や一定60枚／秒の処理能力を保証しない。

## 収録データの比較

両方式とも60fps、サブフレーム9、Combined Frames 300（目標5秒）で通常Meshを30秒収録した。TimeWireのSkinnedMeshは同じ設定で120秒収録した。SkinnedMeshの検証ではSourceの論理オフセットを+125msに設定し、`FromDspTime`／`ToDspTime`を介してPCMと予定再生を対応付けた。この設定はPlay Modeだけに適用し、保存シーンのオフセットは0のままとした。

解析は実際の`.stmv`から針の頂点を復元し、各PTSで線形補間した12時通過と、AACをデコードした波形のBeep開始を比較する。音声は1msの窓で測定する。最初は針が12時に静止しているため、そのBeepは通過時刻の比較から除外する。差をフレーム間隔で剰余にしない。

| 条件 | 比較イベント | 視覚−音声の中央値 | 差の範囲 | 最初と最後の差の変化 |
| --- | ---: | ---: | ---: | ---: |
| 既存方式・通常Mesh・30秒 | 29 | +45.6ms | +35.9〜+136.4ms | −1.9ms / 28秒 |
| TimeWire・通常Mesh・30秒 | 29 | −20.9ms | −21.3〜−19.0ms | −2.2ms / 28秒 |
| TimeWire・SkinnedMesh・120秒 | 119 | −19.5ms | −20.5〜−17.9ms | +1.0ms / 118秒 |

正の値は針が音声より遅く12時を通過することを示す。この比較では、収録時刻と姿勢の対応付けによりばらつきが減った。2分間の測定で大きな蓄積遅れは見られなかったが、長時間の誤差上限を保証する結果ではない。

約19〜21msの固定差は残る。AACの1024サンプル（48kHzで21.333ms）のprimingと整合するが、この結果だけで原因を確定しない。今回のfMP4先頭はPTS=0、1パケット1024サンプルで、ffprobeのinitial_paddingは0だった。元PCMと復号PCMを別途突き合わせ、エンコーダーの先頭処理と音声プレゼンテーション時刻を整理する必要がある。推測した固定値を時刻へ足して、同期が解消した扱いにはしていない。

通常MeshのTimeWire収録では20枠、SkinnedMeshでは3枠のキャプチャが欠けた。実際の最大PTS間隔はそれぞれ322ms、36msである。時計の同期改善はGPU、EditorやGC等による処理停止を解消するものではない。

## Timelineの処理停止テスト

同じKAGURAシーンで40秒再生し、20秒の時点でメインスレッドを600ms停止した。停止前の15〜19秒と、停止後の23〜38秒の時計差の中央値を比較した。今回のTimeWireテストはローカル単調増加Source／Transportと、DSPClockのTimelineを組み合わせる。DSPに切り替える効果とTimeWireによるセッション管理を分けて解釈する。

| 条件 | Director−単調増加時刻の変化 | 元楽曲PCMとの相関 | PCMのサンプル原点の変化 |
| --- | ---: | ---: | ---: |
| GameTime・TimeWireなし | −277.74ms | 1.0（4箇所） | 0サンプル |
| TimeWire Transport＋DSPClock、ネイティブ音声速度維持 | −15.87ms | 1.0（4箇所） | 0サンプル |

メインスレッドから読むDSPには1024サンプル／48kHz＝21.333msのブロック単位の揺れがある。−15.87msを蓄積ドリフトや時計の精度限界と解釈しない。実PCMの4・10・24・30秒位置を波形で照合すると、GameTimeでは停止後にTimelineが音声より約279ms遅れた。採用構成では停止前の約14msから停止後の約19msの差となり、278msの残留遅れはなかった。これはコールバック時刻に対応するTimeline値で、画面・スピーカーの時刻ではない。

GameTimeのままTimeWireからグラフ速度を補正する方式も試した。この方式では時計差の変化は約0.3msに減ったが、元楽曲PCMとの相関は0.48〜0.68へ低下した。音声速度やシークの影響を否定できないため採用しなかった。解析の`pcmCorrelationVerified`は各照合が0.95以上の場合だけtrueにする。`complete`は測定が最後まで終わった印であり、同期合格の印ではない。

## KAGURAの1曲分

保存シーンと同じAudioDspClockSourceを入力にして、DSPClock／Preserve Native Rateの構成でTimelineの全長287.6秒を再生した。SenderとFFmpegの収録はこの測定では有効にしない。ListenerのPCMと、楽曲の4・10・120・128・246・254秒の参照波形6箇所の相関はすべて1.0だった。

PCMのサンプル原点の範囲は1.708ms（82サンプル）で、コールバック時刻に写像したTimeline−音声の差は約3.1〜15.7msだった。OSの単調増加時計−DSPの差は音声スレッドの30秒区間中央値で約65.2ms変化した。変化にはブロック単位の段差もあるため、線形回帰のppmをそのまま発振器の周波数誤差としない。OS時計の単独利用だけで音声との長時間同期が成立するという結果ではない。

この構成ではDSPの継続観測とネイティブ音声時計を使うため、GameTimeのフレーム上限に由来する大きな残留遅れを収録時計へ持ち込まない。長時間の保証、外部時計との音声リサンプリング、KAGURAの全楽曲をFFmpegで収録した場合のAAC／メッシュの比較は別途必要である。

## KAGURAの配信用収録とiPhone再生

`timewire_KAGURA_20261003`へ、保存シーンの30fps・サブフレーム9・Combined Frames 300（目標10秒）でTimeline全長287.6秒を収録した。AudioDspClockSource、DSPClock、Preserve Native Rateを使用し、論理オフセットは0。終了後にGPU readback、圧縮、FFmpeg、アップロードの完了を確認してPlay Modeを終了した。

- メッシュ29チャンク、8,609フレーム。シーケンスは連続、実フレームのPTSは厳密に増加。最終PTSは287.5756042秒。
- 音声29チャンク、13,805,568サンプル＝287.616秒。サンプル位置は連続し、FFmpegでの復号エラーはなかった。
- 1秒間隔の監視ではクロックは同じ世代のLockedを維持した。キャプチャ枠の欠落は19、最大PTS間隔は295.125ms。収録のかくつきが完全に解消した結果ではない。
- 保存したAACを楽曲の4・10・120・128・246・254秒の参照波形と照合すると、相関は0.986〜0.998。基準Timeline位置からの音声のオフセットは全6箇所で2,045サンプル（約42.604ms）で、測定位置間の変化は0サンプルだった。これは保存音声の固定位置差の測定であり、各メッシュ姿勢と音声、実機の画面とスピーカーの差そのものではない。

収録直後の音声プレイリストには終了マーカーがなく、iOSでは再生開始後に音声位置が約260秒へ移り、メッシュの追いつきを待つ状態になった。今回の収録済みデータの`audio.m3u8`を`EXT-X-PLAYLIST-TYPE:VOD`と`EXT-X-ENDLIST`で確定し、既存のiPhone「STM Sync」を再起動した。13メッシュ・4テクスチャを読み込み、冒頭から再生位置が進み、ログ上の音声位置・メッシュ目標位置・提示位置の一致を確認した。収録停止時にプレイリストを自動で確定する実装は、この作業では変更していない。

測定は`Logs/TimeWireKaguraCapture_20261003.csv`、`Logs/TimeWireKaguraCapture_20261003_validation.json`、実機ログは`Logs/iPhoneTimeWireKaguraPlaybackVod_20261003.log`に保存した。ユーザーの実機視聴では「ズレがあるようにはあまり感じない」との結果だった。これは体感上の確認であり、画面とスピーカーの物理的な出力時刻は測定していない。Senderの処理遅延とヒープ再利用の検討は`SENDER_BUFFER_REUSE.md`に記載する。

## 検証の範囲

コンパイル、TimeWireのEditMode 75件（Core／Unity／拡張契約41、NTP 16、OSC 18）、PlayMode 3件、解析のPythonテスト4件が成功した。ネイティブレートを維持するTimelineがSourceの速度補正に追従せず、一時停止は反映する回帰検証を追加した。

Editor内の収録データと時計を測定し、今回のKAGURAデータを既存のiPhoneアプリで再生した。iOS／Android／Webの新規ビルド、スピーカーと画面の物理的な出力タイミングは検証していない。音声付きReceiverは既存の音声プレイヤー位置を基準に再生する。音声なしReceiverのdeltaTime経路は今回TimeWireへ移していない。OS時計とDSPの発振器周波数比の推定、音声リサンプリング、外部時計での再生制御も今後の項目である。

DSP入力ではAudioListener.pause中に時刻とRate=0を保持し、再開時に世代を更新して再取得する。実Editorの600ms停止試験で時刻差=0、再開後にAcquiringからLockedへ復帰することも確認した。保存したKAGURAシーンでもRestart→Pauseで600ms待って位置変化=0、DirectorのPaused→Playingへの復帰を確認した。

## 再実行

1. **Tools > StreamingMesh > TimeWire > Configure Local Sample Clocks**をEdit Modeで実行する。設定済みの3シーンを保存するメニューである。
2. サーバーを`python3 Tools/streamingmesh_dev_server.py --host 127.0.0.1 --port 8000`で起動する。
3. SyncDiagnostic SenderをPlay Modeにし、比較用の新しいChannel名を選ぶ。Frame Rate=60、Subframes Per Keyframe=9、Combined Frames=300に設定し、Create Channel完了後にRecord from Startを実行する。停止後は音声とメッシュの最終アップロードが終わってからPlay Modeを終了する。
4. 以下のコマンドで実データを測る。旧方式との比較は、収録開始前にUse TimeWire Clockを無効にして別Channelへ収録する。

```sh
python3 Tools/analyze_sync_diagnostic.py \
  --channel DevData/channels/timewire_sync_mesh_20261003 \
  --report Logs/TimeWire_SyncMesh_20261003.json
python3 Tools/analyze_sync_diagnostic.py \
  --channel DevData/channels/timewire_sync_skinned_20261003 \
  --report Logs/TimeWire_SyncSkinned_20261003.json
```

Timelineの比較は**Tools > StreamingMesh > KAGURA > Verify Game Clock With Hitch**と**Verify TimeWire Clock With Hitch**、全楽曲の測定は**Verify TimeWire Full Song**を使う。測定用のクロックはPlay Mode内だけに作り、保存シーンのコントローラーから分離する。`Logs/KaguraClockComparison_*`に時計CSVとListener PCMを出力し、`Tools/analyze_kagura_clock_comparison.py`で参照楽曲の波形と照合する。CSVのPlayable値だけで音声同期を判定しない。

## 保存した測定データ

- `Logs/KaguraClockComparison_20261003_022451/`：既存GameTimeの処理停止テスト。
- `Logs/KaguraClockComparison_20261003_023618/`：採用しなかったグラフ速度補正の実験。
- `Logs/KaguraClockComparison_20261003_025431/`：DSP入力を使用したTimeWireの1曲分測定。
- `Logs/KaguraClockComparison_20261003_025134/`：ネイティブ音声速度を保つTimeWire／DSPClockの処理停止テスト。
- `DevData/channels/baseline_sync_mesh_20261003/`：既存方式の通常Mesh。
- `DevData/channels/timewire_sync_mesh_20261003/`：TimeWireの通常Mesh。
- `DevData/channels/timewire_sync_skinned_20261003/`：TimeWireのSkinnedMesh、120秒。
- `DevData/channels/timewire_KAGURA_20261003/`：TimeWireによるKAGURAの1曲分収録、287.6秒、音声プレイリストは収録後にVODとして確定。

収録アセットは開発サーバーの`DevData/channels`、測定CSV・Listener PCM・解析JSONは`Logs`に置く。どちらも実行時の測定データで、公開パッケージには含めない。元のサンプル配信Channelを上書きせず、比較用Channelを追加した。

## 2026-10-03：Int64・マイクロ秒への移行

TimeWire Core／NTP／OSC の実運用から BigInteger を除去し、時計内部を1µsへ統一した。NTPとOSCのwire形式は保持し、入力境界で量子化する。ClockRateはlongの整数比、doubleの利便性入力は1ppb。DSP変換を音声スレッドから外し、ネットワークの送受信領域も再利用する。

独立したBigIntegerのテスト用比較計算で100年、負の値、異なるtimebase、丸め、境界を検証した。ウォームアップ後の.NET測定でFromSeconds10,000回とObserve/GetSnapshot5,000回の確保量0byte、Unityの正のコントロール付きGC.Allocテストで時計パイプライン1,000回の確保イベント0件を確認した。保存先は `Logs/TimeWireInt64Migration/`。詳細は独立パッケージの `Core/INT64_CLOCKS.ja.md`。

Unity 6000.6.3f1のEditModeテスト92件がすべて成功した。DSP mailboxの取得・リセット、NTP／OSCのループバック通信とバッファ再利用も含む。Unityを使わない.NET側のCoreテスト48件も成功した。
PlayModeテスト3件も成功し、Unity以外の実運用コードは.NET Standard 2.1向けにエラー・警告なしでコンパイルできた。

今回の変更後のiPhone再収録／出力同期と、StreamingMesh全体のGC改善はまだ測定していない。以前のKAGURA録画の測定結果を、この変更後の実機検証として扱わない。

## 2026-10-03：Int64版でKAGURAを再収録

`DevData/channels/timewire_KAGURA_int64_20261003_101256/`へ、30fps・サブフレーム9・300枚結合で再収録した。AudioDspClockSource、TimelineのDSPClock、Preserve Native Rateを使用した。診断用のStopwatchで288秒経過したときに停止したため、最終メッシュPTSは287.311657秒、音声は287.36秒である。Timeline全長287.6秒の末尾約0.29秒は含まれないが、6.6秒から始まる277.24秒の楽曲は収録範囲内にある。

- メッシュ27チャンク・7,881フレーム。シーケンス連続、PTSは厳密に増加した。
- 音声29チャンク・13,793,280サンプル。音声インデックスは連続し、デコード後の長さも一致した。
- GPU readback、圧縮、FFmpeg、アップロードの終了を確認してから、`audio.m3u8`をVOD／ENDLISTとして確定した。LAN配信サーバーから13メッシュのメタデータを取得できる。
- 音声波形を楽曲の6箇所で照合すると相関は0.988〜0.998程度。冒頭の位置差+31.375msが途中から−44.729msへ変わり、76.104msの段差がある。同期良好や累積ドリフトなしという検証結果ではない。

計測結果は`Logs/KaguraSenderPerformance_int64_fullsong_20261003_101334/`に保存した。収録中のCLIによるEditor操作と診断配列の一度の拡張も計測に含む。最大約15.8秒のメインスレッド停止をSender固有の性能として扱わない。実機での出力同期はまだ測定していない。

## 同収録のAudio CPU調査

90秒以降のAudioRecorderマーカーが20msを超えた15箇所すべてで、同じ計測フレームにGCマーカーを確認した。通常の非ゼロAudioRecorderマーカーの中央値は約0.192ms。マーカーは前フレーム中の複数コールバックを合計した経過時間なので、1ブロックの独占CPU時間やProfilerのTotal Audio CPU値とは同一視しない。DSPのタイムスタンプ連続性違反は0件だったが、コールバック受信間隔の遅延と保存波形の段差は別に存在する。

`STMAudioRecorder.OnAudioFilterRead`は毎ブロックPCM配列を確保し、キューへ渡す際にlockを使う。FFmpegへのパイプ書き込みは別スレッドで、キューロックの外にある。したがってFFmpegを音声スレッドで直接実行している構造ではない。全体の確保量は約40.3MB/sであり、48kHz・stereo・16bitのPCMペイロード約0.192MB/sだけでは説明できない。GCによる停止は有力な原因候補だが、PCM変換・確保・lock待ちの個別計測と、Total Audio CPUの内訳との直接照合は未実施。詳細は同フォルダーの`audio-cpu-investigation.json`。

追加の波形照合は0.5秒窓を0.25秒間隔で実施し、1,091窓で高い相関を確認した。位置差は楽曲開始から約60秒で−59.896ms、約61.1秒でさらに−16.208ms変化した（合計−76.104ms）。その後は同じ位置差を維持した。この収録では近接した二つの永続的な変化であり、全曲を通じて頻繁に増える結果ではない。短い音切れや相関の低い区間の全異常を否定する計測ではない。詳細は`audio-step-scan.json`。

この調査時点のPCM配列・lockは、後続の`Pcm16RingBuffer`改修で除去した。改修内容と検証は`SENDER_BUFFER_REUSE.md`を参照する。
