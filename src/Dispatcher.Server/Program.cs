using Dispatcher.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDispatcherServer(builder.Configuration);
var workspaceConnection = builder.Configuration.GetConnectionString("Dispatcher");
var workspaceRole = builder.Configuration["Dispatcher:Workspace:DatabaseRole"];
var workspaceEnabled = !string.IsNullOrWhiteSpace(workspaceConnection) &&
                       !string.IsNullOrWhiteSpace(workspaceRole);
if (workspaceEnabled)
{
    builder.Services.AddWorkspaceServer(workspaceConnection!, workspaceRole!);
}

var app = builder.Build();
app.MapDispatcherServer();
if (workspaceEnabled)
{
    app.MapWorkspaceServer();
}
app.Run();
