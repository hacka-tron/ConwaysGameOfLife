namespace GameOfLife.Core;

/// <summary>Computes one generation of a board.</summary>
public interface ILifeEngine
{
    Board Step(Board board);
}

/// <summary>
/// Conway's rule (B3/S23). A live cell with 2 or 3 live neighbours survives, a dead cell with
/// exactly 3 comes alive, and every other cell dies or stays dead.
/// </summary>
public sealed class LifeEngine : ILifeEngine
{
    public Board Step(Board board)
    {
        ArgumentNullException.ThrowIfNull(board);
        int width = board.Width;
        int height = board.Height;
        int stride = width + 2;

        // A one-cell border lets every cell read its 8 neighbours without bounds checks.
        // Dead edges leave the border empty. Wrapped edges copy the opposite side into it.
        var grid = new byte[stride * (height + 2)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (board[x, y])
                {
                    grid[(y + 1) * stride + x + 1] = 1;
                }
            }
        }

        if (board.EdgeMode == EdgeMode.Wrap)
        {
            FillWrappedBorder(grid, width, height);
        }

        var next = new byte[Board.PackedLength(width, height)];
        for (int y = 1; y <= height; y++)
        {
            for (int x = 1; x <= width; x++)
            {
                int i = y * stride + x;
                int neighbours =
                    grid[i - stride - 1] + grid[i - stride] + grid[i - stride + 1]
                    + grid[i - 1] + grid[i + 1]
                    + grid[i + stride - 1] + grid[i + stride] + grid[i + stride + 1];

                if (neighbours == 3 || (neighbours == 2 && grid[i] == 1))
                {
                    Board.SetBit(next, (y - 1) * width + (x - 1));
                }
            }
        }

        return Board.FromTrustedPacked(width, height, board.EdgeMode, next);
    }

    private static void FillWrappedBorder(byte[] grid, int width, int height)
    {
        int stride = width + 2;
        for (int y = 1; y <= height; y++)
        {
            grid[y * stride] = grid[y * stride + width];
            grid[y * stride + width + 1] = grid[y * stride + 1];
        }

        // Rows are copied after the side columns so the corners wrap as well.
        Array.Copy(grid, height * stride, grid, 0, stride);
        Array.Copy(grid, stride, grid, (height + 1) * stride, stride);
    }
}
