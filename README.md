# desknav

desknav is a keyboard-first Windows desktop navigation system. The repository
provides a native Kanata pointer layer and tested boundary components for
translating stock-Kanata TCP input into control-plane work. Its architecture
defines the Desknav UI, Komorebi, and UI Automation boundaries that compose
the complete system.

## Repository guide

- [ARCHITECTURE.md](ARCHITECTURE.md) defines the system values, principles,
  components, interaction paths, recovery, and verification.
- [BACKLOG.md](BACKLOG.md) records future work in priority order and the
  evidence required to complete each item.
- [CONTRIBUTING.md](CONTRIBUTING.md) defines contribution practices,
  verification, and pull-request requirements.

## Run the target overlay

Start stock Kanata with its TCP server bound to loopback:

```powershell
kanata --cfg src\Desknav.Kanata\desknav.kbd --port 127.0.0.1:5829
```

In another terminal, start Desknav against that endpoint:

```powershell
dotnet run --project src\Desknav.App -- Kanata:Endpoint=127.0.0.1:5829
```

`CAP Space f` discovers the foreground window's eligible controls and displays
their labels. Escape hides the overlay. Labels are display-only; Desknav does
not accept label input or perform actions. Press Ctrl+C, or stop Kanata, to
exit Desknav.

## Inspect UI Automation targets

The target dump scans the foreground window after a short delay and writes
the captured UI Automation control tree as JSON:

```powershell
dotnet run --project src\Desknav.TargetDump -- --delay 2000
```

Switch focus to the window to inspect before the delay expires. The dump marks
eligible controls, exclusions, and unavailable properties according to the
[UI Automation capture contract](ARCHITECTURE.md#ui-automation).

For automation, bypass foreground-window selection with a decimal or
hexadecimal window handle:

```powershell
dotnet run --project src\Desknav.TargetDump -- --window-handle 0x12345
```
