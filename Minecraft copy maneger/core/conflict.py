"""Conflict detection for selected mod files.

Detects when multiple .jar files with the same filename (e.g. from different
version folders or environments) are selected simultaneously for deployment.
"""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Union

from .scanner import ModFileNode


@dataclass
class ConflictGroup:
    """Represents a conflict where the same filename is selected from multiple paths."""
    filename: str
    paths: List[Path]

    @property
    def count(self) -> int:
        return len(self.paths)


class ConflictDetector:
    """Detects filename conflicts across selected mod files."""

    @staticmethod
    def detect(selected_files: List[Union[ModFileNode, Path]]) -> List[ConflictGroup]:
        """Group selected files by their lowercase filename and find duplicates."""
        name_to_paths: Dict[str, List[Path]] = defaultdict(list)
        name_to_orig_name: Dict[str, str] = {}

        for item in selected_files:
            if isinstance(item, ModFileNode):
                path = item.full_path
                orig_name = item.name
            else:
                path = Path(item)
                orig_name = path.name

            key = orig_name.lower()
            name_to_paths[key].append(path)
            if key not in name_to_orig_name:
                name_to_orig_name[key] = orig_name

        conflicts: List[ConflictGroup] = []
        for key, paths in name_to_paths.items():
            if len(paths) > 1:
                conflicts.append(ConflictGroup(
                    filename=name_to_orig_name[key],
                    paths=paths
                ))

        # Sort conflicts alphabetically by filename
        conflicts.sort(key=lambda c: c.filename.lower())
        return conflicts

    @staticmethod
    def format_warning_message(conflicts: List[ConflictGroup], max_display: int = 5) -> str:
        """Format conflict groups into a readable warning message."""
        if not conflicts:
            return ""

        lines = [
            f"同名のMODが複数選択されています（合計 {len(conflicts)} 件の衝突）。",
            "modsフォルダ直下には同一ファイル名のMODを1つしか配置できないため、どちらか一方を選択解除してください。\n",
        ]

        for i, group in enumerate(conflicts[:max_display]):
            lines.append(f"【{group.filename}】({group.count} 箇所)")
            for p in group.paths:
                lines.append(f"  • {p}")
            lines.append("")

        if len(conflicts) > max_display:
            lines.append(f"... 他 {len(conflicts) - max_display} 件の衝突があります。")

        return "\n".join(lines)
