using System.Net;
using System.Web;
using ArticWebServer.Services;
using ArticWebServer.Utils;

namespace ArticWebServer.Server
{
    // HTTP server koji prima zahteve klijenata i obrađuje ih konkurentno
    // Prijem zahteva: HttpListener
    // Obrada zahteva: ThreadPool niti (kontrolisan broj paralelnih obrada)
    // Sinhronizacija: lock/Monitor za red čekanja i keš
    public class HttpServer
    {
        private readonly HttpListener _listener;
        private readonly CacheService _cacheService;
        private readonly ArticSearchService _searchService;

        // Red čekanja za pristigle zahteve (deljeni resurs između niti)
        private readonly Queue<HttpListenerContext> _requestQueue = new Queue<HttpListenerContext>();
        private readonly object _queueLock = new object();

        // Maksimalan broj niti koje istovremeno obrađuju zahteve
        private readonly int _maxWorkers;

        // Trenutni broj aktivnih radnih niti
        private int _activeWorkers = 0;
        private readonly object _workerLock = new object();

        // Signal za gašenje servera
        private volatile bool _isRunning = false;

        public HttpServer(string prefix, int maxWorkers = 4, int cacheTtlSeconds = 60)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _maxWorkers = maxWorkers;
            _cacheService = new CacheService(cacheTtlSeconds);
            _searchService = new ArticSearchService();
        }

        public void Start()
        {
            _listener.Start();
            _isRunning = true;

            Logger.Log($"Server pokrenut. Sluša na: {string.Join(", ", _listener.Prefixes)}");
            Logger.Log($"Maksimalan broj radnih niti: {_maxWorkers}");
            Logger.Log($"Primeri poziva:");
            Logger.Log($"  http://localhost:8080/search?q=cats");
            Logger.Log($"  http://localhost:8080/search?q=monet&author=true");

            // Pokretanje radnih niti iz ThreadPool-a koje obrađuju red zahteva
            for (int i = 0; i < _maxWorkers; i++)
            {
                ThreadPool.QueueUserWorkItem(WorkerLoop);
            }

            // Glavna nit prima zahteve i stavlja ih u red
            AcceptRequests();
        }

        // Blokira i prihvata dolazne HTTP zahteve, stavlja ih u red čekanja
        private void AcceptRequests()
        {
            while (_isRunning)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    Logger.Log($"Primljen zahtev: {context.Request.Url}");

                    // Smeštamo zahtev u deljeni red i budimo radnu nit
                    lock (_queueLock)
                    {
                        _requestQueue.Enqueue(context);
                        Monitor.Pulse(_queueLock); // Buđenje jedne radne niti
                    }
                }
                catch (HttpListenerException)
                {
                    // Server je zatvoren
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Greška pri primanju zahteva: {ex.Message}");
                }
            }
        }

        // Radna nit koja uzima zahteve iz reda i obrađuje ih
        // Blokira se na Monitor.Wait kada je red prazan
        private void WorkerLoop(object? state)
        {
            Logger.Log("Radna nit pokrenuta i čeka na zahteve...");

            while (_isRunning)
            {
                HttpListenerContext? context = null;

                // Kritična sekcija: uzimanje zahteva iz dela reda
                lock (_queueLock)
                {
                    while (_requestQueue.Count == 0 && _isRunning)
                    {
                        // Red je prazan - blokiramo se i čekamo na Pulse
                        Monitor.Wait(_queueLock);
                    }

                    if (_requestQueue.Count > 0)
                    {
                        context = _requestQueue.Dequeue();
                    }
                }

                if (context != null)
                {
                    // Pratimo broj aktivnih radnika
                    lock (_workerLock)
                    {
                        _activeWorkers++;
                        Logger.Log($"Aktivnih radnih niti: {_activeWorkers}/{_maxWorkers}");
                    }

                    try
                    {
                        ProcessRequest(context);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Greška pri obradi zahteva: {ex.Message}");
                        try
                        {
                            SendErrorResponse(context, "Interna greška servera.");
                        }
                        catch { }
                    }
                    finally
                    {
                        lock (_workerLock)
                        {
                            _activeWorkers--;
                        }
                    }
                }
            }
        }

        // Obrada jednog HTTP zahteva
        private void ProcessRequest(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string? rawQuery = context.Request.Url?.Query;

            Logger.Log($"Obrada zahteva: {path}{rawQuery}");

            if (path == "/search")
            {
                HandleSearchRequest(context);
            }
            else if (path == "/")
            {
                SendHomeResponse(context);
            }
            else
            {
                SendErrorResponse(context, $"Putanja '{path}' nije podržana. Koristite /search?q=...");
            }
        }

        // Rukovanje pretragom: proverava keš, po potrebi poziva API
        private void HandleSearchRequest(HttpListenerContext context)
        {
            var queryParams = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
            string? q = queryParams["q"];
            string? authorParam = queryParams["author"];
            bool isAuthorSearch = authorParam == "true";

            if (string.IsNullOrWhiteSpace(q))
            {
                SendHtmlResponse(context, 400,
                    ArticSearchService.BuildErrorHtml("Parametar 'q' je obavezan. Primer: /search?q=cats"));
                return;
            }

            // Ključ keša uključuje tip pretrage (full-text vs autor)
            string cacheKey = isAuthorSearch ? $"author:{q}" : $"fulltext:{q}";
            Logger.Log($"Upit za pretragu: '{q}', tip: {(isAuthorSearch ? "autor" : "full-text")}, kes ključ: '{cacheKey}'");

            string? cachedResult = null;
            bool fetchedFromApi = false;

            try
            {
                // GetOrLock: ako je u kešu - vraća odmah,
                // ako drugi thread obrađuje isti upit - blokira se dok ne dobije rezultat,
                // ako nije ni u kešu ni u obradi - vraća null i mi preuzimamo odgovornost
                cachedResult = _cacheService.GetOrLock(cacheKey);

                if (cachedResult != null)
                {
                    // Keš pogodak - vraćamo keširan odgovor
                    Logger.Log($"Zahtev za '{q}' opslužen iz keša");
                    string cachedHtml = ArticSearchService.FormatAsHtml(cachedResult, q);
                    SendHtmlResponse(context, 200, cachedHtml);
                    return;
                }

                // Keš promašaj - pozivamo API
                Logger.Log($"Dohvatanje podataka za '{q}' sa ARTIC API-ja...");
                string jsonResult = _searchService.Search(q, isAuthorSearch);
                fetchedFromApi = true;

                // Proveravamo da li su pronađeni rezultati
                var parsed = Newtonsoft.Json.Linq.JObject.Parse(jsonResult);
                var data = parsed["data"];

                if (data == null || !data.HasValues)
                {
                    // Nema rezultata - šaljemo grešku klijentu, ne kešujemo
                    _cacheService.ReleaseLock(cacheKey);
                    SendHtmlResponse(context, 404,
                        ArticSearchService.BuildErrorHtml($"Nisu pronađena umetnička dela za upit: <b>{q}</b>"));
                    return;
                }

                // Upisujemo u keš i obaveštavamo čekajuće niti
                _cacheService.Set(cacheKey, jsonResult);

                string html = ArticSearchService.FormatAsHtml(jsonResult, q);
                Logger.Log($"Zahtev za '{q}' uspešno obrađen, rezultat dodat u keš. Keš veličina: {_cacheService.GetValidCount()}");
                SendHtmlResponse(context, 200, html);
            }
            catch (HttpRequestException ex)
            {
                if (fetchedFromApi == false)
                    _cacheService.ReleaseLock(cacheKey);

                Logger.LogError($"Greška pri pozivanju ARTIC API-ja za '{q}': {ex.Message}");
                SendHtmlResponse(context, 502,
                    ArticSearchService.BuildErrorHtml($"Greška pri komunikaciji sa Art Institute of Chicago API-jem: {ex.Message}"));
            }
            catch (Exception ex)
            {
                _cacheService.ReleaseLock(cacheKey);
                Logger.LogError($"Neočekivana greška za '{q}': {ex.Message}");
                SendHtmlResponse(context, 500,
                    ArticSearchService.BuildErrorHtml($"Interna greška servera: {ex.Message}"));
            }
        }

        // Šalje HTML odgovor klijentu
        private void SendHtmlResponse(HttpListenerContext context, int statusCode, string html)
        {
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }

        private void SendErrorResponse(HttpListenerContext context, string message)
        {
            SendHtmlResponse(context, 500, ArticSearchService.BuildErrorHtml(message));
        }

        // Početna stranica servera
        private void SendHomeResponse(HttpListenerContext context)
        {
            string html = @"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>ARTIC Web Server</title>
<style>
body{font-family:Arial,sans-serif;margin:40px;background:#f5f5f5;}
h1{color:#8B0000;}
.example{background:white;padding:15px;margin:8px 0;border-radius:8px;box-shadow:0 2px 4px rgba(0,0,0,0.1);}
a{color:#8B0000;}
</style></head><body>
<h1>Art Institute of Chicago - Web Server</h1>
<p>Koristite <b>/search</b> endpoint za pretragu umetničkih dela.</p>
<h3>Primeri pretrage:</h3>
<div class='example'><a href='/search?q=cats'>Full-text pretraga: /search?q=cats</a></div>
<div class='example'><a href='/search?q=monet'>Full-text pretraga: /search?q=monet</a></div>
<div class='example'><a href='/search?q=claude+monet&author=true'>Pretraga po autoru: /search?q=claude+monet&author=true</a></div>
<div class='example'><a href='/search?q=picasso&author=true'>Pretraga po autoru: /search?q=picasso&author=true</a></div>
<h3>Parametri:</h3>
<ul>
<li><b>q</b> - tekst za pretragu (obavezan)</li>
<li><b>author=true</b> - pretraga po autoru (opciono, podrazumevano je full-text)</li>
</ul>
</body></html>";
            SendHtmlResponse(context, 200, html);
        }

        public void Stop()
        {
            _isRunning = false;
            _listener.Stop();

            // Budimo sve radne niti koje čekaju da bi mogle da završe
            lock (_queueLock)
            {
                Monitor.PulseAll(_queueLock);
            }

            Logger.Log("Server zaustavljen.");
        }
    }
}