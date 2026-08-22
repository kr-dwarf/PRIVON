namespace Privon.Storage;

/// <summary>
/// Writes a file by first writing to a temporary file in the same directory (so the final
/// move is on the same volume), then atomically replacing the destination. If anything
/// fails before the replace step, the destination file is left completely untouched.
/// </summary>
public static class AtomicFileWriter
{
    public static void WriteAllBytes(string path, byte[] data) => WriteAtomic(path, stream => stream.Write(data, 0, data.Length));

    /// <summary>
    /// Lower-level form that lets callers write directly to the temp file's stream. If
    /// <paramref name="writeAction"/> throws, the temp file is deleted and the destination
    /// is never touched.
    /// </summary>
    public static void WriteAtomic(string path, Action<Stream> writeAction)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("Path must have a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeAction(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup only */ }
    }
}
