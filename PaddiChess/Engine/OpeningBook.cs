using PaddiXiangqi.Core;

namespace PaddiXiangqi.Engine;

public sealed record OpeningBookOptions(string Path, string Name, int MaxFullmoves = 20, bool RandomBest = false);
public sealed record OpeningBookHit(string Move, string Name, int BookScore);

public static class OpeningBook
{
    public static Task<OpeningBookHit?> TryChooseAsync(string startFen, string history, OpeningBookOptions? options, CancellationToken ct) =>
        options is null ? Task.FromResult<OpeningBookHit?>(null) : Task.Run(() => Choose(startFen, history, options, ct), ct);

    private static OpeningBookHit? Choose(string startFen, string history, OpeningBookOptions options, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(options.Path)) return null;
            var game = new XiangqiGame(); game.LoadFen(startFen);
            // No known opening history: let the selected engine adjudicate this position.
            // A first-move attachment can recover its one missing ply unambiguously.
            var initialKey = PositionKey(XiangqiGame.InitialFen);
            if (PositionKey(startFen) != initialKey)
            {
                var initial = new XiangqiGame(); string? first = null;
                foreach (var move in initial.AllLegalMoves())
                {
                    initial.TryMoveUci(move.Uci, out _);
                    if (PositionKey(initial.CurrentFen()) == PositionKey(startFen)) first = move.Uci;
                    initial.Undo();
                }
                if (first is null) return null;
                history = first + " " + history; game.NewGame();
            }
            var visited = new HashSet<string> { PositionKey(game.CurrentFen()) };
            foreach (var move in history.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                if (!game.TryMoveUci(move, out _) || !visited.Add(PositionKey(game.CurrentFen()))) return null;
            }
            if (game.FullmoveNumber > Math.Clamp(options.MaxFullmoves, 1, 40)) return null;
            using var db = new XqbDatabase(options.Path, ct);
            db.Validate();
            var legal = game.AllLegalMoves().Select(m => m.Uci).ToHashSet(StringComparer.Ordinal);
            var choices = new List<BookMove>();
            foreach (var entry in db.Lookup(game.CurrentFen()).Where(m => legal.Contains(m.Move)).DistinctBy(m => m.Move))
            {
                game.TryMoveUci(entry.Move, out _);
                var repeats = visited.Contains(PositionKey(game.CurrentFen()));
                game.Undo();
                if (!repeats) choices.Add(entry);
            }
            ct.ThrowIfCancellationRequested();
            if (choices.Count == 0) return null;
            var bestScore = choices.Max(m => m.Score);
            var best = choices.Where(m => m.Score == bestScore).OrderBy(m => m.Move, StringComparer.Ordinal).ToArray();
            var selected = best[options.RandomBest ? Random.Shared.Next(best.Length) : 0];
            return new(selected.Move, options.Name, selected.Score);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        { ct.ThrowIfCancellationRequested(); return null; }
    }
    private static string PositionKey(string fen) => string.Join(' ', fen.Split(' ').Take(2));
}
