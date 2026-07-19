# Phase 02: Progress and density

**Produces:** truthful progress UI and tightly packed cards.

**Status:** Completed

1. Update `src/StreamPlayer.App/MainWindow.xaml` and `MainWindow.xaml.cs` to show an accessible indeterminate catalog activity indicator before local state is read and during remote refresh; hide it on all terminal paths.
   - Static check: startup, refresh success, refresh failure, and load failure all set activity visibility deterministically.
2. Reduce card minimum height and gaps, and top-align outer list rows without reducing existing action visibility.
   - Static check: the card retains its existing title, URL, metadata, status, pin, and Play bindings.
