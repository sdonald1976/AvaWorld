namespace AvaWorld.Simulation;

/// <summary>Where the world persists itself between runs.</summary>
public interface IWorldStore
{
    /// <summary>The saved world, or null if this world has never run before.</summary>
    Task<WorldState?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(WorldState state, CancellationToken ct = default);
}
