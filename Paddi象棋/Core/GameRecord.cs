namespace PaddiXiangqi.Core;

public sealed class GameRecord
{
    public string Format { get; set; } = "PaddiXiangqi/1";
    public string Title { get; set; } = "未命名棋谱";
    public string StartFen { get; set; } = XiangqiGame.InitialFen;
    public List<string> Moves { get; set; } = [];
    public Dictionary<int, string> Notes { get; set; } = [];
    public Dictionary<int, double> Scores { get; set; } = [];
    public Dictionary<int, string> ScoreLabels { get; set; } = [];
    public Dictionary<int, List<LlmThoughtRecord>> LlmThoughts { get; set; } = [];
    public int? AgreedDrawPly { get; set; }
    public bool ExternalAdjudication { get; set; }
    public int? PendingDrawOfferPly { get; set; }
    public bool? PendingDrawOfferingRed { get; set; }
    public string? PendingDrawExplanation { get; set; }
    public int CurrentPly { get; set; }
}

/// <summary>
/// A record of what a model explicitly returned for one completed move. It contains no
/// API endpoint or credential and never represents hidden reasoning the service did not expose.
/// </summary>
public sealed class LlmThoughtRecord
{
    public int Ply { get; set; }
    public string Side { get; set; } = "";
    public string Model { get; set; } = "";
    public string MoveNotation { get; set; } = "";
    public string UciMove { get; set; } = "";
    public string Action { get; set; } = "走棋";
    public string RequestedReasoningEffort { get; set; } = "";
    public string? SentReasoningEffort { get; set; }
    public bool ReasoningFellBack { get; set; }
    public string? ReasoningFallbackReason { get; set; }
    public string? VisibleThinking { get; set; }
    public string? Explanation { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
