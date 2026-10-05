using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private sealed record LlmModelChoice(string ProfileId, string ProfileName, string ModelId)
    {
        public override string ToString() => $"{ModelId} · {ProfileName}";
    }

    private readonly Dictionary<string, string> _llmProfileKeys = new(StringComparer.Ordinal);
    private LlmServiceProfile? _editingLlmProfile;
    private CancellationTokenSource? _modelFetchCancellation;
    private bool _loadingLlmProfile;
    private long _modelFetchRevision;
    private readonly ModelCatalogViewModel _modelCatalog = new();
    private bool _updatingLlmChoices;

    private void InitializeLlmProfileControls()
    {
        ModelServicesPage.DataContext = _modelCatalog;
        if (_preferences.LlmServices.Count == 0)
            _preferences.LlmServices.Add(new LlmServiceProfile { Name = "默认服务", BaseUrl = _preferences.LlmBaseUrl });

        LlmProfileList.SelectionChanged += (_, _) =>
        {
            if (_loadingLlmProfile || LlmProfileList.SelectedItem is not LlmServiceProfile profile) return;
            SaveEditingLlmProfile();
            ShowLlmProfile(profile);
        };
        LlmProfileNameBox.LostFocus += (_, _) =>
        {
            if (_loadingLlmProfile) return;
            SaveEditingLlmProfile();
            RefreshLlmProfilePicker(_editingLlmProfile);
            SaveSettings();
        };
        LlmProfileBaseUrlBox.LostFocus += async (_, _) =>
        {
            if (_loadingLlmProfile) return;
            SaveEditingLlmProfile();
            SaveSettings();
            await AutoFetchLlmModelsAsync();
        };
        LlmProfileApiKeyBox.LostFocus += async (_, _) =>
        {
            if (_loadingLlmProfile) return;
            SaveEditingLlmProfile();
            await AutoFetchLlmModelsAsync();
        };
        RefreshLlmProfilePicker(_preferences.LlmServices.FirstOrDefault(profile =>
            profile.Id == _preferences.RedLlmServiceId) ?? _preferences.LlmServices[0]);
        RefreshLlmModelChoices();
    }

    private void RefreshLlmProfilePicker(LlmServiceProfile? selected)
    {
        _loadingLlmProfile = true;
        try
        {
            LlmProfileList.ItemsSource = _preferences.LlmServices.ToArray();
            LlmProfileList.SelectedItem = selected;
        }
        finally { _loadingLlmProfile = false; }
        if (selected is not null) ShowLlmProfile(selected);
    }

    private void ShowLlmProfile(LlmServiceProfile profile)
    {
        if (!ReferenceEquals(_editingLlmProfile, profile)) CancelModelDirectoryRequest();
        _loadingLlmProfile = true;
        try
        {
            _editingLlmProfile = profile;
            LlmProfileNameBox.Text = profile.Name;
            LlmProfileBaseUrlBox.Text = profile.BaseUrl;
            LlmProfileApiKeyBox.Text = _llmProfileKeys.GetValueOrDefault(profile.Id, "");
            LlmModelFilterBox.Text = "";
            LlmProfileStatusText.Text = profile.Models.Count == 0
                ? "填入地址和 Key 后会自动尝试获取模型。"
                : $"已保存 {profile.Models.Count} 个模型 ID；可刷新列表。";
        }
        finally { _loadingLlmProfile = false; }
        RenderLlmProfileModels();
    }

    private void SaveEditingLlmProfile()
    {
        if (_loadingLlmProfile || _editingLlmProfile is not { } profile) return;
        var oldUrl = profile.BaseUrl.TrimEnd('/');
        var oldKey = _llmProfileKeys.GetValueOrDefault(profile.Id, "");
        profile.Name = string.IsNullOrWhiteSpace(LlmProfileNameBox.Text)
            ? "未命名服务" : LlmProfileNameBox.Text.Trim();
        profile.BaseUrl = LlmProfileBaseUrlBox.Text?.Trim() ?? "";
        _llmProfileKeys[profile.Id] = LlmProfileApiKeyBox.Text ?? "";
        var addressChanged = !string.Equals(oldUrl, profile.BaseUrl.TrimEnd('/'), StringComparison.Ordinal);
        if (addressChanged || oldKey != _llmProfileKeys[profile.Id]) CancelModelDirectoryRequest();
        if (addressChanged)
        {
            profile.Models.Clear();
            profile.EnabledModels.Clear();
            RefreshLlmModelChoices();
            RenderLlmProfileModels();
        }
    }

    private async Task AutoFetchLlmModelsAsync()
    {
        if (_editingLlmProfile is not { } profile || profile.Models.Count > 0) return;
        if (string.IsNullOrWhiteSpace(profile.BaseUrl) ||
            string.IsNullOrWhiteSpace(_llmProfileKeys.GetValueOrDefault(profile.Id))) return;
        await FetchLlmProfileModelsAsync();
    }

    private void LlmProfileAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveEditingLlmProfile();
        var profile = new LlmServiceProfile
        {
            Name = $"API 服务 {_preferences.LlmServices.Count + 1}"
        };
        _preferences.LlmServices.Add(profile);
        RefreshLlmProfilePicker(profile);
        SaveSettings();
    }

    private void LlmProfileDelete_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_editingLlmProfile is not { } profile) return;
        if ((_redLlm && SelectedLlmChoice(true)?.ProfileId == profile.Id) ||
            (_blackLlm && SelectedLlmChoice(false)?.ProfileId == profile.Id) ||
            ((_externalObserving || _externalRunning) && ExternalControllerBox.SelectedIndex == 1 &&
             SelectedLlmChoice(ExternalSideBox.SelectedIndex == 0)?.ProfileId == profile.Id))
        {
            LlmProfileStatusText.Text = "此服务正在执棋；请先关闭对应方的大模型接管。";
            return;
        }
        CancelModelDirectoryRequest();
        _preferences.LlmServices.Remove(profile);
        _llmProfileKeys.Remove(profile.Id);
        if (_preferences.LlmServices.Count == 0)
            _preferences.LlmServices.Add(new LlmServiceProfile { Name = "默认服务" });
        _editingLlmProfile = null;
        RefreshLlmProfilePicker(_preferences.LlmServices[0]);
        RefreshLlmModelChoices();
        SaveSettings();
    }

    private async void LlmProfileFetch_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await FetchLlmProfileModelsAsync();

    private async Task FetchLlmProfileModelsAsync()
    {
        SaveEditingLlmProfile();
        if (_editingLlmProfile is not { } profile) return;
        var key = _llmProfileKeys.GetValueOrDefault(profile.Id, "");
        if (string.IsNullOrWhiteSpace(profile.BaseUrl) || string.IsNullOrWhiteSpace(key))
        {
            LlmProfileStatusText.Text = "请先填写此服务的 API 地址和 Key。";
            return;
        }
        CancelModelDirectoryRequest();
        using var cancellation = new CancellationTokenSource();
        _modelFetchCancellation = cancellation;
        var revision = _modelFetchRevision;
        var address = profile.BaseUrl;
        bool IsCurrent() => !_closing && !cancellation.IsCancellationRequested &&
            revision == _modelFetchRevision && ReferenceEquals(_editingLlmProfile, profile) &&
            _preferences.LlmServices.Contains(profile) && profile.BaseUrl == address &&
            _llmProfileKeys.GetValueOrDefault(profile.Id, "") == key;
        LlmProfileFetchButton.IsEnabled = false;
        LlmProfileStatusText.Text = "正在获取模型列表…";
        try
        {
            var models = await _llmClient.ListModelsAsync(
                new LlmConnectionSettings(address, key, ""), cancellation.Token);
            if (!IsCurrent()) return;
            var keptManual = profile.EnabledModels.Where(model => !models.Contains(model));
            profile.Models = models.Concat(keptManual).Distinct(StringComparer.Ordinal)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToList();
            if (IsCurrent())
            {
                RenderLlmProfileModels();
                LlmProfileStatusText.Text = models.Count == 0
                    ? "连接成功，但接口未列出模型；请手动添加模型 ID。"
                    : $"连接成功，找到 {models.Count} 个模型。勾选后即可分配给红方或黑方。";
            }
            SaveSettings();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrent())
                LlmProfileStatusText.Text = $"获取失败：{ex.Message} 可手动添加模型 ID。";
        }
        finally
        {
            if (ReferenceEquals(_modelFetchCancellation, cancellation))
            {
                _modelFetchCancellation = null;
                LlmProfileFetchButton.IsEnabled = true;
            }
        }
    }

    private void CancelModelDirectoryRequest()
    {
        _modelFetchRevision++;
        _modelFetchCancellation?.Cancel();
        _modelFetchCancellation = null;
        LlmProfileFetchButton.IsEnabled = true;
    }

    private void LlmManualAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveEditingLlmProfile();
        if (_editingLlmProfile is not { } profile) return;
        var model = LlmManualModelBox.Text?.Trim() ?? "";
        if (model.Length is < 1 or > 160 || model.Any(char.IsControl))
        {
            LlmProfileStatusText.Text = "请输入有效的模型 ID（1–160 个字符）。";
            return;
        }
        if (!profile.Models.Contains(model)) profile.Models.Add(model);
        if (!profile.EnabledModels.Contains(model)) profile.EnabledModels.Add(model);
        profile.Models.Sort(StringComparer.OrdinalIgnoreCase);
        LlmManualModelBox.Text = "";
        LlmModelFilterBox.Text = "";
        RenderLlmProfileModels();
        RefreshLlmModelChoices();
        LlmProfileStatusText.Text = $"已启用 {model}。";
        SaveSettings();
    }

    private void RenderLlmProfileModels()
    {
        if (_loadingLlmProfile || _editingLlmProfile is not { } profile) return;
        _modelCatalog.Replace(profile.Models, profile.EnabledModels, (name, enabled) =>
        {
            if (enabled) { if (!profile.EnabledModels.Contains(name)) profile.EnabledModels.Add(name); }
            else profile.EnabledModels.Remove(name);
            RefreshLlmModelChoices(); SaveSettings();
        });
    }

    private void RefreshLlmModelChoices()
    {
        _updatingLlmChoices = true;
        try
        {
            var redChoice = SelectedLlmChoice(true);
            var blackChoice = SelectedLlmChoice(false);
            var choices = _preferences.LlmServices
                .SelectMany(profile => profile.EnabledModels.Distinct(StringComparer.Ordinal)
                    .Select(model => new LlmModelChoice(profile.Id, profile.Name, model)))
                .OrderBy(choice => choice.ProfileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RedActiveModelList.ItemsSource = choices;
            BlackActiveModelList.ItemsSource = choices;
            RedActiveModelList.SelectedItem = choices.FirstOrDefault(choice =>
                choice.ProfileId == (redChoice?.ProfileId ?? _preferences.RedLlmServiceId) &&
                choice.ModelId == (redChoice?.ModelId ?? _preferences.RedLlmModel));
            BlackActiveModelList.SelectedItem = choices.FirstOrDefault(choice =>
                choice.ProfileId == (blackChoice?.ProfileId ?? _preferences.BlackLlmServiceId) &&
                choice.ModelId == (blackChoice?.ModelId ?? _preferences.BlackLlmModel));
        }
        finally { _updatingLlmChoices = false; }
        if (_ready) RefreshUi();
    }

    private LlmModelChoice? SelectedLlmChoice(bool red) =>
        (red ? RedActiveModelList : BlackActiveModelList).SelectedItem as LlmModelChoice;

    private void OnActiveLlmModelChanged(bool red)
    {
        if (!_ready || _updatingLlmChoices) return;
        SaveSettings();
        RefreshUi();
        if ((red && _redLlm) || (!red && _blackLlm))
        {
            CancelSearch();
            _enginePaused = false;
            MaybeStartSearch();
        }
    }
}
