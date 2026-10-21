using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly IEventRepository _eventRepository;
        private readonly IProductRepository _productRepository;
        private readonly ePriTrackerContext _context;
        private readonly IMemoryCache _cache;

        public EventController(IEventRepository eventRepo, IProductRepository productRepo, ePriTrackerContext context, IMemoryCache cache)
        {
            _eventRepository = eventRepo;
            _productRepository = productRepo;
            _context = context;
            _cache = cache;
        }

        // =======================================================
        // PHẦN 1: API DÀNH CHO ADMIN QUẢN LÝ SỰ KIỆN
        // =======================================================

        [HttpGet("crawlPreview")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CrawlPreviewEvents()
        {
            try
            {
                var scrapedEvents = await _eventRepository.GetCurrentTikiEvents();
                if (scrapedEvents == null) scrapedEvents = new List<Event>();

                var dbEvents = await _context.Event.ToListAsync();

                var finalEvents = new List<Event>();
                var activeDbEventIds = new HashSet<Guid>(); // Rổ lưu ID các sự kiện còn sống

                // 1. XỬ LÝ SỰ KIỆN CÒN TRÊN TIKI
                foreach (var scraped in scrapedEvents)
                {
                    string scrapedTitle = scraped.Title?.Trim().ToLower() ?? "";
                    string scrapedBaseLink = scraped.EventLink?.Split('?')[0] ?? "";

                    var existingDbEvent = dbEvents.FirstOrDefault(e =>
                        (e.Title != null && e.Title.Trim().ToLower() == scrapedTitle) ||
                        (e.EventLink != null && e.EventLink.Split('?')[0] == scrapedBaseLink)
                    );

                    if (existingDbEvent != null)
                    {
                        scraped.IsPublished = existingDbEvent.IsPublished;
                        scraped.EventId = existingDbEvent.EventId;

                        activeDbEventIds.Add(existingDbEvent.EventId); // Đánh dấu sự kiện này còn sống
                    }
                    else
                    {
                        scraped.IsPublished = false;
                        scraped.EventId = Guid.Empty;
                    }
                    finalEvents.Add(scraped);
                }

                // 2. LÔI CÁC SỰ KIỆN "ĐÃ BỐC HƠI" TỪ DB RA (EXPIRED)
                // Tìm các sự kiện có trong DB nhưng KHÔNG CÓ trong rổ activeDbEventIds
                var expiredEvents = dbEvents.Where(e => !activeDbEventIds.Contains(e.EventId)).ToList();

                foreach (var expired in expiredEvents)
                {
                    expired.IsExpired = true; // Bật cờ hết hạn
                    finalEvents.Add(expired); // Trộn chung vào danh sách trả về cho React
                }

                return Ok(finalEvents);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("postEvent")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> PostEvent([FromBody] Event tikiEvent)
        {
            try
            {
                // Kiểm tra dựa trên Tiêu đề (Title) hoặc Đường dẫn (EventLink) của sự kiện
                var existingEvent = await _context.Event
                    .FirstOrDefaultAsync(e => e.Title == tikiEvent.Title || e.EventLink == tikiEvent.EventLink);

                if (existingEvent == null)
                {
                    // Nếu chưa có, tạo mới hoàn toàn
                    tikiEvent.EventId = Guid.NewGuid();
                    tikiEvent.CreatedAt = DateTime.UtcNow;
                    tikiEvent.IsPublished = true;

                    _context.Event.Add(tikiEvent);
                    await _context.SaveChangesAsync();

                    return Ok(new { message = "Đã đăng sự kiện thành công!" });
                }
                else
                {
                    // Nếu đã tồn tại trong DB, chỉ cần cập nhật lại trạng thái thành Published = true và giữ nguyên ID cũ
                    existingEvent.IsPublished = true;
                    existingEvent.CreatedAt = DateTime.UtcNow; // Cập nhật lại thời gian mới nhất nếu muốn

                    await _context.SaveChangesAsync();
                    return Ok(new { message = "Sự kiện này đã có sẵn, đã cập nhật trạng thái hiển thị!" });
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // API MỚI: Dành cho Admin lấy TẤT CẢ sự kiện (cả ẩn và hiện) để quản lý
        [HttpGet("getAllEventsAdmin")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAllEventsAdmin()
        {
            var events = await _context.Event.OrderByDescending(e => e.CreatedAt).ToListAsync();
            return Ok(events);
        }

        // API MỚI: Bật/Tắt trạng thái IsPublished (Soft Delete / Gỡ bài / Đăng lại)
        [HttpPut("togglePublish/{eventId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> TogglePublish(Guid eventId)
        {
            var evt = await _context.Event.FindAsync(eventId);
            if (evt == null) return NotFound(new { message = "Không tìm thấy sự kiện." });

            evt.IsPublished = !evt.IsPublished; // Đảo ngược trạng thái
            await _context.SaveChangesAsync();

            return Ok(new { message = evt.IsPublished ? "Đã khôi phục (Đăng lại) sự kiện!" : "Đã gỡ bài thành công!" });
        }

        // =======================================================
        // PHẦN 2: API DÀNH CHO USER VÀ ADMIN ĐỂ HIỂN THỊ
        // =======================================================

        [HttpGet("getPublishedEvents")]
        [Authorize(Roles = "User, Admin")] // Yêu cầu đăng nhập, cả User và Admin đều xem được
        public async Task<IActionResult> GetPublishedEvents()
        {
            // TÍNH NĂNG TỰ ĐỘNG XÓA: Lấy ra các sự kiện cũ hơn 14 ngày
            var expirationDate = DateTime.UtcNow.AddDays(-14);
            var expiredEvents = await _context.Event.Where(e => e.CreatedAt < expirationDate).ToListAsync();

            if (expiredEvents.Any())
            {
                _context.Event.RemoveRange(expiredEvents); // Dọn rác cứng khỏi DB
                await _context.SaveChangesAsync();
            }

            // CHỈ TRẢ VỀ CHO USER CÁC SỰ KIỆN ĐANG BẬT ISPUBLISHED = TRUE
            var events = await _context.Event
                .Where(e => e.IsPublished == true)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            return Ok(events);
        }

        [HttpGet("getLiveEventProducts/{eventId}")]
        [Authorize(Roles = "User, Admin")]
        public async Task<IActionResult> GetLiveEventProducts(Guid eventId)
        {
            try
            {
                string cacheKey = $"EventProducts_{eventId}";

                // BƯỚC 1: KIỂM TRA RAM (CACHE) TRƯỚC
                // Nếu dữ liệu đã có sẵn trong Cache do Hangfire cào sẵn, trả về NGAY LẬP TỨC (< 10ms)
                if (_cache.TryGetValue(cacheKey, out var cachedProducts))
                {
                    return Ok(cachedProducts);
                }

                // BƯỚC 2: FALLBACK (Nếu Cache rỗng hoặc Job chưa kịp chạy)
                var evt = await _context.Event.FindAsync(eventId);
                if (evt == null) return NotFound(new { message = "Không tìm thấy sự kiện trong hệ thống." });

                var products = await _productRepository.GetLiveProductsFromEventAsync(evt.EventLink);

                if (products == null || products.Count == 0)
                {
                    return BadRequest(new { message = "Sự kiện này đã kết thúc trên Tiki hoặc không có sản phẩm." });
                }

                // BƯỚC 3: LƯU VÀO CACHE CHO NHỮNG USER SAU BẤM VÀO
                var cacheOptions = new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(30));
                _cache.Set(cacheKey, products, cacheOptions);

                return Ok(products);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("trackDeal")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> TrackDeal([FromBody] TrackDealDto request)
        {
            try
            {
                // Bước 1: Lấy thông tin User đang đăng nhập từ Token
                var userId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value);

                // Bước 2: TÌM HOẶC TẠO MỚI PRODUCT
                var product = await _context.Product
                    .FirstOrDefaultAsync(p => p.ProductLink == request.ProductLink);

                if (product == null)
                {
                    // Nếu sản phẩm này chưa từng có trong hệ thống, tạo Product mới
                    product = new Product
                    {
                        ProductId = Guid.NewGuid(),
                        ProductName = request.ProductName,
                        ProductLink = request.ProductLink,
                        ImageURL = request.ImageURL,

                        // Khi mới thêm vào, Giá khởi điểm và Giá mới nhất đều bằng giá hiện tại
                        InitialPrice = request.CurrentPrice,
                        LatestPrice = request.CurrentPrice,

                        AddedAt = DateTime.UtcNow,
                        LastUpdatedAt = DateTimeOffset.UtcNow
                    };

                    _context.Product.Add(product);
                    await _context.SaveChangesAsync(); // Lưu để sinh ra ProductId
                }

                // Bước 3: KIỂM TRA TRÙNG LẶP TRONG BẢNG ITEM
                // Xem User này đã nối với Product này trong danh sách theo dõi chưa
                bool alreadyTracking = await _context.Item
                    .AnyAsync(i => i.UserId == userId && i.ProductId == product.ProductId);

                if (alreadyTracking)
                {
                    return BadRequest(new { message = "Bạn đã theo dõi sản phẩm này rồi!" });
                }

                // Bước 4: LƯU VÀO DANH SÁCH THEO DÕI CỦA USER (BẢNG ITEM)
                var newItem = new Item
                {
                    ItemId = Guid.NewGuid(),
                    UserId = userId,
                    ProductId = product.ProductId
                };

                _context.Item.Add(newItem);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Đã thêm vào danh sách theo dõi thành công!" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("deleteEvent/{eventId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteEvent(Guid eventId)
        {
            try
            {
                // 1. Tìm sự kiện trong DB
                var evt = await _context.Event.FindAsync(eventId);
                if (evt == null)
                {
                    return NotFound(new { message = "Không tìm thấy sự kiện để xóa." });
                }

                // 2. Xóa và lưu lại
                _context.Event.Remove(evt);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Đã xóa sự kiện thành công." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}