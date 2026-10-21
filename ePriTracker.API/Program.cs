using ePriTracker.Application.Interfaces;
using ePriTracker.Application.Services;
using ePriTracker.Domain.Interfaces;
using ePriTracker.Infrastructure.Context;
using ePriTracker.Infrastructure.Crawlers;
using ePriTracker.Infrastructure.Factories;
using ePriTracker.Infrastructure.Repositories;
using ePriTracker.Infrastructure.Services;
using ePriTracker.Infrastructure.Security;

// Thêm using cho Application Services khi bạn tạo xong (ví dụ: ePriTracker.Application.Services)
// using ePriTracker.Application.Services; 

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Text;
var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. CẤU HÌNH DATABASE & CACHE
// ==========================================
// SQL Server
builder.Services.AddDbContext<ePriTrackerContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Redis Cache
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("RedisConnection");
    options.InstanceName = "ePriTracker:";
});

// ==========================================
// 2. CẤU HÌNH AUTHENTICATION (JWT)
// ==========================================
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = jwtSettings["SecretKey"] ?? throw new InvalidOperationException("JWT SecretKey is missing.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
        ValidateIssuer = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidateAudience = true,
        ValidAudience = jwtSettings["Audience"],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});

// ==========================================
// 3. ĐĂNG KÝ DEPENDENCY INJECTION (DI)
// ==========================================
// Infrastructure Layer
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<ICacheService, RedisCacheService>();
builder.Services.AddScoped<IUserRepository, UserRepository>();


// Crawler Strategies & Factory
builder.Services.AddHttpClient<IEcommerceCrawlerStrategy, HACOMCrawlerStrategy>();
// builder.Services.AddScoped<IEcommerceCrawlerStrategy, ShopeeCrawlerStrategy>(); // Thêm các sàn khác tại đây
builder.Services.AddScoped<ICrawlerFactory, CrawlerFactory>();

// Application Layer (Service điều phối)
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
// ==========================================
// 4. CẤU HÌNH API & SWAGGER
// ==========================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ePriTracker API",
        Version = "v1"
    });

    // Cấu hình Bearer Auth cho Swagger UI
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Nhập Token vào đây theo dạng: Bearer {your_token}"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ==========================================
// 5. CẤU HÌNH HTTP REQUEST PIPELINE
// ==========================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(); // Sử dụng UI của Swagger thay vì map mặc định
}

app.UseHttpsRedirection();

// Cấu hình CORS (nếu frontend ở port khác)
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Middleware bảo mật
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();