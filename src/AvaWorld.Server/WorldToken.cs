using System.Security.Cryptography;

namespace AvaWorld.Server;

/// <summary>
/// The shared secret a client must present to enter the world.
///
/// This was not needed when the world lived on the same machine as everything else. It is now: the
/// server is a long-running service on its own box, and anything that can reach the port can
/// otherwise walk in, see where Ava is, and — once step four lands — tell her where to go.
///
/// Same shape as the companion's <c>ApiOptions</c> token: read from the environment if set,
/// otherwise generated once and kept beside the world file, so a first run needs no setup and a
/// second run is still protected.
/// </summary>
public static class WorldToken
{
    public const string EnvironmentVariable = "AVAWORLD_TOKEN";

    private const string FileName = ".avaworld-token";

    /// <summary>
    /// The token for this world, creating one if this is the first run. <paramref name="beside"/>
    /// is the world file, so the token lives with the world it protects.
    /// </summary>
    public static string ResolveOrCreate(string beside)
    {
        if (FromEnvironment() is { } configured)
            return configured;

        var path = PathBeside(beside);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0)
                return existing;
        }

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, generated);
        return generated;
    }

    /// <summary>
    /// The token a client should present: the environment first, then the file beside the world —
    /// which is how a client on the same machine works with no configuration at all.
    /// </summary>
    public static string? ResolveForClient(string beside)
    {
        if (FromEnvironment() is { } configured)
            return configured;

        var path = PathBeside(beside);
        if (!File.Exists(path))
            return null;

        var existing = File.ReadAllText(path).Trim();
        return existing.Length > 0 ? existing : null;
    }

    /// <summary>
    /// Constant-time comparison. The timing of a token check is not a realistic attack here, but
    /// writing the careless version is how the careless version ends up somewhere it matters.
    /// </summary>
    public static bool Matches(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(presented));

    public static string PathBeside(string worldFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(worldFile))!, FileName);

    private static string? FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
