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

## 2. Route semantic actions through the control plane so each action has one owner

Wire complete semantic intents from Kanata into the control plane. Route one
semantic action through the control plane to a component. Complete the action
with an observable outcome and confirmed Kanata restoration.

Done when deterministic integration tests exercise intent ingress and the
component boundary, and prove that a result cannot complete an action other
than the one that requested it. One case times out an action, starts a later
action while the component remains connected, and then delivers the earlier
action's result. Another ends an action on disconnect, restarts its component,
starts a later action, and then delivers the earlier result. The tests confirm
Kanata restoration for every outcome.

## 3. Present visible targets so the user can choose a desktop action

Add Desknav UI overlays, read-only target discovery, and explicitly selected
point or activation actions. The control plane retains ownership of the
semantic action while the UI owns presentation and the selected one-shot
effect.

Done when Windows acceptance proves point without activation, explicit
activation, cancellation after targets appear, focus change before the
one-shot action, and no stale overlay or command character.

## 4. Route by focused context so one intent produces the defined desktop result

Select the component that can deliver the intent's defined result in the
observed focus context. Komorebi, Desknav UI, and UI Automation report their
capabilities and refusals; the control plane owns the routing decision.

Done when behavior and Windows acceptance tests cover eligible and ineligible
desktop states, context changes during routing, explicit refusal, and the
absence of silent fallback to a different result.

## 5. Define runtime failure outcomes so recovery never replays an action

Handle the expected failure events defined in
[ARCHITECTURE.md](ARCHITECTURE.md) while distinguishing a refusal from an
action whose external outcome is unknown. Stop the application after
unexpected action-owner failure.

Done when Windows acceptance covers timeout, explicit component refusal,
outcome unknown, disconnect, repeated restoration, and restarts of Kanata,
Desknav UI, and Komorebi. A separate acceptance case proves that unexpected
action-owner failure stops the application without replaying an ambiguous
one-shot action.
