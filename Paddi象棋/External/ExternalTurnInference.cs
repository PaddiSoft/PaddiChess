using System.Diagnostics;
using System.Text.Json;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

public readonly record struct ExternalClockReading(int? Top, int? Bottom);

public static class ExternalTurnInference
{
    public static bool? FromPosition(string fen)
    {
        var game = new XiangqiGame();
        game.LoadFen(fen);
        if (OpeningPositions.Find(fen) is { } opening) return opening.RedToMove;
        var redInCheck = XiangqiGame.IsInCheck(game.Board, true);
        var blackInCheck = XiangqiGame.IsInCheck(game.Board, false);
        return redInCheck == blackInCheck ? null : redInCheck;
    }

    /// <summary>True means the clock above the board is running; false means below.</summary>
    public static bool? RunningClock(ExternalClockReading before, ExternalClockReading after)
    {
        if (before.Top is not { } upperBefore || before.Bottom is not { } lowerBefore ||
            after.Top is not { } upperAfter || after.Bottom is not { } lowerAfter) return null;
        var upperDecrease = upperBefore - upperAfter;
        var lowerDecrease = lowerBefore - lowerAfter;
        if (upperDecrease is >= 1 and <= 2 && lowerDecrease == 0) return true;
        if (lowerDecrease is >= 1 and <= 2 && upperDecrease == 0) return false;
        return null;
    }

    public static async Task<ExternalClockReading?> ReadClocksAsync(byte[] png, BoardCalibration geometry, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var bridge = Path.Combine(AppContext.BaseDirectory, "Native", "PaddiBridge");
        if (!File.Exists(bridge)) return null;
        var start = new ProcessStartInfo(bridge)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("recognize-clocks");
        Process? started;
        try { started = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        using var process = started;
        if (process == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            var payload = JsonSerializer.Serialize(new
            {
                png = Convert.ToBase64String(png), top = geometry.Top, bottom = geometry.Bottom
            });
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) { _ = await error; return null; }
            return JsonSerializer.Deserialize<ExternalClockReading>(await output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}
