# APP-ACTIVATION - pointer

| | |
| --- | --- |
| **Id** | `APP-ACTIVATION` |
| **Version** | 0.9.1 (draft) |
| **Home** | `Contracts/app-activation/README.md` |
| **Owner** | CyrFlip |
| **This product's role** | **consumer** |

## What this repository must do to stay conformant

1. **Launch Arguments & Target Resolution:** Parse command-line invocations into `StreamLaunchRequest` (`Url`, `ChannelId`, `None`, `Invalid`).
2. **Safe Degradation:** Unrecognized or invalid launch parameters report friendly status in UI without crashing the application.
3. **One copy per session (rule 2):** The first copy holds the session-local lock for its lifetime; a later copy forwards its launch and exits with code 0, opening no window. If the running copy does not answer, the later copy says so and exits - never a second instance.
4. **Pipe hand-off (rules 3-4):** The running copy listens on the local pipe (current user only, bounded connect), survives any empty, malformed, oversized or aborted connection, and acts on a forwarded launch exactly as on its own command line.
5. **Focus Restoration (rule 5):** A forwarded launch brings the application to the front - from minimized, and via the compact panel when the catalog is hidden behind it.

The lock and pipe names are frozen for the product's life. Their session and profile qualifiers are a dated exception in the contract registry. Rules 1 (URI scheme) and 6 (one-shot fallback) are not adopted.

Evidence: `src/StreamsPlayer.Core/Models.cs` (`StreamLaunchRequest.Parse`), `SingleInstanceIdentity.cs`, `ActivationMessage.cs`, `ActivationPipe.cs`; `src/StreamsPlayer.App/App.xaml.cs`, `SingleInstanceLock.cs`, `ForegroundActivation.cs`, `MainWindow.Launch.cs`. Tests: `StreamLaunchRequestTests`, `ActivationMessageTests`, `SingleInstanceIdentityTests`, `ActivationPipeTests`.
