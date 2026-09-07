"""Profile management for saving and restoring mod configurations.

Saves selected mod relative paths into a JSON file so that mod setups
(e.g., 'Lightweight QoL', 'Create Tech', 'Dimension RPG') can be swapped
with a single click.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional


@dataclass
class ModProfile:
    name: str
    selected_rel_paths: List[str]
    description: str = ""
    updated_at: str = ""


class ProfileManager:
    """Manages mod configuration profiles stored in a JSON file."""

    def __init__(self, storage_path: Path):
        self.storage_path = Path(storage_path).resolve()
        self._profiles: Dict[str, ModProfile] = {}
        self.load()

    def load(self) -> None:
        """Load profiles from disk."""
        self._profiles.clear()
        if not self.storage_path.exists():
            return

        try:
            with open(self.storage_path, "r", encoding="utf-8") as f:
                data = json.load(f)
                for name, item in data.items():
                    self._profiles[name] = ModProfile(
                        name=name,
                        selected_rel_paths=item.get("selected_rel_paths", []),
                        description=item.get("description", ""),
                        updated_at=item.get("updated_at", ""),
                    )
        except Exception:
            self._profiles.clear()

    def save_all(self) -> None:
        """Persist all profiles to disk."""
        try:
            self.storage_path.parent.mkdir(parents=True, exist_ok=True)
            data = {}
            for name, prof in self._profiles.items():
                data[name] = {
                    "selected_rel_paths": sorted(prof.selected_rel_paths),
                    "description": prof.description,
                    "updated_at": prof.updated_at,
                }
            with open(self.storage_path, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2, ensure_ascii=False)
        except Exception as e:
            raise IOError(f"プロファイルの保存に失敗しました: {e}")

    def list_profiles(self) -> List[str]:
        """Return sorted list of profile names."""
        return sorted(list(self._profiles.keys()))

    def get_profile(self, name: str) -> Optional[ModProfile]:
        return self._profiles.get(name)

    def save_profile(self, name: str, rel_paths: List[str], description: str = "") -> ModProfile:
        name = name.strip()
        if not name:
            raise ValueError("プロファイル名を入力してください。")

        profile = ModProfile(
            name=name,
            selected_rel_paths=rel_paths,
            description=description,
            updated_at=datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        )
        self._profiles[name] = profile
        self.save_all()
        return profile

    def delete_profile(self, name: str) -> bool:
        if name in self._profiles:
            del self._profiles[name]
            self.save_all()
            return True
        return False
