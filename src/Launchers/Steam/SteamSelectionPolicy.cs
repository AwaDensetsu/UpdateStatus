namespace PlayniteUpdateStatus.Launchers.Steam
{
    public enum SteamSelectionResult { Waiting, Accepted, Finished, Rejected }
    // Explicit Play may retry a saved update error. Result codes describe the
    // previous attempt and must not prevent requesting a new one.
    public static class SteamSelectionPolicy
    {
        public const uint KnownFlags = 16777215;
        public const uint WorkFlags = 2 | 32 | 128 | 256 | 512 | 1024 | 65536 | 131072 | 262144 | 524288 | 1048576 | 2097152 | 4194304 | 8388608;
        public static bool Known(uint flags) { return flags != 0 && (flags & ~KnownFlags) == 0; }
        public static bool Pending(uint flags)
        { return Known(flags) && (flags & (8192 | 2048 | 4096 | 32768)) == 0 && (flags & WorkFlags) != 0; }
        public static bool Complete(uint flags)
        { return Known(flags) && (flags & 4) != 0 && (flags & (WorkFlags | 8 | 8192 | 2048 | 4096 | 32768)) == 0; }
        public static bool CanClose(uint flags)
        { return Known(flags) && (flags & (8192 | 2048 | 4096 | 32768)) == 0 && (flags & (WorkFlags | 8 | 4)) != 0; }
    }
}
