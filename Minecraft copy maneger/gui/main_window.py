"""Main window of Minecraft Copy Manager application."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Optional

from PySide6.QtCore import QObject, QThread, Qt, Signal, QUrl
from PySide6.QtGui import QDesktopServices, QIcon
from PySide6.QtWidgets import (
    QCheckBox,
    QFileDialog,
    QFrame,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QSplitter,
    QVBoxLayout,
    QWidget,
)

from core.conflict import ConflictDetector
from core.deployer import DeployDiff, DeployMode, DeployResult, ModDeployer
from core.profile_manager import ProfileManager
from core.scanner import ModFolderNode, ModScanner

from .deploy_panel import DeployPanel
from .mod_tree_widget import ModTreeWidget
from .profile_bar import ProfileBar

CONFIG_FILENAME = "config.json"
PROFILES_FILENAME = "profiles.json"


class DeployWorker(QThread):
    """Background worker for copying and syncing mods safely."""

    progress = Signal(int, int, str)
    finished = Signal(object)  # DeployResult

    def __init__(
        self,
        selected_mods,
        mods_dir: Path,
        mode: DeployMode,
        create_backup: bool,
    ):
        super().__init__()
        self.selected_mods = selected_mods
        self.mods_dir = mods_dir
        self.mode = mode
        self.create_backup = create_backup

    def run(self) -> None:
        def progress_cb(cur, tot, msg):
            self.progress.emit(cur, tot, msg)

        result = ModDeployer.execute(
            selected_mods=self.selected_mods,
            mods_dir=self.mods_dir,
            mode=self.mode,
            create_backup=self.create_backup,
            progress_callback=progress_cb,
        )
        self.finished.emit(result)


class MainWindow(QMainWindow):
    """Main Application Window."""

    def __init__(self):
        super().__init__()
        self.setWindowTitle("Minecraft Copy Manager - MOD構成デプロイツール")
        self.resize(1240, 940)
        self.setMinimumSize(920, 720)

        # Base directories
        self.app_dir = Path(__file__).resolve().parent.parent
        self.config_path = self.app_dir / CONFIG_FILENAME
        self.profile_manager = ProfileManager(self.app_dir / PROFILES_FILENAME)

        self._scanned_root: Optional[ModFolderNode] = None
        self._deploy_worker: Optional[DeployWorker] = None
        self._saved_last_profile: Optional[str] = None
        self._saved_last_rel_paths: list[str] = []
        self._normal_geom = {"x": 60, "y": 40, "w": 1240, "h": 940}

        self._setup_ui()
        self._load_config()

        # Connect signals
        self._connect_signals()

        # Initial scan if repo path exists
        repo_path = self.get_repo_path()
        if repo_path and repo_path.exists() and repo_path.is_dir():
            self.rescan_repository()

    def _setup_ui(self) -> None:
        central_widget = QWidget(self)
        self.setCentralWidget(central_widget)

        main_layout = QVBoxLayout(central_widget)
        main_layout.setContentsMargins(14, 14, 14, 14)
        main_layout.setSpacing(12)

        # 1. Top Path Configuration Header
        top_frame = QFrame()
        top_frame.setProperty("class", "cardFrame")
        top_layout = QVBoxLayout(top_frame)
        top_layout.setSpacing(10)

        # Repo path row
        repo_row = QHBoxLayout()
        repo_row.setSpacing(8)
        lbl_repo = QLabel("管理元 (MOD倉庫):")
        lbl_repo.setFixedWidth(130)
        lbl_repo.setStyleSheet("font-weight: bold; color: #89b4fa;")
        repo_row.addWidget(lbl_repo)

        self.edit_repo_path = QLineEdit()
        self.edit_repo_path.setPlaceholderText("例: D:\\Minecraft_mod_management")
        repo_row.addWidget(self.edit_repo_path, stretch=1)

        self.btn_browse_repo = QPushButton("参照...")
        self.btn_browse_repo.clicked.connect(self._browse_repo_path)
        repo_row.addWidget(self.btn_browse_repo)

        self.btn_open_repo = QPushButton("開く")
        self.btn_open_repo.clicked.connect(self._open_repo_folder)
        repo_row.addWidget(self.btn_open_repo)

        self.btn_rescan = QPushButton("🔄 再読み込み")
        self.btn_rescan.clicked.connect(self.rescan_repository)
        repo_row.addWidget(self.btn_rescan)

        top_layout.addLayout(repo_row)

        # Mods destination row
        mods_row = QHBoxLayout()
        mods_row.setSpacing(8)
        lbl_mods = QLabel("適用先 (mods):")
        lbl_mods.setFixedWidth(130)
        lbl_mods.setStyleSheet("font-weight: bold; color: #a6e3a1;")
        mods_row.addWidget(lbl_mods)

        self.edit_mods_path = QLineEdit()
        # Default Minecraft mods path on Windows
        default_mods = os.path.expandvars(r"%APPDATA%\.minecraft\mods")
        self.edit_mods_path.setText(default_mods)
        self.edit_mods_path.setPlaceholderText("例: %APPDATA%\\.minecraft\\mods")
        mods_row.addWidget(self.edit_mods_path, stretch=1)

        self.btn_browse_mods = QPushButton("参照...")
        self.btn_browse_mods.clicked.connect(self._browse_mods_path)
        mods_row.addWidget(self.btn_browse_mods)

        self.btn_default_mods = QPushButton("標準mods")
        self.btn_default_mods.setToolTip(r"Minecraftの標準modsフォルダ（%APPDATA%\.minecraft\mods）に設定")
        self.btn_default_mods.clicked.connect(self._set_default_mods_path)
        mods_row.addWidget(self.btn_default_mods)

        top_layout.addLayout(mods_row)

        # Header Options row
        opt_row = QHBoxLayout()
        self.chk_show_disabled = QCheckBox("無効フォルダ（_backup, 有効化しないmod 等）もツリーに表示する")
        self.chk_show_disabled.setChecked(True)
        self.chk_show_disabled.toggled.connect(self._on_show_disabled_toggled)
        opt_row.addWidget(self.chk_show_disabled)
        opt_row.addStretch()

        top_layout.addLayout(opt_row)
        main_layout.addWidget(top_frame)

        # 2. Middle Splitter Area (Left: Tree, Right: Profile + Deploy)
        self.splitter = QSplitter(Qt.Orientation.Horizontal)
        self.splitter.setHandleWidth(6)

        # Left Container (Mod Tree)
        left_container = QWidget()
        left_layout = QVBoxLayout(left_container)
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.setSpacing(6)

        tree_header = QLabel("【MODライブラリ ツリー】")
        tree_header.setStyleSheet("font-weight: bold; font-size: 14px; color: #89b4fa;")
        left_layout.addWidget(tree_header)

        self.tree_widget = ModTreeWidget()
        left_layout.addWidget(self.tree_widget, stretch=1)
        self.splitter.addWidget(left_container)

        # Right Container (Profile + Deploy Panel)
        right_container = QWidget()
        right_layout = QVBoxLayout(right_container)
        right_layout.setContentsMargins(0, 0, 0, 0)
        right_layout.setSpacing(8)

        # Profile management
        self.profile_bar = ProfileBar()
        right_layout.addWidget(self.profile_bar)

        # Deployment controls
        self.deploy_panel = DeployPanel()
        right_layout.addWidget(self.deploy_panel, stretch=1)
        self.splitter.addWidget(right_container)

        # Set initial splitter proportions (52% left, 48% right for comfortable preview)
        self.splitter.setSizes([600, 560])
        main_layout.addWidget(self.splitter, stretch=1)

    def _connect_signals(self) -> None:
        # Path edits
        self.edit_mods_path.textChanged.connect(self._on_mods_path_changed)
        self.edit_repo_path.returnPressed.connect(self.rescan_repository)

        # Tree selection change
        self.tree_widget.selection_changed.connect(self._on_tree_selection_changed)

        # Profile bar signals
        self.profile_bar.profile_selected.connect(self._load_profile)
        self.profile_bar.save_requested.connect(self._save_profile)
        self.profile_bar.new_requested.connect(self._new_profile)
        self.profile_bar.delete_requested.connect(self._delete_profile)

        # Deploy panel signals & auto-save on option changes
        self.deploy_panel.deploy_requested.connect(self._start_deployment)
        self.deploy_panel.refresh_requested.connect(self._update_preview)
        self.deploy_panel.rb_sync.toggled.connect(self._save_config)
        self.deploy_panel.rb_add.toggled.connect(self._save_config)
        self.deploy_panel.chk_backup.toggled.connect(self._save_config)
        self.deploy_panel.btn_toggle_diff.clicked.connect(self._save_config)

    def get_repo_path(self) -> Optional[Path]:
        raw = self.edit_repo_path.text().strip()
        if not raw:
            return None
        expanded = os.path.expandvars(raw)
        return Path(expanded)

    def get_mods_path(self) -> Optional[Path]:
        raw = self.edit_mods_path.text().strip()
        if not raw:
            return None
        expanded = os.path.expandvars(raw)
        return Path(expanded)

    def _browse_repo_path(self) -> None:
        initial_dir = str(self.get_repo_path() or "")
        dir_path = QFileDialog.getExistingDirectory(
            self, "MOD管理元（倉庫）フォルダを選択", initial_dir
        )
        if dir_path:
            self.edit_repo_path.setText(dir_path)
            self.rescan_repository()
            self._save_config()

    def _browse_mods_path(self) -> None:
        initial_dir = str(self.get_mods_path() or "")
        dir_path = QFileDialog.getExistingDirectory(
            self, "Minecraft modsフォルダを選択", initial_dir
        )
        if dir_path:
            self.edit_mods_path.setText(dir_path)
            self._save_config()

    def _set_default_mods_path(self) -> None:
        default_mods = os.path.expandvars(r"%APPDATA%\.minecraft\mods")
        self.edit_mods_path.setText(default_mods)
        self._save_config()

    def _open_repo_folder(self) -> None:
        path = self.get_repo_path()
        if not path or not path.exists():
            QMessageBox.warning(self, "警告", "管理元フォルダが存在しないか、指定されていません。")
            return
        try:
            os.startfile(str(path))
        except Exception:
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(path)))

    def _on_mods_path_changed(self, text: str) -> None:
        self.deploy_panel.set_destination_path(self.get_mods_path())
        self._update_preview()
        self._save_config()

    def _on_show_disabled_toggled(self, checked: bool) -> None:
        self.tree_widget.set_show_disabled(checked)
        self._save_config()

    def rescan_repository(self) -> None:
        """Scan the repo path and populate the tree widget."""
        repo_path = self.get_repo_path()
        if not repo_path:
            return

        if not repo_path.exists() or not repo_path.is_dir():
            QMessageBox.critical(
                self, "エラー", f"指定された管理元フォルダが存在しません:\n{repo_path}"
            )
            return

        try:
            self._scanned_root = ModScanner.scan(repo_path)
            show_disabled = self.chk_show_disabled.isChecked()
            self.tree_widget.populate(self._scanned_root, show_disabled=show_disabled)
            self.deploy_panel.append_log(
                f"[{self.get_time_str()}] 管理元を読み込みました: {self._scanned_root.total_jar_count} 個のMODを検出"
            )

            # Restore previous selection state (profile or explicit relative paths)
            if self._saved_last_profile and self.profile_manager.get_profile(self._saved_last_profile):
                self._load_profile(self._saved_last_profile)
            elif self._saved_last_rel_paths:
                self.tree_widget.set_selected_rel_paths(set(self._saved_last_rel_paths))

            self._update_preview()
        except Exception as e:
            QMessageBox.critical(self, "走査エラー", f"フォルダの走査中にエラーが発生しました:\n{e}")

    def _on_tree_selection_changed(self) -> None:
        """Called whenever checkbox selection changes in the tree."""
        self._update_preview()

    def _update_preview(self) -> None:
        """Recalculate diff, detect conflicts, and refresh right panel."""
        selected_mods = self.tree_widget.get_selected_mod_nodes()
        mods_path = self.get_mods_path()
        mode = self.deploy_panel.get_deploy_mode()

        # 1. Conflict detection
        conflicts = ConflictDetector.detect(selected_mods)

        # 2. Diff calculation
        diff: Optional[DeployDiff] = None
        if mods_path and not conflicts:
            try:
                diff = ModDeployer.calculate_diff(selected_mods, mods_path, mode)
            except Exception:
                diff = None

        self.deploy_panel.set_destination_path(mods_path)
        self.deploy_panel.update_summary(selected_mods, diff, conflicts)

    def _start_deployment(self) -> None:
        """Trigger deployment in background thread."""
        if self._deploy_worker and self._deploy_worker.isRunning():
            return

        mods_path = self.get_mods_path()
        if not mods_path:
            QMessageBox.warning(self, "エラー", "適用先modsフォルダが指定されていません。")
            return

        selected_mods = self.tree_widget.get_selected_mod_nodes()
        if not selected_mods:
            QMessageBox.warning(self, "確認", "反映するMODが選択されていません。")
            return

        # Double check conflicts
        conflicts = ConflictDetector.detect(selected_mods)
        if conflicts:
            self.deploy_panel._show_conflict_details()
            return

        mode = self.deploy_panel.get_deploy_mode()
        create_backup = self.deploy_panel.is_backup_enabled()

        # Confirmation dialog with details
        diff = ModDeployer.calculate_diff(selected_mods, mods_path, mode)
        mode_text = "【同期モード】" if mode == DeployMode.SYNC else "【追加モード】"
        msg = (
            f"{mode_text} でmodsフォルダへ反映します。\n\n"
            f"• 新規配置: {len(diff.to_add)} 個\n"
            f"• 上書き更新: {len(diff.to_update)} 個\n"
            f"• 削除整理: {len(diff.to_delete)} 個\n"
            f"• 変更なし: {len(diff.to_keep)} 個\n"
            f"• 手動配置の保護MOD: {len(diff.unmanaged_files)} 個\n\n"
            f"適用先: {mods_path}\n\n"
            "実行してよろしいですか？"
        )
        reply = QMessageBox.question(
            self,
            "デプロイの確認",
            msg,
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.Yes,
        )
        if reply != QMessageBox.StandardButton.Yes:
            return

        # Disable main deploy button during execution
        self.deploy_panel.btn_deploy.setEnabled(False)
        self.deploy_panel.set_progress(0, max(1, diff.total_changes), "デプロイ準備中...")

        self._deploy_worker = DeployWorker(
            selected_mods=selected_mods,
            mods_dir=mods_path,
            mode=mode,
            create_backup=create_backup,
        )
        self._deploy_worker.progress.connect(self.deploy_panel.set_progress)
        self._deploy_worker.finished.connect(self._on_deployment_finished)
        self._deploy_worker.start()

    def _on_deployment_finished(self, result: DeployResult) -> None:
        self.deploy_panel.btn_deploy.setEnabled(True)
        for line in result.logs:
            self.deploy_panel.append_log(line)

        if result.success:
            self.deploy_panel.set_progress(100, 100, "完了")
            summary = (
                f"反映が正常に完了しました！\n\n"
                f"• 追加: {result.added_count} 個\n"
                f"• 更新: {result.updated_count} 個\n"
                f"• 削除: {result.deleted_count} 個\n"
                f"• 維持: {result.kept_count} 個"
            )
            if result.backup_path:
                summary += f"\n\n※ 直前状態をバックアップしました:\n{result.backup_path}"
            QMessageBox.information(self, "完了", summary)
        else:
            self.deploy_panel.set_progress(0, 1, "エラー")
            QMessageBox.critical(self, "デプロイ失敗", f"デプロイ中にエラーが発生しました:\n{result.error_message}")

        self._update_preview()

    # Profile handling
    def _refresh_profile_list(self, current_name: Optional[str] = None) -> None:
        names = self.profile_manager.list_profiles()
        self.profile_bar.set_profiles(names, current_name=current_name)

    def _load_profile(self, name: str) -> None:
        prof = self.profile_manager.get_profile(name)
        if not prof:
            return
        self.tree_widget.set_selected_rel_paths(set(prof.selected_rel_paths))
        self.deploy_panel.append_log(
            f"[{self.get_time_str()}] プロファイル「{name}」を適用しました ({len(prof.selected_rel_paths)} 個のMOD)"
        )
        self._save_config()

    def _save_profile(self, name: str) -> None:
        selected_rel_paths = self.tree_widget.get_selected_rel_paths()
        try:
            self.profile_manager.save_profile(name, selected_rel_paths)
            self._refresh_profile_list(current_name=name)
            self.deploy_panel.append_log(
                f"[{self.get_time_str()}] プロファイル「{name}」を上書き保存しました"
            )
            QMessageBox.information(self, "保存完了", f"プロファイル「{name}」を上書き保存しました。")
            self._save_config()
        except Exception as e:
            QMessageBox.critical(self, "エラー", f"プロファイルの保存に失敗しました:\n{e}")

    def _new_profile(self, name: str) -> None:
        selected_rel_paths = self.tree_widget.get_selected_rel_paths()
        try:
            self.profile_manager.save_profile(name, selected_rel_paths)
            self._refresh_profile_list(current_name=name)
            self.deploy_panel.append_log(
                f"[{self.get_time_str()}] 新規プロファイル「{name}」を作成しました"
            )
            QMessageBox.information(self, "作成完了", f"プロファイル「{name}」を作成・保存しました。")
            self._save_config()
        except Exception as e:
            QMessageBox.critical(self, "エラー", f"プロファイルの作成に失敗しました:\n{e}")

    def _delete_profile(self, name: str) -> None:
        try:
            self.profile_manager.delete_profile(name)
            self._refresh_profile_list(current_name=None)
            self.deploy_panel.append_log(
                f"[{self.get_time_str()}] プロファイル「{name}」を削除しました"
            )
            self._save_config()
        except Exception as e:
            QMessageBox.critical(self, "エラー", f"プロファイルの削除に失敗しました:\n{e}")

    # Window geometry tracking
    def resizeEvent(self, event) -> None:
        if not self.isMaximized():
            geom = self.geometry()
            self._normal_geom = {"x": geom.x(), "y": geom.y(), "w": geom.width(), "h": geom.height()}
        super().resizeEvent(event)

    def moveEvent(self, event) -> None:
        if not self.isMaximized():
            geom = self.geometry()
            self._normal_geom = {"x": geom.x(), "y": geom.y(), "w": geom.width(), "h": geom.height()}
        super().moveEvent(event)

    # Config persistence
    def _load_config(self) -> None:
        """Load persistent settings from config.json."""
        self._refresh_profile_list()

        if not self.config_path.exists():
            return

        try:
            with open(self.config_path, "r", encoding="utf-8") as f:
                data = json.load(f)

            if "repo_path" in data and data["repo_path"]:
                self.edit_repo_path.setText(data["repo_path"])
            if "mods_path" in data and data["mods_path"]:
                self.edit_mods_path.setText(data["mods_path"])
            if "show_disabled" in data:
                self.chk_show_disabled.setChecked(data["show_disabled"])

            # Deploy panel options
            if "deploy_mode" in data:
                self.deploy_panel.set_deploy_mode(data["deploy_mode"])
            if "create_backup" in data:
                self.deploy_panel.set_backup_enabled(data["create_backup"])
            if "diff_detail_visible" in data:
                self.deploy_panel.set_diff_detail_visible(data["diff_detail_visible"])

            # Profile & selection state
            if "last_profile" in data and data["last_profile"]:
                self._saved_last_profile = data["last_profile"]
                self._refresh_profile_list(current_name=data["last_profile"])
            if "last_selected_rel_paths" in data and isinstance(data["last_selected_rel_paths"], list):
                self._saved_last_rel_paths = data["last_selected_rel_paths"]

            # Splitter layout
            if "splitter_sizes" in data and isinstance(data["splitter_sizes"], list) and len(data["splitter_sizes"]) == 2:
                self.splitter.setSizes(data["splitter_sizes"])

            # Window geometry & state
            if "window_geometry" in data:
                geom = data["window_geometry"]
                w = max(1240, geom.get("w", 1240))
                h = max(940, geom.get("h", 940))
                x = geom.get("x", 60)
                y = geom.get("y", 40)
                self._normal_geom = {"x": x, "y": y, "w": w, "h": h}
                self.setGeometry(x, y, w, h)

            if data.get("is_maximized", False):
                self.showMaximized()
        except Exception:
            pass

    def _save_config(self) -> None:
        """Save persistent settings to config.json."""
        is_max = self.isMaximized()
        geom_dict = self._normal_geom if is_max else {
            "x": self.geometry().x(),
            "y": self.geometry().y(),
            "w": self.geometry().width(),
            "h": self.geometry().height(),
        }

        data = {
            "repo_path": self.edit_repo_path.text().strip(),
            "mods_path": self.edit_mods_path.text().strip(),
            "show_disabled": self.chk_show_disabled.isChecked(),
            "last_profile": self.profile_bar.get_current_profile_name(),
            "last_selected_rel_paths": self.tree_widget.get_selected_rel_paths(),
            "deploy_mode": self.deploy_panel.get_deploy_mode().value,
            "create_backup": self.deploy_panel.is_backup_enabled(),
            "diff_detail_visible": self.deploy_panel.is_diff_detail_visible(),
            "splitter_sizes": self.splitter.sizes(),
            "is_maximized": is_max,
            "window_geometry": geom_dict,
        }
        try:
            with open(self.config_path, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2, ensure_ascii=False)
        except Exception:
            pass

    def closeEvent(self, event) -> None:
        self._save_config()
        super().closeEvent(event)

    @staticmethod
    def get_time_str() -> str:
        from datetime import datetime
        return datetime.now().strftime("%H:%M:%S")
