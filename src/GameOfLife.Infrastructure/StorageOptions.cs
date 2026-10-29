using System.ComponentModel.DataAnnotations;

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

    /// <summary>Approximate memory the in-process board cache may use, in bytes.</summary>
    [Range(1, long.MaxValue)]
    public long CacheSizeLimitBytes { get; set; } = 64 * 1024 * 1024;
}
