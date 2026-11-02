# Architecture

This document explains how the Game of Life API is built and why. The [README](../README.md) covers running it and the API itself.

## Components

```
Browser UI (wwwroot)  ──HTTP──▶  BoardsController ──▶ BoardService ──▶ CachedBoardStore ──▶ LmdbBoardStore ──▶ data.mdb
                                        │                  │
                               BoardRequestValidator   LifeSimulator ──▶ LifeEngine
```

- **GameOfLife.Core** holds the domain with no I/O. `Board` is an immutable board with packed cells and a content-hash ID. `LifeEngine` computes one generation. `LifeSimulator` runs many generations and detects cycles. `BoardService` implements the API operations and saves results as they are computed. `IBoardStore` is the storage interface.
- **GameOfLife.Infrastructure** implements storage. `LmdbBoardStore` writes to LMDB, and `CachedBoardStore` keeps recent records in memory in front of it.
- **GameOfLife.Api** is the ASP.NET Core host. Controllers map HTTP to `BoardService`, `BoardRequestValidator` checks uploads against the configured limits, and `BoardExceptionHandler` turns failures into problem details. It also serves the browser UI from `wwwroot`.

## A board's ID is its content

The ID comes from a SHA-256 hash of the board's width, height, edge rule and cells. The same board always gets the same ID, so uploading it twice returns the same ID, and an ID always means exactly one board. Because the ID depends only on the board, a client can work it out without asking the server. The browser UI does this as you draw.

The ID keeps the first 128 bits of the hash and writes them as 26 lowercase characters, using the same alphabet as a ULID. 128 bits is the usual minimum for unique IDs. At that size two boards will not share an ID by chance, and nobody can find two that do on purpose. The store also compares the full board whenever an ID already exists, so a clash would be refused rather than return the wrong board. Size and edge rule are part of the hash because the same live cells evolve differently on a board of another size or with different edges. The hashed text starts with a version tag, so a future change to the format can produce new IDs without clashing with old ones.

## Computing generations once

Every board has exactly one next generation, so stored boards form a graph of next links that fills in as requests arrive. Results are saved when first computed.

- **Next** follows the stored link if there is one. Otherwise it computes one generation and stores the new board and the link together.
- **Final** steps forward until a board's ID repeats or it reaches a board whose final state is already known. A still life repeats after one step and an oscillator after its period. The result is saved for every board on the way, so asking about the starting board or any board after it is a single lookup.
- **N ahead** steps forward up to N. If the final state turns up first, any later generation follows from arithmetic on the cycle. So asking a settled board for generation 10<sup>18</sup> takes milliseconds.

### Final states for every board on the way

All boards on one run share the same final board and period. Only the steps differ, and they follow from each board's position on the run. A board before the final board counts down to it. A cycle member past the final board goes round the cycle once more to come back to it. So one run saves a final state for every board it stepped through, cycle members included, in one transaction.

Each step of a run also checks whether the current board's final state is already known. If it is, the run stops there and adds that board's steps to its own. Two different starting boards whose futures meet only compute the shared part once. This can also answer a board that needs more than `MaxSteps` generations to settle, as long as it joins a known path within the limit. `MaxSteps` still caps the generations computed in one request.

The extra check costs one key lookup per generation. On the random 256 x 256 board below it made no measurable difference. That run saved about 2,400 final state records, and the whole database stayed under 1 MB.

### The final state of an oscillator

A cycle has several boards, so "the final state" needs one fixed answer. It is the cycle member with the smallest ID. Every board that leads into the same cycle then reports the same final board, which keeps the cache consistent. `steps` is how many generations it takes to first reach that board.

Only boards whose IDs are returned to a client are stored. The intermediate boards of a long run are dropped, because an ID cannot be turned back into a board and nobody can ask for one they were never given. Their final state records are kept, because they are keyed by ID alone. A later run computes the same IDs as it steps, and a client who uploads or reaches one of those boards later gets its final state straight away.

## Edge rules

Each board carries its own edge rule. With dead edges, cells beyond the border count as dead. With wrapped edges, opposite edges join, so the board behaves like the surface of a torus. The rule is chosen per upload and defaults to dead. It is part of the hash, so the same cells under each rule are two different boards with two different IDs, and every generation of a board keeps its rule.

## Storage

Boards are stored in LMDB, an embedded key-value store, through the LightningDB package.

| Key | Value |
|---|---|
| `board:{id}` | Format version, width, height, edge rule and the cells packed as bits. |
| `next:{id}` | ID of the next generation. |
| `final:{id}` | Steps, period and the ID of the final board. Written for every board a run passes through, so the board itself may not be stored. |

Every lookup is by key, and every value is written once and never changes, so a key-value store fits better than a relational database. LMDB was chosen for four reasons.

- Each commit is flushed to disk before it returns, and LMDB never overwrites live data. A crash cannot lose or corrupt committed boards, and there is no recovery step on startup.
- Reads are lock-free from memory-mapped pages, which suits data that is written once and read many times.
- It runs inside the process, so the project needs nothing beyond `dotnet run`. The NuGet package includes the native library for Windows, macOS and Linux.
- A board and the link to it are written in one transaction.

RocksDB was the main alternative. It is built for heavy write volume, which this service does not have. Microsoft's FASTER KV is faster still, but it saves to disk at checkpoints, so a crash can lose recent writes unless a checkpoint is forced before every response.

An in-process cache sits in front of the store. Boards never change and their links only go from unknown to known, so a cached entry can at worst be missing a link that was saved since.

### Cache access pattern

- **Read-through.** A read checks the in-memory cache by board ID first. On a miss it reads the board and its `next` and `final` records from LMDB in one read transaction and caches them together as one entry.
- **Write to the store, then drop the cache entry.** Writes go straight to LMDB. Saving a link then removes the affected cache entries, so the next read loads the fresh record. Uploaded boards are not cached until they are first read.
- **Bounded by size.** Each entry is charged its packed cells plus a fixed overhead, against `Storage:CacheSizeLimitBytes`. When an entry would go over the limit it is not cached, and about 5% of entries are evicted in the background, least recently used first.
- **Final state lookups during a run.** These check the cache first and then LMDB. Most of them miss, because the boards are new, and an LMDB key lookup takes microseconds. Misses are not cached, because a missing final state can become known at any time.

## Browser UI

The UI is three static files in `src/GameOfLife.Api/wwwroot` with no framework and no build step. It draws boards on a canvas and talks to the API like any other client.

- It computes a drawn board's ID in the browser with the same SHA-256 layout as the server, so the ID updates as you draw. Every board the server returns is hashed again in the browser, and a mismatch would show a warning, so the two implementations cannot drift apart unnoticed.
- A drawn board is saved with `POST /boards` the first time an operation needs it.
- Every request is shown in the API log, and IDs there can be copied with a click.
- All server data is written to the page with `textContent`, never as HTML.

## Performance

A 256 x 256 board takes under a millisecond per generation on an Apple Silicon laptop. A random 256 x 256 board measured during development took 2,356 generations and 1.7 seconds to settle, and asking again took 9 ms. The worst case at the default limits is about 7 seconds of work, and requests stop early if the client disconnects.

## Scaling beyond one machine

An embedded store ties the service to one machine. The design is ready to move past that.

- Every value is fully determined by its key, so two servers writing the same key always write the same value. There are no write conflicts to resolve.
- Computing generations holds no state, so any number of API instances can run behind a load balancer.
- Hash IDs spread evenly, so the data shards cleanly by ID prefix.

Moving to a distributed store such as DynamoDB, Cosmos DB or a Redis cluster means writing one new `IBoardStore` class. The one rule to keep is that a board must be durably stored before its ID is returned.

## Known limits

- The first request for a far generation or a final state on a new board computes on the request thread, for up to `MaxSteps` generations. There is no cap on concurrent requests, so several slow requests at once compete for CPU. Rate limiting or a work queue would fix this in production.
- With `Wrap` edges, a board one or two cells wide counts the same cell as a neighbour more than once, the way a torus that small works mathematically. A single live cell on a 1 x 1 wrapped board therefore has 8 live neighbours and dies.
- LMDB allows 126 concurrent readers by default. Reads take microseconds, so this is far beyond what one instance would reach, but it is a setting to raise under heavy load.

## Code layout

```
src/
  GameOfLife.Core            Domain logic with no I/O
    Board.cs                 Immutable board, packed cells, content-hash ID
    LifeEngine.cs            One generation under the B3/S23 rule, dead or wrapped edges
    LifeSimulator.cs         Runs generations, detects cycles, stops at known final states
    BoardService.cs          The API operations, saving results as they are computed
    Storage/IBoardStore.cs   Storage interface
  GameOfLife.Infrastructure
    LmdbBoardStore.cs        LMDB storage
    CachedBoardStore.cs      In-memory cache in front of any store
  GameOfLife.Api
    Controllers/             HTTP endpoints
    BoardRequestValidator.cs Upload validation against the configured limits
    BoardExceptionHandler.cs Maps failures to problem details
    wwwroot/                 Browser UI
tests/
  GameOfLife.Tests           Unit tests (Unit/) and in-memory API tests (Integration/)
```

## What I would do next

- **Faster generations.** Packing 64 cells into each machine word lets one step compute many cells at once. Hashlife caches the future of repeated regions and suits very large patterns.
- **Resume long runs.** Remember how far a board got before hitting the step limit, so a later request continues from there. The final state records along a path are a step towards this.
- **Operations.** Rate limiting, metrics, a container image, and a retention policy for boards nobody has requested in a long time.
