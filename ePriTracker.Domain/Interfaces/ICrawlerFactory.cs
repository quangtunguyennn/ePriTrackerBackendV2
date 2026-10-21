namespace ePriTracker.Domain.Interfaces;

public interface ICrawlerFactory
{
    IEcommerceCrawlerStrategy GetStrategy(string url);
    IEnumerable<IEcommerceCrawlerStrategy> GetAllStrategies();
}