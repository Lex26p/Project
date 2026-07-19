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
var facilityRole = builder.Configuration["Dispatcher:Facility:DatabaseRole"];
var equipmentRole = builder.Configuration["Dispatcher:Equipment:DatabaseRole"];
var registryEnabled = !string.IsNullOrWhiteSpace(workspaceConnection) &&
                      !string.IsNullOrWhiteSpace(facilityRole) &&
                      !string.IsNullOrWhiteSpace(equipmentRole);
if (registryEnabled)
{
    builder.Services.AddRegistryServer(workspaceConnection!, facilityRole!, equipmentRole!);
}
var historyRole = builder.Configuration["Dispatcher:History:DatabaseRole"];
var historyMaxPageSize = builder.Configuration.GetValue<int?>("Dispatcher:History:MaxPageSize");
var historyMaxAggregateBuckets = builder.Configuration.GetValue<int?>("Dispatcher:History:MaxAggregateBuckets");
var historyEnabled = !string.IsNullOrWhiteSpace(workspaceConnection) &&
                     !string.IsNullOrWhiteSpace(historyRole) &&
                     historyMaxPageSize > 0 &&
                     historyMaxAggregateBuckets > 0;
if (historyEnabled)
{
    builder.Services.AddHistoryServer(
        workspaceConnection!,
        historyRole!,
        new Dispatcher.History.HistoryQueryLimits(historyMaxPageSize!.Value, historyMaxAggregateBuckets!.Value));
}

var app = builder.Build();
app.MapDispatcherServer();
if (workspaceEnabled)
{
    app.MapWorkspaceServer();
}
if (registryEnabled)
{
    app.MapRegistryServer();
}
if (historyEnabled)
{
    app.MapHistoryServer();
}
app.Run();
