# Conway's Game of Life API

A REST API for Conway's Game of Life in C# on .NET 8, with persistent storage and a small browser UI.

## Run it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). `dotnet --list-sdks` should list an `8.x` version. Nothing else is needed, since storage is embedded.

```bash
git clone https://github.com/hacka-tron/ConwaysGameOfLife.git
cd ConwaysGameOfLife
dotnet run --project src/GameOfLife.Api   # http://localhost:5168
dotnet test                               # 55 unit and API tests
```

- Browser UI at http://localhost:5168
- Swagger at http://localhost:5168/swagger
- Boards are stored in `src/GameOfLife.Api/data/` and survive restarts. Delete the folder to start empty.

With Homebrew's `dotnet@8` on macOS, add `/opt/homebrew/opt/dotnet@8/bin` to `PATH` and set `DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec` first.

## API

| Method | Path | Returns |
|---|---|---|
| `POST` | `/boards` | The stored board with its ID. `201` if new, `200` if this exact board was already stored. |
| `GET` | `/boards/{id}` | The stored board. |
| `GET` | `/boards/{id}/next` | The next generation, with its own ID. |
| `GET` | `/boards/{id}/generations/{n}` | The board after `n` generations, with its own ID. |
| `GET` | `/boards/{id}/final` | The state the board settles into, how many generations it took, and the cycle length. |
| `GET` | `/settings` | The size and step limits, and the edge rule used when an upload does not name one. |
| `GET` | `/health` | `Healthy` when storage is reachable. |

```bash
curl -s localhost:5168/boards -H 'content-type: application/json' \
  -d '{"cells":[[0,0,0,0,0],[0,0,0,0,0],[0,1,1,1,0],[0,0,0,0,0],[0,0,0,0,0]]}'
```

`edgeMode` is optional. `"dead"` treats cells beyond the border as dead and is the default. `"wrap"` joins opposite edges. Any other value, including an empty string, is rejected. The same cells with a different edge rule are a different board with a different ID.

```json
{ "id": "1ccg4h2ptv9qxkxyg2zfhqejat", "width": 5, "height": 5, "edgeMode": "dead", "cells": [[0,0,0,0,0], …] }
```

Errors use RFC 7807 problem details.

| Status | When |
|---|---|
| `400` | The grid is empty, ragged, larger than the configured maximum, or contains values other than 0 and 1. An unknown edge rule. A negative generation count. |
| `404` | No board has that ID. |
| `413` | The request body is larger than the configured limit. |
| `422` | The board did not settle within the configured step limit. |
| `507` | Storage has reached its configured maximum size. |

## Design

- **IDs are content hashes.** An ID is the first 128 bits of a SHA-256 of the board's size, edge rule and cells, written as 26 base32 characters. The same board always gets the same ID, so uploads are idempotent.
- **Each result is computed once.** A board's next generation and final state are stored when first computed, and later requests are lookups. Finding one board's final state also saves the final state of every board it passed through, and a later run stops as soon as it reaches any of them.
- **Cycles are detected and skipped.** Final state is found when a board's ID repeats. For an oscillator it is the cycle member with the smallest ID, with the steps to reach it and the period. Generation N on a cycling board is found by arithmetic, so huge N is instant.
- **Storage is durable.** LMDB, an embedded key-value store, commits every write to disk before the response, so data survives restarts and crashes.
- **Limits are enforced.** Board size, step limit, storage size and request size are configurable and validated at startup. Boards that do not settle within the step limit return 422.

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) covers the design in depth, including storage choices, scaling and known limits.

## Configuration

Set in `src/GameOfLife.Api/appsettings.json`, or per run with environment variables such as `GameOfLife__MaxSteps=5000`. Invalid values stop the app at startup.

| Setting | Default | Meaning |
|---|---|---|
| `GameOfLife:MaxWidth` | 256 | Widest board accepted. |
| `GameOfLife:MaxHeight` | 256 | Tallest board accepted. |
| `GameOfLife:MaxSteps` | 10000 | Most generations computed for one request. |
| `GameOfLife:DefaultEdgeMode` | `Dead` | Edge rule for uploads that do not name one. `Dead` treats cells beyond the border as dead. `Wrap` joins opposite edges. |
| `Storage:Path` | `data/boards` | Folder for the LMDB files. |
| `Storage:MaxSizeBytes` | 1 GB | Most space the store may use. |
| `Storage:CacheSizeLimitBytes` | 64 MB | Memory for the in-process cache. |
| `Kestrel:Limits:MaxRequestBodySize` | 1 MB | Largest request body. A full 256 x 256 board is about 130 KB. |

## AI usage

Built with an AI coding assistant. Every prompt is recorded in [docs/PROMPTS.md](docs/PROMPTS.md).
