namespace Glossa.Core.Lookup;

/// <summary>
/// Finished cards by key (word and line), the most recently used kept: a re-read dialogue or a menu opens without
/// the AI (Настройки → Нагрузка на ПК → «Помнить готовые карточки»). Used from the UI thread only.
/// </summary>
public sealed class CardCache(int capacity)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, WordCard Card)>> _map = [];
    private readonly LinkedList<(string Key, WordCard Card)> _order = new();

    public int Count => _map.Count;

    public WordCard? Get(string key)
    {
        if (!_map.TryGetValue(key, out var node)) return null;
        _order.Remove(node);
        _order.AddFirst(node);
        return node.Value.Card;
    }

    public void Put(string key, WordCard card)
    {
        if (_map.TryGetValue(key, out var old)) _order.Remove(old);
        _map[key] = _order.AddFirst((key, card));
        while (_map.Count > capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }
}
