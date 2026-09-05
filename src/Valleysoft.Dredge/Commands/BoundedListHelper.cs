namespace Valleysoft.Dredge.Commands;

internal static class BoundedListHelper
{
    public static void AddItems<T>(List<T> items, IEnumerable<T> pageItems, int? limit)
    {
        int remaining = limit is null ? int.MaxValue : Math.Max(0, limit.Value - items.Count);
        items.AddRange(pageItems.Take(remaining));
    }

    public static bool IsLimitReached<T>(List<T> items, int? limit) =>
        limit is not null && items.Count >= limit.Value;
}
