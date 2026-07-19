# Phase 01: Current-session logging facade

**Produces:** a best-effort local `Current.log` and process/WPF exception logging.

**Consumes:** approved SP-0013 contract.

1. Add `src/StreamsPlayer.App/CurrentLog.cs`: create `%LOCALAPPDATA%\StreamsPlayer\Current.log` anew, serialize UTC log lines with `Information` and `Error` entries, allow concurrent writer callbacks, and make all file failures no-ops. Static check: no write exceptions escape its public methods.
2. Update `App.xaml.cs` to construct and retain the facade before `MainWindow`, subscribe to dispatcher/AppDomain/task exception surfaces, log startup/shutdown, and dispose it. Static check: all three exception surfaces call the facade and no handler changes exception handling semantics.
3. Accept the facade in `MainWindow` while retaining its optional launch-request constructor compatibility. Static check: all `new MainWindow` calls compile with the updated constructor.
