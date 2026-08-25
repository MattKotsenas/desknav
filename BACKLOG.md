# Backlog

This file records future work in priority order. Delete an item when its
outcome is complete. [ARCHITECTURE.md](ARCHITECTURE.md) remains the source of
truth for system shape.

## 1. Interpret command gestures through one control-plane workflow

Wire Kanata command-mode generations and recognized gesture tokens into one
control-plane coordinator. Route target discovery to its boundary owner.
Target discovery follows the cancellation and supersession contract in
[ARCHITECTURE.md](ARCHITECTURE.md).

Done when a deterministic scenario begins in the current delegated command
mode, injects a recognized current-generation gesture through the production
Kanata delegated-gesture protocol boundary, and observes the coordinator issue
the corresponding target-discovery request. A current discovery result causes
the coordinator to issue the current revisioned presentation request. Escape
before completion issues discovery cancellation and no presentation request.
The supersession scenario withholds cancellation completion, observes
cancellation dispatch followed by the superseding request, and only then
releases cancellation completion. Obsolete results cannot advance the later
workflow. A gesture captured in a prior command-mode generation produces no
boundary request after a newer generation becomes active. The Escape scenario
withholds cancellation completion after observing dispatch, observes Kanata in
passthrough, and then makes control-plane IPC unavailable. The next ordinary
key still reaches Windows within the direct-passthrough acceptance bound.
Architecture tests prove workflow state is confined to coordinator policy,
workflow requests to boundary owners originate with the coordinator, and
sibling boundary owners cannot command one another. Each external adapter is
accessible only to its designated boundary owner. A repository check proves
Desknav production code does not reference keyboard-hook, raw-input, HID,
key-state, or alternate continuous-pointer APIs. A closed-ingress check proves
physical gestures can enter the control plane only through Kanata's
delegated-gesture protocol. With control-plane IPC unavailable, Windows
acceptance proves held movement, wheel input, and physical button taps still
flow directly through Kanata. With IPC active, passthrough gestures produce no
control-plane-observable traffic or state change through any channel.

## 2. Present visible targets so the user can choose a desktop action

Implement the target-selection workflow defined in
[ARCHITECTURE.md](ARCHITECTURE.md) with live target discovery, overlay
presentation, and point, UIA activation, or coordinate-activation operations.

Done when Windows acceptance proves point without activation, explicit
activation, cancellation after targets appear, focus change before the
one-shot action, no stale overlay or command character, and no mismatch
between a visible label and the target it selects. Deterministic scenarios
deliver a stale overlay confirmation from an older workflow and prove no label
revision becomes active, then deliver the matching current confirmation and
prove the revision becomes active. Kanata simulation proves each label gesture
carries the revision active at capture. A label captured for an older active
revision remains inactive after a newer revision becomes current, while a
label captured for the current revision selects from that target map. After
Escape invalidates a confirmed target map, a late label captured for that map
produces no one-shot-action request. Another scenario holds an older overlay
render, activates a newer scene, releases the old render, and proves the newer
scene remains visible. A separate scenario proves the same newer-wins result
for delayed cleanup. With current overlay cleanup withheld after command-mode
exit, Kanata still reaches observed passthrough and the next ordinary key
reaches Windows within the direct-passthrough acceptance bound.

One-shot-action scenarios lose a UIA or pointer result after dispatch and prove
the executor records exactly one dispatch for that logical action across every
exercised recovery path and operation identity. A conflicting operation waits
while the effect is ambiguous, while an unrelated target-discovery and
presentation workflow completes. Within the operation's recovery budget, the
owner establishes a safe fence and dispatches the conflict, or gives it an
explicit terminal refusal. Command-mode exit also reaches observed Kanata
passthrough and the next ordinary key reaches Windows within the direct-
passthrough acceptance bound while the action fence remains unresolved.

## 3. Route by focused context so one command produces the defined desktop result

Implement the focus-context routing defined in
[ARCHITECTURE.md](ARCHITECTURE.md) across Komorebi, Desknav UI, and UI
Automation.

Done when behavior and Windows acceptance tests cover eligible and ineligible
desktop states, context changes during routing, explicit refusal, and the
absence of silent fallback to a different result. With Komorebi reconciliation
withheld after command-mode exit, Kanata still reaches observed passthrough and
the next ordinary key reaches Windows within the direct-passthrough acceptance
bound. Another scenario holds an older Komorebi effect, establishes newer
desired state, releases the older completion, and proves observable state
reconciles to the newer revision. A Komorebi operation also completes while an
unrelated one-shot-action fence remains unresolved.

## 4. Complete runtime failure outcomes so recovery respects each operation

Handle the expected failure events defined in
[ARCHITECTURE.md](ARCHITECTURE.md). Apply boundary-local cancellation,
desired-state revisions, process and connection identities, and
operation-specific recovery across every external boundary.

Done when Windows acceptance covers timeout, explicit component refusal,
outcome unknown, disconnect, superseded mode changes, and restarts of Kanata,
Desknav UI, and Komorebi. A separate acceptance case proves that unexpected
owner failure stops the application without repeating an ambiguous effect.
After reconnect or restart, deterministic scenarios deliver messages from the
prior process and connection lifetimes plus out-of-order messages from the
current connection, and prove they cannot affect current semantic work. A
valid current-connection envelope carrying an obsolete request or operation
identity is also rejected. A current-lifetime message carrying the current
semantic identity then completes the current work. For every stateful boundary
owner, a valid current-connection message carrying an older desired-state
revision after a newer one is rejected. Reconnect and restart scenarios cover
both quiescent and in-flight state, resolve the in-flight work according to its
operation policy, and restore observable external state to the latest accepted
desired-state revision.

A dispatched one-shot action whose result is lost is not dispatched again
during any timeout, reconnect, or restart recovery under any operation
identity. Composition tests prove exactly one runtime command recipient and
writer exists for each boundary. Architecture tests prove each boundary owner
owns its boundary-work scheduling and no synchronization gate spans owners.
The delegated-gesture, target-selection, and focused-context passthrough
scenarios above run for every terminal path whose policy leaves command mode.

## 5. Publish dogfoodable behavior for a deployment consumer

Publish a deterministic artifact under the
[artifact and deployment ownership](ARCHITECTURE.md#artifact-production-and-deployment-have-separate-owners)
contract.

Done when repository verification reproduces the artifact, verifies its
identity and exact contents against tested source, and proves its ordinary
passthrough mappings emit directly through Kanata without a control-plane
round trip. The deployment consumer's cross-repository acceptance identifies
and exercises that artifact and proves startup, restart, genuine rollback
between distinct artifacts, live pointer behavior, that the artifact's Kanata
path is the sole active keyboard hook and continuous-pointer provider, and
that Mousemaster and prior artifact versions are not running.
