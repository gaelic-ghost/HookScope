namespace HookScope.Persistence;

public static class SqliteRuntime
{
    private static readonly Lock InitializationLock = new();
    private static bool isInitialized;

    public static void Initialize()
    {
        lock (InitializationLock)
        {
            if (isInitialized)
            {
                return;
            }

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
            isInitialized = true;
        }
    }
}
