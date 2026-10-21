using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Services;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. C?U HÌNH DATABASE & HTTP CLIENT
// ==========================================

// Config DbContext
builder.Services.AddDbContext<ePriTrackerContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DBConnection")));

// B?T BU?C: ??ng ký HttpClient Factory cho PriceCrawlerService s? d?ng
builder.Services.AddHttpClient();

// ==========================================
// 2. C?U HÌNH AUTHENTICATION (JWT) & CORS
// ==========================================
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["JwtSettings:Issuer"],
        ValidAudience = builder.Configuration["JwtSettings:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:SecretKey"]!))
    };
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// ==========================================
// 3. ??NG KÝ REPOSITORIES & SERVICES (DI)
// ==========================================
// 1. ??ng ký các Service x? lý t?ng sàn TM?T thông qua IEcommerceService
builder.Services.AddSingleton<TikiBrowserService>();
builder.Services.AddSingleton<IEcommerceService>(sp => sp.GetRequiredService<TikiBrowserService>());

//builder.Services.AddSingleton<ShopeeBrowserService>();
//builder.Services.AddSingleton<IEcommerceService>(sp => sp.GetRequiredService<ShopeeBrowserService>());

//builder.Services.AddSingleton<LazadaBrowserService>();
//builder.Services.AddSingleton<IEcommerceService>(sp => sp.GetRequiredService<LazadaBrowserService>());

builder.Services.AddSingleton<ChototService>();
builder.Services.AddSingleton<IEcommerceService>(sp => sp.GetRequiredService<ChototService>());

// 2. ??ng ký Factory ?? ??nh tuy?n ??ng theo URL s?n ph?m
builder.Services.AddSingleton<IEcommerceServiceFactory, EcommerceServiceFactory>();

// 3. Các Repository và Service nghi?p v? khác
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IPriceCrawlerService, PriceCrawlerService>();
builder.Services.AddScoped<ISuggestionsCrawlerService, SuggestionCrawlService>();
//builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IEventCacheService, EventCacheService>();
builder.Services.AddSingleton<ScraperMetricsService>();
builder.Services.AddControllers();

// ==========================================
// 4. C?U HÌNH HANGFIRE
// ==========================================
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DBConnection"), new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.Zero,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));
builder.Services.AddMemoryCache();
builder.Services.AddHangfireServer();

// ==========================================
// 5. C?U HÌNH SWAGGER
// ==========================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "ePriTracker API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token directly below (do not type 'Bearer ')."
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
// 6. PIPELINE & MIDDLEWARE
// ==========================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

// B?t Dashboard Hangfire (URL: https://localhost:<port>/hangfire)
app.UseHangfireDashboard();

// ==========================================
// 7. C?U HÌNH TIMEZONE & ??NG KÝ HANGFIRE JOBS
// ==========================================
// Thi?t l?p múi gi? Vi?t Nam an toàn ?a n?n t?ng không c?n th? vi?n ngoài
TimeZoneInfo vietnamTz;
try
{
    vietnamTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); // Dành cho Linux / Docker
}
catch (TimeZoneNotFoundException)
{
    vietnamTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); // Dành cho Windows / IIS
}

// 1. Job t? ??ng crawl giá m?i 6 ti?ng
RecurringJob.AddOrUpdate<IPriceCrawlerService>(
    "update-prices-job",
    service => service.UpdateAllTrackedProductPricesAsync(),
    //"0 */6 * * *",
    "*/2 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);

// 2. Job c?p nh?t g?i ý m?i 6 ti?ng
RecurringJob.AddOrUpdate<ISuggestionsCrawlerService>(
    "update-suggestions-job",
    service => service.UpdateAllTrackedProductSuggestionsAsync(CancellationToken.None),
    //"0 */6 * * *",
    "*/2 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);

RecurringJob.AddOrUpdate<IEventCacheService>(
    "refresh-event-cache-job",
    service => service.RefreshLiveEventProductsCacheAsync(),
    //"*/30 * * * *",
    "*/2 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);
BackgroundJob.Enqueue<IEventCacheService>(service => service.RefreshLiveEventProductsCacheAsync());

app.MapControllers();

//app.MapGet("/shopee-login", async (ShopeeBrowserService shopeeService) =>
//{
//    await shopeeService.InteractiveLoginAsync();
//    return Results.Ok("?ã l?u phiên ??ng nh?p Shopee thành công!");
//});
app.Run();