# Backlog

This file records future work in priority order. Delete an item when its
outcome is complete. [ARCHITECTURE.md](ARCHITECTURE.md) remains the source of
truth for system shape.

## 1. Package runtime behavior so deployed configuration matches tested source

Produce a pinned Desknav artifact as the single production source for Kanata
behavior. Deployment owns provisioning and lifecycle; this repository owns
the artifact's runtime behavior.

Done when Windows acceptance proves startup, restart, rollback, and live
pointer movement from the pinned artifact, with any replaced movement path
removed or explicitly gated.

## 2. Interpret command gestures through one control-plane workflow

Wire Kanata command-mode generations and recognized gesture tokens into one
control-plane coordinator. Route target discovery to its boundary owner.
Target discovery follows the cancellation and supersession contract in
[ARCHITECTURE.md](ARCHITECTURE.md).

Done when deterministic scenarios prove that a current discovery result
produces current presentation, Escape before completion produces no
presentation, cancellation is dispatched before a superseding request, and
obsolete results cannot advance the later workflow. A separate scenario
withholds overlay and Komorebi cleanup after command-mode exit, observes
Kanata in passthrough, and proves that the next ordinary key reaches Windows.

## 3. Present visible targets so the user can choose a desktop action

Implement the target-selection workflow defined in
[ARCHITECTURE.md](ARCHITECTURE.md) with live target discovery, overlay
presentation, and point or activation operations.

Done when Windows acceptance proves point without activation, explicit
activation, cancellation after targets appear, focus change before the
one-shot action, no stale overlay or command character, and no mismatch
between a visible label and the target it selects. Deterministic scenarios
deliver a stale presentation revision from an older workflow and prove labels
remain inactive, then deliver the matching current revision and prove labels
become active. A label captured for the older revision remains inactive after
the newer revision becomes current, while a label captured for the current
revision selects from that target map. Other scenarios lose a pointer result
after dispatch and prove a conflicting pointer operation waits for the
boundary to fence the ambiguous one.

## 4. Route by focused context so one command produces the defined desktop result

Implement the focus-context routing defined in
[ARCHITECTURE.md](ARCHITECTURE.md) across Komorebi, Desknav UI, and UI
Automation.

Done when behavior and Windows acceptance tests cover eligible and ineligible
desktop states, context changes during routing, explicit refusal, and the
absence of silent fallback to a different result.

## 5. Define runtime failure outcomes so recovery respects each operation

Handle the expected failure events defined in
[ARCHITECTURE.md](ARCHITECTURE.md). Add boundary-local cancellation,
desired-state revisions, process and connection identities, and
operation-specific recovery.

Done when Windows acceptance covers timeout, explicit component refusal,
outcome unknown, disconnect, superseded mode changes, and restarts of Kanata,
Desknav UI, and Komorebi. A separate acceptance case proves that unexpected
owner failure stops the application without repeating an ambiguous effect.
For every terminal path whose policy leaves command mode, tests withhold
unrelated cleanup, observe Kanata in passthrough, and prove the next ordinary
key reaches Windows.
