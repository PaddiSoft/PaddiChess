using PaddiXiangqi.Core;
using System.Text.Json.Serialization;

namespace PaddiXiangqi.Engine;

public enum EngineGamePhase { Opening, MiddleGame, Endgame }

public sealed record EnginePhaseSettings
{
    public bool Enabled { get; init; }
    public int OpeningFullmoves { get; init; } = 10;
    public int EndgameAttackers { get; init; } = 6;
    // The legacy threshold's numeric value survives upgrades. The UI now
    // describes its material weighting explicitly instead of counting all men.
    public int? EndgameMaterialPoints { get; init; }
    [JsonIgnore]
    public int MaterialThreshold => Math.Clamp(EndgameMaterialPoints ?? EndgameAttackers, 1, 12);
    public int OpeningTimeMs { get; init; } = 800;
    public int MiddleTimeMs { get; init; } = 5000;
    public int EndgameTimeMs { get; init; } = 5000;

    public static int MaterialPoints(XiangqiGame game)
    {
        var points = 0;
        foreach (var piece in game.Board)
            points += char.ToUpperInvariant(piece) switch { 'R' => 2, 'N' or 'C' => 1, _ => 0 };
        return points;
    }

    public EngineGamePhase Phase(XiangqiGame game)
    {
        var pieces = 0;
        foreach (var piece in game.Board)
        {
            if (piece == '\0') continue;
            pieces++;
        }
        // Phase is only a configurable budget heuristic, not a difficulty estimate.
        // Four rooks are materially different from four cannons; missing defenders
        // and pawns alone must not override the user's endgame threshold.
        if (MaterialPoints(game) <= MaterialThreshold)
            return EngineGamePhase.Endgame;

        // Imported screenshots have no complete history. An exact first-round
        // position still identifies its opening offset; do not replay guessed
        // moves or call every dense imported middlegame an opening.
        var fromStart = game.StartFen.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] ==
                        XiangqiGame.InitialFen.Split(' ')[0];
        var importedOpening = fromStart ? null : OpeningPositions.Find(game.StartFen);
        var fullmove = Math.Max(game.FullmoveNumber,
            importedOpening is { } opening ? 1 + (opening.Ply + game.Ply) / 2 : game.FullmoveNumber);
        return (fromStart || importedOpening.HasValue) && fullmove <= Math.Clamp(OpeningFullmoves, 1, 40) && pieces >= 28
            ? EngineGamePhase.Opening : EngineGamePhase.MiddleGame;
    }

    public EngineSettings Apply(XiangqiGame game, EngineSettings settings)
    {
        if (!Enabled || settings.Level != 20) return settings;
        var milliseconds = Phase(game) switch
        {
            EngineGamePhase.Opening => OpeningTimeMs,
            EngineGamePhase.Endgame => EndgameTimeMs,
            _ => MiddleTimeMs
        };
        return settings with { MoveTimeOverrideMs = Math.Clamp(milliseconds, 200, 120_000) };
    }

    public static string DisplayName(EngineGamePhase phase) => phase switch
    {
        EngineGamePhase.Opening => "开局",
        EngineGamePhase.Endgame => "残局",
        _ => "中局"
    };
}
