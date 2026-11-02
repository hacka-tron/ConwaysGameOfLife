using GameOfLife.Core;

namespace GameOfLife.Tests.Unit;

public class BoardTests
{
    [Theory]
    [InlineData(new[] { "...", "...", "..." }, true)]
    [InlineData(new[] { "...", "...", "..#" }, false)]
    [InlineData(new[] { "#.." }, false)]
    public void IsEmptyOnlyWhenNoCellIsAlive(string[] rows, bool expected)
    {
        Assert.Equal(expected, Patterns.Parse(rows).IsEmpty);
    }

    [Theory]
    [InlineData(new[] { "...", "...", "..." }, FinalKind.Extinct)]
    [InlineData(new[] { "....", ".##.", ".##.", "...." }, FinalKind.StillLife)]
    public void FinalKindTellsExtinctionFromAStillLife(string[] rows, FinalKind expected)
    {
        Assert.Equal(expected, new FinalState(Patterns.Parse(rows), Steps: 0, Period: 1).Kind);
    }

    [Fact]
    public void SameCellsHaveSameId()
    {
        Assert.Equal(Patterns.Parse(Patterns.Block).Id, Patterns.Parse(Patterns.Block).Id);
    }

    [Fact]
    public void DifferentCellsHaveDifferentIds()
    {
        Assert.NotEqual(Patterns.Parse(Patterns.BlinkerHorizontal).Id, Patterns.Parse(Patterns.BlinkerVertical).Id);
    }

    [Fact]
    public void SameLiveCellOnDifferentSizedBoardsHasDifferentIds()
    {
        var small = Patterns.Parse("...", ".#.", "...");
        var large = Patterns.Parse("....", ".#..", "....", "....");

        Assert.NotEqual(small.Id, large.Id);
    }

    [Fact]
    public void EdgeModeIsPartOfTheId()
    {
        Assert.NotEqual(
            Patterns.Parse(EdgeMode.Dead, Patterns.Block).Id,
            Patterns.Parse(EdgeMode.Wrap, Patterns.Block).Id);
    }

    [Fact]
    public void IdIsStableAcrossReleases()
    {
        // Stored IDs must keep resolving. If this fails, the ID format changed and needs a new version prefix.
        Assert.Equal(
            "05an0qnggbt15b316pw8tn6kw3",
            Patterns.Parse("#.", ".#").Id);
    }

    [Fact]
    public void RoundTripsThroughPackedForm()
    {
        var board = Patterns.Parse(Patterns.GliderOn(7));
        var copy = Board.FromPacked(board.Width, board.Height, board.EdgeMode, board.PackedCells);

        Assert.Equal(board.Id, copy.Id);
        Assert.True(board.ContentEquals(copy));
    }

    [Fact]
    public void FromPackedRejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => Board.FromPacked(3, 3, EdgeMode.Dead, new byte[1]));
    }

    [Fact]
    public void FromPackedRejectsBitsBeyondTheLastCell()
    {
        // A 3x3 board uses 9 bits, so bit 9 of the second byte must be zero.
        Assert.Throws<ArgumentException>(() => Board.FromPacked(3, 3, EdgeMode.Dead, [0, 0b10]));
    }

    [Fact]
    public void RejectsEmptyGrid()
    {
        Assert.Throws<ArgumentException>(() => Board.FromCells(new bool[0, 3], EdgeMode.Dead));
    }
}
