using ePriTracker.Application.DTOs;
using ePriTracker.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ePriTracker.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Bắt buộc người dùng phải đăng nhập (có token) mới được gọi API
    public class ProductController : ControllerBase
    {
        private readonly IProductService _productService;

        public ProductController(IProductService productService)
        {
            _productService = productService;
        }

        // Lấy Email từ JWT Token
        private string GetCurrentUserEmail()
        {
            var emailClaim = User.FindFirst(ClaimTypes.Email) ?? User.FindFirst("email");
            if (emailClaim == null)
            {
                throw new UnauthorizedAccessException("Không tìm thấy email trong token. Vui lòng đăng nhập lại.");
            }
            return emailClaim.Value;
        }

        [HttpPost]
        public async Task<IActionResult> AddProduct([FromBody] AddProductRequest request)
        {
            try
            {
                string email = GetCurrentUserEmail();
                var result = await _productService.AddProductAsync(request.ProductUrl, email);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAllProducts()
        {
            string email = GetCurrentUserEmail();
            var products = await _productService.GetAllProductsAsync(email);
            return Ok(products);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetProductById(long id)
        {
            string email = GetCurrentUserEmail();
            var product = await _productService.GetProductByIdAsync(id, email);
            if (product == null) return NotFound("Sản phẩm không tồn tại hoặc bạn không có quyền xem.");

            return Ok(product);
        }

        [HttpGet("{id}/suggestions")]
        public async Task<IActionResult> GetSuggestions(long id)
        {
            string email = GetCurrentUserEmail();
            var suggestions = await _productService.GetSuggestionsAsync(id, email);
            return Ok(suggestions);
        }

       

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteProduct(long id)
        {
            string email = GetCurrentUserEmail();
            bool result = await _productService.DeleteProductAsync(id, email);

            if (!result) return NotFound("Không tìm thấy sản phẩm để xóa.");
            return Ok(new { message = "Xóa sản phẩm thành công." });
        }
    }

    // DTO để nhận dữ liệu từ Body của Request Post
    public class AddProductRequest
    {
        public string ProductUrl { get; set; } = string.Empty;
    }
}