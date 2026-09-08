# nbt-diff

Compares Minecraft NBT data: two files at tag level, or two Java Edition world directories
recursively in a Beyond Compare style two-pane view. Read-only. Runs on Windows and Linux.

Status: early scaffold. See `docs/PLAN.md` for what exists and what is next.

## Build

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/NbtDiff.App -- <left> <right>
```

Design: `docs/DESIGN.md`. Stages: `docs/PLAN.md`. Third-party attribution: `third_party/NOTICE`.
