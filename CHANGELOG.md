# CHANGELOG

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
