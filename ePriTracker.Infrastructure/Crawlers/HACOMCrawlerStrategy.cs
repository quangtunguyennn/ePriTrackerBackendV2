using ePriTracker.Domain.Interfaces;
using ePriTracker.Domain.Models.Entities;
using HtmlAgilityPack;
using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ePriTracker.Infrastructure.Crawlers
{
    public class HACOMCrawlerStrategy : IEcommerceCrawlerStrategy
    {
        private const int HacomPlatformId = 5; // ID của HACOM trong bảng tracker.platforms

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            // Giữ nguyên tiếng Việt có dấu, không escape thành \uXXXX
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly HttpClient _httpClient;
        public string PlatformName => "HACOM";

        public HACOMCrawlerStrategy(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            }
            if (!_httpClient.DefaultRequestHeaders.Contains("Accept-Language"))
            {
                _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en;q=0.5");
            }
        }

        public bool CanHandle(string url)
        {
            return url.Contains("hacom.vn", StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        //  CRAWL CHI TIẾT SẢN PHẨM
        // =====================================================================
        public async Task<Product> FetchProductDataAsync(string url)
        {
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Text thuần của trang (đã bỏ script/style) để dùng cho regex fallback.
            var pageText = GetPageText(doc);
            // Phần đầu trang (trước "Thông số nổi bật") chứa khối giá + mã SP + bảo hành,
            // tránh bắt nhầm số hotline/bảo hành ở footer.
            var headText = CutBefore(pageText, "Thông số nổi bật");

            // ---- Tên ----
            string name = Clean(doc.DocumentNode.SelectSingleNode("//h1")?.InnerText)
                          ?? GetMeta(doc, "og:title")
                          ?? "Unknown Product";

            // ---- Giá ----
            decimal price = ExtractPrice(doc, headText);

            // ---- Ảnh ----
            string? imageUrl = GetMeta(doc, "og:image")
                               ?? doc.DocumentNode
                                     .SelectSingleNode("//img[contains(@src,'cdn-files.hacom.vn')]")
                                     ?.GetAttributeValue("src", "");
            if (string.IsNullOrWhiteSpace(imageUrl)) imageUrl = null;

            // ---- SKU (Mã sản phẩm của HACOM, vd: LTAC1016) ----
            string sku = Grab(headText, "Mã sản phẩm", @"[A-Za-z0-9_.\-]+") ?? SkuFromUrl(url);

            // ---- Thông số kỹ thuật + thông tin chung -> JSON ----
            var specs = ExtractSpecs(doc);

            var general = new Dictionary<string, string>();
            AddIfNotNull(general, "Mã sản phẩm", sku);
            AddIfNotNull(general, "Thương hiệu", Grab(headText, "Thương hiệu"));
            AddIfNotNull(general, "Bảo hành", Grab(headText, "Bảo hành"));
            AddIfNotNull(general, "Tình trạng", Grab(headText, "Tình trạng"));

            var allSpecs = new Dictionary<string, Dictionary<string, string>>();
            if (general.Count > 0) allSpecs["Thông tin chung"] = general;
            foreach (var kv in specs) allSpecs[kv.Key] = kv.Value;

            string? techSpecsJson = allSpecs.Count > 0
                ? JsonSerializer.Serialize(allSpecs, JsonOpts)
                : null;

            // ---- Link review YouTube (nếu có) ----
            var youtube = ExtractYoutubeLinks(doc);
            string? youtubeJson = youtube.Count > 0
                ? JsonSerializer.Serialize(youtube, JsonOpts)
                : null;

            var product = new Product
            {
                PlatformId = HacomPlatformId,
                ProductName = Truncate(name, 500),
                ProductUrl = url,
                ImageUrl = imageUrl,
                ExternalSku = Truncate(sku, 100),
                TechSpecs = techSpecsJson,
                YoutubeReviewLinks = youtubeJson,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = 1
            };

            product.PriceHistories.Add(new PriceHistory
            {
                Price = price,
                CrawledAt = DateTimeOffset.UtcNow
            });

            return product;
        }

        // =====================================================================
        //  GIÁ: meta tag -> JSON-LD -> regex trên text
        // =====================================================================
        private static decimal ExtractPrice(HtmlDocument doc, string headText)
        {
            // 1) <meta property="product:price:amount" content="22899000"> (ổn định nhất)
            if (TryParseMoney(GetMeta(doc, "product:price:amount"), out var p) && p > 0) return p;
            if (TryParseMoney(GetMeta(doc, "og:price:amount"), out p) && p > 0) return p;

            // 2) JSON-LD (schema.org Product/Offer)
            var ldNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (ldNodes != null)
            {
                foreach (var node in ldNodes)
                {
                    var found = FindPriceInJson(node.InnerText);
                    if (found > 0) return found;
                }
            }

            // 3) Regex trên khối giá của trang
            foreach (var label in new[] { "Giá mua online", "Giá khuyến mại", "Giá niêm yết" })
            {
                var m = Regex.Match(headText, Regex.Escape(label) + @"\s*:?\s*\**\s*([\d\.,]{5,})");
                if (m.Success && TryParseMoney(m.Groups[1].Value, out p) && p > 0) return p;
            }

            return 0;
        }

        private static decimal FindPriceInJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            try
            {
                using var jd = JsonDocument.Parse(json);
                return WalkJsonForPrice(jd.RootElement);
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        private static decimal WalkJsonForPrice(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                    {
                        if ((prop.Name == "price" || prop.Name == "lowPrice")
                            && TryParseMoney(prop.Value.ToString(), out var p) && p > 0)
                            return p;

                        var r = WalkJsonForPrice(prop.Value);
                        if (r > 0) return r;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        var r = WalkJsonForPrice(item);
                        if (r > 0) return r;
                    }
                    break;
            }
            return 0;
        }

        /// <summary>"22899000", "22899000.00", "22.899.000 VND" -> 22899000</summary>
        private static bool TryParseMoney(string? raw, out decimal value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();

            // Dạng số thuần có tối đa 2 chữ số thập phân (meta / JSON-LD)
            if (Regex.IsMatch(raw, @"^\d+(\.\d{1,2})?$"))
                return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

            // Dạng hiển thị có dấu chấm/phẩy ngăn cách hàng nghìn
            var digits = Regex.Replace(raw, @"[^\d]", "");
            return decimal.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        // =====================================================================
        //  BẢNG THÔNG SỐ KỸ THUẬT -> { "Nhóm": { "Tên thông số": "Giá trị" } }
        // =====================================================================
        private static Dictionary<string, Dictionary<string, string>> ExtractSpecs(HtmlDocument doc)
        {
            var result = new Dictionary<string, Dictionary<string, string>>();

            var tables = doc.DocumentNode.SelectNodes("//table");
            if (tables == null) return result;

            // Trang render bảng 2 lần (tab + khối "Xem thêm") -> lấy bảng nhiều dòng nhất
            var table = tables
                .OrderByDescending(t => t.SelectNodes(".//tr")?.Count ?? 0)
                .First();

            var rows = table.SelectNodes(".//tr");
            if (rows == null) return result;

            string group = "Khác";

            foreach (var tr in rows)
            {
                var cells = tr.SelectNodes("./th|./td");
                if (cells == null || cells.Count == 0) continue;

                string label = CellText(cells[0]);
                string value = cells.Count > 1 ? CellText(cells[1]) : string.Empty;

                // Dòng tiêu đề nhóm: chỉ có cột 1, cột 2 rỗng (vd "Bộ vi xử lý (CPU)")
                if (label.Length > 0 && value.Length == 0)
                {
                    group = label;
                    continue;
                }

                if (value.Length == 0) continue;

                // Dòng chỉ có giá trị (vd "Ổ đĩa quang" -> "Không")
                if (label.Length == 0) label = "Giá trị";

                if (!result.TryGetValue(group, out var dict))
                {
                    dict = new Dictionary<string, string>();
                    result[group] = dict;
                }
                dict.TryAdd(label, value);
            }

            return result;
        }

        private static string CellText(HtmlNode node)
        {
            var html = Regex.Replace(node.InnerHtml, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(p|li|div)>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", string.Empty);
            var text = WebUtility.HtmlDecode(html);

            var lines = text.Split('\n')
                .Select(l => Regex.Replace(l, @"[ \t\u00A0]+", " ").Trim())
                .Where(l => l.Length > 0);

            return string.Join("\n", lines);
        }

        // =====================================================================
        //  YOUTUBE
        // =====================================================================
        private static List<string> ExtractYoutubeLinks(HtmlDocument doc)
        {
            var links = new List<string>();
            var nodes = doc.DocumentNode.SelectNodes("//iframe[@src]|//a[@href]|//video/source[@src]");
            if (nodes == null) return links;

            foreach (var n in nodes)
            {
                var link = n.GetAttributeValue("src", null!) ?? n.GetAttributeValue("href", null!);
                if (string.IsNullOrWhiteSpace(link)) continue;
                link = WebUtility.HtmlDecode(link);

                bool isVideo = Regex.IsMatch(link, @"youtube\.com/(embed/|watch\?)|youtu\.be/", RegexOptions.IgnoreCase);
                if (isVideo && !links.Contains(link)) links.Add(link);
            }
            return links; // bỏ qua link kênh/channel của HACOM vì không khớp pattern trên
        }

        // =====================================================================
        //  GỢI Ý SẢN PHẨM TƯƠNG TỰ (giữ nguyên logic cũ, chỉ đồng bộ cách lấy SKU)
        // =====================================================================
        public async Task<List<ProductRelation>> CrawlSuggestionsAsync(long productId, string searchKeyword, decimal currentPrice)
        {
            var suggestions = new List<ProductRelation>();
            if (string.IsNullOrWhiteSpace(searchKeyword)) return suggestions;

            string cleanedKeyword = Uri.EscapeDataString(ExtractCoreKeyword(searchKeyword));
            string searchUrl = $"https://hacom.vn/tim?q={cleanedKeyword}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return suggestions;

                var html = await response.Content.ReadAsStringAsync();
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);

                // Selector linh hoạt hơn: Thử bắt nhiều dạng container sản phẩm của HACOM
                var productNodes = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'product-item') or contains(@class, 'p-item')]")
                                ?? htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'item')]");

                if (productNodes == null) return suggestions;

                foreach (var node in productNodes.Take(5))
                {
                    var nameNode = node.SelectSingleNode(".//h3/a") ?? node.SelectSingleNode(".//a[contains(@class, 'p-name')]");
                    var priceNode = node.SelectSingleNode(".//strong[contains(@class, 'price')]")
                                 ?? node.SelectSingleNode(".//span[contains(@class, 'p-price')]")
                                 ?? node.SelectSingleNode(".//*[contains(@class, 'price')]");

                    if (nameNode == null || priceNode == null) continue;

                    string currentName = Clean(nameNode.InnerText) ?? "";
                    if (string.IsNullOrWhiteSpace(currentName)) continue;

                    string href = nameNode.GetAttributeValue("href", "");
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    string currentLink = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? href
                        : "https://hacom.vn" + (href.StartsWith("/") ? href : "/" + href);

                    string externalSku = SkuFromUrl(currentLink);

                    if (TryParseMoney(priceNode.InnerText, out decimal parsedPrice) && parsedPrice > 0)
                    {
                        var targetProduct = new Product
                        {
                            PlatformId = HacomPlatformId,
                            ProductName = Truncate(currentName, 500),
                            ProductUrl = currentLink,
                            ExternalSku = Truncate(externalSku, 100),
                            CreatedAt = DateTimeOffset.UtcNow,
                            Status = 1
                        };
                        targetProduct.PriceHistories.Add(new PriceHistory { Price = parsedPrice, CrawledAt = DateTimeOffset.UtcNow });

                        suggestions.Add(new ProductRelation
                        {
                            SourceProductId = productId,
                            TargetProduct = targetProduct,
                            RelationType = "SUGGESTION",
                            MatchScore = CalculateMatchScore(searchKeyword, currentName, currentPrice, parsedPrice),
                            CreatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }
            }
            catch
            {
                // Log warning hoặc bỏ qua để không gián đoạn luồng cào các sàn khác
            }

            return suggestions;
        }

        // Helper tính điểm tương đồng (MatchScore) dựa trên chênh lệch giá và từ khóa
        private static decimal CalculateMatchScore(string keyword, string targetName, decimal sourcePrice, decimal targetPrice)
        {
            decimal score = 0.70m; // Điểm nền
            if (targetName.Contains(ExtractCoreKeyword(keyword), StringComparison.OrdinalIgnoreCase)) score += 0.15m;
            if (sourcePrice > 0 && targetPrice < sourcePrice) score += 0.10m; // Ưu tiên gợi ý giá rẻ hơn
            return Math.Min(score, 0.99m);
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================
        private static string ExtractCoreKeyword(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;
            var splitChars = new[] { '-', '|', '(', '[', ',' };
            string coreName = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries)[0];
            var words = coreName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(5));
        }

        /// <summary>".../laptop-acer-...-ltac1016" -> "LTAC1016"</summary>
        private static string SkuFromUrl(string url)
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
            var last = path.Trim('/').Split('/').Last().Replace(".html", "");
            return last.Split('-').Last().ToUpperInvariant();
        }

        private static string? GetMeta(HtmlDocument doc, string key)
        {
            var node = doc.DocumentNode.SelectSingleNode($"//meta[@property='{key}' or @name='{key}']");
            return Clean(node?.GetAttributeValue("content", ""));
        }

        private static string? Clean(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Regex.Replace(WebUtility.HtmlDecode(s), @"\s+", " ").Trim();
        }

        private static string GetPageText(HtmlDocument doc)
        {
            var clone = doc.DocumentNode.CloneNode(true);
            var junk = clone.SelectNodes("//script|//style|//noscript")?.ToList();
            if (junk != null) foreach (var n in junk) n.Remove();
            return WebUtility.HtmlDecode(clone.InnerText);
        }

        private static string CutBefore(string text, string marker)
        {
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            return idx > 0 ? text[..idx] : text;
        }

        /// <summary>Bắt "Nhãn: giá trị" trong text. Mặc định lấy đến hết dòng.</summary>
        private static string? Grab(string text, string label, string valuePattern = @"[^\r\n]+")
        {
            var m = Regex.Match(text, Regex.Escape(label) + @"\s*:\s*\**\s*(" + valuePattern + ")");
            if (!m.Success) return null;
            var v = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim(' ', '*');
            return v.Length > 0 ? v : null;
        }

        private static void AddIfNotNull(Dictionary<string, string> dict, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) dict[key] = value;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }
}