namespace ArticWebServer.Models
{
    // Predstavlja jedan unos u kes memoriji
    // Čuva rezultat pretrage i vreme kada je unos kreiran
    public class CacheEntry
    {
        public string Data { get; set; }
        public DateTime CreatedAt { get; set; }

        public CacheEntry(string data)
        {
            Data = data;
            CreatedAt = DateTime.UtcNow;
        }

        // Proverava da li je unos istekao na osnovu zadatog vremena trajanja (u sekundama)
        public bool IsExpired(int ttlSeconds)
        {
            return (DateTime.UtcNow - CreatedAt).TotalSeconds > ttlSeconds;
        }
    }
}