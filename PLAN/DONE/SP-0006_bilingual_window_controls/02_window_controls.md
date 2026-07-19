# Phase 02: Window controls

**Status:** Completed

1. Add the top `RU / EN` action and persisted main-window topmost checkbox.
   - Static check: switching language reapplies localized options/statuses; checking topmost sets `MainWindow.Topmost` and saves the preference.
2. Add a localized player topmost checkbox whose saved default is passed back to the main window.
   - Static check: changing it does not mutate main-window `Topmost`; a new player consumes the saved value.
3. Add fullscreen button, F11 toggle, Escape exit, and exact style/state restoration.
   - Static check: enter stores style/resize/state, uses borderless maximized mode, and every exit path restores stored values.
