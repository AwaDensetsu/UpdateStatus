namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Shared by the observer and one-shot helper; the latter always reads again.
    public static class SteamControlPolicy
    {
        public static bool Available(uint installState, uint updateState, uint result, bool hasCounters)
        {
            return hasCounters && (installState & 8192) == 0 && (installState & (2 | 256 | 512 | 1024)) != 0 &&
                (updateState & ~2039U) == 0 && result < 5;
        }
    }
}
