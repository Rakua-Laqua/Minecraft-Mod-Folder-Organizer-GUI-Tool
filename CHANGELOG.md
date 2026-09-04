# CHANGELOG

## Unreleased
### Added
- 選択した親フォルダ配下のJARをサブフォルダまで再帰スキャンするように対応
- スキャン結果に親フォルダからのJAR相対パスを保持し、同名JARを別カテゴリでも安全に区別
- 既定のlang入出力先を `<親フォルダ>/_lang_output/` とし、元JARの相対フォルダ構造を出力側にも保持

### Changed
- `_backup`、`_lang_output`、指定した出力ルート配下とreparse pointを再帰JAR探索から除外
- FileSystemWatcherをサブフォルダ監視に対応し、除外フォルダ内の変更は無視
- 実行直前にJAR一覧を再列挙し、スキャン後に追加されたJARも再スキャン要求として検出
- スキャン一覧は同名JARを識別できるよう相対パス表示に変更

## v2.2.0 - 2026-09-03
### Added
- 外部で編集したlangファイルを、対応するJARの `assets/<modid>/lang/` へ追加・更新する「JARへ反映」機能を追加
- 単一modid／複数modidの既存出力構造を逆変換し、複数JARをまとめて反映
- 同一内容は無変更、未存在エントリは追加、内容が異なる既存エントリは更新として集計
- JARの一時コピーを更新・検証してから元ファイルと置換する安全な更新処理を追加
- 標準的な署名ファイルを含むJARを検出し、確認画面とログに警告を表示
- 抽出時に生成された `*.conflict.*` ファイルをJAR反映対象から自動除外

### Changed
- 実行ボタンを「langを抽出」と「JARへ反映」に分離
- 出力ルートの表記を「lang入出力ルート」に変更
- 画面表示とアセンブリのバージョンを2.2.0へ更新
- ファイルのシンボリックリンクもlang入出力対象から除外し、リンク非追従を徹底

## v2.1.2 - 2026-02-25
### Changed
- 抽出先の `lang` フォルダ生成を廃止し、lang配下のファイルを直接出力するように変更
- 1つのjarにlang候補が1つだけの場合は `modid` フォルダも作らず、`<出力先>/<jar名>/` 直下へ出力するように変更
- 1つのjarにlang候補が複数ある場合は `modid` フォルダを維持し、`<出力先>/<jar名>/<modid>/` 直下へ出力するように変更
- スキャン時の事前計画表示（CreateDir/Copy/Conflict）と実行時ログの出力先表示を新ルールに統一

## v2.1.1 - 2026-02-25
### Fixed
- 1つのjar内に複数のlangルートがある場合でも、抽出結果をjar単位でまとめて保持するように修正
- 抽出先パスから `assets` 階層を除外し、<出力先>/<jar名>/<modid>/lang/... で出力するように修正
- スキャン時の事前計画表示（CreateDir/Copy/Conflict）も新しい出力先ルールに統一

## v2.1.0 - 2026-02-25
### Added
- langフォールバック機能を追加（ターゲットファイルが無い場合、ソースからコピーして自動生成）。
  - 例: `ja_jp.json` が存在しなければ `en_us.json` からコピーして生成。
  - コピー元/コピー先のファイル名（拡張子なし）をオプションで自由に変更可能。
  - 拡張子はソースファイルのものをそのまま保持。
- Options / AppSettings に `LangFallbackEnabled`, `LangFallbackSourceName`, `LangFallbackTargetName` を追加。
- Executor に `ApplyLangFallback` メソッドを追加。
- PlannedOperationType に `FallbackCopy` を追加。
- MainWindow.xaml のオプション領域に「langフォールバック」セクションを追加。
- 設定は `settings.json` に永続化され、次回起動時に復元。

## v1.0.1 - 2026-02-23
### Fixed
- スキャン中にログ更新でクラッシュする問題を修正（`Logger` の `ObservableCollection` 更新を UI スレッドに統一）。

### Docs
- バグ詳細ログ `docs/logs/BUG-0001.md` を追加。
- 報告フロー文書 `docs/バグ・エラー報告フロー.md` を追加。
- `進捗.md` の「バグ / エラー修正ログ」に本件を追記。
