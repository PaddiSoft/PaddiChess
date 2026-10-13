namespace PaddiXiangqi.Engine;

/// <summary>UCI option negotiation and candidate adjudication are distinct capabilities.</summary>
public sealed record RuleCapabilities(string Source, bool Configurable, bool CanQueryAllowedMoves,
    IReadOnlyList<UciEngineOption> Options)
{
    public static RuleCapabilities FromUci(string name, IReadOnlyList<UciEngineOption> options) =>
        new(name, options.Any(o => o.IsRuleOption), false, options.Where(o => o.IsRuleOption).ToArray());
}

public interface IRuleMoveProvider
{
    RuleCapabilities Capabilities { get; }
    Task<NativeRuleMoves> GetAllowedMovesAsync(string startFen, string uciMoves, string currentFen,
        CancellationToken cancellationToken = default);
}
