# desknav

desknav is a keyboard-first Windows desktop navigation system. The repository
provides a native Kanata pointer layer and its simulator behavior suite. Its
architecture defines the control plane, Desknav UI, Komorebi, and UI
Automation boundaries that compose the complete system.

## Kanata runtime

Repository verification produces a runtime zip and SHA-256 checksum under
`artifacts\package`. The artifact contains the tested keymap and a manifest
that pins the upstream Kanata release asset; Desknav does not redistribute
the Kanata executable. CI publishes the same files under an artifact name
containing the source commit.

Artifact identity, startup, restart, and live pointer movement are accepted
only in the dedicated Windows VM:

```powershell
pwsh -File eng\acceptance\Invoke-KanataRuntimeAcceptance.ps1
```

## Repository guide

- [ARCHITECTURE.md](ARCHITECTURE.md) defines the system values, principles,
  components, interaction paths, recovery, and verification.
- [BACKLOG.md](BACKLOG.md) records future work in priority order and the
  evidence required to complete each item.
- [CONTRIBUTING.md](CONTRIBUTING.md) defines contribution practices,
  verification, and pull-request requirements.
