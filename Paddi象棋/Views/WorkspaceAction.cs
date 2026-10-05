using Avalonia.Interactivity;

namespace PaddiXiangqi.Views;

public enum WorkspaceAction
{
    Benchmark,
    BlackLlmTest,
    ClearHash,
    EngineBrowse,
    EngineEvalBrowse,
    EngineManage,
    EnginePluginAdd,
    EnginePluginDefault,
    EnginePluginDelete,
    EnginePluginExport,
    EnginePluginImport,
    EnginePluginProbe,
    EnginePluginSave,
    ExternalCalibrate,
    ExternalEditPosition,
    ExternalLearnSkin,
    ExternalRecognize,
    ExternalStandard,
    ExternalUsePosition,
    LlmManualAdd,
    LlmProfileAdd,
    LlmProfileDelete,
    LlmProfileFetch,
    PlayWorkspace,
    RedLlmTest,
}

public sealed class WorkspaceActionEventArgs(WorkspaceAction action, object? source, RoutedEventArgs originalArgs) : EventArgs
{
    public WorkspaceAction Action { get; } = action;
    public object? Source { get; } = source;
    public RoutedEventArgs OriginalArgs { get; } = originalArgs;
}
