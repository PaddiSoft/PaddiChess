namespace PaddiXiangqi.ViewModels;

/// <summary>Model-directory state; filtering preserves checkbox rows and selections.</summary>
public sealed class ModelCatalogViewModel : ObservableObject
{
    private ModelSelectionViewModel[] _models = [];
    private IReadOnlyList<ModelSelectionViewModel> _items = Array.Empty<ModelSelectionViewModel>();
    private string _filter = "";
    private int _enabledCount;

    public IReadOnlyList<ModelSelectionViewModel> Items => _items;
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value ?? "")) FilterItems(); }
    }
    public bool IsEmpty => _items.Count == 0;
    public string EmptyText => _models.Length == 0 ? "尚无模型；请获取列表或手动添加。" : "没有匹配的模型。";
    public string Summary => $"共 {_models.Length} 个模型 · 已启用 {_enabledCount} 个" +
        (_filter.Trim().Length > 0 ? $" · 匹配 {_items.Count} 个" : "");

    public void Replace(IEnumerable<string> models, IEnumerable<string> enabled,
        Action<string, bool> selectionChanged)
    {
        var selected = enabled.ToHashSet(StringComparer.Ordinal);
        _models = models.Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.Ordinal)
            .Select(model => new ModelSelectionViewModel(model, selected.Contains(model), (name, value) =>
            {
                _enabledCount += value ? 1 : -1;
                Changed(nameof(Summary));
                selectionChanged(name, value);
            })).ToArray();
        _enabledCount = _models.Count(model => model.Enabled);
        FilterItems();
    }

    private void FilterItems()
    {
        var filter = _filter.Trim();
        _items = filter.Length == 0 ? _models :
            _models.Where(model => model.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        Changed(nameof(Items));
        Changed(nameof(IsEmpty));
        Changed(nameof(EmptyText));
        Changed(nameof(Summary));
    }
}
