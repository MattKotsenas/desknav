# desknav

desknav is a keyboard-first Windows desktop navigation system built with
Kanata, Komorebi, UI Automation, and .NET.

It keeps physical input ownership in Kanata while coordinating window-manager
intents, pointer overlays, semantic control activation, and recovery through a
separate .NET control plane.

The repository contract and ownership boundaries live in [AGENTS.md](AGENTS.md).

## Verification

```powershell
pwsh .\eng\verify.ps1
```

The verification script validates repository structure and solution
membership. It supports C# projects targeting `net10.0` or
`net10.0-windows`. When projects are present, it restores the Release graph,
builds, checks formatting, and runs host-safe tests.

## License

[MIT](LICENSE)
