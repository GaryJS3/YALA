using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using YALA.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<ClientState>();
builder.Services.AddScoped<OfflineQueue>();

await builder.Build().RunAsync();
