using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using ArtworkSearchServer.Logging;

namespace ArtworkSearchServer.Services
{
    public class ArtworkService
    {
        private const string BaseUrl = "https://api.artic.edu/api/v1/artworks/search";
        private const string Fields = "id,title,artist_display,date_display";

        private const double MinScore = 20;

        private readonly HttpClient client;
        private readonly Logger logger;
        private readonly int apiDelayMs; // vestacko kasnjenje, samo za demonstraciju stampede-a

        public ArtworkService(HttpClient client, Logger logger, int apiDelayMs)
        {
            this.client = client;
            this.logger = logger;
            this.apiDelayMs = apiDelayMs;
        }

        public string BuildApiUrl(string mode, string term)
        {
            string encoded = Uri.EscapeDataString(term);

            if (mode == "artist")
            {
                return $"{BaseUrl}?query[match][artist_title]={encoded}&fields={Fields}&limit=20";
            }

            return $"{BaseUrl}?q={encoded}&fields={Fields}&limit=20";
        }

        public string Fetch(string url)
        {
            logger.Log($"API poziv ka: {url}");
            HttpResponseMessage response = client.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            string body = response.Content.ReadAsStringAsync().Result;

            if (apiDelayMs > 0)
            {
                Thread.Sleep(apiDelayMs);
            }

            return body;
        }

        public List<JToken> GetRelevantArtworks(JObject root)
        {
            List<JToken> relevant = new List<JToken>();
            JArray items = root["data"] as JArray;

            if (items == null)
            {
                return relevant;
            }

            foreach (JToken item in items)
            {
                double score = 0;
                if (item["_score"] != null)
                {
                    score = (double)item["_score"];
                }

                if (score >= MinScore)
                {
                    relevant.Add(item);
                }
            }

            logger.Log($"Filtriranje: ukupno {items.Count} dela, relevantnih: {relevant.Count}");
            return relevant;
        }

        public string BuildResultsPage(List<JToken> artworks, string mode, string term)
        {
            string modeLabel;
            if (mode == "artist")
            {
                modeLabel = "autoru";
            }
            else
            {
                modeLabel = "pojmu";
            }

            StringBuilder body = new StringBuilder();
            body.Append("<h1>Rezultati pretrage</h1>");
            body.Append($"<p>Pretraga po {modeLabel}: <b>{WebUtility.HtmlEncode(term)}</b> - pronađeno {artworks.Count} dela.</p>");
            body.Append("<p><a href=\"/\">Nova pretraga</a></p>");
            body.Append("<ul>");

            foreach (JToken item in artworks)
            {
                string title = (string)item["title"];
                string artist = (string)item["artist_display"];
                string date = (string)item["date_display"];

                if (title == null)
                {
                    title = "(bez naslova)";
                }

                body.Append("<li>");
                body.Append($"<b>{WebUtility.HtmlEncode(title)}</b><br/>");

                if (artist != null)
                {
                    body.Append($"{WebUtility.HtmlEncode(artist)}<br/>");
                }

                if (date != null)
                {
                    body.Append($"<i>{WebUtility.HtmlEncode(date)}</i>");
                }

                body.Append("</li>");
            }

            body.Append("</ul>");
            return WrapPage("Rezultati: " + term, body.ToString());
        }

        public string BuildHomePage()
        {
            string body =
                "<h1>Pretraga umetničkih dela</h1>" +
                "<p>Art Institute of Chicago - pretraga preko GET zahteva.</p>" +
                "<p>Full Text pretraga: <code>/search?q=pojam</code>, npr. <a href=\"/search?q=cats\">/search?q=cats</a></p>" +
                "<p>Pretraga po autoru: <code>/search?artist=ime</code>, npr. <a href=\"/search?artist=monet\">/search?artist=monet</a></p>";

            return WrapPage("Pretraga umetničkih dela", body);
        }

        public string BuildMessagePage(string heading, string message)
        {
            string body =
                $"<h1>{WebUtility.HtmlEncode(heading)}</h1>" +
                $"<p>{WebUtility.HtmlEncode(message)}</p>" +
                "<p><a href=\"/\">Nazad na pretragu</a></p>";

            return WrapPage(heading, body);
        }

        private string WrapPage(string title, string body)
        {
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\" />" +
                   $"<title>{WebUtility.HtmlEncode(title)}</title></head>" +
                   $"<body style=\"font-family: Arial; max-width: 800px; margin: auto;\">{body}</body></html>";
        }
    }
}