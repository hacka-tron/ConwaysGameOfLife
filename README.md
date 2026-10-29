# Game of Life API

A REST API for Conway's Game of Life, written in C# on .NET 8, with a browser UI for drawing boards and watching the API work.

## Overview

Upload a board and get back an ID. Then ask for its next generation, the board any number of generations ahead, or the state it finally settles into. Every answer is a board with its own ID, so results can be chained and shared.

- **A board's ID is its content.** The ID is a hash of the board's size, edge rule and cells. The same board always gets the same ID, uploading it twice is harmless, and anyone can work out an ID without asking the server. The browser UI does exactly that as you draw.
- **Work is done once.** Each board's next generation and final state are stored the first time they are computed. Asking again is a single lookup, from any board that leads to the same place.
- **Cycles are handled with arithmetic.** Once a board starts repeating, any later generation follows from the cycle length. Asking a settled board for generation 10<sup>18</sup> takes milliseconds.
- **Final state has one clear answer.** Still lifes and oscillators are both detected. For an oscillator the answer is a fixed member of its cycle, along with how many steps it takes to get there and the cycle length. Boards that never settle within the step limit get a clear error.
- **Edges are part of the board.** Each board uses either dead edges, where cells beyond the border are dead, or wrapped edges, where opposite edges join. The same cells under a different rule are a different board with a different ID.
- **Data survives restarts and crashes.** Boards are stored in LMDB, an embedded key-value store that writes every commit to disk before returning and never overwrites live data.
- **Limits are configurable.** Board size, step limit, storage size, cache size and request size all come from settings and are checked at startup.
- **Errors are consistent.** Every failure is an RFC 7807 problem details response with a fitting status code.
- **A browser UI shows it working.** Draw a board, switch edge rules, step, jump, play, find the final state and load any board by ID, with a log of every API call.
- **It is tested.** Unit tests cover the rules, IDs and cycle detection, and in-memory API tests cover every endpoint, validation, configuration and a restart.

## Run it locally

### 1. Install the .NET 8 SDK

Check whether you already have it.

```bash
dotnet --list-sdks
```

A line starting with `8.` means you are set. Otherwise install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 or with a package manager.

```bash
# macOS
brew install dotnet@8

# Windows
winget install Microsoft.DotNet.SDK.8

# Ubuntu
sudo apt-get install -y dotnet-sdk-8.0
```

On macOS, Homebrew installs `dotnet@8` alongside other versions without adding it to your path. Run the two lines it prints after installing, or add these to your shell profile.

```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
```

Nothing else is needed. The storage engine ships inside its NuGet package for Windows, macOS and Linux, so there is no database to install.

### 2. Get the code and start the API

```bash
git clone https://github.com/hacka-tron/ConwaysGameOfLife.git
cd ConwaysGameOfLife
dotnet run --project src/GameOfLife.Api
```

The first run restores packages and builds, which takes a minute. The API is ready when it prints `Now listening on: http://localhost:5168`. Stop it with Ctrl+C.

### 3. Try it

- **Browser UI** at http://localhost:5168. Draw a board or pick a pattern, choose dead or wrapped edges, then run Next generation, Jump ahead, Find final state or Play. The board's ID updates as you draw. The board is saved the first time you run an operation on it, and the API log at the bottom shows every request, with IDs you can click to copy.
- **Swagger** at http://localhost:5168/swagger, to read and call each endpoint.
- **curl**, as shown in the API section below.

Boards are stored in `src/GameOfLife.Api/data/` and survive restarts. Delete that folder to start empty.

### 4. Run the tests

```bash
dotnet test
```

The tests cover the rule engine, cycle detection, every endpoint, validation, configuration and persistence across a restart.

### Changing settings

Limits and defaults live in `src/GameOfLife.Api/appsettings.json` and are listed under Configuration below. Any of them can also be set for one run with an environment variable.

```bash
GameOfLife__MaxSteps=5000 dotnet run --project src/GameOfLife.Api
```

To use another port, pass `--urls`.

```bash
dotnet run --project src/GameOfLife.Api --urls http://localhost:6000
```

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

## Configuration

All limits live in `src/GameOfLife.Api/appsettings.json` and can be overridden with environment variables such as `GameOfLife__MaxSteps=5000`. Invalid values stop the app at startup with a clear message.

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

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains the design. It covers content-hash IDs, how results are computed once and reused, cycle handling, storage, the browser UI, performance, scaling and known limits.
- [docs/PROMPTS.md](docs/PROMPTS.md) records every prompt sent to the AI coding assistant while building this.

## How this was built

This project was built with an AI coding assistant. Every prompt sent to it is recorded in [docs/PROMPTS.md](docs/PROMPTS.md).
