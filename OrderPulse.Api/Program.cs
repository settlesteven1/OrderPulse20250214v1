using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderPulse.Api.Middleware;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.AI;
using OrderPulse.Infrastructure.AI.Parsers;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Repositories;
using OrderPulse.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Authentication (Microsoft Entra ID) ──
// Using raw JwtBearer instead of Microsoft.Identity.Web to avoid
// its extra validation layers that reject Graph-audience tokens.
var tenantId = builder.Configuration["AzureEntraId:TenantId"];
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            ValidateIssuer = false,
            ValidateLifetime = true,
            NameClaimType = "preferred_username",
        };
    });

// ── Database ──
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();
builder.Services.AddScoped<TenantSessionInterceptor>();
builder.Services.AddDbContext<OrderPulseDbContext>((sp, options) =>
{
    var connectionString = builder.Configuration.GetConnectionString("OrderPulseDb");
    var interceptor = sp.GetRequiredService<TenantSessionInterceptor>();
    options.UseSqlServer(connectionString)
           .AddInterceptors(interceptor);
});

// ── Repositories ──
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IReturnRepository, ReturnRepository>();
builder.Services.AddScoped<IEmailMessageRepository, EmailMessageRepository>();

// ── AI Services (needed for review reprocessing) ──
builder.Services.AddSingleton<AzureOpenAIService>();
builder.Services.AddSingleton<IEmailClassifier, EmailClassifierService>();
builder.Services.AddSingleton<IEmailParser<OrderParserResult>, OrderParserService>();
builder.Services.AddSingleton<IEmailParser<ShipmentParserResult>, ShipmentParserService>();
builder.Services.AddSingleton<IEmailParser<DeliveryParserResult>, DeliveryParserService>();
builder.Services.AddSingleton<IEmailParser<ReturnParserResult>, ReturnParserService>();
builder.Services.AddSingleton<IEmailParser<RefundParserResult>, RefundParserService>();
builder.Services.AddSingleton<IEmailParser<CancellationParserResult>, CancellationParserService>();
builder.Services.AddSingleton<IEmailParser<PaymentParserResult>, PaymentParserService>();

// ── Domain Services ──
builder.Services.AddScoped<RetailerMatcher>();
builder.Services.AddScoped<OrderStateMachine>();
builder.Services.AddScoped<EmailBlobStorageService>();
builder.Services.AddScoped<ProcessingLogger>();
builder.Services.AddScoped<IEmailProcessingOrchestrator, EmailProcessingOrchestrator>();

// ── API ──
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── CORS (for Blazor WASM client) ──
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                builder.Configuration["AllowedOrigins"]
                ?? "https://localhost:5001")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ── Health check (no auth required) ──
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "OrderPulse API",
    timestamp = DateTime.UtcNow
}));

// ── Auth diagnostic endpoint (no auth required) ──
app.MapGet("/auth-debug", (HttpContext ctx) =>
{
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    var hasToken = !string.IsNullOrEmpty(authHeader);
    var isAuthenticated = ctx.User?.Identity?.IsAuthenticated ?? false;
    var claims = ctx.User?.Claims.Select(c => new { c.Type, c.Value }).ToList();

    return Results.Ok(new
    {
        hasAuthorizationHeader = hasToken,
        tokenPreview = hasToken ? authHeader[..Math.Min(50, authHeader.Length)] + "..." : null,
        isAuthenticated,
        claimCount = claims?.Count ?? 0,
        claims
    });
});

app.Run();
