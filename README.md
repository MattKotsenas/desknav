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
