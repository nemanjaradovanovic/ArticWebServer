using ArticWebServer.Utils;
using Newtonsoft.Json.Linq;

namespace ArticWebServer.Services
{
    // Servis za komunikaciju sa Art Institute of Chicago API-jem
    public class ArticSearchService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl = "https://api.artic.edu/api/v1/artworks/search";

        public ArticSearchService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add(
                "User-Agent",
                "ArticWebServer/1.0 (sistemsko programiranje projekat)"
            );
        }

        // Šalje zahtev ka Art Institute of Chicago API-ju
        // q      - tekst za pretragu (full-text ili autor)
        // author - ako je true, pretraga se vrši po polju artist_display
        public string Search(string q, bool author = false, int limit = 10, int delayMs = 0)
        {
            if (delayMs > 0)
            {
                Logger.Log($"[DEMO] Vestacko kasnjenje {delayMs}ms pre dohvatanja (za stampede test)");
                System.Threading.Thread.Sleep(delayMs);
            }

            string fields = "id,title,artist_display,date_display,medium_display,place_of_origin";
            string url;

            if (author)
            {
                url = $"{_baseUrl}?q={Uri.EscapeDataString(q)}" +
                      $"&query[match][artist_display]={Uri.EscapeDataString(q)}" +
                      $"&fields={fields}" +
                      $"&limit={limit}";
            }
            else
            {
                url = $"{_baseUrl}?q={Uri.EscapeDataString(q)}" +
                      $"&fields={fields}" +
                      $"&limit={limit}";
            }

            Logger.Log($"Slanje zahteva ka ARTIC API-ju: {url}");

            HttpResponseMessage response = _httpClient.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();

            string responseBody = response.Content.ReadAsStringAsync().Result;
            return responseBody;
        }

        // Formatira JSON odgovor u HTML koji se vraća klijentu
        public static string FormatAsHtml(string jsonResponse, string query)
        {
            try
            {
                JObject parsed = JObject.Parse(jsonResponse);
                var data = parsed["data"];

                if (data == null || !data.HasValues)
                {
                    return BuildErrorHtml($"Nisu pronađena umetnička dela za upit: <b>{query}</b>");
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine("<html><head><meta charset='utf-8'>");
                sb.AppendLine("<title>Art Institute of Chicago - Rezultati pretrage</title>");
                sb.AppendLine("<style>");
                sb.AppendLine("body { font-family: Arial, sans-serif; margin: 40px; background: #f5f5f5; }");
                sb.AppendLine("h1 { color: #8B0000; }");
                sb.AppendLine(".artwork { background: white; padding: 20px; margin: 10px 0; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
                sb.AppendLine(".artwork h3 { margin: 0 0 8px 0; color: #333; }");
                sb.AppendLine(".artwork p { margin: 4px 0; color: #666; font-size: 14px; }");
                sb.AppendLine(".count { color: #8B0000; font-weight: bold; }");
                sb.AppendLine("</style></head><body>");
                sb.AppendLine($"<h1>Art Institute of Chicago</h1>");
                sb.AppendLine($"<h2>Rezultati pretrage za: <i>{query}</i></h2>");

                int count = data.Count();
                sb.AppendLine($"<p class='count'>Pronađeno {count} umetničkih dela:</p>");

                foreach (var artwork in data)
                {
                    string title = artwork["title"]?.ToString() ?? "Bez naslova";
                    string artist = artwork["artist_display"]?.ToString() ?? "Nepoznat autor";
                    string date = artwork["date_display"]?.ToString() ?? "";
                    string medium = artwork["medium_display"]?.ToString() ?? "";
                    string place = artwork["place_of_origin"]?.ToString() ?? "";

                    sb.AppendLine("<div class='artwork'>");
                    sb.AppendLine($"  <h3>{title}</h3>");
                    sb.AppendLine($"  <p><b>Autor:</b> {artist}</p>");
                    if (!string.IsNullOrEmpty(date))
                        sb.AppendLine($"  <p><b>Datum:</b> {date}</p>");
                    if (!string.IsNullOrEmpty(medium))
                        sb.AppendLine($"  <p><b>Tehnika:</b> {medium}</p>");
                    if (!string.IsNullOrEmpty(place))
                        sb.AppendLine($"  <p><b>Poreklo:</b> {place}</p>");
                    sb.AppendLine("</div>");
                }

                sb.AppendLine("</body></html>");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return BuildErrorHtml($"Greška pri obradi odgovora: {ex.Message}");
            }
        }

        public static string BuildErrorHtml(string message)
        {
            return $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>Greška</title>
<style>body{{font-family:Arial,sans-serif;margin:40px;}} .error{{color:#8B0000;background:#fff0f0;padding:20px;border-radius:8px;}}</style>
</head><body>
<h1>Art Institute of Chicago - Greška</h1>
<div class='error'><p>{message}</p></div>
<p><a href='/'>Nazad na početak</a></p>
</body></html>";
        }
    }
}