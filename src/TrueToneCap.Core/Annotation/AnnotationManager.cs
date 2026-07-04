// TrueToneCap.Core/Annotation/AnnotationManager.cs
// 标注管理器 — 图层操作、撤销/重做栈

namespace TrueToneCap.Core.Annotation;

/// <summary>标注操作命令（命令模式）。</summary>
public interface IAnnotationCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

/// <summary>添加图层命令。</summary>
internal sealed class AddLayerCommand : IAnnotationCommand
{
    private readonly AnnotationManager _manager;
    private readonly AnnotationLayer _layer;

    public string Description => $"添加{_layer.Name}";
    public AddLayerCommand(AnnotationManager manager, AnnotationLayer layer)
    { _manager = manager; _layer = layer; }

    public void Execute() => _manager.DoAddLayer(_layer);
    public void Undo() => _manager.DoRemoveLayer(_layer.Id);
}

/// <summary>删除图层命令。</summary>
internal sealed class RemoveLayerCommand : IAnnotationCommand
{
    private readonly AnnotationManager _manager;
    private readonly AnnotationLayer _layer;

    public string Description => $"删除{_layer.Name}";
    public RemoveLayerCommand(AnnotationManager manager, AnnotationLayer layer)
    { _manager = manager; _layer = layer; }

    public void Execute() => _manager.DoRemoveLayer(_layer.Id);
    public void Undo() => _manager.DoAddLayer(_layer);
}

/// <summary>修改图层命令。</summary>
internal sealed class ModifyLayerCommand : IAnnotationCommand
{
    private readonly AnnotationManager _manager;
    private readonly Guid _layerId;
    private readonly AnnotationLayer _before;
    private readonly AnnotationLayer _after;

    public string Description => "修改标注";
    public ModifyLayerCommand(AnnotationManager manager, Guid layerId,
        AnnotationLayer before, AnnotationLayer after)
    {
        _manager = manager;
        _layerId = layerId;
        _before = before;
        _after = after;
    }

    public void Execute() => _manager.DoUpdateLayer(_layerId, _after);
    public void Undo() => _manager.DoUpdateLayer(_layerId, _before);
}

/// <summary>标注管理器 — 管理所有图层及撤销/重做栈。</summary>
public sealed class AnnotationManager
{
    private readonly List<AnnotationLayer> _layers = [];
    private readonly Stack<IAnnotationCommand> _undoStack = new();
    private readonly Stack<IAnnotationCommand> _redoStack = new();
    private int _nextZOrder;

    public IReadOnlyList<AnnotationLayer> Layers => _layers;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action? LayersChanged;

    // ────────────── 图层操作 ──────────────

    public void AddLayer(AnnotationLayer layer)
    {
        var cmd = new AddLayerCommand(this, layer);
        ExecuteCommand(cmd);
    }

    public void RemoveLayer(Guid layerId)
    {
        var layer = _layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null) return;
        var cmd = new RemoveLayerCommand(this, layer.Clone());
        ExecuteCommand(cmd);
    }

    public void UpdateLayer(Guid layerId, AnnotationLayer newState)
    {
        var existing = _layers.FirstOrDefault(l => l.Id == layerId);
        if (existing == null) return;
        var before = existing.Clone();
        var cmd = new ModifyLayerCommand(this, layerId, before, newState.Clone());
        ExecuteCommand(cmd);
    }

    public void ClearAll()
    {
        while (_layers.Count > 0)
        {
            RemoveLayer(_layers[0].Id);
        }
    }

    // ────────────── 内部操作（供 Command 调用） ──────────────

    internal void DoAddLayer(AnnotationLayer layer)
    {
        layer.ZOrder = ++_nextZOrder;
        _layers.Add(layer);
        LayersChanged?.Invoke();
    }

    internal void DoRemoveLayer(Guid id)
    {
        _layers.RemoveAll(l => l.Id == id);
        LayersChanged?.Invoke();
    }

    internal void DoUpdateLayer(Guid id, AnnotationLayer state)
    {
        var idx = _layers.FindIndex(l => l.Id == id);
        if (idx >= 0)
        {
            state.ZOrder = _layers[idx].ZOrder;
            _layers[idx] = state;
        }
        LayersChanged?.Invoke();
    }

    // ────────────── 撤销/重做 ──────────────

    private void ExecuteCommand(IAnnotationCommand cmd)
    {
        cmd.Execute();
        _undoStack.Push(cmd);
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var cmd = _undoStack.Pop();
        cmd.Undo();
        _redoStack.Push(cmd);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var cmd = _redoStack.Pop();
        cmd.Execute();
        _undoStack.Push(cmd);
    }

    public AnnotationLayer? GetLayerAt(float x, float y)
    {
        // 从上层到下层查找
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var bounds = _layers[i].GetBounds();
            if (x >= bounds.Left && x <= bounds.Right &&
                y >= bounds.Top && y <= bounds.Bottom)
            {
                return _layers[i];
            }
        }
        return null;
    }
}
