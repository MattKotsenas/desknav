# Contributing

Read [ARCHITECTURE.md](ARCHITECTURE.md) before changing a system boundary.
[BACKLOG.md](BACKLOG.md) records implementation order; architecture remains the
source of truth for system shape.

## Development

- Start with a failing behavior test and define the observable finish line.
- Keep pure state and policy separate from Windows, UI Automation, pipe, and
  process adapters.
- Run live desktop, cursor, UI Automation, hardware-input, and visual
  acceptance only in the dedicated VM.
- Keep generated Kanata and configuration artifacts reproducible and check
  them for drift.
- Use native library APIs unless a process or operating-system boundary
  requires isolation.
- Remove or explicitly gate an old production path when introducing its
  replacement.
- Use `dotnet` commands to add, remove, or update NuGet packages.
- Treat compiler warnings as errors and keep nullable analysis enabled.

Update the architecture document when a change moves ownership, dependency
direction, session semantics, or a recovery guarantee. Update the backlog when
a finish line changes or becomes complete.

## Pull requests

Keep each pull request to one coherent migration or behavior change. State:

1. The user-visible outcome.
2. The ownership or protocol boundary it changes.
3. The command or artifact that proves the outcome.
4. Any behavior intentionally left for another pull request.

Do not combine repository migration with behavioral redesign unless the
migration cannot preserve the architecture contract.

## Verification

Run the repository verification for code or configuration changes:

```powershell
dotnet run --project eng\build.csproj
```

Documentation-only changes require link and rendered-Markdown inspection.

Before completion, review correctness and craft separately. Ask each reviewer
to falsify the load-bearing claim rather than confirm the implementation.
