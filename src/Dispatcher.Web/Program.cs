using Dispatcher.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IdentitySessionState>();
builder.Services.AddTransient<IdentityApiClient>();
builder.Services.AddTransient<OperationsApiClient>();
builder.Services.AddTransient<ControlApiClient>();
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
builder.Services.AddTransient<DashboardApiClient>();
builder.Services.AddTransient<EditorApiClient>();
builder.Services.AddTransient<KioskApiClient>();

await builder.Build().RunAsync();
