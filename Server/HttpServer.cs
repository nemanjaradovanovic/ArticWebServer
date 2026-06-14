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
            else if (path == "/stamp-test")                  
            {
                SendStampTestPage(context);                   
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

            // Opcioni parametar 'limit' - koliko rezultata vratiti. Default 10.
            int limit = 10;
            string? limitParam = queryParams["limit"];
            if (!string.IsNullOrWhiteSpace(limitParam))
            {
                if (int.TryParse(limitParam, out int parsedLimit))
                {
                    limit = Math.Clamp(parsedLimit, 1, 100);
                }
                else
                {
                    SendHtmlResponse(context, 400,
                        ArticSearchService.BuildErrorHtml($"Parametar 'limit' mora biti broj. Dobijeno: <b>{limitParam}</b>"));
                    return;
                }
            }

            // Opcioni 'delay' (ms) - SAMO za demonstraciju stampede. Default 0.
            int delayMs = 0;
            string? delayParam = queryParams["delay"];
            if (!string.IsNullOrWhiteSpace(delayParam) && int.TryParse(delayParam, out int parsedDelay))
            {
                delayMs = Math.Clamp(parsedDelay, 0, 10000);
            }

            // Keš ključ mora da uključi i limit - različit limit = različit rezultat.
            string cacheKey = isAuthorSearch ? $"author:{q}:{limit}" : $"fulltext:{q}:{limit}";
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
                string jsonResult = _searchService.Search(q, isAuthorSearch, limit, delayMs);
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
            string html = BuildHomeHtml("Web Server (klasicne niti + Monitor)");
            SendHtmlResponse(context, 200, html);
        }
        private string BuildHomeHtml(string subtitle)
        {
            return $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>ARTIC Web Server</title>
<style>
body{{font-family:Arial,sans-serif;margin:0;background:#f5f5f5;color:#222;}}
.wrap{{max-width:820px;margin:0 auto;padding:30px 20px 60px;}}
h1{{color:#8B0000;margin-bottom:4px;}}
.sub{{color:#777;margin-top:0;font-style:italic;}}
h2{{color:#8B0000;margin-top:34px;border-bottom:2px solid #eee;padding-bottom:6px;}}
.card{{background:white;padding:16px 20px;margin:12px 0;border-radius:8px;box-shadow:0 2px 4px rgba(0,0,0,0.08);}}
a{{color:#8B0000;text-decoration:none;}}
a:hover{{text-decoration:underline;}}
code{{background:#eee;padding:2px 6px;border-radius:4px;font-size:14px;}}
table{{border-collapse:collapse;width:100%;background:white;border-radius:8px;overflow:hidden;box-shadow:0 2px 4px rgba(0,0,0,0.08);}}
th,td{{text-align:left;padding:10px 14px;border-bottom:1px solid #eee;font-size:14px;vertical-align:top;}}
th{{background:#8B0000;color:white;}}
.ex{{display:block;margin:6px 0;}}
.note{{background:#fff8e6;border-left:4px solid #e0a800;padding:10px 14px;border-radius:4px;font-size:14px;}}
.btn{{display:inline-block;background:#8B0000;color:white;padding:10px 18px;border-radius:8px;margin-top:6px;}}
ul{{font-size:14px;}} li{{margin:4px 0;}}
</style></head><body><div class='wrap'>

<h1>Art Institute of Chicago - Web Server</h1>
<p class='sub'>{subtitle}</p>

<div class='card'>
Ovaj server pretrazuje kolekciju umetnickih dela muzeja
<b>Art Institute of Chicago</b> preko njihovog javnog API-ja. Posaljes pojam za
pretragu, a server vrati listu dela koja mu odgovaraju, formatiranu kao web stranica.
Svi zahtevi se salju <b>GET</b> metodom.
</div>

<h2>Kako se pretražuje</h2>
<div class='card'>
Osnovni oblik poziva je:
<p><code>http://localhost:8080/search?q=POJAM</code></p>
Gde je <code>POJAM</code> ono što tražite (npr. <code>cats</code>, <code>monet</code>,
<code>samurai</code>). Može se pretražiti <b>bilo šta</b>. Primeri iznad su samo predlozi.
Razmak u pojmu se pise kao <code>+</code> (npr. <code>q=claude+monet</code>).
</div>

<h2>Parametri</h2>
<table>
<tr><th>Parametar</th><th>Obavezan?</th><th>Sta radi</th></tr>
<tr><td><code>q</code></td><td>Da</td>
    <td>Pojam za pretragu. Bez njega server vraća gresku 400.</td></tr>
<tr><td><code>author</code></td><td>Ne</td>
    <td>Ako stavite <code>author=true</code>, pretraga ide po <b>autoru</b> (imenu umetnika).
        Bez njega je podrazumevana <b>full-text</b> pretraga (trazi pojam svuda - naslov, opis, autor...).</td></tr>
<tr><td><code>limit</code></td><td>Ne</td>
    <td>Koliko rezultata vratiti (1-100). Podrazumevano <b>10</b>. Npr. <code>limit=25</code>.</td></tr>
<tr><td><code>delay</code></td><td>Ne</td>
    <td>Vestacko kasnjenje u milisekundama, <b>samo za demonstraciju</b>.
        U običnoj pretrazi se ne koristi.</td></tr>
</table>

<h2>Primeri (klikabilni)</h2>
<div class='card'>
<a class='ex' href='/search?q=cats'>&#9656; Full-text: <code>/search?q=cats</code></a>
<a class='ex' href='/search?q=monet'>&#9656; Full-text: <code>/search?q=monet</code></a>
<a class='ex' href='/search?q=claude+monet&author=true'>&#9656; Po autoru: <code>/search?q=claude+monet&author=true</code></a>
<a class='ex' href='/search?q=picasso&author=true'>&#9656; Po autoru: <code>/search?q=picasso&author=true</code></a>
<a class='ex' href='/search?q=japanese+print&limit=20'>&#9656; Sa limitom: <code>/search?q=japanese+print&limit=20</code></a>
<a class='ex' href='/search?q=qwertzuiop123'>&#9656; Bez rezultata (greska 404): <code>/search?q=qwertzuiop123</code></a>
</div>

<div class='note'>
<b>Šta ako dela ne postoje?</b> Ako za Vaš pojam nema rezultata, server vraca
poruku o gresci (404). Neće pasti niti se zaglaviti. 
</div>

<h2>Test: zastita od cache stampede</h2>
<div class='card'>
Posebna stranica koja šalje vise istovremenih zahteva za isti pojam i pokazuje da
server tada ode na API <b>samo jednom</b>, dok ostali sačekaju isti rezultat.
<br><a class='btn' href='/stamp-test'>Otvori stampede test &rarr;</a>
</div>



</div></body></html>";
        }


        // Test stranica za demonstraciju cache stampede zaštite (sinhroni projekat).
        // Ispaljuje N paralelnih zahteva za ISTI pojam i prikazuje rezime.
        // Dokaz "obrada izvršena jednom" se čita iz KONZOLE servera (1x MISS, ostalo WAIT pa HIT).
        private void SendStampTestPage(HttpListenerContext context)
        {
            string html = @"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>Cache Stampede Test</title>
<style>
body{font-family:Arial,sans-serif;margin:40px;background:#f5f5f5;}
h1{color:#8B0000;}
button{background:#8B0000;color:white;border:none;padding:12px 20px;border-radius:8px;font-size:16px;cursor:pointer;}
button:disabled{background:#999;cursor:not-allowed;}
.box{background:white;padding:20px;margin:15px 0;border-radius:8px;box-shadow:0 2px 4px rgba(0,0,0,0.1);}
.ok{color:#0a7d28;font-weight:bold;}
.row{margin:6px 0;}
input{padding:8px;font-size:15px;}
code{background:#eee;padding:2px 6px;border-radius:4px;}
</style></head><body>
<h1>Cache Stampede - test (sinhroni projekat)</h1>
<div class='box'>
  <div class='row'>Pojam za pretragu:
    <input type='text' id='term' value='monet' style='width:200px'></div>
  <div class='row'>Broj istovremenih zahteva:
    <input type='number' id='count' value='6' min='2' max='50' style='width:80px'></div>
  <div class='row'><button id='btn' onclick='runTest()'>Pokreni test</button></div>
</div>
<div class='box' id='result'>Klikni dugme da pokrenes test.</div>
<script>
function runTest() {
  var btn = document.getElementById('btn');
  var res = document.getElementById('result');
  var n = parseInt(document.getElementById('count').value) || 6;
  var q = (document.getElementById('term').value || 'monet').trim();
  btn.disabled = true;
  res.innerHTML = 'Saljem ' + n + ' istovremenih zahteva za pojam <code>' + q + '</code> ...';

  var t0 = performance.now();
  var calls = [];
  for (var i = 0; i < n; i++) {
    calls.push(fetch('/search?q=' + encodeURIComponent(q) + '&_n=' + i + '&delay=3000', { cache: 'no-store' })
      .then(function(r){ return r.status; }));
  }
  Promise.all(calls).then(function(statuses){
    var t1 = performance.now();
    var ok = statuses.filter(function(s){ return s === 200; }).length;
    var sec = ((t1 - t0) / 1000).toFixed(2);
    res.innerHTML =
      '<div class=""row"">Pojam: <code>' + q + '</code></div>' +
      '<div class=""row"">Poslato istovremeno: <b>' + n + '</b> zahteva</div>' +
      '<div class=""row"">Vreme: <b>' + sec + ' s</b></div>' +
      '<div class=""row ok"">' + ok + '/' + n + ' zahteva vratilo status 200</div>' +
      '<div class=""row"">&rarr; Pogledaj konzolu servera: <b>1x MISS</b>, ostalo <b>WAIT</b> pa <b>HIT</b>.</div>';
    btn.disabled = false;
  });
}
</script>
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