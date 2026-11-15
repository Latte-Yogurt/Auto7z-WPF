namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 跨模块共享的运行时状态。
    /// </summary>
    /// <remarks>
    /// 只有一项：CurrentLanguage 供 AboutWindow 取当前语言。
    /// 不要在这里堆「写了却没人读」的状态——那种字段会误导读者以为它在生效。
    /// </remarks>
    public static class AppState
    {
        public static string CurrentLanguage = "zh-CN";
    }
}
