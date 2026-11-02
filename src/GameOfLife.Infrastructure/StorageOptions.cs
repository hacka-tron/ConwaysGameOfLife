using System.ComponentModel.DataAnnotations;
using GameOfLife.Core.Storage;

namespace GameOfLife.Infrastructure;

/// <summary>Bound from the "Storage" configuration section.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Folder for the LMDB database files. A relative path is resolved against the app's content root.</summary>
    [Required]
    public string Path { get; set; } = "data/boards";

    /// <summary>
    /// Maximum size of the database. LMDB reserves this much address space up front,
    /// but the file only uses disk space for data actually written.
    /// </summary>
    [Range(1024 * 1024, long.MaxValue)]
    public long MaxSizeBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Boards whose packed cells fit in this many bytes are stored in full. Larger boards are stored as
    /// recipes between key boards. The default of 1,024 bytes covers boards of up to 8,192 cells.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int FullBoardMaxBytes { get; set; } = BoardStoragePolicy.Default.FullBoardMaxBytes;

    /// <summary>
    /// For boards stored as recipes, the most generations between boards stored in full. A recipe
    /// replays at most one less than this, about 15 ms at 256 x 256 with the default of 64.
    /// </summary>
    [Range(1, 4096)]
    public int KeyBoardInterval { get; set; } = BoardStoragePolicy.Default.KeyBoardInterval;

    /// <summary>Approximate memory the in-process board cache may use, in bytes.</summary>
    [Range(1, long.MaxValue)]
    public long CacheSizeLimitBytes { get; set; } = 64 * 1024 * 1024;
}
