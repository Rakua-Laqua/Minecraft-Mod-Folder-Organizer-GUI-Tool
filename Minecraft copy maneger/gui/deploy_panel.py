"""Deployment control and preview panel widget."""

from __future__ import annotations

import os
import subprocess
from pathlib import Path
from typing import List, Optional

from PySide6.QtCore import Qt, Signal, QUrl
from PySide6.QtGui import QColor, QDesktopServices, QFont
from PySide6.QtWidgets import (
    QButtonGroup,
    QCheckBox,
    QFrame,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QMessageBox,
    QPlainTextEdit,
    QProgressBar,
    QPushButton,
    QRadioButton,
    QTreeWidget,
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
)

from core.conflict import ConflictDetector, ConflictGroup
from core.deployer import DeployDiff, DeployMode
from core.scanner import ModFileNode, format_bytes


class DeployPanel(QWidget):
    """Panel showing deployment options, diff previews, execution triggers, and logs."""

    deploy_requested = Signal()
    refresh_requested = Signal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self._conflicts: List[ConflictGroup] = []
        self._setup_ui()

    def _setup_ui(self) -> None:
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(10)

        # 1. Preview and Configuration Group
        config_group = QGroupBox("適用設定とプレビュー")
        config_layout = QVBoxLayout(config_group)
        config_layout.setSpacing(8)

        # Selection stats
        self.lbl_selection_summary = QLabel("選択: 0 個のMOD (0 B)")
        self.lbl_selection_summary.setStyleSheet("font-size: 14px; font-weight: bold; color: #a6e3a1;")
        config_layout.addWidget(self.lbl_selection_summary)

        # Deployment mode (Sync vs Add)
        mode_label = QLabel("動作モード:")
        mode_label.setStyleSheet("font-weight: bold; color: #89b4fa;")
        config_layout.addWidget(mode_label)

        self.mode_group = QButtonGroup(self)
        self.rb_sync = QRadioButton("同期モード（推奨）: 選択MODのみを配置し、過去に配置した未選択MODを安全に整理")
        self.rb_sync.setChecked(True)
        self.rb_sync.toggled.connect(lambda: self.refresh_requested.emit())
        self.mode_group.addButton(self.rb_sync)
        config_layout.addWidget(self.rb_sync)

        self.rb_add = QRadioButton("追加モード: 選択MODのみをコピー（削除は行いません）")
        self.rb_add.toggled.connect(lambda: self.refresh_requested.emit())
        self.mode_group.addButton(self.rb_add)
        config_layout.addWidget(self.rb_add)

        # Backup checkbox
        self.chk_backup = QCheckBox("反映前にバックアップを作成する (.modmanager_backup)")
        self.chk_backup.setChecked(True)
        config_layout.addWidget(self.chk_backup)

        # Diff summary line (Clickable to toggle details)
        self.diff_frame = QFrame()
        self.diff_frame.setStyleSheet(
            "background-color: #252538; border: 1px solid #313244; border-radius: 6px; padding: 6px 10px;"
        )
        self.diff_frame.setCursor(Qt.CursorShape.PointingHandCursor)
        self.diff_frame.setToolTip("クリックして差分詳細一覧を開閉できます")
        self.diff_frame.mousePressEvent = lambda event: self._toggle_diff_detail()

        diff_layout = QHBoxLayout(self.diff_frame)
        diff_layout.setContentsMargins(6, 2, 6, 2)
        diff_layout.setSpacing(14)

        self.lbl_diff_add = QLabel("➕ 新規: 0")
        self.lbl_diff_add.setStyleSheet("color: #a6e3a1; font-weight: bold; font-size: 13px;")
        diff_layout.addWidget(self.lbl_diff_add)

        self.lbl_diff_update = QLabel("🔄 更新: 0")
        self.lbl_diff_update.setStyleSheet("color: #89dceb; font-weight: bold; font-size: 13px;")
        diff_layout.addWidget(self.lbl_diff_update)

        self.lbl_diff_delete = QLabel("➖ 削除: 0")
        self.lbl_diff_delete.setStyleSheet("color: #f38ba8; font-weight: bold; font-size: 13px;")
        diff_layout.addWidget(self.lbl_diff_delete)

        self.lbl_diff_keep = QLabel("⏸️ 維持: 0")
        self.lbl_diff_keep.setStyleSheet("color: #a6adc8; font-size: 13px;")
        diff_layout.addWidget(self.lbl_diff_keep)

        diff_layout.addStretch()
        config_layout.addWidget(self.diff_frame)

        # Full-width independent Toggle Button (Prominent & easy to click)
        self.btn_toggle_diff = QPushButton("▼ 差分ファイル詳細を表示")
        self.btn_toggle_diff.setFixedHeight(34)
        self.btn_toggle_diff.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_toggle_diff.setStyleSheet(
            "QPushButton {"
            "  background-color: #28283d; border: 1px solid #3e405b; border-radius: 6px; "
            "  color: #89b4fa; font-weight: bold; font-size: 12px; padding: 4px 12px;"
            "}"
            "QPushButton:hover {"
            "  background-color: #363852; border-color: #585b70; color: #b4befe;"
            "}"
            "QPushButton:pressed {"
            "  background-color: #45475a;"
            "}"
        )
        self.btn_toggle_diff.clicked.connect(self._toggle_diff_detail)
        config_layout.addWidget(self.btn_toggle_diff)

        # Collapsible detailed diff tree
        self.diff_detail_tree = QTreeWidget()
        self.diff_detail_tree.setColumnCount(3)
        self.diff_detail_tree.setHeaderLabels(["ファイル名 / 状態", "サイズ", "備考"])
        self.diff_detail_tree.header().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        self.diff_detail_tree.header().setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        self.diff_detail_tree.header().setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        self.diff_detail_tree.setMinimumHeight(150)
        self.diff_detail_tree.setMaximumHeight(280)
        self.diff_detail_tree.setStyleSheet(
            "QTreeWidget { background-color: #1a1a27; border: 1px solid #313244; border-radius: 6px; font-size: 12px; }"
            "QTreeWidget::item { padding: 4px 6px; }"
        )
        self.diff_detail_tree.setVisible(False)
        config_layout.addWidget(self.diff_detail_tree)

        # Conflict Warning Box (Hidden when no conflicts)
        self.conflict_box = QFrame()
        self.conflict_box.setStyleSheet(
            "background-color: #451b22; border: 1px solid #f38ba8; border-radius: 6px; padding: 8px;"
        )
        conflict_layout = QHBoxLayout(self.conflict_box)
        conflict_layout.setContentsMargins(8, 4, 8, 4)

        self.lbl_conflict = QLabel("⚠️ 同名MODの衝突が検出されました！")
        self.lbl_conflict.setStyleSheet("color: #f38ba8; font-weight: bold;")
        conflict_layout.addWidget(self.lbl_conflict, stretch=1)

        self.btn_show_conflicts = QPushButton("衝突の詳細...")
        self.btn_show_conflicts.setStyleSheet("background-color: #733030; color: white;")
        self.btn_show_conflicts.clicked.connect(self._show_conflict_details)
        conflict_layout.addWidget(self.btn_show_conflicts)

        self.conflict_box.setVisible(False)
        config_layout.addWidget(self.conflict_box)

        layout.addWidget(config_group, stretch=2)

        # 2. Main Action Buttons
        action_layout = QHBoxLayout()
        action_layout.setSpacing(8)

        self.btn_deploy = QPushButton("🚀  modsフォルダへ反映 (デプロイ)")
        self.btn_deploy.setProperty("class", "primaryBtn")
        self.btn_deploy.setFixedHeight(44)
        self.btn_deploy.clicked.connect(self._on_deploy_clicked)
        action_layout.addWidget(self.btn_deploy, stretch=2)

        self.btn_open_mods = QPushButton("📂  modsフォルダを開く")
        self.btn_open_mods.setFixedHeight(44)
        self.btn_open_mods.clicked.connect(self._open_mods_folder)
        action_layout.addWidget(self.btn_open_mods, stretch=1)

        layout.addLayout(action_layout)

        # 3. Execution Log and Progress (Compact so tree gets ample space)
        log_group = QGroupBox("実行ログ・ステータス")
        log_layout = QVBoxLayout(log_group)
        log_layout.setSpacing(6)

        self.progress_bar = QProgressBar()
        self.progress_bar.setValue(0)
        self.progress_bar.setTextVisible(True)
        self.progress_bar.setFormat("%p% - 待機中")
        log_layout.addWidget(self.progress_bar)

        self.log_text = QPlainTextEdit()
        self.log_text.setReadOnly(True)
        self.log_text.setMinimumHeight(70)
        self.log_text.setMaximumHeight(130)
        self.log_text.setPlaceholderText("実行履歴がここに表示されます...")
        log_layout.addWidget(self.log_text)

        log_btn_layout = QHBoxLayout()
        log_btn_layout.addStretch()
        self.btn_clear_log = QPushButton("ログ消去")
        self.btn_clear_log.clicked.connect(self.log_text.clear)
        log_btn_layout.addWidget(self.btn_clear_log)
        log_layout.addLayout(log_btn_layout)

        layout.addWidget(log_group, stretch=0)

        # Store destination path for opening
        self._destination_path: Optional[Path] = None

    def set_destination_path(self, path: Optional[Path]) -> None:
        self._destination_path = path

    def get_deploy_mode(self) -> DeployMode:
        return DeployMode.SYNC if self.rb_sync.isChecked() else DeployMode.ADD

    def is_backup_enabled(self) -> bool:
        return self.chk_backup.isChecked()

    def update_summary(
        self,
        selected_mods: List[ModFileNode],
        diff: Optional[DeployDiff],
        conflicts: List[ConflictGroup],
    ) -> None:
        """Update summary stats, diff labels, and conflict warnings."""
        count = len(selected_mods)
        total_size = sum(m.size_bytes for m in selected_mods)
        self.lbl_selection_summary.setText(f"選択: {count} 個のMOD ({format_bytes(total_size)})")

        self._conflicts = conflicts
        if conflicts:
            self.conflict_box.setVisible(True)
            self.lbl_conflict.setText(f"⚠️ 同名MODの衝突が {len(conflicts)} 件検出されました！")
            self.btn_deploy.setEnabled(False)
            self.btn_deploy.setToolTip("同名MODの衝突を解決するまで反映できません")
        else:
            self.conflict_box.setVisible(False)
            self.btn_deploy.setEnabled(count > 0)
            self.btn_deploy.setToolTip("")

        if diff:
            self.lbl_diff_add.setText(f"➕ 新規: {len(diff.to_add)}")
            self.lbl_diff_update.setText(f"🔄 更新: {len(diff.to_update)}")
            self.lbl_diff_delete.setText(f"➖ 削除: {len(diff.to_delete)}")
            self.lbl_diff_keep.setText(f"⏸️ 維持: {len(diff.to_keep)}")
            self._last_changes = diff.total_changes
            self._update_diff_tree(diff)
            self._update_toggle_btn_text()
        else:
            self._last_changes = 0
            self._update_diff_tree(None)
            self._update_toggle_btn_text()

    def set_deploy_mode(self, mode: DeployMode | str) -> None:
        if str(mode) == DeployMode.ADD or mode == "add":
            self.rb_add.setChecked(True)
        else:
            self.rb_sync.setChecked(True)

    def set_backup_enabled(self, enabled: bool) -> None:
        self.chk_backup.setChecked(enabled)

    def is_diff_detail_visible(self) -> bool:
        return self.diff_detail_tree.isVisible()

    def set_diff_detail_visible(self, visible: bool) -> None:
        self.diff_detail_tree.setVisible(visible)
        self._update_toggle_btn_text()

    def _toggle_diff_detail(self) -> None:
        """Toggle visibility of detailed diff tree."""
        visible = not self.diff_detail_tree.isVisible()
        self.diff_detail_tree.setVisible(visible)
        self._update_toggle_btn_text()

    def _update_toggle_btn_text(self) -> None:
        changes = getattr(self, "_last_changes", 0)
        if self.diff_detail_tree.isVisible():
            self.btn_toggle_diff.setText("▲ 差分ファイル詳細を閉じる")
        else:
            if changes > 0:
                self.btn_toggle_diff.setText(f"▼ 差分ファイル詳細を表示 ({changes} 件の変更)")
            else:
                self.btn_toggle_diff.setText("▼ 差分ファイル詳細を表示")

    def _update_diff_tree(self, diff: Optional[DeployDiff]) -> None:
        """Populate collapsible tree with concrete files categorized by action."""
        self.diff_detail_tree.clear()
        if not diff:
            return

        bold_font = QFont()
        bold_font.setBold(True)

        # 1. New files (to_add)
        if diff.to_add:
            cat_add = QTreeWidgetItem(self.diff_detail_tree)
            cat_add.setText(0, f"➕ 新規配置 ({len(diff.to_add)} 件)")
            cat_add.setForeground(0, QColor("#a6e3a1"))
            cat_add.setFont(0, bold_font)
            for m in diff.to_add:
                item = QTreeWidgetItem(cat_add)
                item.setText(0, f"📦 {m.name}")
                item.setText(1, m.formatted_size)
                item.setText(2, "modsへ新規コピー")
                item.setToolTip(0, f"元パス: {m.rel_path}")
            cat_add.setExpanded(True)

        # 2. Update files (to_update)
        if diff.to_update:
            cat_update = QTreeWidgetItem(self.diff_detail_tree)
            cat_update.setText(0, f"🔄 上書き更新 ({len(diff.to_update)} 件)")
            cat_update.setForeground(0, QColor("#89dceb"))
            cat_update.setFont(0, bold_font)
            for m in diff.to_update:
                item = QTreeWidgetItem(cat_update)
                item.setText(0, f"📦 {m.name}")
                item.setText(1, m.formatted_size)
                item.setText(2, "既存MODと差分があるため上書き")
                item.setToolTip(0, f"元パス: {m.rel_path}")
            cat_update.setExpanded(True)

        # 3. Deleted files (to_delete)
        if diff.to_delete:
            cat_del = QTreeWidgetItem(self.diff_detail_tree)
            cat_del.setText(0, f"➖ 削除整理 ({len(diff.to_delete)} 件)")
            cat_del.setForeground(0, QColor("#f38ba8"))
            cat_del.setFont(0, bold_font)
            for name in diff.to_delete:
                item = QTreeWidgetItem(cat_del)
                item.setText(0, f"🗑️ {name}")
                item.setText(1, "-")
                item.setText(2, "未選択のためmodsから削除")
            cat_del.setExpanded(True)

        # 4. Kept files (to_keep) - collapsed by default
        if diff.to_keep:
            cat_keep = QTreeWidgetItem(self.diff_detail_tree)
            cat_keep.setText(0, f"⏸️ 変更なし・維持 ({len(diff.to_keep)} 件)")
            cat_keep.setForeground(0, QColor("#a6adc8"))
            cat_keep.setFont(0, bold_font)
            for m in diff.to_keep:
                item = QTreeWidgetItem(cat_keep)
                item.setText(0, f"📦 {m.name}")
                item.setText(1, m.formatted_size)
                item.setText(2, "同名・同サイズのためスキップ")
                item.setToolTip(0, f"元パス: {m.rel_path}")
            cat_keep.setExpanded(False)

        # 5. Unmanaged manual mods - collapsed by default
        if diff.unmanaged_files:
            cat_unman = QTreeWidgetItem(self.diff_detail_tree)
            cat_unman.setText(0, f"🛡️ 手動配置の保護MOD ({len(diff.unmanaged_files)} 件)")
            cat_unman.setForeground(0, QColor("#fab387"))
            cat_unman.setFont(0, bold_font)
            for name in diff.unmanaged_files:
                item = QTreeWidgetItem(cat_unman)
                item.setText(0, f"🔒 {name}")
                item.setText(1, "-")
                item.setText(2, "手動配置ファイル（削除されません）")
            cat_unman.setExpanded(False)

        # If there are no diff items at all
        if not (diff.to_add or diff.to_update or diff.to_delete or diff.to_keep or diff.unmanaged_files):
            empty_item = QTreeWidgetItem(self.diff_detail_tree)
            empty_item.setText(0, "📦 （現在、選択されているMODがありません）")
            empty_item.setText(2, "左のツリーからMODを選択してください")
            empty_item.setForeground(0, QColor("#a6adc8"))
            empty_item.setForeground(2, QColor("#6c7086"))

    def _show_conflict_details(self) -> None:
        if not self._conflicts:
            return
        msg = ConflictDetector.format_warning_message(self._conflicts, max_display=10)
        QMessageBox.warning(self, "同名MOD衝突の警告", msg)

    def _on_deploy_clicked(self) -> None:
        if self._conflicts:
            self._show_conflict_details()
            return
        self.deploy_requested.emit()

    def _open_mods_folder(self) -> None:
        if not self._destination_path:
            QMessageBox.information(self, "情報", "適用先modsフォルダが指定されていません。")
            return
        
        path = self._destination_path.resolve()
        if not path.exists():
            try:
                path.mkdir(parents=True, exist_ok=True)
            except Exception as e:
                QMessageBox.critical(self, "エラー", f"フォルダを作成できませんでした:\n{e}")
                return

        # Open in Windows Explorer
        try:
            os.startfile(str(path))
        except Exception:
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(path)))

    def append_log(self, text: str) -> None:
        self.log_text.appendPlainText(text)

    def set_progress(self, current: int, total: int, status_text: str = "") -> None:
        if total <= 0:
            self.progress_bar.setValue(0)
            self.progress_bar.setFormat(status_text or "待機中")
            return
        pct = int((current / total) * 100)
        self.progress_bar.setValue(pct)
        if status_text:
            self.progress_bar.setFormat(f"{pct}% - {status_text}")
        else:
            self.progress_bar.setFormat(f"{pct}% ({current}/{total})")
