using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private IExternalPositionRecognizer _positionRecognizer = new ExternalPositionRecognizer();
    private Task<SkinRecognition?> ReadExternalSkinAsync(ExternalFrame frame, BoardCalibration geometry,
        bool redToMove, CancellationToken ct, bool detectOrientation = false)
        => _positionRecognizer.ReadAsync(frame, geometry, redToMove, ct, detectOrientation,
            _externalSessionSkin, (ExternalSkinBox.SelectedItem as SkinChoice)?.Path);

    private Task EditExternalPositionAsync(string fen, string message, IReadOnlyList<Square>? uncertain = null)
        => ShowExternalPositionEditorAsync(fen, message, uncertain, standardOpening: false);

    private async Task ShowExternalPositionEditorAsync(string fen, string message, IReadOnlyList<Square>? uncertain,
        bool standardOpening)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_externalPermissionLifetime.Token,
            _externalSetupCancellation?.Token ?? CancellationToken.None);
        var context = new ExternalPositionEditorContext(fen, message, uncertain, _externalFrame, _externalCalibration,
            ExternalOrientationBox.SelectedIndex == 1, ExternalTurnBox.SelectedIndex, standardOpening);
        var dialog = new ExternalPositionEditorWindow(context, _externalDesktop, ReadExternalSkinAsync, cancellation.Token);
        var result = await dialog.ShowDialog<ExternalPositionEditResult?>(this);
        if (result == null || cancellation.IsCancellationRequested || _closing) return;
        if (result.Frame != null && result.Geometry != null)
        {
            _externalFrame = result.Frame;
            _externalCalibration = result.Geometry with { RedAtTop = result.RedAtTop };
            SetExternalPreview(result.Frame);
        }
        ExternalOrientationBox.SelectedIndex = result.RedAtTop ? 1 : 0;
        _externalRequestedTurn = null;
        SetExternalTurn(result.RedToMove); _externalTurnKnown = true;
        await ApplyExternalPositionAsync(result.Fen, cancellation.Token);
        if (result.GeometryChanged) ExternalCalibrationText.Text = "已在局面窗口重新定位并同步";
        ExternalStatusText.Text = "局面已同步，可直接接管。";
    }
}
