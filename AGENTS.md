# AGENTS.md

## Mission

desknav provides keyboard-first Windows desktop navigation. Keep the system
small enough that each input path, state transition, and recovery action can be
understood and tested without holding the whole desktop stack in mind.

## Ownership boundaries

- Kanata is the only keyboard hook.
- Kanata owns continuous pointer movement, wheel input, and physical button
  taps.
- The .NET control plane owns semantic intents, focus-context routing, and
  navigation-session coordination.
- Pointer UI owns overlays, read-only target discovery, and explicitly
  requested one-shot actions.
- Komorebi and UI Automation are external systems. Reconcile their observable
  state.

Do not add a second input hook, duplicate pointer-motion state, or a pass-through
wrapper around Kanata, Akka.NET, Komorebi, or UI Automation.

## Coordinator rules

- Use one local Akka.NET actor as the Pointer UI session owner.
- Do not add clustering, persistence, sharding, distributed pub/sub, streams,
  or child actors without a demonstrated requirement.
- Expected pipe, timeout, rejection, and disconnect failures are messages.
- Unexpected actor failures stop the application; restarting ambiguous
  one-shot actions is unsafe.
- Treat connection epoch, request sequence, intent generation, and host session
  token as distinct identities.
- Base restoration is idempotent reconciliation confirmed by a Kanata layer
  echo. External effects may repeat.

## Development

- Start with a failing behavior test and define the observable finish line.
- Keep pure state and policy separate from Windows, UIA, pipe, and process
  adapters.
- Run live desktop, cursor, UIA, and visual acceptance only in the dedicated
  VM.
- Generated Kanata/configuration artifacts must be reproducible and checked for
  drift.
- Use the native library API unless a boundary requires isolation.
- When introducing a new path, remove or explicitly gate the old path; do not
  leave two production mechanisms.
- Use `dotnet` commands to add, remove, or update NuGet packages.
- Treat compiler warnings as errors and keep nullable analysis enabled.

## Pull requests

Each PR should contain one coherent migration or behavior change and state:

1. The user-visible outcome.
2. The ownership or protocol boundary it changes.
3. The command or artifact that proves the outcome.
4. Any behavior intentionally left behind for a later PR.

Do not combine repository migration with behavioral redesign unless the
migration cannot preserve the existing contract.

## Verification

Run:

```powershell
dotnet run --project eng\build.csproj
```

Before completion, review correctness and craft separately. Ask reviewers to
falsify the load-bearing claim rather than confirm the implementation.
