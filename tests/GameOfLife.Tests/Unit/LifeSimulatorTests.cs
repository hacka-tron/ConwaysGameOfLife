using GameOfLife.Core;

namespace GameOfLife.Tests.Unit;

public class LifeSimulatorTests
{
    private readonly LifeSimulator _simulator = new(new LifeEngine());

    [Fact]
    public async Task StillLifeIsAlreadyFinal()
    {
        var block = Patterns.Parse(Patterns.Block);

        var final = (await RunAsync(block)).Final!;

        Assert.Equal((block.Id, 0, 1), (final.Board.Id, final.Steps, final.Period));
    }

    [Fact]
    public async Task DyingBoardEndsEmpty()
    {
        var final = (await RunAsync(Patterns.Parse("...", ".#.", "..."))).Final!;

        Assert.Equal(["...", "...", "..."], Patterns.ToText(final.Board));
        Assert.Equal((1, 1), (final.Steps, final.Period));
    }

    [Fact]
    public async Task BothBlinkerPhasesReportTheSameFinalBoard()
    {
        var horizontal = Patterns.Parse(Patterns.BlinkerHorizontal);
        var vertical = Patterns.Parse(Patterns.BlinkerVertical);

        var fromHorizontal = (await RunAsync(horizontal)).Final!;
        var fromVertical = (await RunAsync(vertical)).Final!;

        Assert.Equal(2, fromHorizontal.Period);
        Assert.Equal(fromHorizontal.Board.Id, fromVertical.Board.Id);
        string canonical = string.CompareOrdinal(horizontal.Id, vertical.Id) < 0 ? horizontal.Id : vertical.Id;
        Assert.Equal(canonical, fromHorizontal.Board.Id);
        // One phase is the canonical board itself, the other is one step away from it.
        Assert.Equal([0, 1], new[] { fromHorizontal.Steps, fromVertical.Steps }.Order());
    }

    [Fact]
    public async Task GliderSettlesIntoABlockAtTheEdge()
    {
        // On a dead-edged board a glider crashes into the corner and becomes a still life.
        var final = (await RunAsync(Patterns.Parse(Patterns.GliderOn(8)))).Final!;

        Assert.Equal(1, final.Period);
        Assert.Equal(4, Patterns.ToText(final.Board).Sum(row => row.Count(c => c == '#')));
    }

    public static TheoryData<string[]> PatternsWithAPath => new()
    {
        Patterns.GliderOn(8),
        Patterns.BlinkerHorizontal,
        // A T shape grows for several generations before settling into four blinkers.
        Patterns.TOn(13),
    };

    [Theory]
    [MemberData(nameof(PatternsWithAPath))]
    public async Task EveryBoardOnThePathGetsTheFinalStateItWouldHaveOnItsOwn(string[] rows)
    {
        var start = Patterns.Parse(rows);

        var result = await RunAsync(start);
        var alongPath = result.FinalStepsAlongPath().ToList();

        Assert.Equal(result.Path.Count, alongPath.Count);
        for (int generation = 0; generation < alongPath.Count; generation++)
        {
            var board = _simulator.Advance(start, generation, CancellationToken.None);
            var own = (await RunAsync(board)).Final!;
            Assert.Equal(board.Id, alongPath[generation].BoardId);
            Assert.Equal(own.Steps, alongPath[generation].Steps);
            Assert.Equal(result.Final!.Board.Id, own.Board.Id);
        }
    }

    [Fact]
    public async Task PathIncludesEveryMemberOfTheCycle()
    {
        var horizontal = Patterns.Parse(Patterns.BlinkerHorizontal);
        var vertical = Patterns.Parse(Patterns.BlinkerVertical);

        var result = await RunAsync(horizontal);

        Assert.Equal([horizontal.Id, vertical.Id], result.Path);
    }

    [Fact]
    public async Task StopsAtTheFirstBoardWhoseFinalStateIsKnown()
    {
        var start = Patterns.Parse(Patterns.GliderOn(12));
        var full = (await RunAsync(start)).Final!;
        var known = KnownAt(start, generation: 5, full);
        var engine = new CountingEngine();

        var result = await new LifeSimulator(engine).RunAsync(start, target: null, maxSteps: 1000, known.Lookup, CancellationToken.None);

        Assert.Equal(5, engine.Steps);
        Assert.Equal((full.Board.Id, full.Steps, full.Period), (result!.Final!.Board.Id, result.Final.Steps, result.Final.Period));
        Assert.Equal(5, result.Path.Count);
        Assert.Equal(Enumerable.Range(0, 5).Select(g => full.Steps - g), result.FinalStepsAlongPath().Select(p => p.Steps));
    }

    [Fact]
    public async Task AKnownFinalStateLetsARunFinishBeyondTheStepLimit()
    {
        var start = Patterns.Parse(Patterns.GliderOn(12));
        var full = (await RunAsync(start)).Final!;
        var known = KnownAt(start, generation: 5, full);

        var result = await _simulator.RunAsync(start, target: null, maxSteps: 6, known.Lookup, CancellationToken.None);

        Assert.True(full.Steps > 6);
        Assert.Equal(full.Steps, result!.Final!.Steps);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(8, true)]
    [InlineData(1_000_000, true)]
    public async Task TargetIsFoundBeforeOrAfterAKnownFinalState(long target, bool finalFound)
    {
        var start = Patterns.Parse(Patterns.GliderOn(12));
        var full = (await RunAsync(start)).Final!;
        var known = KnownAt(start, generation: 5, full);
        var expected = target >= full.Steps ? full.Board : _simulator.Advance(start, (int)target, CancellationToken.None);

        var result = await _simulator.RunAsync(start, target, maxSteps: 1000, known.Lookup, CancellationToken.None);

        Assert.Equal(expected.Id, result!.BoardAtTarget!.Id);
        Assert.Equal(finalFound, result.Final is not null);
    }

    [Fact]
    public async Task ReturnsBoardAtTargetBeforeAnyCycle()
    {
        var glider = Patterns.Parse(Patterns.GliderOn(20));

        var result = await _simulator.RunAsync(glider, target: 4, maxSteps: 100, knownFinal: null, CancellationToken.None);

        Assert.Null(result!.Final);
        Assert.Equal(_simulator.Advance(glider, 4, CancellationToken.None).Id, result.BoardAtTarget!.Id);
    }

    [Theory]
    [InlineData(1_000_000_000L, false)]
    [InlineData(1_000_000_001L, true)]
    public async Task FarFutureOfACycleIsFoundWithoutSteppingThatFar(long target, bool expectVertical)
    {
        var result = await _simulator.RunAsync(
            Patterns.Parse(Patterns.BlinkerHorizontal), target, maxSteps: 10, knownFinal: null, CancellationToken.None);

        Assert.Equal(expectVertical ? Patterns.BlinkerVertical : Patterns.BlinkerHorizontal, Patterns.ToText(result!.BoardAtTarget!));
    }

    [Fact]
    public async Task ReturnsNullWhenStepLimitIsReached()
    {
        Assert.Null(await _simulator.RunAsync(
            Patterns.Parse(Patterns.GliderOn(20)), target: null, maxSteps: 5, knownFinal: null, CancellationToken.None));
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _simulator.RunAsync(Patterns.Parse(Patterns.GliderOn(20)), target: null, maxSteps: 100, knownFinal: null, cancelled.Token));
    }

    private Task<SimulationResult?> RunAsyncNullable(Board board) =>
        _simulator.RunAsync(board, target: null, maxSteps: 1000, knownFinal: null, CancellationToken.None);

    private async Task<SimulationResult> RunAsync(Board board) => (await RunAsyncNullable(board))!;

    /// <summary>Pretends the final state of the board at <paramref name="generation"/> was saved by an earlier request.</summary>
    private KnownFinals KnownAt(Board start, int generation, FinalState full) =>
        new(_simulator.Advance(start, generation, CancellationToken.None).Id, full with { Steps = full.Steps - generation });

    private sealed class KnownFinals(string boardId, FinalState final)
    {
        public ValueTask<FinalState?> Lookup(string id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(id == boardId ? final : null);
    }

    private sealed class CountingEngine : ILifeEngine
    {
        private readonly LifeEngine _inner = new();

        public int Steps { get; private set; }

        public Board Step(Board board)
        {
            Steps++;
            return _inner.Step(board);
        }
    }
}
