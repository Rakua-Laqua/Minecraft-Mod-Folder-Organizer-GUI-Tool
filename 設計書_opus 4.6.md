# Minecraft Mod JAR抽出・lang整理GUIツール 設計書 v1.4

## 1. 目的
- 親フォルダ直下のMinecraft Mod `*.jar` を対象に、安全に展開して処理対象フォルダを作成する。
- 展開後、各Mod内の `assets/<modid>/lang/` を抽出して `親フォルダ直下/<modid>/lang/` に集約する。
- 上記以外の不要ファイルは安全に整理する。
- PowerShellで実現している処理を、誤操作しにくいGUIとして提供する。

## 2. 対象環境
- OS: Windows 10/11
- ランタイム: .NET 8 Desktop Runtime（同梱/自己完結配布を選択可能）
- GUI: WPF（MVVM）
- 入力: 親フォルダ直下に並ぶ `*.jar` ファイル
- 出力既定: `親フォルダ\<jarファイル名(拡張子なし)>\` （複数modid時は `\<modid>\` サブフォルダ付き）

## 3. 用語
- 「親フォルダ」: Mod `*.jar` が直下に並ぶディレクトリ
- 「Modファイル」: 親フォルダ直下にある `*.jar`
- 「作業展開フォルダ」: 1つの `*.jar` を一時展開するディレクトリ（例: `%TEMP%\mod-organizer\foo_xxxx`）
- 「langソース」: `assets/<modid>/lang` に一致するディレクトリ
- 「lang出力先」: `親フォルダ直下/<jarファイル名(拡張子なし)>/` （単一modid）または `親フォルダ直下/<jarファイル名(拡張子なし)>/<modid>/` （複数modid）
- 「競合」: 出力先に同名パスが既に存在する状態
- 「langフォールバック」: 出力先にターゲット言語ファイルが存在しない場合、ソース言語ファイルからコピーして生成する機能

## 4. 要求仕様（機能要件）

### 4.1 フォルダ選択
- 親フォルダをユーザーが選択できる（フォルダピッカー）。
- 親フォルダ直下の `*.jar` のみを処理対象として列挙する。
- 出力ルートは既定で親フォルダ（`TargetDir`）とし、任意変更も可能にする。
- 「親フォルダと同じ場所に出力する」チェックボックスで同期/独立を切り替えられる。
  - ON（既定）: 出力ルートはTargetDirに自動同期、テキスト入力と「変更...」ボタンは無効化
  - OFF: 出力ルートを独立して指定可能

### 4.2 解析（スキャン）
各 `*.jar` に対し以下を判定し、結果一覧に表示する。
- `jar` の読取可否（壊れたアーカイブの検出）
- 出力先候補パス（`<出力ルート>/<jarファイル名(拡張子なし)>/` または `<出力ルート>/<jarファイル名(拡張子なし)>/<modid>/`）
- `assets/<modid>/lang` 候補数（0/1/複数）※アーカイブ内エントリから予測
- 処理方針（A: langあり処理 / B: langなしスキップ）
- 予定操作件数（Extract/CreateDir/Copy/ConflictCopy/Cleanup/Skip）
- 実行前整合性チェック用スナップショット（`jar` サイズ、更新時刻、件数）

### 4.3 実行（jar抽出 + lang集約）
対象: 親フォルダ直下の各 `*.jar`

#### A) langが見つかった場合
1. `jar` を作業展開フォルダへ安全展開する（Zip Slip対策必須）。
2. 各langソースの `modid` を保持し、出力パスを決定する。
   - 単一modid: `出力ルート/<jarファイル名(拡張子なし)>/`
   - 複数modid: `出力ルート/<jarファイル名(拡張子なし)>/<modid>/`
3. langソース配下の全ファイルを再帰的に出力パスへ配置する。
4. 競合時は「ファイル単位で比較」して扱いを決める。
   - 同一内容: 追加コピーは作らずスキップ（重複扱い）
   - 異なる内容: 既存を残し、流入側を競合コピーとして保存
5. 競合コピー名は比較しやすく一意な規則で作る。
   - 例: `en_us.conflict.<sourceTag>.<n>.json`
6. 全候補の配置が完了したら、作業展開フォルダを削除する（Cleanup）。
7. コピー中に失敗が発生したModは、既存出力を破壊する操作へ進まない（破壊的操作を回避）。

#### B) langが見つからない場合
- `jar` は解析後、作業展開フォルダをクリーンアップする。
- ステータスを `Skipped (langなし)` として表示/記録する。
- この判定はスキャン（ドライラン）時点で確定表示する。

#### C) 複数lang候補が見つかった場合
- 全候補を対象にA処理を行う（選択式にしない）。
- 同一 `modid` / 同一ファイル名の衝突は、必ず「比較 -> 競合コピー規則」で処理する。

### 4.4 スキャン（プレビュー兼用）
- スキャン実行時に予定操作（Extract/CreateDir/Copy/ConflictCopy/FallbackCopy/Cleanup/Skip）を一覧表示する（ドライラン）。
- 実際のファイル変更は行わない。
- スキャン完了後にのみ「実行」ボタンを有効化する。

### 4.5 スキャン結果の鮮度管理（再スキャン判定）
- スキャン時に `jar` スナップショット（ファイル名、サイズ、最終更新時刻）を保持する。
- 実行直前に再検証を行い、スナップショット差分があれば実行を止めて再スキャンを要求する。
- `FileSystemWatcher` を併用し、親フォルダ選択時に自動起動する。jarの変更(作成/変更/削除/リネーム)を検知し、「再スキャン推奨」をUI上で即通知する。
- FileSystemWatcherは常時有効（OFF切替なし）。

### 4.6 設定永続化
- アプリの全設定を `%LOCALAPPDATA%\ModLangOrganizer\settings.json` にJSON形式で保存・復元する。
- 保存対象: TargetDir, OutputRoot, OutputRootSameAsTarget, BackupZip, CancelGranularity, LangFallback関連。
- デバウンス保存: 設定変更から400ms後に最後の変更のみを保存（テキスト入力中の連続書き込みを回避）。
- アトミック書き込み: `.tmp` ファイルに書いてから `File.Replace` で差し替え。書き込み中クラッシュ時も設定ファイルが破損しない。
- 破損復旧: JSONパース失敗時、`settings.broken.<timestamp>.json` としてバックアップ後、デフォルト値でリセット。
- 起動時に保存済みTargetDirが存在するか確認し、存在しなければ空にリセットして警告ログを出す。
- ウィンドウ閉じ時に `IDisposable.Dispose()` 経由で未保存設定をフラッシュする。

### 4.7 ログ/通知
※ 旧 4.6
- 画面内ログ（時刻/レベル/メッセージ）
- 実行結果サマリ（成功数、警告数、スキップ数、失敗数、Cleanup失敗数）
- 任意でログのファイル出力（例: `run-log.txt`）

### 4.7a 安全対策（誤削除防止）
※ 旧 4.7
- 実行前の確認ダイアログを必須化する。
- 既存のlang出力を上書き削除しない（差分は競合コピーで退避）。
- クリーンアップ対象は作業展開フォルダのみに限定し、親フォルダ直下の既存要素は削除しない。
- 任意で実行前バックアップ（Zip）を作成できる。
  - 範囲: 親フォルダ全体
  - 出力先: 親フォルダと同階層
  - ファイル名: `<フォルダ名>_backup_<yyyyMMdd_HHmmss>.zip`
  - 既定: OFF

### 4.8 langフォールバック機能
- 出力先にターゲット言語ファイル（例: `ja_jp.json`）が存在しない場合、ソース言語ファイル（例: `en_us.json`）をコピーして生成する。
- 既定: OFF（チェックボックスで有効化）
- パラメータ:
  - コピー元ファイル名（拡張子なし、既定: `en_us`）
  - コピー先ファイル名（拡張子なし、既定: `ja_jp`）
- 拡張子はソースファイルの拡張子をそのまま使用（例: `.json`, `.lang`）。
- ターゲットが既に存在する場合はスキップする。
- 対象: 出力 lang ディレクトリ直下のファイルのみ（再帰なし）。
- 実行タイミング: 通常のlangコピー完了後、各lang候補ごとに適用。

### 4.8 セキュリティ要件
※ langフォールバック機能 (4.8) を追加したため、旧 4.8 は 4.9 に繰り下げ

### 4.9 セキュリティ要件
- `jar` 展開時はZip Slip対策を必須とする。
  - 展開先の正規化パスが許可ルート配下かを各エントリで検証
  - `..` や絶対パスを含むエントリを拒否
- 再帰探索/再帰削除ではシンボリックリンク・ジャンクションを辿らない。
  - リンク自体は削除対象として扱えるが、リンク先の実体には入らない。
- 作業展開フォルダは `jar` 名 + ハッシュで一意化し、同時実行時も衝突しないようにする。

## 5. 非機能要件
- UIが固まらない（バックグラウンド処理 + 進捗表示）
- 大量Modでも耐える（逐次処理 + キャンセル対応）
- パスが日本語でも動作（Unicode前提）
- 失敗しても可能な限り継続（`jar` 単位で例外を隔離）
- キャンセル粒度:
  - 既定: `jar` 単位で反映
  - オプション: ファイル単位で反映（重いが応答性向上）

## 6. UI設計

### 6.1 画面構成（単一ウィンドウ）
1) 上部: カスタムタイトルバー + 親フォルダ選択
- カスタムタイトルバー:
  - アプリアイコン（グラデーション付き六角形）
  - アプリ名「Mod Lang Organizer」
  - バージョン表示（v1.0）
  - 右端にjar数バッジ（📦 X jars、スキャン完了時のみ表示）
- フォルダ選択カード:
  - 親フォルダ: [参照...] ボタン + パス表示（読み取り専用）
  - 出力ルート: [変更...] ボタン + パス表示
  - 「親フォルダと同じ場所に出力する」チェックボックス（ON時は出力ルート入力を無効化）

2) オプション領域
- バックアップ: 実行前バックアップ（Zip）チェックボックス
- キャンセル粒度: ラジオボタン
  - `jar` 単位（推奨）
  - ファイル単位
- langフォールバック:
  - 有効/無効チェックボックス
  - コピー元ファイル名（拡張子なし）テキスト入力
  - コピー先ファイル名（拡張子なし）テキスト入力
  - 「→」矢印で方向を視覚的に表現
  - チェックボックスOFF時は入力欄を無効化

3) スキャン結果テーブル
- 列例:
  - Mod名（`jar` ファイル名）
  - `jar` 健全性（OK/破損）
  - lang検出（0/1/複数）
  - 処理方針（A/B）
  - 予定操作件数（Extract/CreateDir/Copy/ConflictCopy/FallbackCopy/Cleanup/Skip）
  - スナップショット状態（最新/要再スキャン）
  - ステータス（未処理/成功/警告/スキップ/失敗）

4) 実行コントロール
- [スキャン] [実行] [キャンセル]
  - 実行ボタンは緑系グラデーションで視覚的に区別
  - キャンセルボタンは赤系（DangerButton）
- 進捗バー: スキャン用と実行用を分離表示
  - スキャン中: `IsScanning` で表示/非表示
  - 実行中: `IsExecuting` で表示/非表示
  - 全体％ + 現在のjar名を表示
- 再スキャン警告: jar変更検出時にアクションボタン横に警告バーを表示

5) ログビュー
- スクロール可能（仮想化パネル VirtualizingPanel 使用）
- ログエントリ: 時刻[HH:mm:ss] + レベル + メッセージ、等幅フォントで表示
- レベル別色分け: Info=ライトグレー, Warning=イエロー, Error=レッド
- 件数バッジ表示（ログセクションヘッダーに「N件」）
- [ログ保存] ボタン（保存ダイアログでファイル名既定: `mod-organizer-log_<yyyyMMdd_HHmmss>.txt`）

6) ステータスバー
- ウィンドウ最下部に常時表示
- 左: 状態テキストメッセージ（「フォルダを選択してください」「スキャン中...」「実行中...」等）
- 右: ビジーインジケータ
  - 色付きドット + テキスト
  - 緑=待機中, アンバー=スキャン中, ブルー=実行中

### 6.2 UXルール
- フォルダ選択後に軽いスキャンを提案（自動実行はしない）
- 実行ボタンは「スキャン完了」かつ「スナップショット最新」のときのみ有効
- 差分検出時は実行不可にし、再スキャンを明示誘導する
- TargetDir変更時はスキャン結果をクリアし、実行ボタンを無効化する
- 実行完了後は `ScanCompleted = false` にリセットし、再実行には再スキャンが必要
- キャンセル時、「処理中」ステータスのModは自動的に「スキップ」に遷移する
- 確認ダイアログには具体的実行内容（対象jar数、出力先、バックアップ有無、langフォールバック設定）を表示
- 実行結果はダイアログでサマリ表示（成功/警告/スキップ/失敗/Cleanup失敗）

### 6.3 デザイン/テーマ
- モダンダークテーマ（Minecraft風カラーパレット）
  - ベース: `#0F1117` 系ダーク
  - アクセント: 紫 `#6C63FF`, 緑 `#10B981`
  - セマンティック: 成功=緑, 警告=アンバー, エラー=赤, 情報=ブルー
  - テキスト: Primary/Secondary/Muted の3階層
- カードUI: 各セクションを角丸ボーダーで囲む
- ボタン: グラデーション背景 + ドロップシャドウ
- フォント:
  - UI: Segoe UI / Yu Gothic UI / Meiryo UI
  - 等幅: Cascadia Code / Consolas
- WPFバリューコンバーター:
  - `BoolToVisibilityConverter`（Invertパラメータ対応）
  - `InverseBoolConverter`
  - `StatusToColorConverter`
  - `LogLevelToColorConverter`
  - `IntegrityToColorConverter`
  - `SnapshotStateToColorConverter`
  - `EnumToBoolConverter`（RadioButton用）

## 7. 処理仕様（アルゴリズム）

### 7.1 Mod列挙
- `TargetDir` 直下の `*.jar` 一覧を取得し、処理対象として扱う。

### 7.2 jar展開
- 各 `jar` について作業展開ディレクトリ `WorkDir` を決定する。
  - 例: `%TEMP%\mod-organizer\<jar名>_<hash>`
- `jar` エントリごとに展開先を正規化し、`WorkDir` 配下のみ許可して展開する。

### 7.3 lang探索
- `WorkDir/assets` が無ければ lang無し扱い（B処理）。
- `WorkDir/assets` 配下を再帰探索し、末尾が `assets/<something>/lang` に一致するディレクトリを候補とする。
  - 正規表現相当: `.../assets/[^/]+/lang`
- 探索時は再解析ポイント（symlink/junction）を辿らない。

### 7.4 A処理（langあり）
- 各lang候補について:
  - `SrcLang = WorkDir/assets/<modid>/lang`
  - 単一modid: `OutLang = OutputRoot/<jarファイル名(拡張子なし)>/` を作成
  - 複数modid: `OutLang = OutputRoot/<jarファイル名(拡張子なし)>/<modid>/` を作成
- `SrcLang` 配下の全ファイルを再帰列挙し、相対パスで出力先を決定する。
  - `DestPath` 未存在: そのままコピー
  - `DestPath` 既存: ファイル単位比較
    - 同一: スキップ
    - 相違: `*.conflict.<sourceTag>.<n>.*` としてコピー
- すべてのコピーが完了した `jar` のみ、`WorkDir` を削除する（Cleanup）。

#### 7.4a langフォールバック処理
- langフォールバックが有効の場合、各lang候補のコピー完了後に実行する。
- `OutLang` 直下のファイルを検索し、ソース名（拡張子なし）に一致するファイルを探す。
- ソースが見つかれば、その拡張子を保持したままターゲット名のファイルとしてコピーする。
- ターゲットが既に存在する場合はスキップする。
- 再帰なし（lang直下のみ対象）。

### 7.5 B処理（langなし）
- `WorkDir` を削除し、出力先には変更を加えない（Skip）。

### 7.6 スキャン（プレビュー）
- 実ファイル操作はせず、予定操作列（Extract/CreateDir/Copy/ConflictCopy/FallbackCopy/Cleanup/Skip）を構築する。
- 競合予測は、存在チェックと比較条件に基づいて算出する。
- プラン構築ロジックは `JarScanner.ScanJar()` 内に統合されており、`PlannedOperations` は `JarScanResult` に直接格納される。

### 7.7 実行直前の整合性チェック
- スキャン時 `jar` スナップショットと実行直前スナップショットを比較する。
- 差分があれば実行中断し、再スキャンを要求する。

### 7.8 キャンセル
- `jar` 単位: 1件完了時点でキャンセル反映（既定）
- ファイル単位: 展開/コピー処理の節目ごとにキャンセル反映（任意）
- キャンセル時、`Processing` ステータスのModは自動的に `Skipped` に遷移する。

### 7.9 クリーンアップ処理
- `WorkDir` のみ再帰削除する（リンク先は辿らない）。
- `OutputRoot` や `TargetDir` 直下の既存ファイル/フォルダは削除しない。

## 8. エラー処理
- `jar` 単位で try/catch
- よくある失敗:
  - アクセス拒否（読み取り専用/権限）
  - 壊れた `jar` / 不正なエントリ
  - ファイルロック
  - パス長制限
  - 作業展開フォルダのクリーンアップ失敗
- 失敗時:
  - ステータスを `失敗` または `警告(Cleanup失敗)` に設定
  - 例外メッセージをログ出力
  - 可能なら次の `jar` へ継続

## 9. 実装方針（アーキテクチャ）
- パターン: MVVM
- 主な層:
  - UI層（WPF XAML）
  - ViewModel層（状態管理、コマンド）
  - Domain層（Scan/Plan/Execute/Validate）
  - Infrastructure層（ファイル操作、ログ、Zip）

### 9.1 主要クラス案
- `MainViewModel` : IDisposable
  - `TargetDir`
  - `OutputRoot`
  - `OutputRootSameAsTarget`
  - `Options`（BackupZip, CancelGranularity, LangFallback*）
  - `ObservableCollection<ModItemViewModel> Mods`
  - `IsScanning`, `IsExecuting`, `ScanCompleted`, `SnapshotFresh`
  - `ProgressPercent`, `ProgressText`, `StatusBarText`, `JarCount`
  - Commands: `BrowseFolderCommand`, `BrowseOutputCommand`, `ScanCommand`, `ExecuteCommand`, `CancelCommand`, `SaveLogCommand`
  - 設定永続化: `LoadSettings()`, `SaveSettings()`, `BuildCurrentSettings()`
  - FileSystemWatcher: `SetupWatcher()`, `OnJarChanged()`

- `ModItemViewModel`
  - `JarFileName`, `Integrity`, `LangCount`, `Strategy`
  - 操作カウント: `ExtractCount`, `CreateDirCount`, `CopyCount`, `ConflictCopyCount`, `CleanupCount`, `SkipCount`
  - `Status`, `SnapshotState`
  - 表示用プロパティ: `IntegrityDisplay`, `LangCountDisplay`, `StrategyDisplay`, `OperationSummary`, `StatusDisplay`, `SnapshotStateDisplay`
  - `FromScanResult(JarScanResult)` ファクトリメソッド

- `JarScanner`
  - `EnumerateJars(targetDir) -> List<string>`
  - `ScanJar(jarPath, outputRoot) -> JarScanResult`
  - ※ プラン構築ロジックも統合（旧 OperationPlanner の役割を包含）

- `ArchiveExtractor`
  - `DetermineWorkDir(jarPath) -> string`
  - `ExtractSecure(jarPath, dstDir, CancellationToken) -> string`
  - `ListEntries(jarPath) -> List<string>`
  - `ComputeShortHash(input) -> string` (private)

- `ConflictResolver`
  - `BuildConflictName(baseName, sourceTag, destDir) -> string`

- `SnapshotValidator`
  - `Validate(IEnumerable<JarScanResult>) -> List<string>`

- `Executor`
  - `ExecuteAsync(List<JarScanResult>, outputRoot, Options, IProgress, CancellationToken) -> ExecutionResult`
  - `CreateBackupAsync(targetDir, CancellationToken)`
  - `ApplyLangFallback(outLangDir, Options, langLogPath)` (private)

- `FileSystemService`
  - `EnsureDir(path)`
  - `CopyFile(src, dst)`
  - `DeleteRecursiveNoFollow(path)`
  - `IsReparsePoint(path) -> bool`
  - `IsSameContent(pathA, pathB) -> bool`
  - `BuildSnapshot(jarPath) -> JarSnapshot`
  - `EnumerateFilesNoFollow(dir, pattern) -> IEnumerable<string>`
  - `EnumerateDirectoriesNoFollow(dir) -> IEnumerable<string>`

- `Logger`
  - `Info/Warn/Error`
  - `Clear()`
  - `ExportAsync(path)`
  - `Entries` (ObservableCollection)
  - `LogAdded` イベント
  - Dispatcher対応（バックグラウンドスレッドからのUI更新）

- `SettingsService` : IDisposable
  - `Load() -> AppSettings`
  - `ScheduleSave(AppSettings)` — 400msデバウンス付き
  - `FlushAsync()` — 即時保存
  - `Dispose()` — 未保存設定をフラッシュ

- `SettingsStore`
  - `Load() -> AppSettings`
  - `Save(AppSettings)`
  - アトミック書き込み（.tmp + Replace）
  - 破損ファイルバックアップ

- `AppSettings`
  - 全設定プロパティ + `Clone()`

### 9.2 モデルクラス
- `JarScanResult` — 1jarのスキャン結果（PlannedOperations含む）
- `LangCandidate` — lang候補情報（ModId, ArchiveLangPath, Files）
- `PlannedOperation` — 予定操作1件（Type, Description, SourcePath, DestinationPath）
- `ExecutionPlan` / `JarExecutionPlan` — 実行計画モデル（定義のみ、実際にはJarScanResult.PlannedOperationsを使用）
- `ExecutionProgress` — jar単位の実行進捗（record型、Index/Current/Total/JarName/Stage/FinalStatus）
- `ExecutionResult` — 実行結果サマリ
- `JarSnapshot` — jarスナップショット（FileName, FileSize, LastWriteTimeUtc + Matches()）
- `LogEntry` — ログエントリ（Timestamp, Level, Message）
- `Options` — ユーザーオプション（BackupZip, CancelGranularity, UseWatcher, LangFallback*）

### 9.3 Enum定義
- `ProcessingStrategy`: LangFound / NoLang
- `PlannedOperationType`: Extract / CreateDir / Copy / ConflictCopy / FallbackCopy / Cleanup / Skip
- `JarIntegrity`: Unknown / OK / Corrupted
- `SnapshotState`: Current / Stale
- `ModStatus`: Pending / Scanning / Scanned / Processing / Success / Warning / Skipped / Failed
- `LogLevel`: Info / Warning / Error
- `CancelGranularity`: PerJar / PerFile
- `ExecutionProgressStage`: Started / Completed

### 9.4 Helperクラス
- `ObservableObject` — INotifyPropertyChanged基底クラス（SetPropertyメソッド）
- `RelayCommand` — 汎用ICommand実装
- `AsyncRelayCommand` — 非同期対応のICommand実装（二重実行防止付き）

## 10. Git運用上の注意
- 競合コピー（`*.conflict.*`）が増えるため、コミット前レビューを必須化する。
- `jar` と同階層に `modid/lang` 出力が生成されるため、追跡対象ルール（`.gitignore`）を明確にする。

## 11. テスト観点
- 正常系:
  - 単一 `jar`、langあり単一modid（jsonのみ / 複数ファイル / サブフォルダあり）
  - 複数 `jar`、langあり複数modid（modidごとに配置）
  - `OutputRoot` 直下に既に `<jar名>/` がある
    - 同一内容ファイル（スキップ）
    - 異なる内容ファイル（競合コピー生成）
  - スキャン差分なしで実行成功
  - langフォールバック: ソースあり・ターゲットなし → コピー生成
  - langフォールバック: ターゲット存在済み → スキップ
  - langフォールバック: ソースなし → スキップ（ログ出力）
  - 設定保存・復元: 設定変更後アプリ再起動で復元されること
  - 設定保存: 高速連打変更でもデバウンスされること
- 異常系:
  - 壊れた `jar` -> 失敗
  - assets無し -> Skip
  - lang無し -> Skip
  - 読み取り専用/権限不足/ロック
  - クリーンアップ失敗 -> 警告
  - シンボリックリンク/ジャンクション混在（リンク先を辿らない）
  - Zip Slip疑いエントリ（`../` 等）-> 展開拒否
  - 設定ファイル破損 -> バックアップ作成 + デフォルトリセット
  - 保存済みTargetDirが存在しない -> リセット + 警告
- 整合性:
  - スキャン後に `jar` 変更 -> 実行前検証で再スキャン要求
  - スキャン結果の予定操作と実行結果が一致（件数/対象）

## 12. 配布
- 方式A: Single-file self-contained（.NET同梱、導入が簡単、容量増）
- 方式B: framework-dependent（軽いがRuntime要）

## 13. 今後の拡張
- 対象を親フォルダ直下だけでなく再帰探索
- 削除前差分のエクスポート（CSV/JSON）
- Git status表示
- 除外ルール（例: `README.md` は残す）
