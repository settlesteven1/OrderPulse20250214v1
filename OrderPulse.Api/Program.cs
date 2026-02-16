using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using OrderPulse.Api.Middleware;
using OrderPulse.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Authentication (Microsoft Entra External ID) ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureEntraId"));

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

// ── Services ──
// TODO: Register repository implementations
// builder.Services.AddScoped<IOrderRepository, OrderRepository>();
// builder.Services.AddScoped<IReturnRepository, ReturnRepository>();
// builder.Services.AddScoped<IEmailMessageRepository, EmailMessageRepository>();
// builder.Services.AddScoped<IEmailProcessingOrchestrator, EmailProcessingOrchestrator>();

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

app.Run();
