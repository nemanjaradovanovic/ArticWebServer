namespace ArticWebServer.Utils
{
    // Thread-safe logger koji koristi lock za sinhronizaciju pristupa konzoli
    public static class Logger
    {
        private static readonly object _consoleLock = new object();

        public static void Log(string message)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [T-{Thread.CurrentThread.ManagedThreadId}] {message}");
            }
        }

        public static void LogError(string message)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [T-{Thread.CurrentThread.ManagedThreadId}] ERROR: {message}");
                Console.ResetColor();
            }
        }

        public static void LogCache(string message)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [T-{Thread.CurrentThread.ManagedThreadId}] CACHE: {message}");
                Console.ResetColor();
            }
        }
    }
}