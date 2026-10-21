using System.Threading;

namespace ePriTrackerBackend.Services
{
    public class ScraperMetricsService
    {
        private int _httpClientCount = 0;
        private int _playwrightCount = 0;
        private int _failedCount = 0;

        // Tăng biến đếm an toàn trong môi trường đa luồng của Hangfire
        public void RecordHttpClientSuccess() => Interlocked.Increment(ref _httpClientCount);
        public void RecordPlaywrightSuccess() => Interlocked.Increment(ref _playwrightCount);
        public void RecordFailure() => Interlocked.Increment(ref _failedCount);

        public object GetMetrics()
        {
            int total = _httpClientCount + _playwrightCount + _failedCount;

            // Tính toán tỷ lệ phần trăm tự động
            string optimizationRate = total > 0
                ? $"{Math.Round((double)_httpClientCount / total * 100, 2)}%"
                : "0%";

            return new
            {
                TotalRequests = total,
                HttpClientSuccess = _httpClientCount,
                PlaywrightSuccess = _playwrightCount,
                Failed = _failedCount,
                HttpClientOptimizationRate = optimizationRate
            };
        }
    }
}