using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OrderPulse.Web;
using OrderPulse.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ── API HttpClient with auth token ──
builder.Services.AddHttpClient("OrderPulseApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress);
})
.AddHttpMessageHandler<ApiAuthorizationMessageHandler>();

builder.Services.AddTransient<ApiAuthorizationMessageHandler>();

// Typed client for convenience
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("OrderPulseApi"));

// ── MSAL Authentication (Microsoft Entra External ID) ──
builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureEntraId", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes.Add(
        builder.Configuration["ApiScope"] ?? "api://orderpulse/.default");
    options.ProviderOptions.LoginMode = "redirect";
});

// ── App Services ──
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<OrderService>();

await builder.Build().RunAsync();
