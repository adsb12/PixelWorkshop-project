using System.Windows;

namespace PixelWorkshop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--run", var id] && Guid.TryParseExact(id, "N", out var workerId))
            return Jobs.Run(workerId);

        if (args.Length != 0)
            return 2;

        using var instance = new Mutex(true, @"Local\PixelWorkshop-Dashboard", out var first);
        if (!first)
            return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var factory = new FactoryWindow();
        return app.Run(factory);
    }
}
