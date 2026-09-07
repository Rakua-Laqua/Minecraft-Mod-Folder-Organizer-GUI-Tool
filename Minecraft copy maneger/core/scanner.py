"""Directory scanner for Minecraft mods repository.

Recursively scans directories to discover .jar files while ignoring
non-jar files (.json, .txt, .toml, .md, etc.). Builds a tree structure
suitable for display in the UI.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Union


def format_bytes(size: int) -> str:
    """Format bytes into a human-readable string (KB, MB, GB)."""
    if size < 1024:
        return f"{size} B"
    elif size < 1024 * 1024:
        return f"{size / 1024:.1f} KB"
    elif size < 1024 * 1024 * 1024:
        return f"{size / (1024 * 1024):.1f} MB"
    else:
        return f"{size / (1024 * 1024 * 1024):.2f} GB"


# Keywords commonly used to mark inactive / backup mod folders
DISABLED_KEYWORDS = ["_backup", "有効化しないmod", "無効化", "disabled", "bak"]


@dataclass
class ModFileNode:
    """Represents a .jar file."""
    name: str
    rel_path: str
    full_path: Path
    size_bytes: int
    is_in_disabled_folder: bool = False

    @property
    def formatted_size(self) -> str:
        return format_bytes(self.size_bytes)


@dataclass
class ModFolderNode:
    """Represents a folder containing mods or subfolders."""
    name: str
    rel_path: str
    full_path: Path
    is_disabled_folder: bool = False
    children: List[Union[ModFolderNode, ModFileNode]] = field(default_factory=list)

    @property
    def total_jar_count(self) -> int:
        count = 0
        for child in self.children:
            if isinstance(child, ModFileNode):
                count += 1
            elif isinstance(child, ModFolderNode):
                count += child.total_jar_count
        return count

    @property
    def total_size_bytes(self) -> int:
        total = 0
        for child in self.children:
            if isinstance(child, ModFileNode):
                total += child.size_bytes
            elif isinstance(child, ModFolderNode):
                total += child.total_size_bytes
        return total

    @property
    def formatted_size(self) -> str:
        return format_bytes(self.total_size_bytes)


class ModScanner:
    """Scans a root directory and builds a hierarchical tree of folders and .jar files."""

    @classmethod
    def is_disabled_folder_name(cls, name: str) -> bool:
        lowered = name.lower()
        return any(kw.lower() in lowered for kw in DISABLED_KEYWORDS)

    @classmethod
    def scan(cls, root_path: Union[str, Path]) -> ModFolderNode:
        """Scan the root directory recursively and return the root ModFolderNode.
        
        Only folders that directly or indirectly contain at least one .jar file
        are included in the returned tree.
        """
        root_path = Path(root_path).resolve()
        if not root_path.exists() or not root_path.is_dir():
            raise FileNotFoundError(f"Root path does not exist or is not a directory: {root_path}")

        return cls._scan_dir(root_path, root_path, is_parent_disabled=False)

    @classmethod
    def _scan_dir(
        cls, current_dir: Path, root_path: Path, is_parent_disabled: bool
    ) -> ModFolderNode:
        rel_path = "" if current_dir == root_path else str(current_dir.relative_to(root_path))
        folder_name = current_dir.name or str(current_dir)
        
        is_disabled = is_parent_disabled or cls.is_disabled_folder_name(folder_name)
        
        folder_node = ModFolderNode(
            name=folder_name,
            rel_path=rel_path,
            full_path=current_dir,
            is_disabled_folder=is_disabled,
        )

        try:
            with os.scandir(current_dir) as entries:
                # Sort entries: directories first, then files alphabetically
                sorted_entries = sorted(entries, key=lambda e: (not e.is_dir(), e.name.lower()))
                
                for entry in sorted_entries:
                    try:
                        if entry.is_dir(follow_symlinks=False):
                            child_dir = Path(entry.path)
                            child_node = cls._scan_dir(child_dir, root_path, is_disabled)
                            # Only include subfolder if it contains at least one .jar
                            if child_node.total_jar_count > 0:
                                folder_node.children.append(child_node)
                        elif entry.is_file(follow_symlinks=False):
                            if entry.name.lower().endswith(".jar"):
                                try:
                                    size = entry.stat().st_size
                                except OSError:
                                    size = 0
                                file_rel_path = str(Path(entry.path).relative_to(root_path))
                                file_node = ModFileNode(
                                    name=entry.name,
                                    rel_path=file_rel_path,
                                    full_path=Path(entry.path),
                                    size_bytes=size,
                                    is_in_disabled_folder=is_disabled,
                                )
                                folder_node.children.append(file_node)
                    except OSError:
                        # Skip files or folders that cannot be accessed
                        continue
        except OSError:
            pass

        return folder_node
