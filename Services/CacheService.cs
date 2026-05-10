using ArticWebServer.Models;
using ArticWebServer.Utils;

namespace ArticWebServer.Services
{
    // Thread-safe kes servis sa vremenskim isticanjem (TTL strategija)
    // Rešava cache stampede problem: ako više niti traži isti ključ koji nije u kešu,
    // samo jedna nit obavlja dohvatanje, ostale čekaju koristeći Monitor.Wait/Pulse
    public class CacheService
    {
        // TTL u sekundama - koliko dugo unos ostaje validan u kešu
        private readonly int _ttlSeconds;

        // Rečnik koji čuva keširane unose
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();

        // Skup ključeva koji su trenutno u obradi (zaštita od cache stampede)
        private readonly HashSet<string> _inProgress = new HashSet<string>();

        // Jedan lock objekat za sinhronizaciju pristupa i kešu i skupu inProgress
        private readonly object _cacheLock = new object();

        public CacheService(int ttlSeconds = 60)
        {
            _ttlSeconds = ttlSeconds;
        }

        // Pokušava da dobavi vrednost iz keša za dati ključ.
        // Vraća null ako nije u kešu ili je isteklo.
        // Ako je drugi thread u toku obrade istog ključa, blokira se i čeka (rešenje cache stampede).
        // Ako vrednost nije ni u kešu ni u obradi, registruje je kao "u obradi" i vraća null
        // kako bi pozivalac obavio dohvatanje.
        public string? GetOrLock(string key)
        {
            lock (_cacheLock)
            {
                // Petlja je neophodna jer Monitor.Wait može biti "lažno" probuđen
                while (true)
                {
                    // Proveravamo da li postoji valjani unos u kešu
                    if (_cache.TryGetValue(key, out CacheEntry? entry))
                    {
                        if (!entry.IsExpired(_ttlSeconds))
                        {
                            Logger.LogCache($"HIT za ključ: '{key}'");
                            return entry.Data;
                        }
                        else
                        {
                            // Unos je istekao - uklanjamo ga
                            _cache.Remove(key);
                            Logger.LogCache($"EXPIRED za ključ: '{key}', uklonjen iz keša");
                        }
                    }

                    // Ako je drugi thread već obrađuje isti ključ - čekamo
                    // Ovo je rešenje za cache stampede problem
                    if (_inProgress.Contains(key))
                    {
                        Logger.LogCache($"WAIT - ključ '{key}' je u obradi od strane druge niti, čekam...");
                        Monitor.Wait(_cacheLock);
                        // Nakon buđenja ponovo proveravamo petlju
                        continue;
                    }

                    // Nema u kešu i niko ne obrađuje - mi preuzimamo odgovornost
                    _inProgress.Add(key);
                    Logger.LogCache($"MISS za ključ: '{key}', ova nit preuzima dohvatanje");
                    return null;
                }
            }
        }

        // Upisuje rezultat u keš i oslobađa ostale niti koje su čekale na isti ključ
        public void Set(string key, string data)
        {
            lock (_cacheLock)
            {
                _cache[key] = new CacheEntry(data);
                _inProgress.Remove(key);
                Logger.LogCache($"SET za ključ: '{key}', sve niti koje su čekale su obaveštene");
                // Budimo SVE niti koje čekaju - one će u petlji naći vrednost u kešu
                Monitor.PulseAll(_cacheLock);
            }
        }

        // Poziva se ako dohvatanje nije uspelo - oslobađamo lock bez upisivanja u keš
        public void ReleaseLock(string key)
        {
            lock (_cacheLock)
            {
                _inProgress.Remove(key);
                Monitor.PulseAll(_cacheLock);
                Logger.LogCache($"RELEASE za ključ: '{key}' (dohvatanje neuspešno)");
            }
        }

        // Vraća broj trenutno keširanih (i još uvek važećih) unosa
        public int GetValidCount()
        {
            lock (_cacheLock)
            {
                return _cache.Count(kvp => !kvp.Value.IsExpired(_ttlSeconds));
            }
        }
    }
}