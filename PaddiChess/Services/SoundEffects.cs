using System.Diagnostics;
using System.Text;

namespace PaddiXiangqi.Services;

public enum GameSound { Move, Capture, Check, Mate }

public sealed class SoundEffects
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pikadesk-sounds-v1");
    public bool Enabled { get; set; } = true;

    public SoundEffects()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, "Move.wav");
            if (!File.Exists(path)) WriteMoveWav(path);
        }
        catch { Enabled = false; }
    }

    public void Play(GameSound kind)
    {
        if (!Enabled) return;
        var path = kind switch
        {
            GameSound.Capture => Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "capture.wav"),
            GameSound.Check => Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "check.wav"),
            GameSound.Mate => Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "mate.wav"),
            _ => Path.Combine(_folder, "Move.wav")
        };
        if (!File.Exists(path)) return;
        try
        {
            ProcessStartInfo info;
            if (OperatingSystem.IsMacOS())
            {
                info = new ProcessStartInfo("afplay");
                info.ArgumentList.Add(path);
            }
            else if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo("powershell");
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add($"(New-Object Media.SoundPlayer '{path.Replace("'", "''")}').PlaySync()");
            }
            else
            {
                var player = new[] { "/usr/bin/paplay", "/usr/bin/aplay", "/usr/bin/ffplay" }.FirstOrDefault(File.Exists);
                if (player is null) return;
                info = new ProcessStartInfo(player);
                if (player.EndsWith("ffplay"))
                {
                    info.ArgumentList.Add("-nodisp");
                    info.ArgumentList.Add("-autoexit");
                    info.ArgumentList.Add("-loglevel");
                    info.ArgumentList.Add("quiet");
                }
                info.ArgumentList.Add(path);
            }
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var process = Process.Start(info);
                    if (process is not null) await process.WaitForExitAsync();
                }
                catch { /* Sound is optional when the OS player is unavailable. */ }
            });
        }
        catch { }
    }

    private static void WriteMoveWav(string path)
    {
        const int rate = 22050;
        const double duration = .14;
        var count = (int)(rate * duration);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + count * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)rate;
            var envelope = Math.Pow(Math.Max(0, 1 - t / duration), 6);
            var tone = Math.Sin(2 * Math.PI * 620 * t) + .35 * Math.Sin(2 * Math.PI * 1240 * t);
            var attack = Math.Min(1, t * 140);
            writer.Write((short)Math.Clamp((int)(tone * envelope * attack * 11000), short.MinValue, short.MaxValue));
        }
    }
}
