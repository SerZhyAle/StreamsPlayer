# Phase 02: Runtime integration

**Status:** Completed

1. Add the Settings action to `MainWindow.xaml` and isolate its coordination in a `MainWindow.Settings.cs` partial.
   - Check: accepted values save once, update tile properties and controls, and Cancel leaves state unchanged.
2. Bind grid tile dimensions and column calculation to the saved size.
   - Check: all three values remain 16:9, recalculate rows, and do not affect List mode.
3. Gate preview start, viewport scheduling, refresh visibility, and setting transitions with the saved preview preference.
   - Check: disabled prevents coordinator start; re-enabled starts only for an active Grid window.
