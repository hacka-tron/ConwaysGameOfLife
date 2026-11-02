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
- **GameOfLife.Api** is the ASP.NET Core host. Controllers map HTTP to `BoardService`, `BoardRequestValidator` checks uploads against the configured limits, `PlaybackRateLimiter` caps how many boards each client receives per second, and `BoardExceptionHandler` turns failures into problem details. It also serves the browser UI from `wwwroot`.

## A board's ID is its content

The ID comes from a SHA-256 hash of the board's width, height, edge rule and cells. The same board always gets the same ID, so uploading it twice returns the same ID, and an ID always means exactly one board. Because the ID depends only on the board, a client can work it out without asking the server. The browser UI does this as you draw.

The ID keeps the first 128 bits of the hash and writes them as 26 lowercase characters, using the same alphabet as a ULID. 128 bits is the usual minimum for unique IDs. At that size two boards will not share an ID by chance, and nobody can find two that do on purpose. The store also compares the full board whenever an ID already exists, so a clash would be refused rather than return the wrong board. Size and edge rule are part of the hash because the same live cells evolve differently on a board of another size or with different edges. The hashed text starts with a version tag, so a future change to the format can produce new IDs without clashing with old ones.

## Computing generations once

Every board has exactly one next generation, so stored boards form a graph of next links that fills in as requests arrive. Results are saved when first computed.

- **Next** follows the stored link if there is one. Otherwise it computes one generation and stores the new board and the link together.
- **Final** steps forward until a board's ID repeats or it reaches a board whose final state is already known. A still life repeats after one step and an oscillator after its period. Every board on the way is saved with its next link and its final state, so asking about the starting board or any board after it needs no new simulation. A board stored as a recipe still replays up to 63 generations when it is loaded.
- **N ahead** first follows saved next links, which costs microseconds per generation and computes nothing. If they reach generation N, or loop back into a cycle, the answer is a stored board. Otherwise it steps forward from where the links end. If the final state turns up first, any later generation follows from arithmetic on the cycle. So asking a settled board for generation 10<sup>18</sup> takes milliseconds.
- **Saved links never change whether a request succeeds.** Generations walked along saved links count against `MaxSteps` just like computed ones, and when the shortcut cannot finish within what is left, the request runs from the start as if no links were saved. Saved links only make a request cheaper. A final state saved by another request can still let a later request finish sooner, as it always could.
- **Every computed generation is saved**, including when a run stops at `MaxSteps`, so those boards can be loaded later by ID.

### Final states for every board on the way

All boards on one run share the same final board and period. Only the steps differ, and they follow from each board's position on the run. A board before the final board counts down to it. A cycle member past the final board goes round the cycle once more to come back to it. So one run saves a final state for every board it stepped through, cycle members included, in one transaction.

Each step of a run also checks whether the current board's final state is already known. If it is, the run stops there and adds that board's steps to its own. Two different starting boards whose futures meet only compute the shared part once. This can also answer a board that needs more than `MaxSteps` generations to settle, as long as it joins a known path within the limit. `MaxSteps` still caps the generations computed in one request.

The extra check costs one key lookup per generation. On the random 256 x 256 board below it made no measurable difference. That run saved about 2,400 final state records, and the whole database stayed under 1 MB.

### Kinds of final state

A board that settles is `extinct` when every cell has died, a `still-life` when live cells remain but never change, and an `oscillator` when it repeats a longer cycle. An empty board is a period 1 cycle like a still life, so the kind is decided by whether any cell is alive.

### The final state of an oscillator

A cycle has several boards, so "the final state" needs one fixed answer. It is the cycle member with the smallest ID. Every board that leads into the same cycle then reports the same final board, which keeps the cache consistent. `steps` is how many generations it takes to first reach that board.

Every board a run steps through is saved, in full or as a recipe as described under Storage, so any of them can be loaded later by ID. A single request saves at most 20,000 generations and 32 MB of full boards, which keeps its memory bounded.

## Edge rules

Each board carries its own edge rule. With dead edges, cells beyond the border count as dead. With wrapped edges, opposite edges join, so the board behaves like the surface of a torus. The rule is chosen per upload and defaults to dead. It is part of the hash, so the same cells under each rule are two different boards with two different IDs, and every generation of a board keeps its rule.

## Storage

Boards are stored in LMDB, an embedded key-value store, through the LightningDB package.

| Key | Value |
|---|---|
| `board:{id}` | A board stored in full. Format version, width, height, edge rule and the cells packed as bits, Brotli-compressed when that is smaller. |
| `recipe:{id}` | A large board stored as a recipe. The ID of a board stored in full and how many generations to step from it. |
| `next:{id}` | ID of the next generation. |
| `final:{id}` | Steps, period and the ID of the final board. Written for every board a run passes through, so the board itself may not be stored. |

Every lookup is by key, and almost every value is written once and never changes, so a key-value store fits better than a relational database. The one exception is a recipe, which can be replaced by a shorter one for the same board. LMDB was chosen for four reasons.

- Each commit is flushed to disk before it returns, and LMDB never overwrites live data. A crash cannot lose or corrupt committed boards, and there is no recovery step on startup.
- Reads are lock-free from memory-mapped pages, which suits data that is written once and read many times.
- It runs inside the process, so the project needs nothing beyond `dotnet run`. The NuGet package includes the native library for Windows, macOS and Linux.
- A board and the link to it are written in one transaction.

RocksDB was the main alternative. It is built for heavy write volume, which this service does not have. Microsoft's FASTER KV is faster still, but it saves to disk at checkpoints, so a crash can lose recent writes unless a checkpoint is forced before every response.

### Two ways to store a board

`BoardStoragePolicy` sorts every board into one of two kinds by size, and the store has a separate read and write path for each.

- **Stored in full.** Boards whose packed cells fit in `Storage:FullBoardMaxBytes`, 1,024 bytes by default, which covers boards of up to 8,192 cells such as 32 x 48 or 64 x 64. A random 32 x 48 board compresses to about 120 bytes, close to the size of a recipe, so a recipe would save little. Reading one is a single lookup.
- **Stored as a recipe.** Larger boards keep their full cells only at key boards. Those are uploads, final boards and one board every `Storage:KeyBoardInterval` generations along a chain, 64 by default. An upload of a board that already has a recipe keeps the recipe, since the board can already be loaded. Every other board is a recipe that names its key board and how many generations to step from it. Reading one loads the key board and replays at most 63 generations, about 15 ms at 256 x 256 since one generation takes about 0.24 ms. Reading the next board in a chain is a single step from the one before. The rebuilt board's ID is checked against the requested ID, so a bad recipe fails loudly instead of returning the wrong board.

A large board can have more than one valid recipe, from different chains. Every recipe for an ID rebuilds the same board, so the store keeps the shortest one it has seen. That also guarantees a recipe never replays further than the interval allows. The rest of the store keeps the rule that every value is fully determined by its key.

### How many boards fit

Measured over about 15,000 saved generations of random boards, counting everything stored for each generation, which is the board or recipe, its next link and its final state. With the default `Storage:MaxSizeBytes` of 1 GB:

| Board | Stored as | Disk per generation | Generations in 1 GB | Complete runs in 1 GB |
|---|---|---|---|---|
| 32 x 48 | In full | about 855 bytes | about 1.25 million | about 4,000 of about 300 generations |
| 256 x 256 | Recipes | about 675 bytes | about 1.6 million | about 580 of about 2,750 generations |
| 256 x 256 | In full, for comparison | about 6,470 bytes | about 166,000 | about 60 |

At 256 x 256, recipes fit about 10 times more boards. Uncompressed full boards, as stored before compression, took about 21 KB each, so the two changes together fit about 30 times more. For small boards the board itself is no longer the main cost. Its next link, its final state and LMDB's own page overhead take most of the space. `Storage:MaxSizeBytes` can be raised to any size the disk allows.

The in-memory cache holds rebuilt boards uncompressed, up to `Storage:CacheSizeLimitBytes`, 64 MB by default. That is about 95,000 boards at 32 x 48 or about 7,700 at 256 x 256, and memory never grows past the limit.

### Compression

Cells are packed eight to a byte, so a 256 x 256 board is 8,192 bytes whatever it holds. Most boards are mostly dead cells, so the packed cells are Brotli-compressed before they are written, and kept raw when compression would not make them smaller. A settled 256 x 256 board drops to about 1.6 KB and a lone glider to under 100 bytes. Random boards barely compress, so nothing is ever stored larger than before.

This also matters for how LMDB lays out data. On a machine with 16 KB pages, an 8 KB value is too big to sit in a normal page, so it takes a whole page of its own. Uncompressed boards measured about 21 KB each on disk. Over the first 1,200 generations of a random 256 x 256 board, compressed boards averaged about 7.9 KB, and settled boards take far less.

IDs hash the uncompressed cells, so compression never changes an ID. The first byte of each stored board says whether it is raw or compressed, and boards written before compression was added still load. When a board is uploaded again, the stored and new boards are compared cell by cell rather than byte by byte, so an older raw copy is not mistaken for a hash clash. The in-memory cache keeps boards uncompressed, so reading a cached board costs nothing extra.

Responses are compressed too, with Brotli or gzip at the fastest level, for any client that asks. Board JSON is mostly repeated zeros, so a batch of 90 boards at 256 x 256 shrinks from about 12 MB to about 1 MB, for about 20 ms of extra work on localhost.

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
- A board's final state is asked for once, as soon as it is loaded or saved. Every later generation reaches the same final board, so Next, Jump, Play and Find final state reuse that answer, and the Final state panel appears once stepping reaches it. Editing the board or loading another one starts over.
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

## Playback

Play shows boards at a chosen speed, up to `MaxBoardsPerSecond`. It used to send one `next` request per generation, so a slow network capped its speed at one board per round trip. It now asks for batches.

- **`GET /boards/{id}/sequence?count=n`** returns up to `n` consecutive generations. The saved next links are read in one read transaction, the rest are computed, and every new board and link is saved in one write transaction. When a generation repeats one already in the batch, the batch ends and `repeatsFrom` says which generation comes next. A still life returns no boards and 0.
- **The UI keeps a buffer** of boards it has not shown yet and asks for the next batch, starting from the last board it has, when the buffer runs low. One request is in flight at a time, so batches arrive in order. Once a batch reports a repeat, the UI loops the boards it has and sends no more requests. An oscillator plays forever for free.
- **Pause keeps the buffer.** Pressing Play again on the same board carries on from it instead of fetching it again. Any edit, load or step drops it. The first batch after Play covers one second rather than three, so playback starts sooner.
- **Failures do not stop playback at once.** Playback carries on from the buffer while a failed batch is retried. A rate limit answer waits for its `Retry-After` time and says so on screen. Any other failure gets one retry, and playback stops only if that fails too.
- **The refill point and batch size are set in time and turned into boards at the chosen speed.** The UI asks for more when the buffer holds less than two round trips of playback, and each batch covers three seconds, between 10 boards and `MaxBatchSize`. The round trip is the slowest of the last five batches. At 30 boards per second with a 300 ms round trip, that means a batch of 90 whenever 18 or fewer are left. At 4 per second it is a batch of 12 at 3 left. So the buffer never runs dry, and a pause or edit throws away only a few seconds of boards.
- **The server sets the cap.** The UI reads `MaxBoardsPerSecond` and `MaxBatchSize` from `/settings`. Its top speed is the cap, and slower preset speeds above it are dropped.

### Rate limit

`next` and `sequence` share a per-client token bucket counted in boards, not requests, so a batch of 90 costs the same as 90 single steps. It refills at `MaxBoardsPerSecond` and holds twice `MaxBatchSize`, which is the most the UI ever buffers ahead. Continuous play at top speed never hits it, and a client pulling faster is held to that rate with `429` and `Retry-After`. Starting over and over on new boards at top speed can reach it briefly, and the UI then waits and says so. Clients are told apart by IP address. Behind a reverse proxy the proxy must forward the client address, or every client would share one bucket. Uploads, generation jumps and final states are not part of this limit.

## Known limits

- The first request for a far generation or a final state on a new board computes on the request thread, for up to `MaxSteps` generations. Only playback is rate limited, so several slow requests at once still compete for CPU. A limit on those endpoints or a work queue would fix this in production.
- The playback limit is per IP address. A client with many addresses, such as a block of IPv6 addresses, gets a bucket for each. A batch is charged in full when it is requested, even if it ends early on a repeat or the board does not exist.
- With `Wrap` edges, a board one or two cells wide counts the same cell as a neighbour more than once, the way a torus that small works mathematically. A single live cell on a 1 x 1 wrapped board therefore has 8 live neighbours and dies.
- Boards travel as JSON grids of 0s and 1s, about 130 KB for a 256 x 256 board before response compression and about 10 to 20 KB after. The server still builds and compresses the full JSON for every request. Packed bits in base64 would cut the work and the size further.
- Loading a board stored as a recipe replays up to 63 generations, about 15 ms at 256 x 256, and `GET /boards/{id}` is not rate limited. A client that loads many different recipe boards, more than the cache holds, makes the server do that replay each time. A rate limit on reads would bound it.
- One request saves at most 20,000 generations and at most 32 MB of full boards, so a run longer than that saves only its first part. Final state records are still written for every board on the run.
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
    Storage/BoardStoragePolicy.cs  Which boards are stored in full and which as recipes
  GameOfLife.Infrastructure
    LmdbBoardStore.cs        LMDB storage
    CachedBoardStore.cs      In-memory cache in front of any store
    BoardCodec.cs            Stored board format, compressed when that is smaller
  GameOfLife.Api
    Controllers/             HTTP endpoints
    BoardRequestValidator.cs Upload validation against the configured limits
    BoardExceptionHandler.cs Maps failures to problem details
    PlaybackRateLimiter.cs   Per-client limit on boards per second through playback
    wwwroot/                 Browser UI
tests/
  GameOfLife.Tests           Unit tests (Unit/) and in-memory API tests (Integration/)
```

## What I would do next

- **Faster generations.** Packing 64 cells into each machine word lets one step compute many cells at once. Hashlife caches the future of repeated regions and suits very large patterns.
- **Resume long runs.** Every generation a run computes is already saved, even when it stops at the step limit. An opt-in "continue" request could walk that saved progress and keep going, while the normal endpoints keep their fixed step limit.
- **Operations.** Rate limiting, metrics, a container image, and a retention policy for boards nobody has requested in a long time.
