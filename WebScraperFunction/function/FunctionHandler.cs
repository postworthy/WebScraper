using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Function
{
    public class FunctionHandler
    {
        private const string YOUTUBE_EMBED = "https://www.youtube.com/embed/";

        public async Task<(int, string)> Handle(HttpRequest request)
        {
            var reader = new StreamReader(request.Body);
            var input = await reader.ReadToEndAsync();

            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return (200, await ScrapeContent(uri));
            else
                return (404, $"Not Found!");
        }


        public static async Task<string> ScrapeContent(Uri uri)
        {
            var htmlUri = await uri.HtmlContentUri();

            string title = null;
            string description = null;
            Uri image = null;
            Uri video = null;

            if (htmlUri != null)
            {
                uri = htmlUri;
                var doc = new HtmlAgilityPack.HtmlDocument();
                try
                {
                    var req = uri.GetWebRequest();
                    using (var resp = await req.GetResponseAsync())
                    {
                        using (var reader = new StreamReader(resp.GetResponseStream(), true))
                        {
                            doc.Load(reader);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ex = ex;
                }
                if (doc.DocumentNode != null)
                {
                    var nodes = doc.DocumentNode.SelectNodes("//title");
                    if (nodes != null && nodes.Count > 0)
                    {
                        title = nodes.First().InnerText.Trim();
                    }

                    nodes = doc.DocumentNode.SelectNodes("//link");
                    if (nodes != null && nodes.Count > 0)
                    {
                        var ogMeta = nodes
                            .Where(m => m.Attributes.SingleOrDefault(a => a.Name.ToLower() == "rel" && a.Value.ToLower().StartsWith("image_src")) != null)
                            .Select(m =>
                            new
                            {
                                Property = m.Attributes["rel"].Value.ToLower(),
                                Content = m.Attributes["href"].Value
                            });
                        if (ogMeta != null && ogMeta.Count() > 0)
                        {
                            image = ogMeta.Where(x => x.Property == "image_src").Select(x => CreateUriSafely(htmlUri, x.Content)).FirstOrDefault();
                        }
                    }

                    nodes = doc.DocumentNode.SelectNodes("//meta");
                    if (nodes != null && nodes.Count > 0)
                    {
                        var ogMeta = nodes
                            .Where(m => m.Attributes.SingleOrDefault(a => a.Name.ToLower() == "property" && a.Value.ToLower().StartsWith("og:")) != null)
                            .Select(m =>
                            new
                            {
                                Property = m.Attributes["property"].Value.ToLower(),
                                Content = m.Attributes["content"] != null ? m.Attributes["content"].Value : (m.Attributes["value"] != null ? m.Attributes["value"].Value : "")
                            });
                        if (ogMeta != null && ogMeta.Count() > 0)
                        {
                            title = (ogMeta.Where(x => x.Property == "og:title" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? title ?? "").Trim();
                            description = ogMeta.Where(x => x.Property == "og:description" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? description ?? "";
                            image = ogMeta.Where(x => x.Property == "og:image" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(htmlUri, x.Content)).FirstOrDefault();
                            video = ogMeta.Where(x => x.Property == "og:video" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(htmlUri, x.Content)).FirstOrDefault();
                            video = CleanYouTube(video);
                        }

                        var twitterMeta = nodes
                            .Where(m => m.Attributes.SingleOrDefault(a => a.Name.ToLower() == "property" && a.Value.ToLower().StartsWith("twitter:")) != null)
                            .Select(m =>
                            new
                            {
                                Property = m.Attributes["property"].Value.ToLower(),
                                Content = m.Attributes["content"] != null ? m.Attributes["content"].Value : (m.Attributes["value"] != null ? m.Attributes["value"].Value : "")
                            });
                        if (twitterMeta != null && twitterMeta.Count() > 0)
                        {
                            if (string.IsNullOrEmpty(title))
                                title = (twitterMeta.Where(x => x.Property == "twitter:title" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? title ?? "").Trim();
                            if (string.IsNullOrEmpty(description))
                                description = twitterMeta.Where(x => x.Property == "twitter:description" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? description ?? "";
                            if (image == null)
                                image = twitterMeta.Where(x => x.Property == "twitter:image" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(htmlUri, x.Content)).FirstOrDefault();
                            if (video == null)
                            {
                                video = twitterMeta.Where(x => x.Property == "twitter:player" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(htmlUri, x.Content)).FirstOrDefault();
                                video = CleanYouTube(video);
                            }
                        }
                    }

                    if (video == null)
                    {
                        nodes = doc.DocumentNode.SelectNodes("//iframe");
                        if (nodes != null && nodes.Count > 0)
                        {
                            var iframes = nodes
                            .Where(i => i.Attributes["src"] != null && i.Attributes["src"].Value.ToLower().StartsWith(YOUTUBE_EMBED))
                            .Select(i => i.Attributes["src"].Value);

                            if (iframes.Count() > 0)
                                video = new Uri(iframes.FirstOrDefault());
                        }
                    }
                }
            }
            else
            {
                var imgUri = await uri.ImageContentUri();
                if (imgUri != null)
                {
                    uri = imgUri;
                    image = imgUri;
                    title = uri.ToString();
                }
            }

            return JsonConvert.SerializeObject(new
            {
                Link = uri,
                Title = title,
                Description = description,
                Image = image,
                Video = video
            });
        }

        private static Uri CreateUriSafely(Uri uri, string content)
        {
            var baseUri = uri.GetLeftPart(UriPartial.Authority);
            var dirUri = string.Join("/", baseUri.Split(new string[] { "/" }, StringSplitOptions.RemoveEmptyEntries).Reverse().Skip(1).Reverse());

            return content.StartsWith("http") ?
                new Uri(content.Trim()) :
                content.StartsWith("/") ?
                    new Uri(baseUri + content.Trim()) :
                    //This may look a bit strange but the split join above could leave you with google.com if the full url is http(s)://google.com
                    content.StartsWith("../") && dirUri.Contains("://") ?
                        new Uri(dirUri + content.Trim().Replace("..", "")) :
                        null;
        }

        private static Uri CleanYouTube(Uri Video)
        {
            if (Video != null)
            {
                string uri = Video.ToString().ToLower();
                if (uri.Contains("youtube.com") && !uri.Contains(YOUTUBE_EMBED))
                {
                    string code = Video.ToString().Split(new string[] { "/v/" }, StringSplitOptions.RemoveEmptyEntries)[1].Split('?')[0];
                    return new Uri(YOUTUBE_EMBED + code);
                }
            }
            return Video;
        }
    }
}