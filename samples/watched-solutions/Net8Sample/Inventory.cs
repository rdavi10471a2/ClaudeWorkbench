namespace Net8Sample;

// Deliberately targets net8.0 while ClaudeWorkbench itself runs on net10.0: the point of this
// fixture is that the WATCHED solution's framework is independent of the app's.
public sealed class Inventory
{
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string sku, int quantity)
    {
        counts[sku] = counts.TryGetValue(sku, out int existing) ? existing + quantity : quantity;
    }

    public int CountOf(string sku) => counts.TryGetValue(sku, out int value) ? value : 0;

    public IReadOnlyCollection<string> Skus => counts.Keys;
}
