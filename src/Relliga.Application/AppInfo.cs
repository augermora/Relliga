namespace Relliga.Application;

public static class AppInfo
{
    public const string Name = "Relliga";
    public const string Publisher = "Relliga contributors";
    public const string SourceUrl = "https://github.com/augermora/Relliga";
    public static string Version => typeof(AppInfo).Assembly.GetName().Version!.ToString(3);
}
