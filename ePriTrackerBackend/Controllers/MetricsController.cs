using ePriTrackerBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    [ApiController]
    [Route("api/admin/metrics")] // Đường dẫn API
    public class MetricsController : ControllerBase
    {
        private readonly ScraperMetricsService _metrics;

        // Tiêm ScraperMetricsService vào Controller
        public MetricsController(ScraperMetricsService metrics)
        {
            _metrics = metrics;
        }

        // Endpoint GET: /api/admin/metrics/scraper
        [HttpGet("scraper")]
        public IActionResult GetScraperMetrics()
        {
            // Trả về thẳng file JSON số liệu đẹp mắt
            return Ok(_metrics.GetMetrics());
        }
    }
}
