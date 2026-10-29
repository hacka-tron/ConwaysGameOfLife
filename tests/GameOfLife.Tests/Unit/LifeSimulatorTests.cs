using GameOfLife.Core;

namespace GameOfLife.Tests.Unit;

public class LifeSimulatorTests
{
    private readonly LifeSimulator _simulator = new(new LifeEngine());

    [Fact]
    public void StillLifeIsAlreadyFinal()
    {
        var block = Patterns.Parse(Patterns.Block);

        var final = Run(block).Final!;

        Assert.Equal((block.Id, 0, 1), (final.Board.Id, final.Steps, final.Period));
    }

    [Fact]
    public void DyingBoardEndsEmpty()
    {
        var final = Run(Patterns.Parse("...", ".#.", "...")).Final!;

        Assert.Equal(["...", "...", "..."], Patterns.ToText(final.Board));
        Assert.Equal((1, 1), (final.Steps, final.Period));
    }

    [Fact]
    public void BothBlinkerPhasesReportTheSameFinalBoard()
    {
        var horizontal = Patterns.Parse(Patterns.BlinkerHorizontal);
        var vertical = Patterns.Parse(Patterns.BlinkerVertical);

        var fromHorizontal = Run(horizontal).Final!;
        var fromVertical = Run(vertical).Final!;

        Assert.Equal(2, fromHorizontal.Period);
        Assert.Equal(fromHorizontal.Board.Id, fromVertical.Board.Id);
        string canonical = string.CompareOrdinal(horizontal.Id, vertical.Id) < 0 ? horizontal.Id : vertical.Id;
        Assert.Equal(canonical, fromHorizontal.Board.Id);
        // One phase is the canonical board itself, the other is one step away from it.
        Assert.Equal([0, 1], new[] { fromHorizontal.Steps, fromVertical.Steps }.Order());
    }

    [Fact]
    public void GliderSettlesIntoABlockAtTheEdge()
    {
        // On a dead-edged board a glider crashes into the corner and becomes a still life.
        var final = Run(Patterns.Parse(Patterns.GliderOn(8))).Final!;

        Assert.Equal(1, final.Period);
        Assert.Equal(4, Patterns.ToText(final.Board).Sum(row => row.Count(c => c == '#')));
    }

    [Fact]
    public void ReturnsBoardAtTargetBeforeAnyCycle()
    {
        var glider = Patterns.Parse(Patterns.GliderOn(20));

        var result = _simulator.Run(glider, target: 4, maxSteps: 100, CancellationToken.None)!;

        Assert.Null(result.Final);
        Assert.Equal(_simulator.Advance(glider, 4, CancellationToken.None).Id, result.BoardAtTarget!.Id);
    }

    [Theory]
    [InlineData(1_000_000_000L, false)]
    [InlineData(1_000_000_001L, true)]
    public void FarFutureOfACycleIsFoundWithoutSteppingThatFar(long target, bool expectVertical)
    {
        var result = _simulator.Run(Patterns.Parse(Patterns.BlinkerHorizontal), target, maxSteps: 10, CancellationToken.None)!;

        Assert.Equal(expectVertical ? Patterns.BlinkerVertical : Patterns.BlinkerHorizontal, Patterns.ToText(result.BoardAtTarget!));
    }

    [Fact]
    public void ReturnsNullWhenStepLimitIsReached()
    {
        Assert.Null(_simulator.Run(Patterns.Parse(Patterns.GliderOn(20)), target: null, maxSteps: 5, CancellationToken.None));
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _simulator.Run(Patterns.Parse(Patterns.GliderOn(20)), target: null, maxSteps: 100, cancelled.Token));
    }

    private SimulationResult Run(Board board) => _simulator.Run(board, target: null, maxSteps: 1000, CancellationToken.None)!;
}
