using System.Net;
using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Services
{
    /// <summary>Tiện ích dùng chung cho các service cào dữ liệu (Shopee/Tiki/Lazada).</summary>
    internal static class ScraperTextHelper
    {
        /// <summary>Giới hạn độ dài description lưu vào DB. Chỉnh theo độ dài cột Product.Description.</summary>
        public const int MaxDescriptionLength = 4000;

        /// <summary>Số gợi ý tối đa trả về cho mỗi sản phẩm.</summary>
        public const int MaxSuggestions = 10;

        /// <summary>
        /// Gợi ý phải có giá >= initialPrice * tỉ lệ này. Loại bỏ phụ kiện giá rẻ bất thường
        /// (vd tìm "iPhone 15 Pro Max" ra ốp lưng 50k). Đặt 0 để tắt.
        /// </summary>
        public const decimal MinSuggestionPriceRatio = 0.3m;

        public static bool IsAcceptableSuggestionPrice(decimal price, decimal initialPrice)
            => price > 0
               && initialPrice > 0
               && price <= initialPrice
               && price >= initialPrice * MinSuggestionPriceRatio;

        /// <summary>
        /// Tên sản phẩm thường rất dài nên tìm kiếm trả về 0 kết quả. Tạo danh sách keyword từ dài đến ngắn
        /// (bỏ phần [khuyến mãi], (quà tặng), ký tự đặc biệt) để thử lần lượt.
        /// </summary>
        public static IReadOnlyList<string> BuildKeywordCandidates(string? keyword)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(keyword)) return list;

            var cleaned = Regex.Replace(keyword, @"\[[^\]]*\]|\([^)]*\)|【[^】]*】", " ");
            cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s\-\.]", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            if (cleaned.Length > 0)
            {
                var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var take in new[] { 10, 6, 4 })
                {
                    var k = string.Join(' ', words.Take(take));
                    if (k.Length >= 3 && !list.Contains(k, StringComparer.OrdinalIgnoreCase)) list.Add(k);
                }
            }

            if (list.Count == 0) list.Add(Regex.Replace(keyword, @"\s+", " ").Trim());
            return list;
        }

        /// <summary>
        /// Chuyển description (có thể là HTML) sang văn bản thuần: bỏ thẻ, giải mã entity, chuẩn hóa xuống dòng,
        /// cắt theo MaxDescriptionLength. Dùng văn bản thuần để tránh lưu HTML của bên thứ ba vào DB (rủi ro XSS khi hiển thị).
        /// </summary>
        public static string ToPlainText(string? input, int maxLength = MaxDescriptionLength)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var s = input;
            if (s.Contains('<'))
            {
                s = Regex.Replace(s, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"<\s*br\s*/?>|</\s*(p|div|li|h[1-6]|tr)\s*>", "\n", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"<[^>]+>", " ");
            }

            s = WebUtility.HtmlDecode(s);
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @" *\n *", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();

            if (s.Length > maxLength)
            {
                var cut = maxLength;
                if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
                s = s[..cut].TrimEnd() + "…";
            }

            return s;
        }
    }
}