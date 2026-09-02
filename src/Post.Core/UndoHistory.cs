namespace Post.Core;
public sealed class UndoHistory<T>(int capacity = 30)
{
    private readonly List<T> _states = [];
    private int _index = -1;
    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index < _states.Count - 1;
    public int Count => _states.Count;
    public void Push(T state)
    {
        if (_index < _states.Count - 1) _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        _states.Add(state); if (_states.Count > capacity) _states.RemoveAt(0); _index = _states.Count - 1;
    }
    public T? Undo() => CanUndo ? _states[--_index] : default;
    public T? Redo() => CanRedo ? _states[++_index] : default;
    public void Clear() { _states.Clear(); _index = -1; }
}
