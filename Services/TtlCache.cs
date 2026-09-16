using System;
using System.Collections.Generic;
using System.Threading;
using ArtworkSearchServer.Logging;

namespace ArtworkSearchServer.Services
{
    public class TtlCache
    {
        private class CacheEntry
        {
            public string Value;
            public DateTime ExpiresAt;
        }

        private readonly Dictionary<string, CacheEntry> entries = new Dictionary<string, CacheEntry>();
        private readonly HashSet<string> inProgress = new HashSet<string>(); // kljucevi koje neka nit upravo pribavlja
        private readonly object locker = new object();

        private readonly TimeSpan ttl;
        private readonly Logger logger;

        public TtlCache(TimeSpan ttl, Logger logger)
        {
            this.ttl = ttl;
            this.logger = logger;
        }

        public string GetOrAdd(string key, Func<string> producer)
        {
            // FAZA 1 (pod lock-om): provera kesa i "rezervacija" kljuca.
            lock (locker)
            {
                while (true)
                {
                    CacheEntry entry;
                    if (entries.TryGetValue(key, out entry))
                    {
                        if (entry.ExpiresAt > DateTime.Now)
                        {
                            logger.Log($"CACHE HIT za '{key}' (nema API poziva)");
                            return entry.Value;
                        }

                        entries.Remove(key);
                        logger.Log($"Unos za '{key}' je istekao i uklonjen iz kesa");
                    }

                    if (inProgress.Contains(key))
                    {
                        logger.Log($"CACHE STAMPEDE za '{key}' -> cekam da druga nit zavrsi (Monitor.Wait)");
                        Monitor.Wait(locker);
                        continue; // posle budjenja ponovo proveravamo kes
                    }

                    inProgress.Add(key);
                    logger.Log($"CACHE MISS za '{key}' -> ja pribavljam sa API-ja");
                    break;
                }
            }

            
            string value = null;
            Exception error = null;
            try
            {
                value = producer();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            
            lock (locker)
            {
                inProgress.Remove(key);

                if (error == null)
                {
                    CacheEntry newEntry = new CacheEntry();
                    newEntry.Value = value;
                    newEntry.ExpiresAt = DateTime.Now.Add(ttl);
                    entries[key] = newEntry;
                    logger.Log($"UPIS u kes za '{key}' (vazi do {newEntry.ExpiresAt:HH:mm:ss}, unosa u kesu: {entries.Count})");
                }

                Monitor.PulseAll(locker);
            }

            if (error != null)
            {
                throw error;
            }

            return value;
        }
    }
}