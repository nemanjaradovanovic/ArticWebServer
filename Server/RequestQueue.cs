using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace ArtworkSearchServer.Server
{
    public class RequestQueue
    {
        private readonly Queue<HttpListenerContext> queue = new Queue<HttpListenerContext>();
        private readonly object locker = new object();
        private bool closed = false;

        public int Enqueue(HttpListenerContext context)
        {
            lock (locker)
            {
                queue.Enqueue(context);
                Monitor.Pulse(locker);
                return queue.Count;
            }
        }

        public HttpListenerContext Dequeue()
        {
            lock (locker)
            {
                while (queue.Count == 0 && !closed)
                {
                    Monitor.Wait(locker);
                }

                if (queue.Count == 0)
                {
                    return null;
                }

                return queue.Dequeue();
            }
        }

        public void Close()
        {
            lock (locker)
            {
                closed = true;
                Monitor.PulseAll(locker);
            }
        }
    }
}