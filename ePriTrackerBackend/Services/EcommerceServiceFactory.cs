namespace ePriTrackerBackend.Services
{
    public interface IEcommerceServiceFactory
    {
        IEcommerceService GetService(string productLink);
    }

    public class EcommerceServiceFactory : IEcommerceServiceFactory
    {
        private readonly IEnumerable<IEcommerceService> _services;

        public EcommerceServiceFactory(IEnumerable<IEcommerceService> services)
        {
            _services = services;
        }

        public IEcommerceService GetService(string productLink)
        {
            var service = _services.FirstOrDefault(s => s.CanHandle(productLink));
            if (service == null)
                throw new NotSupportedException($"Hiện tại hệ thống chưa hỗ trợ nền tảng thương mại điện tử cho link: {productLink}");

            return service;
        }
    }
}