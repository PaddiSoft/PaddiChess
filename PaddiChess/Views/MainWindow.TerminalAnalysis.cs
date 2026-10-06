using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private void ShowTerminalAnalysis()
    {
        var result = _game.Result;
        var terminal = _setupBoard is null && result != GameResult.Ongoing;
        TerminalAnalysisPanel.IsVisible = terminal;
        if (!terminal)
        {
            if (Chart.ScoreLabelOverride != null) { Chart.ScoreLabelOverride = null; Chart.Refresh(); }
            return;
        }

        var verified = !_externalLinked || _externalCompleted;
        var winner = result switch
        { GameResult.RedWins => "红方获胜", GameResult.BlackWins => "黑方获胜", _ => "和棋" };
        var reason = result == GameResult.Draw ? "双方同意和棋" : _game.SideInCheck ? "绝杀" : "无合法着法";
        ScoreText.Text = verified ? winner : "待确认终局";
        WdlText.Text = verified ? "终局结果 · " + winner : "正在核验目标棋盘，尚未结束接管";
        EngineStatusText.Text = verified ? "对局已结束" : "终局核验中";
        TerminalResultText.Text = verified ? $"{reason} · {winner}" : "正在连续核验终局画面";
        DepthText.Text = NodesText.Text = NpsText.Text = "—";
        BestMovePanel.IsVisible = PvPanel.IsVisible = VariationPanel.IsVisible = false;
        Board.AnalysisArrows = [];
        Board.HintFrom = Board.HintTo = null;

        // The previous search belongs to the position before the final move.
        // Keep it clearly labelled; never present its PV as a move to play now.
        if (_game.Ply > 0 && _llmEvaluationHistory.TryGetValue(_game.Ply - 1, out var previous))
        {
            var position = new XiangqiGame(); position.LoadFen(previous.Fen);
            TerminalSearchText.Text = $"最后一手前的搜索：{AnalysisFormatter.ScoreForPlayers(previous.Info, previous.RedToMove)}" +
                $" · 深度 {previous.Info.Depth} · {previous.Info.Nodes:N0} 节点\n" +
                AnalysisFormatter.Variation(position, previous.Info.Pv, 6) + "\n点击棋谱前一手，可查看完整分析。";
        }
        else TerminalSearchText.Text = "终局无需继续搜索。点击棋谱前一手，可重新分析或查看已保存的评分。";

        if (verified)
        {
            _scores[_game.Ply] = result switch { GameResult.RedWins => 1200, GameResult.BlackWins => -1200, _ => 0 };
            _scoreLabels[_game.Ply] = winner;
            Chart.ScoreLabelOverride = winner;
            Chart.Refresh();
        }
    }
}
