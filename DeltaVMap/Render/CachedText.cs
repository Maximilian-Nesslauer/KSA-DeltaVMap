namespace DeltaVMap.Render;

// A display string kept until what it shows changes, so a panel that draws every frame builds
// its text only when a figure moves. owner is compared by reference and key by value; a
// caller builds the text only on the stale path:
//   string s = cache.IsStale(owner, key) ? cache.Set(owner, key, Build()) : cache.Text;
internal sealed class CachedText
{
    private object? _owner;
    private long _key;
    private bool _valid;

    public string Text { get; private set; } = "";

    public bool IsStale(object? owner, long key)
    {
        return !_valid || key != _key || !ReferenceEquals(owner, _owner);
    }

    public string Set(object? owner, long key, string text)
    {
        _owner = owner;
        _key = key;
        _valid = true;
        Text = text;
        return text;
    }

    public void Clear()
    {
        _valid = false;
        _owner = null;
        Text = "";
    }
}
