using System;
using System.Net.Http;
using ArtworkSearchServer.Logging;
using ArtworkSearchServer.Server;
using ArtworkSearchServer.Services;

namespace ArtworkSearchServer
{
    public class Program
    {
        private const int Port = 8080;
        private const int WorkerCount = 8;                                     
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);  

        
        private const int ApiDelayMs = 0;

        
        private static readonly HttpClient httpClient = new HttpClient();

        public static void Main(string[] args)
        {
            httpClient.DefaultRequestHeaders.Add("User-Agent", "ArtworkSearchServer/1.0");

            Logger logger = new Logger();
            TtlCache cache = new TtlCache(CacheTtl, logger);
            ArtworkService artworkService = new ArtworkService(httpClient, logger, ApiDelayMs);
            HttpServer server = new HttpServer(Port, WorkerCount, cache, artworkService, logger);

            server.Start();

            Console.WriteLine();
            Console.WriteLine("=================================================");
            Console.WriteLine($"  Server radi na: http://localhost:{Port}/");
            Console.WriteLine($"  Radnickih niti: {WorkerCount} | TTL kesa: {CacheTtl.TotalSeconds}s | Kasnjenje API-ja: {ApiDelayMs}ms");
            Console.WriteLine("  Pritisni ENTER za gasenje servera.");
            Console.WriteLine("=================================================");
            Console.WriteLine();

            Console.ReadLine();

            server.Stop();
        }
    }
}