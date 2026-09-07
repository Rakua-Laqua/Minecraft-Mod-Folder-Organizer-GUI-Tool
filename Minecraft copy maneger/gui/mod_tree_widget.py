"""Hierarchical tree widget for mod folders and .jar files.

Supports:
- Checkbox tri-state propagation (parent <-> children)
- Partial check state when some children are unselected
- Incremental search filtering
- Showing/hiding inactive / backup folders
- Batch actions (Select All, Deselect All, Invert, Expand, Collapse)
- State export/import for profiles
"""

from __future__ import annotations

from pathlib import Path
from typing import Dict, List, Optional, Set

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QColor, QFont, QIcon
from PySide6.QtWidgets import (
    QAbstractItemView,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QPushButton,
    QStyle,
    QTreeWidget,
    QTreeWidgetItem,
    QVBoxLayout,
    QWidget,
)

from core.scanner import ModFileNode, ModFolderNode


class ModTreeItem(QTreeWidgetItem):
    """Custom QTreeWidgetItem holding node reference and metadata."""

    def __init__(self, node: ModFolderNode | ModFileNode, parent=None):
        super().__init__(parent)
        self.node = node
        self.is_file = isinstance(node, ModFileNode)
        self.is_disabled = node.is_disabled_folder if hasattr(node, "is_disabled_folder") else node.is_in_disabled_folder

        self.setText(0, node.name)
        self.setText(1, node.formatted_size)
        self.setFlags(
            self.flags()
            | Qt.ItemFlag.ItemIsUserCheckable
            | Qt.ItemFlag.ItemIsEnabled
            | Qt.ItemFlag.ItemIsSelectable
        )
        self.setCheckState(0, Qt.CheckState.Unchecked)

        # Visual styling for folders vs files
        if self.is_file:
            # Mod jar icon
            self.setText(0, f"📦  {node.name}")
        else:
            # Folder icon with total mod count
            count_str = f" ({node.total_jar_count} MOD)" if node.total_jar_count > 0 else ""
            self.setText(0, f"📁  {node.name}{count_str}")
            font = self.font(0)
            font.setBold(True)
            self.setFont(0, font)

        if self.is_disabled:
            # Dim disabled / backup items
            self.setForeground(0, QColor("#6c7086"))
            self.setForeground(1, QColor("#6c7086"))


class ModTreeWidget(QWidget):
    """Container widget with search bar, action buttons, and tree view."""

    # Emitted whenever any checkbox state changes
    selection_changed = Signal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self._updating_checks = False
        self._root_node: Optional[ModFolderNode] = None
        self._all_items: List[ModTreeItem] = []
        self._file_items_by_rel_path: Dict[str, ModTreeItem] = {}
        self._show_disabled = True

        self._setup_ui()

    def _setup_ui(self) -> None:
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(8)

        # 1. Search Bar
        search_layout = QHBoxLayout()
        search_layout.setSpacing(6)
        self.search_input = QLineEdit()
        self.search_input.setPlaceholderText("🔍  MOD名・フォルダ名で絞り込み...")
        self.search_input.setClearButtonEnabled(True)
        self.search_input.textChanged.connect(self._on_search_changed)
        search_layout.addWidget(self.search_input)

        self.btn_clear_search = QPushButton("クリア")
        self.btn_clear_search.clicked.connect(self.search_input.clear)
        search_layout.addWidget(self.btn_clear_search)
        layout.addLayout(search_layout)

        # 2. Batch Operation Buttons
        btn_layout = QHBoxLayout()
        btn_layout.setSpacing(6)

        self.btn_select_all = QPushButton("全選択")
        self.btn_select_all.clicked.connect(self.select_all)
        btn_layout.addWidget(self.btn_select_all)

        self.btn_deselect_all = QPushButton("全解除")
        self.btn_deselect_all.clicked.connect(self.deselect_all)
        btn_layout.addWidget(self.btn_deselect_all)

        self.btn_invert = QPushButton("選択反転")
        self.btn_invert.clicked.connect(self.invert_selection)
        btn_layout.addWidget(self.btn_invert)

        btn_layout.addStretch()

        self.btn_expand_all = QPushButton("すべて展開")
        self.btn_expand_all.clicked.connect(self.expand_all)
        btn_layout.addWidget(self.btn_expand_all)

        self.btn_collapse_all = QPushButton("すべて折りたたむ")
        self.btn_collapse_all.clicked.connect(self.collapse_all)
        btn_layout.addWidget(self.btn_collapse_all)

        layout.addLayout(btn_layout)

        # 3. Tree View
        self.tree = QTreeWidget()
        self.tree.setColumnCount(2)
        self.tree.setHeaderLabels(["MOD / フォルダ名", "サイズ"])
        self.tree.header().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        self.tree.header().setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        self.tree.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.tree.itemChanged.connect(self._on_item_changed)
        layout.addWidget(self.tree)

        # 4. Status summary footer
        self.lbl_status = QLabel("MODが読み込まれていません")
        self.lbl_status.setStyleSheet("color: #a6adc8; font-size: 12px;")
        layout.addWidget(self.lbl_status)

    def populate(self, root_node: ModFolderNode, show_disabled: bool = True) -> None:
        """Populate tree widget with the scanned root folder node."""
        self._updating_checks = True
        self.tree.clear()
        self._all_items.clear()
        self._file_items_by_rel_path.clear()
        self._root_node = root_node
        self._show_disabled = show_disabled

        for child in root_node.children:
            self._add_node_recursive(child, parent_item=None)

        # Expand top-level items by default
        for i in range(self.tree.topLevelItemCount()):
            self.tree.topLevelItem(i).setExpanded(True)

        self._updating_checks = False
        self._update_status_label()
        self._apply_disabled_filter()
        self.selection_changed.emit()

    def _add_node_recursive(
        self, node: ModFolderNode | ModFileNode, parent_item: Optional[ModTreeItem]
    ) -> ModTreeItem:
        if parent_item is None:
            item = ModTreeItem(node, self.tree)
        else:
            item = ModTreeItem(node, parent_item)

        self._all_items.append(item)

        if isinstance(node, ModFileNode):
            self._file_items_by_rel_path[node.rel_path] = item
        elif isinstance(node, ModFolderNode):
            for child in node.children:
                self._add_node_recursive(child, item)

        return item

    def set_show_disabled(self, show: bool) -> None:
        """Toggle visibility of disabled / backup folders."""
        self._show_disabled = show
        self._apply_disabled_filter()

    def _apply_disabled_filter(self) -> None:
        """Hide disabled items if show_disabled is False."""
        self.tree.setUpdatesEnabled(False)
        for item in self._all_items:
            if item.is_disabled and not self._show_disabled:
                item.setHidden(True)
            else:
                # Obey search filter if active
                if not self.search_input.text().strip():
                    item.setHidden(False)
        self.tree.setUpdatesEnabled(True)

    def _on_item_changed(self, item: QTreeWidgetItem, column: int) -> None:
        """Handle checkbox clicks with two-way tri-state propagation."""
        if column != 0 or self._updating_checks or not isinstance(item, ModTreeItem):
            return

        self._updating_checks = True
        state = item.checkState(0)

        # 1. Downward propagation: if user changed a folder to Checked/Unchecked, apply to all descendants
        if not item.is_file and state in (Qt.CheckState.Checked, Qt.CheckState.Unchecked):
            self._propagate_down(item, state)

        # 2. Upward propagation: update parent folder state based on siblings
        self._propagate_up(item)

        self._updating_checks = False
        self.selection_changed.emit()

    def _propagate_down(self, parent: ModTreeItem, state: Qt.CheckState) -> None:
        """Recursively set check state on all children."""
        for i in range(parent.childCount()):
            child = parent.child(i)
            if isinstance(child, ModTreeItem):
                child.setCheckState(0, state)
                if not child.is_file:
                    self._propagate_down(child, state)

    def _propagate_up(self, item: QTreeWidgetItem) -> None:
        """Recursively recalculate and set check state of parent items."""
        parent = item.parent()
        if not parent or not isinstance(parent, ModTreeItem):
            return

        total_children = parent.childCount()
        checked_count = 0
        partial_count = 0

        for i in range(total_children):
            child = parent.child(i)
            st = child.checkState(0)
            if st == Qt.CheckState.Checked:
                checked_count += 1
            elif st == Qt.CheckState.PartiallyChecked:
                partial_count += 1

        if checked_count == total_children:
            parent.setCheckState(0, Qt.CheckState.Checked)
        elif checked_count == 0 and partial_count == 0:
            parent.setCheckState(0, Qt.CheckState.Unchecked)
        else:
            parent.setCheckState(0, Qt.CheckState.PartiallyChecked)

        # Recurse up to top-level
        self._propagate_up(parent)

    def _on_search_changed(self, text: str) -> None:
        """Filter tree items by search text in real time."""
        query = text.strip().lower()
        self.tree.setUpdatesEnabled(False)

        if not query:
            # Restore visibility according to disabled filter
            for item in self._all_items:
                if item.is_disabled and not self._show_disabled:
                    item.setHidden(True)
                else:
                    item.setHidden(False)
            self.tree.setUpdatesEnabled(True)
            return

        def check_item_match(item: ModTreeItem) -> bool:
            node_name = item.node.name.lower()
            name_match = query in node_name

            child_matched = False
            for i in range(item.childCount()):
                child = item.child(i)
                if isinstance(child, ModTreeItem) and check_item_match(child):
                    child_matched = True

            visible = name_match or child_matched
            if item.is_disabled and not self._show_disabled:
                visible = False

            item.setHidden(not visible)
            if visible and child_matched:
                item.setExpanded(True)

            return visible

        for i in range(self.tree.topLevelItemCount()):
            top_item = self.tree.topLevelItem(i)
            if isinstance(top_item, ModTreeItem):
                check_item_match(top_item)

        self.tree.setUpdatesEnabled(True)

    def select_all(self) -> None:
        """Select all visible items."""
        self._updating_checks = True
        for item in self._all_items:
            if not item.isHidden():
                item.setCheckState(0, Qt.CheckState.Checked)
        self._updating_checks = False
        self.selection_changed.emit()

    def deselect_all(self) -> None:
        """Deselect all items."""
        self._updating_checks = True
        for item in self._all_items:
            item.setCheckState(0, Qt.CheckState.Unchecked)
        self._updating_checks = False
        self.selection_changed.emit()

    def invert_selection(self) -> None:
        """Invert selection for all mod files and update parents."""
        self._updating_checks = True
        for item in self._all_items:
            if item.is_file and not item.isHidden():
                current = item.checkState(0)
                new_state = Qt.CheckState.Unchecked if current == Qt.CheckState.Checked else Qt.CheckState.Checked
                item.setCheckState(0, new_state)

        # Recalculate folder states from bottom up
        for item in reversed(self._all_items):
            if not item.is_file:
                self._update_folder_state(item)

        self._updating_checks = False
        self.selection_changed.emit()

    def _update_folder_state(self, folder_item: ModTreeItem) -> None:
        total = folder_item.childCount()
        if total == 0:
            return
        checked = sum(1 for i in range(total) if folder_item.child(i).checkState(0) == Qt.CheckState.Checked)
        partial = sum(1 for i in range(total) if folder_item.child(i).checkState(0) == Qt.CheckState.PartiallyChecked)

        if checked == total:
            folder_item.setCheckState(0, Qt.CheckState.Checked)
        elif checked == 0 and partial == 0:
            folder_item.setCheckState(0, Qt.CheckState.Unchecked)
        else:
            folder_item.setCheckState(0, Qt.CheckState.PartiallyChecked)

    def expand_all(self) -> None:
        self.tree.expandAll()

    def collapse_all(self) -> None:
        self.tree.collapseAll()

    def get_selected_mod_nodes(self) -> List[ModFileNode]:
        """Return list of selected ModFileNode objects."""
        selected: List[ModFileNode] = []
        for item in self._all_items:
            if item.is_file and item.checkState(0) == Qt.CheckState.Checked:
                if isinstance(item.node, ModFileNode):
                    selected.append(item.node)
        return selected

    def get_selected_rel_paths(self) -> List[str]:
        """Return list of relative paths of all selected mods."""
        return [m.rel_path for m in self.get_selected_mod_nodes()]

    def set_selected_rel_paths(self, rel_paths: Set[str]) -> None:
        """Restore selection from a set of relative paths."""
        self._updating_checks = True
        rel_paths_set = set(rel_paths)

        # First set files
        for rel_path, item in self._file_items_by_rel_path.items():
            state = Qt.CheckState.Checked if rel_path in rel_paths_set else Qt.CheckState.Unchecked
            item.setCheckState(0, state)

        # Then calculate folder states bottom-up
        for item in reversed(self._all_items):
            if not item.is_file:
                self._update_folder_state(item)

        self._updating_checks = False
        self.selection_changed.emit()

    def _update_status_label(self) -> None:
        if not self._root_node:
            self.lbl_status.setText("MODが読み込まれていません")
            return
        total_jars = self._root_node.total_jar_count
        total_size = self._root_node.formatted_size
        self.lbl_status.setText(f"検出: 合計 {total_jars} 個のMOD ({total_size})")
