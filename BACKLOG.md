# Backlog

This file records implementation status and order. The architecture remains
defined by [ARCHITECTURE.md](ARCHITECTURE.md).

Each backlog item states an observable outcome and the proof that closes it.
An item can span more than one pull request only when every pull request has
its own independently observable finish line.

## Complete

### Repository verification

**Outcome:** one command installs the pinned Kanata simulator and runs the
repository test suite in CI and locally.

**Proof:** the [repository verification](CONTRIBUTING.md#verification) passes
locally and in CI.

### Native pointer movement

**Outcome:** Caps followed by Space enters a persistent pointer layer. Held
H/J/K/L keys accelerate in four directions, compose diagonally, stop on
release, and exit through Caps or Escape.

**Proof:** the production keymap passes the Kanata simulator behavior suite,
including independent-axis release.

## Ordered work

### 1. Reproducible runtime consumption

**Outcome:** a pinned Desknav artifact is the single production source for its
Kanata behavior.

**Boundary:** deployment owns provisioning and lifecycle; this repository owns
the generated runtime behavior. Any replaced movement path is removed or
explicitly gated.

**Finish line:** the dedicated VM proves startup, restart, rollback, and live
pointer movement from the pinned artifact.

### 2. Semantic session input

**Outcome:** a complete semantic intent enters the control plane, reaches one
test provider host, returns a correlated outcome, and restores the Kanata base
layer.

**Boundary:** this slice establishes real intent ingress and the process
protocol without adding desktop target actions.

**Finish line:** deterministic behavior tests reorder responses, timeouts,
disconnects, and layer echoes across distinct connection epochs, request
sequences, intent generations, host session tokens, and restoration operation
IDs. Stale work cannot complete the current session.

### 3. Visible-target actions

**Outcome:** a semantic command presents visible desktop targets and performs
the explicitly selected point or activation action.

**Boundary:** Pointer UI owns overlays, read-only target discovery, and the
selected one-shot action. The control plane owns session lifetime and outcome.

**Finish line:** VM acceptance proves point-without-activation, explicit
activation, cancellation after targets appear, focus change before commit, and
outcome-unknown handling after commit. No stale overlay or command character
remains.

### 4. Focus-context routing

**Outcome:** one semantic intent resolves to the provider that can deliver its
defined result in the focused context.

**Boundary:** provider selection belongs to the control plane. Komorebi,
Pointer UI, and UI Automation expose observable capabilities and distinct
refusals; they do not choose fallback behavior.

**Finish line:** behavior and VM tests cover eligible and ineligible desktop
states, context changes during resolution, provider refusal, and the absence of
silent fallback to a different semantic result.

### 5. Runtime recovery

**Outcome:** expected provider and transport failures restore keyboard
ownership, while ambiguous committed actions remain outcome unknown and are
never replayed.

**Boundary:** the coordinator owns bounded leases, revocation, process-session
replacement, and Kanata reconciliation.

**Finish line:** end-to-end VM tests exercise timeout, disconnect, Pointer UI
restart, stale result delivery, restoration retry, and unexpected coordinator
failure.
