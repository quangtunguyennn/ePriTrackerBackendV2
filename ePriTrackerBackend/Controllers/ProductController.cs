using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")] // Route sẽ là: /api/Product
    [ApiController]
    public class ProductController : ControllerBase
    {
        private readonly IProductRepository _repository;
        public ProductController(IProductRepository repository)
        {
            _repository = repository;
        }

        // Endpoint: POST /api/product/add
        [HttpPost("/api/product/add")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> AddProduct([FromBody] string productLink)
        {
            try
            {
                var userEmail = User?.Identity?.Name;
                if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

                await _repository.AddProduct(productLink, userEmail);
                return Ok(new { message = "Thêm sản phẩm vào danh sách theo dõi thành công!" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Endpoint: GET /api/product/all
        [HttpGet("/api/product/all")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> GetAll()
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

            var products = await _repository.GetAll(userEmail);
            return Ok(products);
        }

        // Endpoint: GET /product?id=...
        [HttpGet("/product")]
        [Authorize(Roles = "User, Admin")]
        public async Task<ActionResult<Product>> GetById([FromQuery] Guid id)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

            var product = await _repository.GetById(id);

            if (product == null) return NotFound(new { message = "Không tìm thấy sản phẩm." });

            return product;
        }

        // Endpoint: GET /api/product/suggestion/{productId}
        [Authorize(Roles = "User")]
        [HttpGet("/api/product/suggestion/{productId}")]
        public async Task<IActionResult> GetAllBetterProducts(Guid productId)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
            try
            {
                // Controller gọi xuống Repository
                var result = await _repository.GetAllBetterProducts(productId);

                // Trả về HTTP Status 200 (Thành công) kèm dữ liệu
                return Ok(result);
            }
            catch (Exception ex)
            {
                // Trả về HTTP Status 400 (Lỗi) kèm câu thông báo lỗi
                return BadRequest(new { message = ex.Message });
            }
        }

        // Endpoint: DELETE /api/product/delete?id=...
        [Authorize(Roles = "User")]
        [HttpDelete("/api/product/delete")]
        public async Task<IActionResult> DeleteProduct([FromQuery] Guid id)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

            try
            {
                // Đã truyền thêm userEmail vào để khớp với Interface mới và đảm bảo bảo mật
                bool isDeleted = await _repository.DeleteProduct(id, userEmail);

                if (!isDeleted)
                {
                    return BadRequest("delete error");
                }

                return Ok(new { message = "Delete successfully" });
            }
            catch (Exception ex)
            {
                // Bắt lỗi từ DB quăng ra (như "Sản phẩm không tồn tại")
                return BadRequest(new { message = ex.Message });
            }
        }

        [Authorize(Roles ="User")]
        [HttpGet("/api/product/refresh-suggestions")]
        public async Task<IActionResult> RefreshSuggestions(Guid productId)
        {
            var userEmail = User?.Identity?.Name;
            if (string.IsNullOrEmpty(userEmail)) return Unauthorized();

            var suggestions = await _repository.RefreshSuggestions(productId);
            return Ok(suggestions);
        }
    }
}