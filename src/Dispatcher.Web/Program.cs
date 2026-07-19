using Dispatcher.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddTransient(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddTransient(sp =>
{
    var navigation = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    return new HubConnectionBuilder()
        .WithUrl(new Uri(new Uri(navigation.BaseUri), "hubs/runtime"))
        .Build();
});
builder.Services.AddTransient<RealtimeWidgetClient>();
builder.Services.AddTransient<WorkspaceApiClient>();
builder.Services.AddTransient<RegistryApiClient>();
builder.Services.AddTransient<HistoryApiClient>();

await builder.Build().RunAsync();
