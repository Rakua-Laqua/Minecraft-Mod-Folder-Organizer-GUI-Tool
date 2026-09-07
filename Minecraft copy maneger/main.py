#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Minecraft Copy Manager - Main Entry Point.

A modern GUI tool to manage, profile, and selectively deploy Minecraft mods
from a centralized repository into .minecraft/mods.
"""

import ctypes
import os
import sys
import traceback
from pathlib import Path

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QApplication, QMessageBox

from gui.main_window import MainWindow
from gui.styles import DARK_THEME_QSS


def handle_uncaught_exception(exc_type, exc_value, exc_traceback):
    """Global exception handler to avoid silent crashes."""
    if issubclass(exc_type, KeyboardInterrupt):
        sys.__excepthook__(exc_type, exc_value, exc_traceback)
        return

    err_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
    print(f"Unhandled exception:\n{err_msg}", file=sys.stderr)

    if QApplication.instance():
        QMessageBox.critical(
            None,
            "致命的なエラー",
            f"予期しないエラーが発生しました:\n\n{exc_value}\n\n詳細はコンソールログをご確認ください。",
        )


def main():
    # Set Windows AppUserModelID for distinct taskbar icon
    if sys.platform == "win32":
        try:
            myappid = "antigravity.minecraft.copymanager.v1"
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
        except Exception:
            pass

    # Global exception hook
    sys.excepthook = handle_uncaught_exception

    app = QApplication(sys.argv)
    app.setApplicationName("Minecraft Copy Manager")
    app.setOrganizationName("MinecraftModManager")

    # Apply modern dark theme stylesheet
    app.setStyleSheet(DARK_THEME_QSS)

    window = MainWindow()
    window.show()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
