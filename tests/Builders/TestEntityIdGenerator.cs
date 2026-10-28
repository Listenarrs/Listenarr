namespace Listenarr.Tests.Builders;

internal static class TestEntityIdGenerator
{
    internal const int GeneratedIdFloor = 1_000_000_000;

    private static int _nextId = GeneratedIdFloor;

    internal static int Next()
    {
        var id = Interlocked.Increment(ref _nextId);
        if (id <= GeneratedIdFloor)
        {
            throw new InvalidOperationException(
                "The reserved test-entity ID namespace has been exhausted.");
        }

        return id;
    }

    internal static int Explicit(int id)
    {
        if (id >= GeneratedIdFloor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id),
                id,
                $"Explicit fixture IDs must be less than {GeneratedIdFloor}; "
                + "higher values are reserved for generated builder identities.");
        }

        return id;
    }
}
