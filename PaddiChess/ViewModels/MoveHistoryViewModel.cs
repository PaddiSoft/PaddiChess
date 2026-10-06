using System.Collections.ObjectModel;
using System.Windows.Input;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.ViewModels;

public sealed class HistoryMoveViewModel(int ply, bool red, string label, Action<int> navigate) : ObservableObject
{
    private string _label = label;
    private bool _selected;
    public int Ply { get; } = ply;
    public bool Red { get; } = red;
    public string Label { get => _label; set => Set(ref _label, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public ICommand Select { get; } = new ActionCommand(() => navigate(ply));
}

public sealed class HistoryRowViewModel : ObservableObject
{
    private HistoryMoveViewModel? _black;
    public string Number { get; init; } = "";
    public HistoryMoveViewModel? Start { get; init; }
    public HistoryMoveViewModel? Red { get; init; }
    public HistoryMoveViewModel? Black { get => _black; set => Set(ref _black, value); }
    public bool IsStart => Start != null;
    public bool IsTurn => Start == null;
}

/// <summary>Incremental data rows; visual containers are recycled by the view.</summary>
public sealed class MoveHistoryViewModel(Action<int> navigate)
{
    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];
    public IReadOnlyList<HistoryMoveViewModel> Moves => _moves;
    private readonly List<HistoryMoveViewModel> _moves = [];
    private readonly List<ChessMove> _rendered = [];
    private string? _startFen;
    private int _selectedPly = -1, _number;
    private bool _startsRed;
    private HistoryRowViewModel? _openRow;

    public void Update(string startFen, IReadOnlyList<ChessMove> history, int selectedPly,
        IReadOnlyDictionary<int, string> notes)
    {
        var rebuild = _startFen != startFen || history.Count < _rendered.Count;
        for (int i = 0; !rebuild && i < _rendered.Count; i++) rebuild = _rendered[i] != history[i];
        string Label(int ply) => (ply == 0 ? "— 开始 —" : history[ply - 1].Notation +
            (history[ply - 1].IsCheck ? " 将" : "")) + (notes.ContainsKey(ply) ? "  ✎" : "");
        if (rebuild)
        {
            Rows.Clear(); _moves.Clear(); _rendered.Clear(); _openRow = null; _selectedPly = -1;
            _startFen = startFen;
            var fields = startFen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _startsRed = fields.Length < 2 || fields[1] == "w";
            _number = fields.Length >= 6 && int.TryParse(fields[5], out var number) ? number : 1;
            var start = new HistoryMoveViewModel(0, false, Label(0), navigate);
            _moves.Add(start); Rows.Add(new() { Start = start });
        }
        for (int i = _rendered.Count; i < history.Count; i++)
        {
            bool red = (i % 2 == 0) == _startsRed;
            var move = new HistoryMoveViewModel(i + 1, red, Label(i + 1), navigate);
            if (red || _openRow == null)
            {
                _openRow = new() { Number = (_number++).ToString(), Red = red ? move : null };
                Rows.Add(_openRow);
            }
            if (!red) { _openRow.Black = move; _openRow = null; }
            _moves.Add(move); _rendered.Add(history[i]);
        }
        for (int i = 0; i < _moves.Count; i++) _moves[i].Label = Label(i);
        if (_selectedPly >= 0 && _selectedPly < _moves.Count) _moves[_selectedPly].IsSelected = false;
        if (selectedPly < _moves.Count) _moves[selectedPly].IsSelected = true;
        _selectedPly = selectedPly;
    }
}
