# Architecture

How the Game of Life API is built and why. The [README](../README.md) covers running it and the endpoints.

## At a glance

```
Browser UI ──HTTP──▶ BoardsController ──▶ BoardService ──▶ CachedBoardStore ──▶ LmdbBoardStore ──▶ data.mdb
                                               │
                                         LifeSimulator ──▶ LifeEngine
```

- **GameOfLife.Core** is the domain with no I/O. It holds the board, the engine, cycle detection, the four API operations and the storage policy.
- **GameOfLife.Infrastructure** is storage. LMDB on disk with an in-memory cache in front.
- **GameOfLife.Api** is the ASP.NET Core host. Validation, problem details, the playback rate limit, response compression and the browser UI.

## What stands out

### 1. A board's ID is its content

The ID is the first 128 bits of a SHA-256 of the board's width, height, edge rule and cells, written as 26 base32 characters. The same board always gets the same ID, so uploads are idempotent and the browser can work out an ID without asking the server. The store compares the full board whenever an ID already exists, so even an impossible hash clash would fail loudly instead of returning the wrong board.

### 2. Nothing is computed twice

Every board has exactly one next generation, so stored boards form a graph of next links that fills in as requests arrive.

- **Next** follows a saved link, or computes one step and saves it.
- **Final state** steps until a board repeats or reaches a board whose final state is already known. It then saves a final state for every board on the way, cycle members included, because they all share the same final board and differ only in steps. Two boards whose futures meet compute the shared part once.
- **N ahead** walks saved next links first, which computes nothing. Past a board's final state, any generation follows from cycle arithmetic, so generation 10<sup>18</sup> of a settled board takes milliseconds.
- **One answer for oscillators.** The final state of a cycle is its member with the smallest ID, so every board leading into a cycle reports the same final board. Kinds are `extinct`, `still-life` and `oscillator`.

`MaxSteps` caps the generations one request walks or computes. Walked links count against it, so saved progress makes a request cheaper but never changes whether it succeeds.

### 3. Small boards are stored whole, large boards as recipes

`BoardStoragePolicy` is the one place that decides, and the store has a separate read and write path for each kind.

- **Stored in full.** Boards of up to 1 KB packed, which covers 8,192 cells such as 32 x 48 or 64 x 64. A random 32 x 48 board compresses to about 120 bytes, so a recipe would save little.
- **Stored as a recipe.** Larger boards keep full cells only at key boards, which are uploads, final boards and every 64th generation. Every other board stores the ID of its key board and how many generations to step from it. Loading one replays at most 63 generations, about 15 ms at 256 x 256, and the rebuilt board is checked against its ID.

Stored boards are Brotli-compressed when that is smaller, and responses are compressed with Brotli or gzip. IDs hash the uncompressed cells, so neither changes an ID.

Measured capacity of the default 1 GB store, counting each board with its next link and final state.

| Board | Stored as | Per board | Boards in 1 GB |
|---|---|---|---|
| 32 x 48 | In full | about 855 bytes | about 1.25 million |
| 256 x 256 | Recipes | about 675 bytes | about 1.6 million |
| 256 x 256 | In full, for comparison | about 6,470 bytes | about 166,000 |

### 4. Durable embedded storage

LMDB flushes every commit to disk before returning and never overwrites live pages, so a crash cannot lose or corrupt committed boards and there is no recovery step. It runs in-process, so `dotnet run` is all it takes. A board and the links to it are written in one transaction. RocksDB is built for write volume this service does not have, and FASTER KV can lose recent writes between checkpoints.

| Key | Value |
|---|---|
| `board:{id}` | A board stored in full, compressed when smaller |
| `recipe:{id}` | Key board ID and steps to replay |
| `next:{id}` | ID of the next generation |
| `final:{id}` | Final board ID, steps and period |

The cache is read-through and size-bounded at 64 MB. Writes go to LMDB and then drop the affected cache entries.

### 5. Batched, rate-limited playback

Play fetches boards in batches from `GET /boards/{id}/sequence` and plays them from a buffer. The buffer refills while two round trips of playback are left, and each batch covers about three seconds at the chosen speed. Once a batch reports that the sequence repeats, the UI loops the boards it has and stops asking. `next` and `sequence` share a per-client token bucket counted in boards, so the server's `MaxBoardsPerSecond` is both the UI's top speed and the limit a script cannot exceed.

### 6. A browser UI that checks the server

The UI is three static files with no framework. It hashes boards in the browser with the same layout as the server, so the ID updates as you draw, and it re-hashes every board the server returns and warns on a mismatch. It asks for a board's final state once, when the board is loaded or saved. Cells are drawn as stars that age and leave fading trails, worked out in the browser from consecutive boards at no extra cost. Server data is written with `textContent`, never as HTML.

## Scaling beyond one machine

Every value except a recipe is fully determined by its key, and every recipe for an ID rebuilds the same board, so servers sharing a store never conflict. Computing holds no state, so API instances scale behind a load balancer, and hash IDs shard evenly by prefix. Moving to DynamoDB, Cosmos DB or Redis means one new `IBoardStore` class.

## Known limits

- The first far jump or final state on a new board computes on the request thread, for up to `MaxSteps` generations. Only playback is rate limited, so slow requests at once compete for CPU.
- Loading a recipe board costs up to about 15 ms of replay, and reads are not rate limited.
- The playback limit is per IP address, so a client with many addresses gets a bucket for each.
- One request saves at most 20,000 generations and 32 MB of full boards.
- With `Wrap` edges, a board one or two cells wide counts the same cell as a neighbour more than once, as a torus that small does.

## Code layout

```
src/
  GameOfLife.Core
    Board.cs                       Immutable board, packed cells, content-hash ID
    LifeEngine.cs                  One generation, dead or wrapped edges
    LifeSimulator.cs               Runs generations, detects cycles, stops at known final states
    BoardService.cs                The API operations
    Storage/BoardStoragePolicy.cs  Which boards are stored in full and which as recipes
    Storage/IBoardStore.cs         Storage interface
  GameOfLife.Infrastructure
    LmdbBoardStore.cs              LMDB storage with the two storage paths
    CachedBoardStore.cs            In-memory cache
    BoardCodec.cs                  Stored board format and compression
  GameOfLife.Api
    Controllers/                   HTTP endpoints
    PlaybackRateLimiter.cs         Per-client boards per second
    wwwroot/                       Browser UI
tests/GameOfLife.Tests             Unit and in-memory API tests
```

## What I would do next

- **Faster generations.** Pack 64 cells per machine word, or use Hashlife for very large patterns.
- **Rate limits on reads and heavy endpoints.** Bound recipe replays and long first runs.
- **Operations.** Metrics, a container image and a retention policy for boards nobody has asked for in a long time.
