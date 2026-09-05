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

/// <summary>标注管理器 — 管理所有图层及撤销/重做栈。
/// ═══ 2026-08-16(复审 P1-3): 加同步锁 — HdrCaptureWindow 渲染线程遍历
/// Layers 与 UI 线程 AddLayer/Undo/Redo 并发 → List 版本检查异常/图集半重建。
/// 所有修改与读取统一走锁; 渲染线程用 GetLayersSnapshot() 抓快照。</summary>
public sealed class AnnotationManager
{
    private readonly object _lock = new();
    private readonly List<AnnotationLayer> _layers = [];
    private readonly Stack<IAnnotationCommand> _undoStack = new();
    private readonly Stack<IAnnotationCommand> _redoStack = new();
    private int _nextZOrder;

    public IReadOnlyList<AnnotationLayer> Layers { get { lock (_lock) { return _layers.ToArray(); } } }
    public int LayerCount { get { lock (_lock) { return _layers.Count; } } }
    public bool CanUndo { get { lock (_lock) { return _undoStack.Count > 0; } } }
    public bool CanRedo { get { lock (_lock) { return _redoStack.Count > 0; } } }

    /// <summary>获取图层快照 (渲染线程安全遍历)。</summary>
    public AnnotationLayer[] GetLayersSnapshot() { lock (_lock) { return _layers.ToArray(); } }

    public event Action? LayersChanged;

    // ────────────── 图层操作 ──────────────

    public void AddLayer(AnnotationLayer layer)
    {
        lock (_lock)
        {
            var cmd = new AddLayerCommand(this, layer);
            ExecuteCommandLocked(cmd);
        }
    }

    public void RemoveLayer(Guid layerId)
    {
        lock (_lock)
        {
            var layer = _layers.FirstOrDefault(l => l.Id == layerId);
            if (layer == null) return;
            // 直接存储原始引用（已从列表移除，不会被外部修改）
            // 修复: Clone() 会生成新 Guid，导致 DoRemoveLayer 找不到目标
            var cmd = new RemoveLayerCommand(this, layer);
            ExecuteCommandLocked(cmd);
        }
    }

    public void UpdateLayer(Guid layerId, AnnotationLayer newState)
    {
        lock (_lock)
        {
            var existing = _layers.FirstOrDefault(l => l.Id == layerId);
            if (existing == null) return;
            var before = existing.Clone();
            var cmd = new ModifyLayerCommand(this, layerId, before, newState.Clone());
            ExecuteCommandLocked(cmd);
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            while (_layers.Count > 0)
            {
                RemoveLayerLocked(_layers[0].Id);
            }
        }
    }

    // ────────────── 内部操作（供 Command 调用） ──────────────

    internal void DoAddLayer(AnnotationLayer layer)
    {
        lock (_lock)
        {
            DoAddLayerLocked(layer);
        }
    }

    internal void DoRemoveLayer(Guid id)
    {
        lock (_lock)
        {
            DoRemoveLayerLocked(id);
        }
    }

    internal void DoUpdateLayer(Guid id, AnnotationLayer state)
    {
        lock (_lock)
        {
            DoUpdateLayerLocked(id, state);
        }
    }

    // ────────────── 撤销/重做 ──────────────

    private void ExecuteCommand(IAnnotationCommand cmd)
    {
        lock (_lock) { ExecuteCommandLocked(cmd); }
    }

    /// <summary>调用方必须已持有 _lock。</summary>
    private void ExecuteCommandLocked(IAnnotationCommand cmd)
    {
        cmd.Execute();
        _undoStack.Push(cmd);
        _redoStack.Clear();
    }

    /// <summary>调用方必须已持有 _lock。</summary>
    private void RemoveLayerLocked(Guid layerId)
    {
        var layer = _layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null) return;
        var cmd = new RemoveLayerCommand(this, layer);
        ExecuteCommandLocked(cmd);
    }

    public void Undo()
    {
        lock (_lock)
        {
            if (!CanUndoLocked) return;
            var cmd = _undoStack.Pop();
            cmd.Undo();
            _redoStack.Push(cmd);
        }
    }

    public void Redo()
    {
        lock (_lock)
        {
            if (!CanRedoLocked) return;
            var cmd = _redoStack.Pop();
            cmd.Execute();
            _undoStack.Push(cmd);
        }
    }

    private bool CanUndoLocked => _undoStack.Count > 0;
    private bool CanRedoLocked => _redoStack.Count > 0;

    public AnnotationLayer? GetLayerAt(float x, float y)
    {
        lock (_lock)
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

    // ────────────── 锁内原始操作（Command 与锁内调用共用）──────────────

    private void DoAddLayerLocked(AnnotationLayer layer)
    {
        layer.ZOrder = ++_nextZOrder;
        _layers.Add(layer);
        LayersChanged?.Invoke();
    }

    private void DoRemoveLayerLocked(Guid id)
    {
        _layers.RemoveAll(l => l.Id == id);
        LayersChanged?.Invoke();
    }

    private void DoUpdateLayerLocked(Guid id, AnnotationLayer state)
    {
        var idx = _layers.FindIndex(l => l.Id == id);
        if (idx >= 0)
        {
            state.ZOrder = _layers[idx].ZOrder;
            _layers[idx] = state;
        }
        LayersChanged?.Invoke();
    }
}
