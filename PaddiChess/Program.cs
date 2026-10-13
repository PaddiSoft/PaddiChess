using Avalonia;
using System;

namespace PaddiXiangqi;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--x11-worker")
        {
            var output = Console.OpenStandardOutput(); Console.SetOut(Console.Error);
            External.X11DesktopWorker.ServeAsync(Console.OpenStandardInput(), output).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--recognition-worker")
        {
            var output = Console.OpenStandardOutput();
            Console.SetOut(Console.Error);
            External.RecognitionWorkerProtocol.ServeAsync(Console.OpenStandardInput(), output).GetAwaiter().GetResult();
            return;
        }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到程序路径");
        var assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? typeof(Program).Assembly.Location : null;
        if (args.Length == 3 && args[0] == "--recognition-benchmark")
        {
            Sessions.RecognitionBenchmark.RunAsync(args[1], args[2], cpu =>
                External.RecognitionWorkerClient.ApplicationStartInfo(executable, assembly, cpu)).GetAwaiter().GetResult();
            return;
        }
        External.RecognitionHost.Create = () => new External.RecognitionWorkerClient(cpu =>
            External.RecognitionWorkerClient.ApplicationStartInfo(executable, assembly, cpu));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
