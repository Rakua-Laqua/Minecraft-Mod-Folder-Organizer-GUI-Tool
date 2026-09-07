"""Deployer module for copying and syncing mods into Minecraft's mods directory.

Handles both "Sync" mode (keeps only chosen mods among those previously managed
via .modmanager.json, protecting manually placed user mods) and "Add" mode
(copies chosen mods without deleting anything).
"""

from __future__ import annotations

import json
import os
import shutil
from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum
from pathlib import Path
from typing import Callable, Dict, List, Optional, Set, Tuple

from .scanner import ModFileNode

MANIFEST_FILENAME = ".modmanager.json"
BACKUP_DIR_NAME = ".modmanager_backup"


class DeployMode(str, Enum):
    SYNC = "sync"  # Clean up unselected managed mods, keep manual mods, copy new mods
    ADD = "add"    # Only copy selected mods, never delete anything


@dataclass
class DeployDiff:
    """Summary of changes that will be applied to the mods directory."""
    to_add: List[ModFileNode] = field(default_factory=list)
    to_update: List[ModFileNode] = field(default_factory=list)
    to_keep: List[ModFileNode] = field(default_factory=list)
    to_delete: List[str] = field(default_factory=list)  # Filenames in mods dir to delete
    unmanaged_files: List[str] = field(default_factory=list)  # Preserved manual mods

    @property
    def total_changes(self) -> int:
        return len(self.to_add) + len(self.to_update) + len(self.to_delete)


@dataclass
class DeployResult:
    """Outcome of a deployment operation."""
    success: bool
    added_count: int = 0
    updated_count: int = 0
    deleted_count: int = 0
    kept_count: int = 0
    backup_path: Optional[Path] = None
    error_message: str = ""
    logs: List[str] = field(default_factory=list)


class ModDeployer:
    """Executes copy / sync deployment of mods to Minecraft mods directory."""

    @staticmethod
    def get_manifest_path(mods_dir: Path) -> Path:
        return mods_dir / MANIFEST_FILENAME

    @classmethod
    def load_manifest(cls, mods_dir: Path) -> Set[str]:
        """Load the list of filenames previously managed by this tool."""
        manifest_file = cls.get_manifest_path(mods_dir)
        if not manifest_file.exists():
            return set()
        try:
            with open(manifest_file, "r", encoding="utf-8") as f:
                data = json.load(f)
                files = data.get("managed_files", [])
                return set(files)
        except Exception:
            return set()

    @classmethod
    def save_manifest(cls, mods_dir: Path, managed_files: Set[str]) -> None:
        """Save the updated list of managed filenames."""
        manifest_file = cls.get_manifest_path(mods_dir)
        data = {
            "version": "1.0",
            "last_synced": datetime.now().isoformat(),
            "managed_files": sorted(list(managed_files)),
        }
        try:
            with open(manifest_file, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2, ensure_ascii=False)
        except Exception as e:
            raise IOError(f"マニフェストファイルの保存に失敗しました: {e}")

    @classmethod
    def calculate_diff(
        cls,
        selected_mods: List[ModFileNode],
        mods_dir: Path,
        mode: DeployMode = DeployMode.SYNC,
    ) -> DeployDiff:
        """Analyze what changes will take place without altering the filesystem."""
        diff = DeployDiff()
        if not mods_dir.exists():
            # If mods dir doesn't exist yet, all selected are additions
            diff.to_add = list(selected_mods)
            return diff

        managed_filenames = cls.load_manifest(mods_dir)

        # Map selected mods by lowercase name
        selected_by_name: Dict[str, ModFileNode] = {
            m.name.lower(): m for m in selected_mods
        }

        # Inspect current files in mods_dir
        existing_files: Dict[str, Path] = {}
        try:
            for entry in os.scandir(mods_dir):
                if entry.is_file() and entry.name.lower().endswith(".jar"):
                    existing_files[entry.name.lower()] = Path(entry.path)
        except OSError:
            pass

        # Classify selected mods (add, update, or keep)
        for mod in selected_mods:
            key = mod.name.lower()
            if key in existing_files:
                target_file = existing_files[key]
                try:
                    src_stat = mod.full_path.stat()
                    dst_stat = target_file.stat()
                    # If size and mtime are identical, no need to re-copy
                    if src_stat.st_size == dst_stat.st_size and abs(src_stat.st_mtime - dst_stat.st_mtime) < 1.0:
                        diff.to_keep.append(mod)
                    else:
                        diff.to_update.append(mod)
                except OSError:
                    diff.to_update.append(mod)
            else:
                diff.to_add.append(mod)

        # In SYNC mode, find files that were managed before but are not selected now
        if mode == DeployMode.SYNC:
            for managed_name in managed_filenames:
                key = managed_name.lower()
                if key not in selected_by_name and key in existing_files:
                    diff.to_delete.append(existing_files[key].name)

        # List unmanaged (manually placed) files currently in mods dir
        for name_lower, path in existing_files.items():
            if path.name not in managed_filenames and name_lower not in selected_by_name:
                diff.unmanaged_files.append(path.name)

        return diff

    @classmethod
    def execute(
        cls,
        selected_mods: List[ModFileNode],
        mods_dir: Path,
        mode: DeployMode = DeployMode.SYNC,
        create_backup: bool = True,
        progress_callback: Optional[Callable[[int, int, str], None]] = None,
    ) -> DeployResult:
        """Deploy the selected mods into the Minecraft mods folder."""
        logs: List[str] = []

        def log(msg: str) -> None:
            logs.append(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}")

        mods_dir = Path(mods_dir).resolve()
        if not mods_dir.exists():
            try:
                mods_dir.mkdir(parents=True, exist_ok=True)
                log(f"modsフォルダを作成しました: {mods_dir}")
            except Exception as e:
                return DeployResult(
                    success=False,
                    error_message=f"modsフォルダの作成に失敗しました: {e}",
                    logs=logs,
                )

        diff = cls.calculate_diff(selected_mods, mods_dir, mode)
        total_ops = len(diff.to_delete) + len(diff.to_add) + len(diff.to_update)
        current_op = 0

        def update_progress(msg: str) -> None:
            nonlocal current_op
            if progress_callback:
                progress_callback(current_op, max(1, total_ops), msg)

        # 1. Create backup if enabled and there are deletions or updates
        backup_folder: Optional[Path] = None
        if create_backup and (diff.to_delete or diff.to_update):
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            backup_folder = mods_dir / BACKUP_DIR_NAME / timestamp
            try:
                backup_folder.mkdir(parents=True, exist_ok=True)
                # Backup files to delete
                for del_name in diff.to_delete:
                    src = mods_dir / del_name
                    if src.exists():
                        shutil.copy2(src, backup_folder / del_name)
                # Backup files to update
                for mod in diff.to_update:
                    src = mods_dir / mod.name
                    if src.exists():
                        shutil.copy2(src, backup_folder / mod.name)
                log(f"バックアップを作成しました: {backup_folder.name}")
            except Exception as e:
                log(f"警告: バックアップの作成中にエラーが発生しました: {e}")

        # 2. Deletions (SYNC mode only)
        deleted_count = 0
        if mode == DeployMode.SYNC:
            for del_name in diff.to_delete:
                current_op += 1
                target = mods_dir / del_name
                try:
                    if target.exists():
                        target.unlink()
                        deleted_count += 1
                        log(f"削除 (未選択): {del_name}")
                        update_progress(f"削除中: {del_name}")
                except Exception as e:
                    log(f"エラー: {del_name} の削除に失敗しました: {e}")

        # 3. Additions and Updates (Copying files)
        added_count = 0
        updated_count = 0
        mods_to_copy = [(m, True) for m in diff.to_add] + [(m, False) for m in diff.to_update]

        for mod, is_new in mods_to_copy:
            current_op += 1
            dest_file = mods_dir / mod.name
            action_label = "新規コピー" if is_new else "更新コピー"
            update_progress(f"{action_label}: {mod.name}")
            try:
                shutil.copy2(mod.full_path, dest_file)
                if is_new:
                    added_count += 1
                else:
                    updated_count += 1
                log(f"{action_label}: {mod.name}")
            except Exception as e:
                log(f"エラー: {mod.name} のコピーに失敗しました: {e}")

        kept_count = len(diff.to_keep)
        if kept_count > 0:
            log(f"変更なし (スキップ): {kept_count} 個のMOD")

        # 4. Update manifest
        try:
            current_managed = cls.load_manifest(mods_dir)
            if mode == DeployMode.SYNC:
                # In SYNC mode, managed files are strictly the currently selected files
                new_managed = {m.name for m in selected_mods}
            else:
                # In ADD mode, union previously managed files and newly selected files
                new_managed = current_managed.union({m.name for m in selected_mods})
            cls.save_manifest(mods_dir, new_managed)
            log("マニフェスト (.modmanager.json) を更新しました")
        except Exception as e:
            log(f"警告: マニフェスト更新エラー: {e}")

        update_progress("デプロイ完了")
        log(f"完了: 追加 {added_count} / 更新 {updated_count} / 削除 {deleted_count} / 維持 {kept_count}")

        return DeployResult(
            success=True,
            added_count=added_count,
            updated_count=updated_count,
            deleted_count=deleted_count,
            kept_count=kept_count,
            backup_path=backup_folder,
            logs=logs,
        )
