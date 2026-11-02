using System.Net;
using System.Net.Http.Json;
using GameOfLife.Api;
using GameOfLife.Core;
using GameOfLife.Core.Storage;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GameOfLife.Tests.Integration;

/// <summary>
/// Small boards are stored in full and large boards as recipes between key boards. Most tests here force
/// every board to be a recipe, with a key board every 4 generations, so the recipe path runs on small boards.
/// </summary>
public sealed class RecipeStorageTests : IDisposable
{
    private static readonly Dictionary<string, string> AllRecipes = new()
    {
        ["Storage:FullBoardMaxBytes"] = "0",
        ["Storage:KeyBoardInterval"] = "4",
    };

    private readonly TempStorage _storage = new();
    private readonly LifeSimulator _simulator = new(new LifeEngine());

    public void Dispose() => _storage.Dispose();

    [Fact]
    public void SmallBoardsAreStoredInFullAndLargeBoardsAsRecipes()
    {
        var policy = BoardStoragePolicy.Default;

        Assert.Equal(BoardStorageKind.Full, policy.KindOf(Patterns.Parse(Patterns.GliderOn(48))));
        Assert.Equal(BoardStorageKind.Full, policy.KindOf(Board.FromCells(new bool[48, 32], EdgeMode.Dead)));
        Assert.Equal(BoardStorageKind.Recipe, policy.KindOf(Patterns.Parse(Patterns.GliderOn(128))));
    }

    [Fact]
    public void ChainsOfLargeBoardsKeepAKeyBoardEveryInterval()
    {
        var policy = new BoardStoragePolicy(KeyBoardInterval: 4, FullBoardMaxBytes: 0);
        var boards = Generations(Patterns.Parse(Patterns.GliderOn(20)), 10);

        var fromKey = policy.StartChain(stepsFromKey: 0);
        var fromRecipe = policy.StartChain(stepsFromKey: 2);
        foreach (var board in boards)
        {
            fromKey.Add(board);
            fromRecipe.Add(board);
        }

        Assert.Equal("rrrKrrrKrr", Shape(fromKey));
        Assert.Equal("rKrrrKrrrK", Shape(fromRecipe));
    }

    [Fact]
    public void ChainsOfSmallBoardsStoreEveryBoardInFull()
    {
        var chain = BoardStoragePolicy.Default.StartChain(stepsFromKey: 0);
        foreach (var board in Generations(Patterns.Parse(Patterns.GliderOn(20)), 10))
        {
            chain.Add(board);
        }

        Assert.Equal("KKKKKKKKKK", Shape(chain));
    }

    [Fact]
    public async Task EveryBoardOnAFinalStateRunCanBeLoaded()
    {
        using var factory = new ApiFactory(_storage.Path, AllRecipes);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(12));

        var final = await GetFinalAsync(client, await UploadAsync(client, start));

        var store = factory.Services.GetRequiredService<IBoardStore>();
        for (int generation = 0; generation <= final.Steps; generation++)
        {
            var expected = _simulator.Advance(start, generation, CancellationToken.None);
            var loaded = await client.GetFromJsonAsync<BoardResponse>($"/boards/{expected.Id}");
            Assert.Equal(Patterns.ToGrid(Patterns.ToText(expected)), loaded!.Cells);

            var record = await store.GetAsync(expected.Id, CancellationToken.None);
            Assert.InRange(record!.StepsFromKey, 0, 3);
        }
    }

    [Fact]
    public async Task JumpingAlongASavedChainOnlyReplaysFromTheNearestKeyBoard()
    {
        var engine = new CountingEngine();
        using var baseFactory = new ApiFactory(_storage.Path, AllRecipes);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILifeEngine>(engine)));
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(20));
        string id = await UploadAsync(client, start);
        await client.GetAsync($"/boards/{id}/sequence?count=30");

        engine.Steps = 0;
        var jumped = await client.GetFromJsonAsync<BoardResponse>($"/boards/{id}/generations/27");

        Assert.Equal(_simulator.Advance(start, 27, CancellationToken.None).Id, jumped!.Id);
        // Generation 27 is 3 past the key board at 24, so loading it replays 3 generations and computes nothing else.
        Assert.InRange(engine.Steps, 0, 3);
    }

    [Fact]
    public async Task PlaybackOverRecipeBoardsReturnsTheRightBoards()
    {
        using var factory = new ApiFactory(_storage.Path, AllRecipes);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(20));
        string id = await UploadAsync(client, start);
        await client.GetAsync($"/boards/{id}/sequence?count=10");

        var replayed = await client.GetFromJsonAsync<BoardSequenceResponse>($"/boards/{id}/sequence?count=15");

        Assert.Equal(
            Enumerable.Range(1, 15).Select(g => _simulator.Advance(start, g, CancellationToken.None).Id),
            replayed!.Boards.Select(b => b.Id));
    }

    [Fact]
    public async Task LargeBoardsUseRecipesWithTheDefaultSettings()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(128));
        string id = await UploadAsync(client, start);

        var sequence = await client.GetFromJsonAsync<BoardSequenceResponse>($"/boards/{id}/sequence?count=100");

        var store = factory.Services.GetRequiredService<IBoardStore>();
        var steps = new List<int>();
        foreach (var board in sequence!.Boards)
        {
            steps.Add((await store.GetAsync(board.Id, CancellationToken.None))!.StepsFromKey);
        }

        // Generation 64 is the only key board in the first 100, and everything else replays at most 63.
        Assert.Equal([64], steps.Select((s, i) => (s, generation: i + 1)).Where(x => x.s == 0).Select(x => x.generation));
        Assert.Equal(63, steps.Max());
    }

    private List<Board> Generations(Board start, int count)
    {
        var boards = new List<Board>();
        for (int i = 0; i < count; i++)
        {
            start = _simulator.Advance(start, 1, CancellationToken.None);
            boards.Add(start);
        }

        return boards;
    }

    // K for a link stored in full, r for a recipe.
    private static string Shape(ChainBuilder chain) =>
        string.Concat(chain.Links.Select(link => link.FullBoard is null ? 'r' : 'K'));

    private static async Task<string> UploadAsync(HttpClient client, Board board)
    {
        var response = await client.PostAsJsonAsync("/boards", new UploadBoardRequest { Cells = Patterns.ToGrid(Patterns.ToText(board)) });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardResponse>())!.Id;
    }

    private static async Task<FinalStateResponse> GetFinalAsync(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/boards/{id}/final");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FinalStateResponse>())!;
    }

    private sealed class CountingEngine : ILifeEngine
    {
        private readonly LifeEngine _inner = new();

        public int Steps { get; set; }

        public Board Step(Board board)
        {
            Steps++;
            return _inner.Step(board);
        }
    }
}
