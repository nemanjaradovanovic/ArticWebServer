using System;
using System.Threading;

namespace ArtworkSearchServer.Logging
{
    public class Logger
    {
        private readonly object locker = new object();

        public void Log(string message)
        {
            lock (locker)
            {
                int threadId = Thread.CurrentThread.ManagedThreadId;
                string time = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.WriteLine($"[{time}] [Nit {threadId}] {message}");
            }
        }
    }
}