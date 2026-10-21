using ePriTracker.Domain.Interfaces;

namespace ePriTracker.Infrastructure.Factories;

public class CrawlerFactory : ICrawlerFactory
{
    private readonly IEnumerable<IEcommerceCrawlerStrategy> _strategies;

    public CrawlerFactory(IEnumerable<IEcommerceCrawlerStrategy> strategies)
    {
        _strategies = strategies;
    }

    public IEcommerceCrawlerStrategy GetStrategy(string url)
    {
        return _strategies.FirstOrDefault(s => s.CanHandle(url))
            ?? throw new NotSupportedException($"Không tìm thấy Crawler Strategy phù hợp cho URL: {url}");
    }

    public IEnumerable<IEcommerceCrawlerStrategy> GetAllStrategies()
    {
        return _strategies;
    }
}