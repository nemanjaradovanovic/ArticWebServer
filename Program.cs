using ArticWebServer.Server;
using ArticWebServer.Utils;

namespace ArticWebServer
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // Konfiguracija servera
            string prefix = "http://localhost:8080/";
            int maxWorkers = 4;       // Maksimalan broj paralelnih obrada
            int cacheTtlSeconds = 60; // Vreme važenja kes unosa u sekundama

            Logger.Log("=== Art Institute of Chicago Web Server ===");
            Logger.Log($"Konfiguracija: maxWorkers={maxWorkers}, cacheTTL={cacheTtlSeconds}s");

            HttpServer server = new HttpServer(prefix, maxWorkers, cacheTtlSeconds);

            // Hvatamo Ctrl+C za čisto gašenje
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Logger.Log("Primljen signal za gašenje...");
                server.Stop();
            };

            try
            {
                server.Start(); // Blokira dok server radi
            }
            catch (Exception ex)
            {
                Logger.LogError($"Greška pri pokretanju servera: {ex.Message}");
                Logger.LogError("Napomena: Pokrenite Visual Studio kao Administrator ili proverite da li je port 8080 slobodan.");
            }
        }
    }
}