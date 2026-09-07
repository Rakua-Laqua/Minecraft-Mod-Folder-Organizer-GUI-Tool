"""Profile selection and management bar widget."""

from __future__ import annotations

from typing import List, Optional

from PySide6.QtCore import Signal
from PySide6.QtWidgets import (
    QComboBox,
    QGroupBox,
    QHBoxLayout,
    QInputDialog,
    QLabel,
    QMessageBox,
    QPushButton,
    QVBoxLayout,
    QWidget,
)


class ProfileBar(QGroupBox):
    """Widget for selecting, saving, and managing mod setup profiles."""

    profile_selected = Signal(str)
    save_requested = Signal(str)
    new_requested = Signal(str)
    delete_requested = Signal(str)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__("プロファイル管理", parent)
        self._block_signals = False
        self._setup_ui()

    def _setup_ui(self) -> None:
        layout = QVBoxLayout(self)
        layout.setSpacing(8)

        row_layout = QHBoxLayout()
        row_layout.setSpacing(6)

        self.combo_profiles = QComboBox()
        self.combo_profiles.setMinimumWidth(180)
        self.combo_profiles.addItem("（プロファイル未選択）", None)
        self.combo_profiles.currentIndexChanged.connect(self._on_combo_changed)
        row_layout.addWidget(self.combo_profiles, stretch=1)

        self.btn_new = QPushButton("＋ 新規保存")
        self.btn_new.setToolTip("現在の選択状態を新しいプロファイルとして保存します")
        self.btn_new.clicked.connect(self._on_new_clicked)
        row_layout.addWidget(self.btn_new)

        self.btn_save = QPushButton("💾 上書き")
        self.btn_save.setToolTip("選択中のプロファイルに現在のチェック状態を上書き保存します")
        self.btn_save.clicked.connect(self._on_save_clicked)
        self.btn_save.setEnabled(False)
        row_layout.addWidget(self.btn_save)

        self.btn_delete = QPushButton("🗑 削除")
        self.btn_delete.setProperty("class", "dangerBtn")
        self.btn_delete.setToolTip("選択中のプロファイルを削除します")
        self.btn_delete.clicked.connect(self._on_delete_clicked)
        self.btn_delete.setEnabled(False)
        row_layout.addWidget(self.btn_delete)

        layout.addLayout(row_layout)

    def set_profiles(self, names: List[str], current_name: Optional[str] = None) -> None:
        """Update combo box with available profile names."""
        self._block_signals = True
        self.combo_profiles.clear()
        self.combo_profiles.addItem("（プロファイル未選択）", None)

        selected_idx = 0
        for i, name in enumerate(names):
            self.combo_profiles.addItem(name, name)
            if name == current_name:
                selected_idx = i + 1

        self.combo_profiles.setCurrentIndex(selected_idx)
        self._update_button_states()
        self._block_signals = False

    def get_current_profile_name(self) -> Optional[str]:
        data = self.combo_profiles.currentData()
        return data if isinstance(data, str) else None

    def _on_combo_changed(self, index: int) -> None:
        self._update_button_states()
        if not self._block_signals:
            name = self.get_current_profile_name()
            if name:
                self.profile_selected.emit(name)

    def _update_button_states(self) -> None:
        has_selection = self.get_current_profile_name() is not None
        self.btn_save.setEnabled(has_selection)
        self.btn_delete.setEnabled(has_selection)

    def _on_new_clicked(self) -> None:
        name, ok = QInputDialog.getText(
            self,
            "新規プロファイルの保存",
            "プロファイル名を入力してください（例: 1.20.1 軽量構成, 工業MODなど）:",
        )
        if ok and name.strip():
            self.new_requested.emit(name.strip())

    def _on_save_clicked(self) -> None:
        name = self.get_current_profile_name()
        if not name:
            return
        reply = QMessageBox.question(
            self,
            "プロファイルの上書き確認",
            f"プロファイル「{name}」を現在の選択状態で上書きしますか？",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.Yes,
        )
        if reply == QMessageBox.StandardButton.Yes:
            self.save_requested.emit(name)

    def _on_delete_clicked(self) -> None:
        name = self.get_current_profile_name()
        if not name:
            return
        reply = QMessageBox.warning(
            self,
            "プロファイルの削除確認",
            f"プロファイル「{name}」を削除してもよろしいですか？\n（MODファイル自体は削除されません）",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if reply == QMessageBox.StandardButton.Yes:
            self.delete_requested.emit(name)
