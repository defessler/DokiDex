namespace App;

public class Inventory
{
    private readonly Dictionary<string, int> _items = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string name, int quantity)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required", nameof(name));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        _items[name] = _items.TryGetValue(name, out var current) ? current + quantity : quantity;
    }

    public void Remove(string name, int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (!_items.TryGetValue(name, out var current) || current < quantity)
            throw new InvalidOperationException($"not enough '{name}' in inventory");
        var remaining = current - quantity;
        if (remaining == 0) _items.Remove(name);
        else _items[name] = remaining;
    }

    public int CountOf(string name) => _items.TryGetValue(name, out var current) ? current : 0;

    public int TotalItems => _items.Values.Sum();
}
