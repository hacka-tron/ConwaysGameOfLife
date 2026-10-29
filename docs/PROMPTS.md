# Prompt log

Every prompt sent to the AI assistant (Claude Code) while working on this exercise, in order and verbatim, typos included. A short note after each one says what it led to.

## Session 1 · 29 October 2026

### 1

```text
can you pick up the netdocuments assessment from downloads and create an artifact of what they are asking?
```

The assistant read the assessment PDF and made a one-page summary of the requirements, grading criteria and open design questions.

### 2

```text
ok I want to design this such that each board setup has a unique id, e.g. 1 square at 1,1 has a unique id, so does 2 squares at 1,1 and 1,2 etc. so you upload a board, it find the id associated with that board and returns it. for next generation, it returns the id of the board of that next generation. also we can store the final state of boards as we get them so we dont need to calculate it each time
```

Led to the content-addressed design. A board's ID is a hash of its contents, each board links to its next generation, and final states are saved once computed.

### 3

```text
do boards need to calculate each run, or is there some way to compute that more efficiently?
```

Discussed caching, skipping ahead once a board repeats, faster stepping techniques such as packed bits and Hashlife, and batching database writes.

### 4

```text
would it make sense to use a reddis cache to save the data in memory, and then use the db to save the data in cold storage? or can we just use reddis, and as the app starts it rehydrates from logs?
```

Compared Redis as a cache, Redis as the only store, and an in-process cache in front of a durable store.

### 5

```text
is IMemory cache the system memory?
```

Explained that IMemoryCache lives in the app's own process memory.

### 6

```text
as far as maximum board sizes, what limits would you set so that this is computationally efficient?
```

Proposed 256 x 256 boards and 10,000 generations per request, and keeping only the boards whose IDs are returned to clients.

### 7

```text
but wouldn't we be limited by the filled cells mor than anything?
```

Compared storing every cell with storing only live cells, and why a hard size limit is still needed.

### 8

```text
ok lets start with 256 x 256, but give me the option to configure that in teh settings, and also give the user option to configure the values of maximum steps and all these decisions on the server.
```

Limits, the edge rule and storage settings were made configurable in appsettings.json. A first implementation was started.

### 9

```text
also do we need a sql db? couldn't nosql fit in here?
```

### 10

```text
you didn't answer my questions about nosql
```

### 11

```text
yeah if it is just key value pair just use the simplest option, no point in switching
```

### 12

```text
thats not what I said at all, sql lite is not best for key value pairs
```

### 13

```text
stop implementing, lets think of the architecture first
```

Prompts 9 to 13 were sent while the first implementation was being written. The assistant misread prompt 11 as approval to keep SQLite. Implementation was paused to settle the architecture, with storage redesigned as key-value pairs and RocksDB proposed as an embedded key-value store.

### 14

```text
what do you mean runs inside the app with nothing to install? as in its natively packaged with C#
```

Explained embedded and server databases, and how the RocksDB NuGet package bundles native binaries for each platform.

### 15

```text
Also I want you to keep a list of the prompts I send you as that was one of teh requirements of the project, so recordm them somewhere
```

Started this log.

### 16

```text
how about Faster KV, that seems like a good option, and I heard it has high performance
```

Compared Microsoft's FASTER KV with RocksDB on durability, API complexity, maintenance status and whether its speed matters for this workload.

### 17

```text
how about lightning db
```

Evaluated LightningDB, the .NET wrapper for LMDB, against RocksDB for a write-once, read-many workload.

### 18

```text
ok also why embedded over distributed architecture?
```

Discussed the tradeoffs of an embedded store against a distributed one, and why the content-addressed design makes a later move to distributed storage straightforward.

### 19

```text
ok thats good, now continue with the implementation
```

Implementation resumed with LightningDB (LMDB) as the key-value store in place of SQLite.

### 20

```text
also on a separate agent, I want to make a simple gui for this to make it easier to visualize this process, so a user can draw out a board entry for example, and then run the various api operations
```

A second agent built a browser UI served by the API as static files. A user can draw a board, upload it, and step, jump ahead or find the final state while watching the board and the API calls.

### 21

```text
ok can you run it so I can test it out
```

The API was started locally so the owner could try the browser UI and the endpoints by hand.

### 22

```text
this is good, but there is no option to manually enter an id. also these ids are very long, is there a better way to make them shorter?
```

A field was added to the browser UI for loading a board by ID, and shorter ID formats were weighed.

### 23

```text
what are the changes for collisions at 16 chars? also can including special chars help?
```

Collision odds for 16 character IDs were worked out, along with how much a larger character set would shorten them.

### 24

```text
what is the industry standard for unique keys like this?
```

Common industry ID formats were compared, which moved the recommendation to 128 bit IDs.

### 25

```text
yeah that is fine, also is there any way to not force someone to click upload manually? like they just draw the board, and get a deterministic id?
```

IDs moved to 26 lowercase characters (128 bits). The browser UI now works out a drawn board's ID as you draw and saves the board on its own the first time an operation needs it.

### 26

```text
like each board would just be a has, and that hash can be compared?
```

Confirmed the approach. The browser hashes the drawn board the same way the server does, so the ID it shows matches the server's and can be compared directly.

### 27

```text
one more thing, can we make load just auto load a generaed board? also the upload button isn't necessary, we will also have a standalone api if the reviewers want to use that
```

The Upload button was removed from the browser UI and the ID field now loads a board as soon as a complete ID is pasted or typed.

### 28

```text
so if we load a board not stored, then I want to generate that board in our db (if its a valid id)
```

An ID is a one-way hash, so a board cannot be rebuilt from an ID that was never stored. The UI now says so when an ID is not found, and IDs that encode the board itself were offered as an alternative.

### 29

```text
also I think the api log and history is redundant, just keep the api log
```

The History panel was removed from the browser UI. The API log stays.

### 30

```text
also the whole UI flickers when I click the button, why is that? can we avoid this without a frontend framework?
```

The flicker was traced and fixed in plain JavaScript and CSS, without a framework.

### 31

```text
ok last thing, but the patterns should clear the board and paste the pattern, and you should let people copy the ids from the API calls
```

Patterns now replace the board instead of adding to it, and board IDs in the API log can be copied with a click.
