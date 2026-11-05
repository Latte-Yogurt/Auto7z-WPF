namespace Auto7z_WPF.Services
{
    /// <summary>
    /// 跨模块共享的运行时状态。
    /// </summary>
    /// <remarks>
    /// 只有两项：CurrentLanguage 供 AboutWindow 取当前语言，SevenZUsageCount 记录配置里的使用次数。
    /// 不要在这里堆「写了却没人读」的状态——那种字段会误导读者以为它在生效。
    /// </remarks>
    public static class AppState
    {
        public static string CurrentLanguage = "zh-CN";

        public static long SevenZUsageCount = 0;
    }
}
