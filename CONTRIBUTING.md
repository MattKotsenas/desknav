# Contributing

Read [ARCHITECTURE.md](ARCHITECTURE.md) before changing a system boundary.
Consult [BACKLOG.md](BACKLOG.md) when selecting or changing planned work.

Use `dotnet` commands to add, remove, or update NuGet packages.

When a change replaces a production mechanism, remove the superseded
mechanism or keep it behind an explicit feature flag.

Update the architecture document when a change moves ownership, dependency
direction, action semantics, or a recovery guarantee. Update a backlog item
when its intended outcome changes.

## Pull requests

Each pull request contains exactly one logical idea. Its description states:

1. The user-visible outcome, if any.
2. Why the change matters and why it has this shape.
3. The evidence that the issue is fixed, preferably an automated test.

Every change in the pull request must be necessary and sufficient for that
single idea.

## Verification

Run the repository verification for code or configuration changes:

```powershell
dotnet run --project eng\build.csproj
```

For documentation-only changes, inspect links and rendered Markdown.
