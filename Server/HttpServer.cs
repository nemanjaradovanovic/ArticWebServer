using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using ArtworkSearchServer.Logging;
using ArtworkSearchServer.Services;

namespace ArtworkSearchServer.Server
{
    
    public class HttpServer
    {
        private readonly int port;
        private readonly int workerCount;

        private readonly HttpListener listener;
        private readonly RequestQueue queue;
        private readonly List<Thread> workers;
        private Thread acceptorThread;

        private readonly TtlCache cache;
        private readonly ArtworkService artworkService;
        private readonly Logger logger;

        public HttpServer(int port, int workerCount, TtlCache cache, ArtworkService artworkService, Logger logger)
        {
            this.port = port;
            this.workerCount = workerCount;
            this.cache = cache;
            this.artworkService = artworkService;
            this.logger = logger;

            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            queue = new RequestQueue();
            workers = new List<Thread>();
        }

        public void Start()
        {
            listener.Start();

            for (int i = 0; i < workerCount; i++)
            {
                Thread worker = new Thread(WorkerLoop);
                worker.Name = $"Worker-{i + 1}";
                workers.Add(worker);
                worker.Start();
            }

            acceptorThread = new Thread(AcceptorLoop);
            acceptorThread.Name = "Acceptor";
            acceptorThread.Start();

            logger.Log($"Server pokrenut na http://localhost:{port}/ sa {workerCount} radnickih niti.");
        }

        private void AcceptorLoop()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext(); 
                }
                catch (Exception)
                {
                    break; 
                }

                int queued = queue.Enqueue(context);
                logger.Log($"Primljen zahtev {context.Request.HttpMethod} {context.Request.Url.PathAndQuery} -> u redu: {queued}");
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                HttpListenerContext context = queue.Dequeue(); //blokira dok nema zahteva

                if (context == null)
                {
                    break; 
                }

                HandleRequest(context);
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "GET")
                {
                    string msg = artworkService.BuildMessagePage("Nedozvoljena metoda", "Server prihvata samo GET zahteve.");
                    WriteResponse(context, 405, msg);
                    return;
                }

                string path = context.Request.Url.AbsolutePath;

                if (path == "/")
                {
                    WriteResponse(context, 200, artworkService.BuildHomePage());
                    return;
                }

                if (path != "/search")
                {
                    string msg = artworkService.BuildMessagePage("Nepostojeća putanja", $"Putanja '{path}' ne postoji.");
                    WriteResponse(context, 404, msg);
                    return;
                }

                HandleSearch(context);
            }
            catch (Exception ex)
            {
                logger.Log($"GRESKA u obradi zahteva: {ex.Message}");
                try
                {
                    string msg = artworkService.BuildMessagePage("Greška servera", "Došlo je do greške pri obradi zahteva. Pokušaj ponovo.");
                    WriteResponse(context, 500, msg);
                }
                catch (Exception)
                {
                    context.Response.Abort();
                }
            }
        }
    
        private void HandleSearch(HttpListenerContext context)
        {
            string q = context.Request.QueryString["q"];
            string artist = context.Request.QueryString["artist"];

            string mode;
            string term;

            if (!string.IsNullOrWhiteSpace(artist))
            {
                mode = "artist";
                term = artist.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(q))
            {
                mode = "q";
                term = q.Trim();
            }
            else
            {
                string msg = artworkService.BuildMessagePage("Nedostaje parametar", "Zadaj parametar pretrage: /search?q=pojam ili /search?artist=ime.");
                WriteResponse(context, 400, msg);
                return;
            }

            string key = mode + ":" + term.ToLowerInvariant();
            string apiUrl = artworkService.BuildApiUrl(mode, term);

            string json = cache.GetOrAdd(key, () => artworkService.Fetch(apiUrl));

            JObject root = JObject.Parse(json);
            List<JToken> artworks = artworkService.GetRelevantArtworks(root);

            if (artworks.Count == 0)
            {
                string msg = artworkService.BuildMessagePage("Nema rezultata", $"Za zadatu pretragu ('{term}') nije pronađeno nijedno delo.");
                WriteResponse(context, 404, msg);
                return;
            }

            WriteResponse(context, 200, artworkService.BuildResultsPage(artworks, mode, term));
        }

        private void WriteResponse(HttpListenerContext context, int statusCode, string html)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }

        public void Stop()
        {
            logger.Log("Gasenje servera...");

            listener.Stop();        
            acceptorThread.Join();  

            queue.Close(); //budimo radnicke niti da zavrse preostale zahteve i izadju
            foreach (Thread worker in workers)
            {
                worker.Join();
            }

            listener.Close();
            logger.Log("Server ugasen.");
        }
    }
}