using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Views;

public partial class PiecePaletteView : UserControl
{
    private readonly PiecePaletteViewModel _model = new();
    public PositionEditorState? Editor { get; private set; }
    public PiecePaletteView() { InitializeComponent(); DataContext = _model; }
    public void Attach(PositionEditorState? editor)
    {
        if (Editor is not null) Editor.Changed -= EditorChanged;
        Editor = editor;
        if (Editor is not null) Editor.Changed += EditorChanged;
        _model.Update(editor);
    }
    private void EditorChanged(object? sender, EventArgs e) => _model.Update(Editor);
    private void Piece_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PiecePaletteItemViewModel { CanAdd: true } item }) Editor?.SelectPiece(item.Piece);
    }
}
