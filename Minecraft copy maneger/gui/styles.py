"""Modern dark theme styles for Minecraft Copy Manager.
Inspired by Catppuccin / Modern Flat Dark palette with Minecraft emerald accents.
"""

DARK_THEME_QSS = """
/* Global defaults */
QWidget {
    background-color: #181825;
    color: #cdd6f4;
    font-family: "Segoe UI", "Yu Gothic UI", "Meiryo", sans-serif;
    font-size: 13px;
    selection-background-color: #45475a;
    selection-color: #cdd6f4;
}

/* Main window */
QMainWindow {
    background-color: #181825;
}

/* Frames and Cards */
QFrame.cardFrame {
    background-color: #1e1e2e;
    border: 1px solid #313244;
    border-radius: 8px;
    padding: 10px;
}

QGroupBox {
    background-color: #1e1e2e;
    border: 1px solid #313244;
    border-radius: 8px;
    margin-top: 14px;
    padding: 12px 10px 10px 10px;
    font-weight: bold;
    font-size: 13px;
    color: #89b4fa;
}
QGroupBox::title {
    subcontrol-origin: margin;
    subcontrol-position: top left;
    left: 12px;
    padding: 0 5px;
    background-color: #1e1e2e;
}

/* Inputs & Edits */
QLineEdit {
    background-color: #252538;
    border: 1px solid #3b3d54;
    border-radius: 6px;
    padding: 6px 10px;
    color: #cdd6f4;
    font-size: 13px;
}
QLineEdit:focus {
    border: 1px solid #2ecc71;
    background-color: #2b2b40;
}
QLineEdit:disabled {
    background-color: #1e1e2e;
    color: #6c7086;
    border-color: #2d2d3e;
}

/* Push Buttons */
QPushButton {
    background-color: #313244;
    border: 1px solid #45475a;
    border-radius: 6px;
    padding: 6px 14px;
    color: #cdd6f4;
    font-weight: 500;
}
QPushButton:hover {
    background-color: #45475a;
    border-color: #585b70;
}
QPushButton:pressed {
    background-color: #585b70;
}
QPushButton:disabled {
    background-color: #1e1e2e;
    border-color: #2b2b3d;
    color: #585b70;
}

/* Primary Action Button (Deploy) */
QPushButton.primaryBtn {
    background-color: #27ae60;
    border: 1px solid #2ecc71;
    color: #ffffff;
    font-weight: bold;
    font-size: 14px;
    padding: 10px 20px;
    border-radius: 6px;
}
QPushButton.primaryBtn:hover {
    background-color: #2ecc71;
    border-color: #58d68d;
}
QPushButton.primaryBtn:pressed {
    background-color: #219653;
}
QPushButton.primaryBtn:disabled {
    background-color: #2b3b32;
    border-color: #2d4538;
    color: #688a75;
}

/* Secondary Button */
QPushButton.secondaryBtn {
    background-color: #3b4261;
    border: 1px solid #545c7e;
    color: #cdd6f4;
}
QPushButton.secondaryBtn:hover {
    background-color: #48527a;
}

/* Danger Button */
QPushButton.dangerBtn {
    background-color: #5c2626;
    border: 1px solid #853b3b;
    color: #f38ba8;
}
QPushButton.dangerBtn:hover {
    background-color: #733030;
}

/* Combo Box */
QComboBox {
    background-color: #252538;
    border: 1px solid #3b3d54;
    border-radius: 6px;
    padding: 6px 12px;
    color: #cdd6f4;
}
QComboBox:hover {
    border-color: #585b70;
}
QComboBox::drop-down {
    subcontrol-origin: padding;
    subcontrol-position: top right;
    width: 24px;
    border-left: 1px solid #3b3d54;
}
QComboBox QAbstractItemView {
    background-color: #1e1e2e;
    border: 1px solid #45475a;
    border-radius: 4px;
    color: #cdd6f4;
    selection-background-color: #313244;
    outline: none;
    padding: 4px;
}

/* Tree Widget */
QTreeWidget {
    background-color: #1e1e2e;
    border: 1px solid #313244;
    border-radius: 6px;
    padding: 4px;
    outline: none;
    color: #cdd6f4;
}
QTreeWidget::item {
    padding: 4px 6px;
    border-radius: 4px;
    margin: 1px 0;
}
QTreeWidget::item:hover {
    background-color: #2b2b3f;
}
QTreeWidget::item:selected {
    background-color: #363a4f;
    color: #ffffff;
}
QHeaderView::section {
    background-color: #252538;
    color: #a6adc8;
    padding: 5px 8px;
    border: none;
    border-right: 1px solid #313244;
    font-weight: bold;
}

/* Radio Button & Check Box */
QRadioButton, QCheckBox {
    spacing: 8px;
    color: #cdd6f4;
}
QRadioButton::indicator, QCheckBox::indicator {
    width: 16px;
    height: 16px;
    border: 1px solid #585b70;
    border-radius: 3px;
    background-color: #252538;
}
QRadioButton::indicator {
    border-radius: 8px;
}
QCheckBox::indicator:hover, QRadioButton::indicator:hover {
    border-color: #2ecc71;
}
QCheckBox::indicator:checked {
    background-color: #27ae60;
    border-color: #2ecc71;
    image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='12' height='12' viewBox='0 0 24 24' fill='none' stroke='white' stroke-width='3' stroke-linecap='round' stroke-linejoin='round'><polyline points='20 6 9 17 4 12'></polyline></svg>");
}
QCheckBox::indicator:indeterminate {
    background-color: #27ae60;
    border-color: #2ecc71;
    image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='12' height='12' viewBox='0 0 24 24' fill='none' stroke='white' stroke-width='3' stroke-linecap='round'><line x1='5' y1='12' x2='19' y2='12'></line></svg>");
}
QRadioButton::indicator:checked {
    background-color: #27ae60;
    border-color: #2ecc71;
}

/* Progress Bar */
QProgressBar {
    background-color: #252538;
    border: 1px solid #3b3d54;
    border-radius: 5px;
    text-align: center;
    color: #ffffff;
    font-weight: bold;
    height: 18px;
}
QProgressBar::chunk {
    background-color: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #27ae60, stop:1 #2ecc71);
    border-radius: 4px;
}

/* Text Edit (Logs) */
QTextEdit, QPlainTextEdit {
    background-color: #12121b;
    border: 1px solid #2b2b3f;
    border-radius: 6px;
    color: #a6adc8;
    font-family: "Consolas", "Cascadia Code", "Courier New", monospace;
    font-size: 12px;
    padding: 6px;
}

/* Splitter */
QSplitter::handle {
    background-color: #252538;
}
QSplitter::handle:hover {
    background-color: #2ecc71;
}

/* ScrollBar */
QScrollBar:vertical {
    background-color: #181825;
    width: 10px;
    margin: 0px;
}
QScrollBar::handle:vertical {
    background-color: #3b3d54;
    border-radius: 5px;
    min-height: 20px;
}
QScrollBar::handle:vertical:hover {
    background-color: #585b70;
}
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
    height: 0px;
}
QScrollBar:horizontal {
    background-color: #181825;
    height: 10px;
    margin: 0px;
}
QScrollBar::handle:horizontal {
    background-color: #3b3d54;
    border-radius: 5px;
    min-width: 20px;
}
QScrollBar::handle:horizontal:hover {
    background-color: #585b70;
}
QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal {
    width: 0px;
}

/* Tooltips */
QToolTip {
    background-color: #2b2b3f;
    color: #cdd6f4;
    border: 1px solid #45475a;
    border-radius: 4px;
    padding: 4px 8px;
}
"""
