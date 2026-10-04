# ライブ公開範囲とサーバーアーカイブの設計

2026-10-04。**設計仕様。以下のAPIと追従動作はまだ実装していない。**
現在動作しているv6は追記型目録を前提にする。この仕様へ移行するときは
Sender・サーバー・Receiverを揃えて更新する。旧データとの互換性は要求しない。

共通境界をファイルからフレームへ変更し、format・圧縮・通信を分離する方針は
[フレーム単位のストリームとプロトコル分離](STREAM_PIPELINE_DESIGN.md)に記す。
この文書のREST・JSON・stmv・HLSは最初のHTTP profileの仕様であり、Coreの必須wire形式ではない。

## 保存と公開の責務

Senderにアーカイブを保存しない。Senderは上限付きの送信待ちバッファを持ち、
サーバーの受領確認後に解放する。AACエンコードに必要な一時ファイルは完成版の
アーカイブではなく、送信完了後に片付ける対象である。

サーバーは収録中の全区間を一時保管する。そのうち最新の限られた範囲だけを
ライブ目録に公開する。完了時に`archive`を選ぶと全区間を永続化し、
`delete`を選ぶとチャンネルを公開から外して一時保管を削除する。

完了時まで選択を延期する以上、過去のデータを収録中に物理削除してはならない。
公開2〜3ファイルはサーバーのディスク使用量の上限ではない。
全区間をメモリに置く必要はなく、サーバーのディスク／オブジェクトストレージと
永続ジャーナルへ保存する。容量不足は送信失敗として扱い、完了扱いにしない。

| 層 | 収録中 | `archive`完了 | `delete`完了 |
| --- | --- | --- | --- |
| Sender | 未確認の送信データだけ | 送信バッファ・一時ファイルを解放 | 同左 |
| サーバー保存 | 全区間の一時保管 | 全区間・初期リソース・目録を永続化 | 配信猶予後に削除 |
| ライブ公開 | 最新の可変ファイル数 | 最後の範囲で終了、アーカイブURLを通知 | 未作成と同じ404 Not Found |
| Receiver | 範囲内の有限先読み | 現在時刻を維持してアーカイブへ移行 | 取得・再生を停止 |

過去ファイルの一時保管はライブURLの任意ファイル取得で公開しない。
公開中と配信猶予中のメディア、公開済み初期リソースだけを取得可能にする。
アーカイブ公開後は全区間を取得できる。認証情報や内部ジャーナルは配信対象外。

## 収録単位と完了API

チャンネル作成時にサーバーが一意な`recordingId`とpush tokenを発行する。
全アップロードと完了要求をその収録に結び付ける。チャンネル名を再利用しても
収録IDとメディアURLを再利用しない。古い要求が新しい収録を削除してはならない。

```http
POST /channels/channel_KAGURA/complete
Authorization: Bearer <push-token>
Content-Type: application/json
```

```json
{
  "recordingId": "<server-issued-id>",
  "action": "archive",
  "vertex": {
    "fileCount": 20,
    "lastFile": "000019.stmv",
    "lastSequence": 1799,
    "endTicks": 600000000
  },
  "audio": {
    "fileCount": 20,
    "lastFile": "audio-000019.m4s",
    "endSample": 2880000,
    "endTicks": 600000000
  }
}
```

`action`は`archive`または`delete`。例の数値は契約説明用であり、実際には
最終AAC fragmentのサンプル数と実際の頂点範囲を送る。音声なしは`audio: null`。
`stma`に不要な`sequence`を戻さず、音声はファイルIDと累積サンプルで照合する。

サーバーは収録IDとtokenを確認し、取り込みをロックした上で最終受領点を照合する。
アーカイブではファイルの実在・受領時ハッシュ・初期リソース・頂点sequenceの連続性・
音声sampleの連続性・目録と実データの対応を確認する。A/Vの終端が完全に同じ値である
ことは要求しない。末尾の短いfragmentや丸めを許容し、両トラックの実終端を記録する。
削除は欠損のある収録も片付けられるが、収録IDと権限の照合を省略しない。

サーバー内部の状態は`live → finalizing → archived | deleted`。`deleted`は公開しない。
Receiver向けに公開するstateは`live / finalizing / archived`だけとする。
削除済みと未作成は、どちらも同じ404 Not Found応答にする。
完了要求に必要なデータが未受領なら
409で欠損を返し、`live`を維持する。処理失敗時は再試行可能な状態を残し、
「成功したがデータが欠けている」状態にしない。完了後の追加アップロードは409。

同じ収録・同じ要求の再送は同じ成功結果を返す。確定後の別action／別最終受領点は409。
受領結果、token照合情報、完了処理の記録を永続化し、再起動後も同じ判定を行う。
アーカイブ構築は一時領域で行い、検証と永続化後に公開する。
アップロードも同一ID・同一内容の再送は成功、異なる内容は409とし、目録を重複追記しない。

認証済みSenderへの成功応答は`recordingId`、要求した`action`、`ok: true`、
`archiveUrl`（削除ならnull）を返す。Receiver向けの削除通知には使わない。
削除後も最小限の完了記録を残し、応答喪失による再送で処理を二重実行しない。
通信切断や無更新を正常完了とは扱わない。未完了の収録は復旧／管理対象として残す。

### Senderが完了を送れる条件

`Stop()`を呼んだ直後に完了APIを呼んではならない。現行実装ではGPU readback、
圧縮Task、AAC writer／エンコーダー、HTTP送信がまだ動いている。

1. 新規captureとPCM入力を止め、収録世代を固定する。
2. 予約済みGPU readbackを回収し、順番にcommit、最後の頂点チャンクをflushする。
3. PCMリングをdrainし、エンコーダー終了後の最終fragmentまで取り込む。
4. 圧縮と全メディア・目録送信を終え、サーバーの成功応答を確認する。
5. 確定した最終受領点を指定して完了APIを送る。

キューが空であることだけでは、実行中のHTTPや後から発生するAAC出力を保証できない。
完了待機中の再収録を禁止し、失敗時は完了要求を送らず原因と再試行状態を表示する。
Senderを閉じると最後の送信が失われる可能性があるため、完了応答まで終了待機を表示する。

## サーバーの可変保持設定

`liveChunkCount`をチャンネル単位で指定する。標準3、最小2、最大16を初期仕様とする。
サーバー全体の既定値と認証済み管理設定で変更でき、Receiverから変更はしない。
開発サーバーの既定値は`--live-chunk-count 3`、収録中の変更は
`PATCH /channels/{channel}/policy`へ`recordingId`と`liveChunkCount`を送る契約とする。
push tokenで認証し、確定済み収録への変更は409。不正な保持数は400として丸めない。
設定変更も状態revisionを更新する。縮小時は範囲外のReceiverを追従させ、拡大時は
保管している過去分を再公開できる。ただしHLSの同一URLへ削除済み項目を再挿入しない。
HLSの拡大は以後の追加から徐々に行い、実際に公開できた範囲を報告する。

頂点は、音声との連続した共通終端に重なる最新Nファイルを対象とする。
音声はNファイルを基準に、ネイティブHLSの安全条件を満たすまで過去側を増やす。
ファイルがまだ揃わない開始直後は、少ない数で待機／開始判定する。
アップロード途中のファイルや未受領の参照は公開しない。

`stmj`と`stma`をそれぞれ単純に末尾N行へ切ると、一方の送信が遅れたときに
同期可能な範囲が消える。片方だけ先へ進んだデータは一時保管し、共通範囲が
進むまで現在公開中の連続範囲を保持する。保持数はこの公開範囲について計算する。
音声なしは頂点だけで決める。欠損／discontinuityを跨いで「連続」と見なさない。

ファイルの実範囲は`[startTicks, endTicks)`。
公称の頂点チャンク秒数と`audioSegmentDurationSeconds`は概算用とし、
範囲の決定・選択・消費済み判定には使わない。
音声時刻を基準に表示を遅らせる現在のReceiverでは、頂点公開範囲に
`MeshPresentationDelaySeconds`を加えた区間と、音声区間の積集合が実際の再生可能範囲。
サーバーは両トラックの元の範囲を通知し、Receiverが表示遅延を加味する。

### HLSに関する下限と配信猶予

[RFC 8216 §6.2.2](https://www.rfc-editor.org/rfc/rfc8216.html#section-6.2.2)では、
未終了プレイリストから項目を除く際、残る長さを`3 * EXT-X-TARGETDURATION`未満に
できない。従って音声を常に厳密に2ファイルへ制限する設定は採用しない。
3ファイルでも実際の長さが不足すれば4以上残す。
`EXT-X-TARGETDURATION`は収録中に変更せず、AACの実長を満たす値を開始時に決める。
判定は[§4.3.3.1](https://www.rfc-editor.org/rfc/rfc8216.html#section-4.3.3.1)の
「EXTINFを最も近い整数へ丸めてtarget以下」を用いる。単に公称秒数だけで決めない。

HLSの`EXT-X-MEDIA-SEQUENCE`は保持し、先頭削除数だけ増やす。
ライブには`EXT-X-PLAYLIST-TYPE:EVENT/VOD`を付けない。
削除した音声ファイルのURLも「そのファイルの長さ＋そのファイルを含んだ最長公開
プレイリストの長さ」以上の間は有効にする。チャンネル削除の場合も最後の
プレイリスト長以上はメディアの取得を許す（[§6.2.1](https://www.rfc-editor.org/rfc/rfc8216.html#section-6.2.1)）。
頂点にも旧snapshotと実行中取得を保護する配信猶予を設ける。

つまり公開目録、猶予付き取得、アーカイブ候補の保存は別管理であり、
ディスク上のファイルを数えてN個を超えたら即unlinkする実装にはしない。

## 静的情報と更新状態

`stream.json`は初期リソースと形式情報の静的目録として維持する。
頻繁に変わる公開範囲を追加すると、現行の`ResourceFingerprint()`が変わって
Reconnect時にTexture／Material／Meshを作り直すため、動的情報は分ける。
新しい契約はv7とし、実装・検証の完了時に形式ドキュメントを更新する。
全JSONプロパティはlowerCamelCase。

```http
GET /channels/channel_KAGURA/stream.state.json
```

状態snapshotには次を持たせる。

- `recordingId`、単調増加する`revision`、`state`、`archiveUrl`。
- `liveChunkCount`、実際に公開している`vertexChunkCount`と`audioChunkCount`。
- `vertexStartTicks` / `vertexEndTicks`、`audioStartTicks` / `audioEndTicks`。
- 同じrevisionで選ばれた`vertexChunks` / `audioChunks`のメタデータ。
  各要素は既存のstmj/stma情報と受領済みサイズ・ハッシュを持ち、ファイル名は収録内で一意。
- `finalVertexFile`: 完了APIで検証した最終頂点ファイル。未確定ならnull。
  Receiverが最終ファイルの末尾K例外を検証するために使う。
- HLS絶対時刻へ対応させるための、収録時刻0に固定した`recordingOriginUnixMs`。

状態と公開ファイル一覧を一つの原子的snapshotとして返す。順番が前後した応答は
`recordingId`と`revision`で破棄し、別世代の頂点と音声一覧を混ぜない。
HLSは収録ごとの安定したURLで更新し、ネイティブプレーヤーの実際のTimelineに
基づいて時刻を対応付ける。最新stateとプレーヤーの取得済みHLSが同じrevisionとは限らない。
別収録へ切り替わった場合は同じチャンネル名でもストリーム世代を変更する。

現行`RequestPlaylistDiff()`の「文字列が長くなった分だけ読む」は廃止する。
同じ長さ・短くなる目録も完全snapshotで照合し、取得済み判定は収録ID＋ファイルID＋
内容ハッシュで行う。メディア本体を毎回取得するわけではない。
ライブmetadataと取得済みIDも公開窓／猶予に合わせて捨て、収録時間に比例して増やさない。

アーカイブには全区間のstmj/stma、静的目録、全GPU形式の初期リソース、
音声init、`ENDLIST`付きVOD HLSを保存する。
アーカイブの長い目録は逐次走査／範囲インデックスで扱い、全頂点・音声を
Receiverメモリへ展開しない。公開窓へ変更する前の追記文字列を永続保持しない。

## Receiverの状態管理の確認

2026-10-04に現行コードを照合した結果、**既存の状態だけではライブとアーカイブを
安全に扱えない**。描画・復元状態と配信元の状態は役割が異なる。
追加する状態はReceiver側で管理し、描画側のenumへ配信元の状態を混ぜない。
以下の表は現行の棚卸しであり、これらの変数へさらにフラグを足す実装方針ではない。

| 現行の状態・処理 | 再利用できる役割 | 足りない判定 |
| --- | --- | --- |
| `StreamingPlaybackState` | キーフレーム待ち、復元不足、描画、最終姿勢保持 | `Holding`はライブの続き待ちでもアーカイブ末尾でも発生する |
| `m_WantsToPlay` | 利用者の再生／停止意図 | 配信終了・削除と独立に保存する必要がある |
| `m_PlaybackClockStarted` / `m_AudioSeekRequested` | 再バッファリング中の時計開始・seek待ち | ライブ追従、アーカイブ切り替え、終了を表せない |
| `m_PlaybackGeneration` / rendererの`m_ImportGeneration` | 古い解析結果の無効化 | 状態応答・HTTP取得にも同じ収録／世代の照合が必要 |
| `ConnectionStatus` | 利用者向け表示 | 文字列を状態判定に使わず、型付き状態から表示を作る |
| `m_DurationSeconds` | これまで受領した頂点の最大終端 | ライブのseek開始・終了、確定した全長を区別できない |
| `IStreamingAudioPlayer.State` | 音声デバイス／プレーヤーの状態 | 配信元のlive/archiveを表さず、終了の報告もOS間で不統一 |

### 分離する状態と権威ある情報

配信元の状態は有効なsnapshot内の`Live / Finalizing / Archived`だけで持つ。
`stream.state.json`の明示的な`state`を唯一の判定元とする。
URL名、ファイル数、追加が止まったこと、音声プレーヤーの終了イベントから
live/archiveを推測しない。未知のstateは未対応形式として扱い、Archiveに既定しない。
未取得は「有効snapshotなし」で表し、Unknown用の別変数や配信モードを追加しない。
`Finalizing`は公開範囲を持つ完了処理中であり、Archivedの全区間seekをまだ許可しない。

Receiverの上位状態は一つのFSMが持つ
`Disconnected / Preparing / Paused / Playing / Ended / Faulted`の6種類に整理する。
Preparingは接続、buffer待ち、seek、ライブ復帰、音声ソース切り替えをまとめて扱う。
Faultedは失敗理由を一つ持ち、404は`NotFound`として表示する。Deletedは持たない。
利用者の再生意図だけは`PlaybackIntent: Play / Pause`として独立して保持する。
準備中にPauseを押された場合や同じ収録のアーカイブ化で、意図を失わないためである。
`LivePaused`のような組み合わせenumは作らない。

収録コンテキストには`recordingId`、採用revision、元の`timebaseHz`、配信元状態、
両トラックの公開範囲、確定した最終範囲、取得先を持つ。
`SeekableStartSeconds` / `SeekableEndSeconds`と、確定時だけ値を持つ
`FinalDurationSeconds`を公開する。ライブの最大終端を確定全長と呼ばない。
開始要求は単一の`PlaybackRequest`に`Latest / Beginning / Position`の方針と位置を持たせる。
時刻0を「最新へ戻る」の意味にも使わない。ライブStop後とPause後のPlayを
使い分けるため、Stop時に次回の要求を登録し、実行完了時に消費する。
音声なしでも同じ上位状態を使い、時計やデータ取得方式から配信モードを決めない。

### FSMとストラテジーの責務

`ReceiverPlaybackController`を状態遷移の唯一の所有者にする。
ReceiverはUnity lifecycleと公開APIの入口、描画・音声・HTTPは実行アダプターとする。
データ取得callbackやUIがそれぞれ状態変数を書き換える構造にしない。

| 所有者 | 保持する情報 | 保持しない重複情報 |
| --- | --- | --- |
| Session | 収録ID、検証済みsnapshot、静的リソース参照、再生カーソル | `isLive`、別の最大終端、削除履歴 |
| Controller | FSMの現在状態、PlaybackIntent、未消費のPlaybackRequest | 初期化中／再生中／seek中などの並列bool |
| PreparingのOperation | 一つの準備工程、取消IDと要求handle | 工程ごとの独立した待機フラグ |
| PlaybackPolicy | snapshotに応じた計算・判断だけ | 状態、HTTP、リソース、再生意図 |

Sessionはadapterが正規化した情報を保持する。CoreのsnapshotはJSONやstmj/stmaではなく、
モード・能力・公開範囲・track／codec descriptorを表す。
HTTP固有の公開ファイル数と目録はHTTP adapterが管理する。

Preparing内の工程は`Resources → Snapshot → Buffers → AudioSeek → Ready`。
不要な工程は飛ばし、音声なしならAudioSeekを飛ばす。新規接続はResourcesから、
通常のseek／ライブ復帰／バッファ設定変更は既存リソースを使い必要工程から始める。
準備理由と工程はPreparingのpayload内にのみ存在し、別の上位状態へ移ったら破棄する。
Faultedの失敗理由もFaultedのpayload内だけに持つ。
構築・遷移APIで「Playingなのにseek待ち」などの不正なpayload組み合わせを作らせない。

| 現行の変数 | 整理後 |
| --- | --- |
| `m_AudioPlaying` | FSMがPlayingかどうかから導出 |
| `m_PlaybackClockStarted` | 独立boolを廃止。Readyを通過したFSM遷移で時計を開始 |
| `m_AudioSeekRequested` | AudioSeek工程への入場時に一回だけseekを発行 |
| `IsInitializing` | PreparingのResources工程から導出 |
| `m_PlayIntentConfigured` | 起動時の設定を一回適用し、実行時フラグを廃止 |
| `m_WantsToPlay` | PlaybackIntentに集約 |
| `m_RequestedStartTime` | PlaybackRequestの位置へ集約 |
| `m_DurationSeconds` | 検証済みsnapshotとpolicyから導出 |
| `ConnectionStatus` | FSMの状態・工程・失敗理由から表示を生成 |
| `m_FetchingPlaylists` | HTTPアダプターのactive handleから導出 |

一方、世代番号は状態フラグではなく非同期処理の安全性のために残す。
Operationを置換すると古い要求をcancelし、遅れた結果は取消IDで無視する。
renderer内部の世代とlease管理はその所有者に留め、上位FSMの変数へ複製しない。
buffer準備状況や音声時計もアダプターから読み、boolとして二重に保存しない。

`LivePlaybackPolicy`と`ArchivePlaybackPolicy`をステートレスなstrategyとして使う。
Live/Finalizingには前者、Archivedには後者をsnapshotから選ぶ。
policyの仕事は開始位置、seek範囲、再開位置、データ不足時の判断、確定終端の判定。
結果は`Wait / Reposition / Finish / Fail`などの判断値として返し、
HTTP開始、音声停止、FSM遷移はControllerが一度だけ実行する。
policy自身がPlayerやReceiverを書き換えない。下表のモード差はこの層へ集約する。

FSMはUnityのメインスレッドで遷移し、非同期完了は取消ID付きイベントとして渡す。
現在の状態に適用できないイベントと旧Operationの完了は無視する。
音声SetPlayingやseekは遷移／工程への入場時に実行し、毎フレームの判定から重複発行しない。
高頻度の時計更新・描画・ring buffer処理は既存アダプターで継続し、
毎フレーム新しい状態オブジェクトやstrategyを生成しない。汎用FSMライブラリは必須にしない。

| 現在状態とイベント | 遷移・処理 |
| --- | --- |
| 新規Connect／明示Reconnect | 旧OperationをcancelしてPreparing。policyで開始要求を解決 |
| PreparingでPause／Play | intentのみ変更。工程を重複開始しない |
| PreparingでReady | intentがPlayならPlaying、PauseならPaused |
| PlayingでPause | Paused。音声を停止し、時計は同じ位置を維持 |
| PausedでPlay | policy判定。位置が有効ならPlaying、不足／範囲外ならPreparing |
| seek／ライブ脱落／再準備が必要な設定変更 | intentを維持してPreparing、新しいOperationを一つ作る |
| Playingでデータ不足 | policyのWait/RepositionならPreparing、FinishならEnded、FailならFaulted |
| Stop | intentをPauseにし、policyの次回開始要求を保存。旧Operationをcancelし、準備済みSessionがあればPaused、なければDisconnected |
| 状態取得先404 | Faulted(NotFound)。取得・再生を停止、Connect以外で再試行しない |
| EndedでPlay | Archive policyで先頭開始要求を作りPreparing |

NotFound時の外部表示は常にNot Found。FSMの内部表現を理由付きFaultedへまとめても、
存在履歴を推定した状態や通知にはしない。

### モードによる操作・イベントの差

| 操作・イベント | Live / Finalizing | Archived |
| --- | --- | --- |
| 新規接続 | 静的ロード後に最新の共通範囲から開始 | 確定範囲の先頭から開始 |
| 自動再接続 | 最新範囲へ復帰、再生意図を維持 | 元の絶対時刻を維持して再取得 |
| `Seek()` | 最新snapshotのseek可能範囲へ制限 | 確定した全再生範囲へ制限 |
| `Pause()` → `Play()` | 時刻が範囲外／遅延過大なら復帰、範囲内なら継続 | 停止位置から継続 |
| `Stop()` | 時刻0へseekせず停止、次回Playで最新へ戻る | 停止して先頭へ戻す |
| 再生可能データ不足 | Preparing、状態更新を待つ／範囲外なら復帰 | 未取得ならPreparing、欠損ならFaulted、確定末尾ならEnded |
| 取得済み終端で新規ファイルなし | それだけでは終了としない | 確定末尾を消費してEnded |
| `Play()`をEndedで操作 | 確定前にEndedへ遷移しない | 明示操作で先頭から再開 |

状態監視はPausedやPreparing中も続ける。メディア取得は有限先読みを維持する。
状態取得先の404ではどの再生状態からも取得・音声を止め、Faulted(NotFound)へ遷移する。
削除されたのか最初から存在しないのかは判定・表示しない。
NotFoundでのPlay/Seekは拒否し、明示的な接続操作だけで再確認する。
ネットワーク失敗で最後の有効snapshotを破棄して配信モードを変更しない。

同じ`recordingId`の`Live → Finalizing → Archived`では、絶対再生時刻と利用者の
再生意図を引き継ぐ。現在位置が範囲内なら先頭・最新へのシークをしない。
音声ソース変更が必要ならPreparingへ入り、変更後のseek確認とバッファ準備を待つ。
PausedはPausedへ戻す。異なるrecordingIdや明示的な新規接続は別セッションとして扱う。
同じ収録では逆行したstate／revisionを採用せず、矛盾した応答はエラーとして記録する。

切り替え時はチャンネルの状態取得先、初期リソース取得先、頂点／音声の取得先を
別に保持する。現在の`ResourceFingerprint()`は`m_ChannelAddress`も含むため、
その値をarchive URLへ入れ替えるだけでは同じリソースでもcache missになる。
収録内で安定した初期リソースのURL／内容ハッシュをfingerprintの入力とし、
音声・目録の取得先変更だけではTexture／Material／Meshを再構築しない。
採用済みセッションの制御取得先も収録IDに結び付け、同名チャンネルの別収録と混ぜない。

### 終了と非同期処理の不足

現行`Receiver.UpdateSynchronizedPlayback()`は音声State 3をEndedへ反映しない。
Webはendedイベントを3として返すが`endOfStream()`を呼ばず、Androidは
STATE_ENDEDも0へまとめ、Appleは3を返す。この整数をそのまま上位状態としない。
音声側は型付きの準備・時計・終了・エラーを通知し、seek完了は要求世代と照合する。

終端判定にはArchivedの確定範囲と最後のフレーム／音声区間の消費を使う。
末尾では未来フレームの先読み条件を要求せず、最後の姿勢をその表示区間まで保持する。
現行の`CanPlayAt()`は先読みフレーム数と未来PTSを必要とするため、ここをそのまま
使うと確定末尾の手前で音声を止め、終了待ちになり得る。
LiveのHoldingをEndedと見なす変更や、音声endedだけによる全体終了は行わない。

状態監視のcoroutine分離に加え、HTTP経路も分離する。
現行`HttpManager`は`ThreadManager`の一つの直列HTTPキューを使うので、
大きな頂点取得中には別coroutineから出したstate要求も待たされる。
状態取得1件とメディア取得の上限付き枠を独立にし、timeout／cancelを持たせる。
`StopAllCoroutines()`では別GameObjectのThreadManager上のHTTPは止まらない。
世代チェックに加え、要求handleのAbort／Disposeと未実行キューの取り消しが必要。

KAGURAのUIも`DurationSeconds`を上限とする0起点sliderから、明示的なseek範囲へ変更する。
ライブ／完了処理中／アーカイブ、現在再生状態を型付き状態から表示し、
Faulted(NotFound)や他の失敗理由で無効な再生操作を有効にしない。

## Receiverの開始・追従・先読み

「常に最新」は、途中接続・Reconnect・公開範囲からの脱落時に最新へ戻り、
通常再生中は有限の遅延を保つ意味で扱う。新ファイルが追加されるたびに再シークすると
再生が途切れるため、その動作にはしない。

1. 静的リソースのロード完了後に、改めて最新stateを読む。8Kテクスチャを読み込んで
   いる間に、最初に取得した短い公開窓が期限切れになることを想定する。
2. 元のPTSを保った実再生範囲から、最新共通終端より完成ファイル1個程度後ろを
   開始目標にする。揺らぎに応じた余裕を確保し、公開範囲へclampする。
   選択した頂点ファイル先頭のキーフレームから復元し、音声も同じ絶対再生時刻へseekする。
3. 再生時刻が有効範囲にある間は継続する。範囲外へ遅れたとき、または設定した
   最大ライブ遅延を超えたときに、一度だけ最新目標へ復帰する。
4. 復帰では音声を止め、旧取得／解析を世代番号で無効化し、頂点・音声の再生バッファを
   更新する。初期Texture／Material／Meshと安全に再利用できる配列プールは維持する。
5. 一時停止中も状態監視を続け、復帰は再開操作時に行う。ライブのseek可能範囲は
   現在の公開範囲に制限する。アーカイブへ切り替わった後は全区間をseekできる。

先読みは既存のファイル数設定を維持し、頂点とWeb音声それぞれ
`min(Receiver指定数, 再生目標以降の公開済みファイル数)`を上限にする。
公開3・先読み3は「現在＋次の2」であり、別にさらに3個を確保しない。
処理中ファイルやHTTP取得中も枠に数える。過去音声保持は別枠だがライブ公開窓の
開始より前へ際限なく保持しない。実範囲とバイト上限の両方で制限する。
未完成の未来ファイルはpollingで待ち、404を繰り返してReconnectしない。

状態pollingはチャンク取得／バッファ枠待ちから分離する。現在の`FetchPlayLists()`は
枠待ち中に次回更新を妨げるため、そのままでは停止・縮小・範囲外復帰を検知できない。
短いチャンクでは0.5〜1秒程度を目安に上限付きpollingし、state revisionが同じなら
解析・取得を増やさない。通信失敗はbackoffし、削除／終了を通常の空目録と区別する。

メディアファイルの404はstateを再取得して範囲外復帰を判定する。
チャンネル状態の404はNotFoundとして停止し、削除を推測する再試行ループを続けない。
HTTP wrapperはnullだけでなくstatus codeと取得世代を通知する必要がある。
完成済みアーカイブの欠損はライブの完成待ちではなくエラーとして扱う。

### 頂点チャンクの独立性

`.stmv`の境界仕様は次のとおりとする。

- 全ファイルの先頭はキーフレーム（K）。
- 最終ファイル以外の末尾は差分フレーム（D）。通常ファイルは最低2フレーム。
- 収録全体の最後のファイルだけはKで終了してよい。K一つだけの最終ファイルも許す。
- ファイル間でフレームを重複・削除しない。sequenceとPTSを連続して受け渡す。
- ファイル途中の定周期Kは維持する。ファイル境界とKの周期を同じ計画で決める。

旧`subframesPerKeyframe`は「Kの後のDの数」なので、0では全フレームがKになり
通常ファイルの末尾条件を満たせない。v7では意味を明確にした
`keyframeIntervalFrames`（K間のフレーム数、最低2）へ変更する。
旧設定4は5へ、KAGURAの旧設定9は10へ変換する。
Unityシーンの値を単に同名扱いして引き継がず、+1する移行処理が必要。

旧`combinedFrames`は上限と目標を兼ねているため、v7では分離する。

| 項目 | 意味 |
| --- | --- |
| `targetChunkFrames` | Senderの希望する1ファイルのフレーム数。厳密な件数ではない |
| `keyframeIntervalFrames` | K一つと後続Dからなる周期の長さG。G >= 2 |
| `maxChunkFrames` | 収録開始時に確定する1ファイルの最大実件数。Receiverの容量・入力検証に使う |
| `.stmv`先頭のframeCount／`stmj.frameCount` | そのファイルが実際に持つ件数。双方とsequence範囲を照合する |

通常のフレーム列を`K D ... D`の周期単位でまとめ、目標件数Tに最も近い
合法なGの倍数Cで分割する。同距離なら小さい方にする。通常ファイルの実件数はC、
最終ファイルは1〜C。Cを`maxChunkFrames`として静的目録に宣言する。
T < Gなら最小の1周期Gにし、実効値が目標より長いことをSenderに表示する。

| 目標T | 周期G | 通常の実件数C | フレーム列 |
| --- | --- | --- | --- |
| 60 | 10 | 60 | `(K D×9)×6` |
| 61 | 10 | 60 | `(K D×9)×6` |
| 66 | 10 | 70 | `(K D×9)×7` |
| 3 | 10 | 10 | `K D×9` |

例えば周期3・目標6で13フレーム収録すると、
`[K D D K D D] [K D D K D D] [K]`になる。
最後のKは次のファイルへ置き、直前の通常ファイルへ付け足さない。
ちょうど通常境界で停止した場合は最後の通常ファイルが最終ファイルになり、空ファイルを作らない。

Cは4096フレームと既存の展開128MiB上限の両方を守る合法な値から選ぶ。
メッシュの既知のレイアウトとK/Dの最大サイズ、サイズ表を含めた保守的な上界で
収録開始前に容量を予約する。少なくとも1周期が収まらない設定は開始時に拒否する。
サイズ超過後にファイルを切って、予約済みのDを次のファイルの先頭に回すことはしない。
入力レイアウトの変化などで上界が崩れた場合も、不正なチャンクを公開せずエラーにする。

captureが実際に受理される時点で、sequence、チャンク番号、周期内位置、K/Dを
同じ計画から決めて固定する。readback完了順やflush時に後からK/Dを変更しない。
現行は周期カウンターをcapture完了前に進めるため、これも予約方式に置き換える。
予約済みフレームはsequence順にcommitし、録画中のT/G変更は次の収録へ適用する。
Stop後は予約済み処理をdrainしてから最終ファイルを確定し、勝手にKを追加しない。

Kで終わる短い末尾を通常ファイルとして先行公開しない。
最終ファイルの例外は、完了APIで検証した`vertex.lastFile`と結び付ける。
サーバーはそのファイルを完了処理まで一時保管し、最終ファイルであると確定した後に
公開する。Receiverも確定した最終ファイルの識別情報に基づいて例外を許す。
単に「現在最後に見えるファイルだから最終」と判定してはならない。

現行Receiverのサイズ表は既に可変frameCountを読めるが、配列プール・フレームリングの
容量は`combinedFrames`に依存する。目標Tではなく宣言上限`maxChunkFrames`へ変更し、
各ファイルの実件数で保持枠とメタデータを扱う。未来PTS／sequenceは実ヘッダーから読み、
`ファイル番号 * T`で計算しない。先読み範囲はこれまでどおり実start/endTicksで選ぶ。
先頭K・通常末尾D・件数・sequence・サイズ上限はサーバーとReceiver双方で検証する。

音声の公称fragment秒数は実効CとframeIntervalから算出し、AAC境界の差は許容する。
T/G変更でHLS target durationを収録途中に変更しない。正確な音声／頂点範囲は
引き続き各ファイルの実PTSを使い、件数変更でA/Vの公開窓をずらさない。

### ネイティブ音声の時刻

[ExoPlayer公式ドキュメント](https://developer.android.com/media/media3/exoplayer/live-streaming)では、
`getCurrentPosition()`と`seekTo()`は現在のライブ窓の先頭に対する相対時刻である。
現行Android bridgeはそれをそのまま絶対ストリーム時刻として返している。
窓が移動するとA/V同期が破綻するため、rolling公開に合わせて修正する。

HLSの`EXT-X-PROGRAM-DATE-TIME`を収録原点＋累積PTSから生成し、
プレーヤーが現在保持する`Timeline.Window.windowStartTimeMs`と相対位置から
絶対再生時刻へ変換する。seekは同じTimelineの対応関係で逆変換し、窓更新時に再照合する。
チャンクの受信時刻をPTS原点に使わず、最新stma先頭を古いTimelineへ足さない。
BehindLiveWindowの復帰も頂点と同じ目標へ合わせる。
音声HLSが頂点より多い過去ファイルを含む場合も、ネイティブの開始位置とライブ遅延を
共通実範囲へ制限し、音声側の既定開始位置だけで頂点範囲外から再生しない。
ネイティブ内部の先読みはファイル数設定と別なので、時間・バイト使用量も実測する。

Web MSEはfMP4の絶対PTSを維持する。Apple AVPlayerのtime／seekable rangeは
Androidと同じ前提にせず実測し、同一の絶対時計インターフェースへ対応付ける。
Apple bridge変更時はmacOSの同梱dylibの再ビルドも必要。

## 完了後のReceiver

`archive`では最後のライブHLSにENDLISTを付け、独立したアーカイブURLに全区間の
VOD HLSを公開する。同じライブHLSの先頭へ削除済み項目を戻さない。
接続中Receiverはアーカイブへ音声／目録を切り替え、絶対時刻を引き継ぐ。
アーカイブURLへ新規接続した場合は全区間の先頭から開始する。
最終共通範囲の終了を検知して終了状態にし、末尾で永久に完成待ちをしない。
片方だけ残る短い末尾を再生可能な共通終端より先まで同期再生しようとしない。

`delete`では公開state／目録に、未作成のチャンネルと同じ404 Not Foundを返す。
応答本文・ヘッダー・一覧表示に削除履歴、旧収録ID、削除理由を載せない。
Receiverは音声、polling、取得を止めてNotFoundを表示し、存在履歴を推測しない。
サーバー内部の完了記録は認証済みSenderの再送判定だけに使う。
既存メディアのURLは必要な猶予期間だけ取得可能とし、
期間後に収録単位で片付ける。アーカイブ／別収録の共有リソースを誤削除しない。

## 実装順と受入試験

サーバーだけ先にファイルを減らす変更は有効化しない。
契約・サーバーの永続保存／snapshot／完了API、Senderの独立チャンク／drain、
Receiverのsnapshot／時刻／追従を揃えてから新形式を公開する。
既存のv6テストURLはこの設計確認では変更しない。

KAGURAを30fpsで新規収録し、2秒=60、3秒=90、5秒=150フレームを比較する。
最初の基準は3秒・公開3・頂点/Web音声先読み3。負荷と通信揺らぎを見るため、
2秒・5秒、公開2への縮小／公開数の拡大も試す。AACは一つの継続エンコーダーで生成する。
既存10秒ファイルを、差分やAAC境界を無視して単純切断したfixtureでは代替しない。

| 試験 | 合格条件 |
| --- | --- |
| 周期に合わない目標（61、66など） | 目標に近い合法件数、全先頭K、通常末尾D、欠落／重複sequenceなし |
| 全停止位置・周期2以上・最終Kのみ | 最終ファイルだけ末尾K可、空ファイルや追加フレームなし |
| 旧subframes=0／1周期が容量上限を超える設定 | 開始時に拒否し、通常末尾Kや上限超過を公開しない |
| 途中接続・長い初期テクスチャロード | 初期ロード後の最新窓から開始し、古い窓で停止しない |
| 頂点／音声の片側アップロード遅延 | 共通実範囲を維持し、未受領ファイルを公開しない |
| 長いpause・通信断・遅い取得 | 範囲外復帰後にA/V同期、旧世代結果が混入しない |
| 同サイズ／短いsnapshot、応答順序逆転 | 最新revisionを採用し、内容を取り逃さない |
| snapshot未取得／無更新／一時的な通信失敗 | live/archiveを推測せず、最後の有効状態を維持 |
| Preparing中の連続Play/Pause/Seek/Stop | 有効Operationは一つ、最終intentを維持、旧seek完了で状態を上書きしない |
| 全FSMイベント・NotFoundからの操作 | 許可遷移のみ、無効操作やrenderer Holdingで勝手にモードを変更しない |
| pause中・再生中のLive→Archived | 同じ絶対時刻と再生意図を維持、静的リソースを再確保しない |
| モード別のStop／Play／Seek／自動再接続 | ライブ範囲と確定範囲を使い分け、無効な時刻へ取得しない |
| 確定末尾・一時的なライブHolding | archiveは最後まで消費してEnded、liveは続き待ち |
| 遅いメディア取得中の状態更新／削除 | 独立したstate要求で検知、古い要求を実際に取り消す |
| 公開数と先読み数の増減 | 公開外を取得せず、プール所有権とメモリ上限を維持 |
| Android HLS窓の繰り上がり | 絶対時刻が窓更新で巻き戻らず、頂点と同期 |
| HLS最低長・猶予 | 指定数より実数が増える条件と旧URLの有効期限が正しい |
| Stop時の遅延readback／AAC末尾／ACK喪失 | 全受領後だけarchive成功、再送で重複しない |
| archive中断・サーバー再起動・二重complete | 永続記録から復旧し、同じ結果／矛盾要求409 |
| 同名チャンネル再収録・古いcomplete | 新しい収録を削除・上書きしない |
| delete完了／未作成チャンネル | 同じ404 Not Found、履歴・削除理由を返さずReceiver停止、猶予後だけ物理削除 |
| archive完了・新規archive接続 | 全区間seek可能、接続中は時計を維持、新規は先頭開始 |
| 長時間ライブ | metadata/取得済みID/メディアバッファが収録時間に比例して増えない |

WebGLと接続済みAndroidネイティブで実測する。配信ファイル数・実範囲・取得数、
window revision、ライブ遅延、音声／頂点PTS差、受信／解析の世代、プール使用量、
WASM使用量と確保済み容量を記録する。Appleは実機を使えるまで未検証と明記する。
サーバーの一時保管容量は全収録に比例することを別に観測する。

2026-10-04の設計確認では、周期2〜40・目標1〜160について境界計算と停止位置を
1,152,482ケース走査し、通常末尾D・次の先頭K・最終端数・件数保存を確認した。
これは参照計算の確認であり、Sender実装、バイト上限、FSM動作、実機再生の合格を意味しない。

## 現行コードで確認した変更箇所

- `Tools/streamingmesh_dev_server.py`: 完了ルート／永続収録状態がなく、目録を全履歴追記。
- `Assets/StreamingMesh/Scripts/Utils/HttpWrapper.cs`: `RequestPlaylistDiff()`は文字数増加を前提。
- `Assets/StreamingMesh/Scripts/Receiver.cs`: metadata pollingと取得枠待ちが同一coroutine。
  初回は公開先頭、動的状態への追従なし。静的manifest全体がresource fingerprintの入力。
- `Assets/StreamingMesh/Scripts/Core/Rendering/StreamingMeshRenderer.cs`: 状態は描画・復元用であり配信モードを表さない。確定末尾の表示条件が必要。
- `Assets/StreamingMesh/Scripts/Core/Rendering/IStreamingAudioPlayer.cs`: 音声の状態・seek完了を型付きで統一し、配信元状態と分離。
- `Assets/StreamingMesh/Scripts/Net/HttpManager.cs` / `Assets/StreamingMesh/Scripts/Core/Threading/ThreadManager.cs`: stateとメディア取得の独立枠・キャンセルが必要。
- `Assets/Samples/UnityChanKAGURA/Scripts/KaguraReceiverControls.cs`: 0起点の全長sliderとStopをモード別の範囲・操作へ変更。
- `Assets/StreamingMesh/STMHttpSender.cs`: キーフレーム周期とファイル境界が独立。周期単位の分割と最終末尾の例外を設計仕様へ合わせる。
  `Stop()`は非同期処理・最終アップロードの完了を保証しない。
- `Assets/StreamingMesh/Scripts/STMHttpBaseSerializer.cs`: 送信成功／drainの明示的通知が必要。
- `Assets/StreamingMesh/Scripts/STMAudioRecorder.cs`: encoder終了後の最終出力を確認する完了条件が必要。
- `Assets/Plugins/WebGL/StreamingMeshFmp4.jslib`: 公開窓からの脱落、完了／削除、取得中断を追加。
- `Assets/StreamingMesh/Android/StreamingMeshAndroidAudio.java`: 窓相対時刻を絶対PTSへ変換。
- `Assets/Plugins/iOS/StreamingMeshAppleAudio.mm`: ネイティブ窓とseekの対応を実測・統一。
