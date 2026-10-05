using Game.Client;
using Game.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// "ApiBaseUrl" comes from wwwroot/appsettings.{Environment}.json. When it is empty the client
// calls its own origin, which suits hosts that proxy /api to the backend.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
var apiBaseAddress = string.IsNullOrWhiteSpace(apiBaseUrl)
    ? new Uri(builder.HostEnvironment.BaseAddress)
    : new Uri(apiBaseUrl);

builder.Services.AddScoped<GameState>();
builder.Services.AddScoped(sp => new HttpClient(new AuthTokenHandler(sp.GetRequiredService<GameState>()))
{
    BaseAddress = apiBaseAddress
});

await builder.Build().RunAsync();
