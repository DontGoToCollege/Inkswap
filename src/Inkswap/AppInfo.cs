namespace Inkswap;

/// <summary>Everything about this app that the shell needs to know. The rest of the shell is generic.</summary>
static class AppInfo
{
    public const string Name = "Inkswap";
    /// <summary>Data folder beside the exe (also holds the downloaded AI model).</summary>
    public const string DataFolder = "Inkswap-Data";
    /// <summary>Folder under %LOCALAPPDATA%, used only when the exe's own folder is read-only.</summary>
    public const string FallbackFolder = "Inkswap";
    /// <summary>Private virtual host the embedded web app is served from. Nothing ever leaves the machine.</summary>
    public const string Host = "app.inkswap.portable";
    public const string Background = "#0e1116";
    public const string Text = "#dbe1ec";
}
