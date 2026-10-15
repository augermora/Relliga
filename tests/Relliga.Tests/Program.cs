namespace Relliga.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--benchmark") return await PerformanceHarness.RunAsync(args.Skip(1).ToArray());
        // Keep the same MTP/in-process routing as xUnit's generated entry point.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return await Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions);
        return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args);
    }
}
