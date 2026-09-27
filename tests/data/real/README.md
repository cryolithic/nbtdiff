# Real-world fixtures

Two snapshots of the same world, both written by Minecraft: `initial`, then `expected` after a short
play session. `RealWorldIntegrationTests` edits a copy of `initial` through the app's own copy/save
code until it should match `expected`, so what nbt-diff writes is checked against what the game
wrote, not against nbt-diff's own reader.

| Set | Client | Contents |
| --- | --- | --- |
| `vanilla` | Fabulously Optimized (vanilla save format) | `level.dat`, player data/stats/advancements, world data, one overworld region/entities/poi file |
| `neoforge` | All the Mods 10 Aeronautics | `level.dat`, player data, modded `data/*.dat` (AE2, Immersive Engineering, Waystones, Modonomicon, NeoForge attachments), FTB Quests/Chunks/Essentials/Teams `.snbt`, two regions, entities, poi |

The player's UUID and name are replaced (`11111111-2222-4333-8444-555555555555` / `Player`) in every
encoding, and region files are rebuilt chunk by chunk, so the committed files carry no identity.
Regenerate or re-check with `tools/fixtures/make-real-fixtures.cs` (file lists alongside it):

```bash
dotnet run tools/fixtures/make-real-fixtures.cs -- "<initial>" "<expected>" tests/data/real/<set> \
    tools/fixtures/<set>.files <player-uuid> <player-name>
dotnet run tools/fixtures/make-real-fixtures.cs -- verify tests/data/real <player-uuid> <player-name>
```

Known gap: six entity chunks in `vanilla` exist only in `initial` (the entities despawned and
Minecraft deleted the chunk records). nbt-diff cannot delete a chunk, so those stay different.
