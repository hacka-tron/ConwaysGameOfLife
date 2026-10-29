using GameOfLife.Core;

namespace GameOfLife.Tests.Unit;

public class LifeEngineTests
{
    private readonly LifeEngine _engine = new();

    [Fact]
    public void LoneCellDies()
    {
        var next = _engine.Step(Patterns.Parse("...", ".#.", "..."));

        Assert.Equal(["...", "...", "..."], Patterns.ToText(next));
    }

    [Fact]
    public void BlockStaysTheSame()
    {
        var block = Patterns.Parse(Patterns.Block);

        Assert.Equal(block.Id, _engine.Step(block).Id);
    }

    [Fact]
    public void BlinkerFlipsBetweenTwoPhases()
    {
        var next = _engine.Step(Patterns.Parse(Patterns.BlinkerHorizontal));

        Assert.Equal(Patterns.BlinkerVertical, Patterns.ToText(next));
        Assert.Equal(Patterns.BlinkerHorizontal, Patterns.ToText(_engine.Step(next)));
    }

    [Fact]
    public void GliderMovesOneCellDiagonallyEveryFourGenerations()
    {
        var board = Patterns.Parse(Patterns.GliderOn(8));
        for (int i = 0; i < 4; i++)
        {
            board = _engine.Step(board);
        }

        string[] expected =
        [
            "........",
            "..#.....",
            "...#....",
            ".###....",
            "........",
            "........",
            "........",
            "........",
        ];
        Assert.Equal(expected, Patterns.ToText(board));
    }

    [Fact]
    public void DeadEdgesCountAsDeadNeighbours()
    {
        // A vertical line on the left edge. Only the middle cell has two live neighbours,
        // and the cell to its right has three. Nothing is born beyond the edge.
        var next = _engine.Step(Patterns.Parse("#..", "#..", "#.."));

        Assert.Equal(["...", "##.", "..."], Patterns.ToText(next));
    }

    [Fact]
    public void WrappedEdgesConnectOppositeSides()
    {
        // The same line, but the left edge now touches the right edge.
        var next = _engine.Step(Patterns.Parse(EdgeMode.Wrap, "#....", "#....", "#....", ".....", "....."));

        Assert.Equal([".....", "##..#", ".....", ".....", "....."], Patterns.ToText(next));
    }

    [Fact]
    public void GliderReturnsHomeAfterCrossingAWrappedBoard()
    {
        // A glider moves one cell diagonally every 4 generations, so it crosses a 6x6 torus in 24.
        var start = Patterns.Parse(EdgeMode.Wrap, Patterns.GliderOn(6));
        var board = start;
        for (int i = 0; i < 24; i++)
        {
            board = _engine.Step(board);
        }

        Assert.Equal(start.Id, board.Id);
    }

    [Fact]
    public void KeepsBoardSizeAndEdgeMode()
    {
        var next = _engine.Step(Patterns.Parse(EdgeMode.Wrap, "###", "...", "...", "..."));

        Assert.Equal((3, 4, EdgeMode.Wrap), (next.Width, next.Height, next.EdgeMode));
    }
}
