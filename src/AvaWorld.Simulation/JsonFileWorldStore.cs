using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaWorld.Simulation;

/// <summary>
/// Saves the world to a JSON file, atomically.
///
/// The world saves on every tick and is killed by closing a window or losing power, so a partial
/// write is not a rare event — it is the expected way this process ends. Writing in place would
/// eventually truncate the file mid-save and lose the world. Instead: write a temp file, flush it
/// to disk, then move it over the target, which is atomic on NTFS. A crash therefore leaves either
/// the previous world or the new one, never half of either.
///
/// JSON rather than a database because this file should be readable by a human at 2am when
/// something has gone wrong with it.
/// </summary>
public sealed class JsonFileWorldStore : IWorldStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public JsonFileWorldStore(string path) => _path = path;

    public string Path => _path;

    public async Task<WorldState?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            // A temp file left behind means we died mid-save; the previous world is gone but the
            // new one made it to disk. Prefer it over starting from nothing.
            var orphan = TempPath;
            if (File.Exists(orphan))
            {
                File.Move(orphan, _path);
            }
            else
            {
                return null;
            }
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<WorldState>(stream, Options, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(WorldState state, CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = TempPath;

        await using (var stream = new FileStream(
            temp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, state, Options, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            // Force the bytes to the platter before the move — otherwise the rename can land while
            // the contents are still in the OS cache, and a power cut leaves an empty file.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private string TempPath => _path + ".saving";
}
