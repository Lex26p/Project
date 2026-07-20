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
var notificationRole = builder.Configuration["Dispatcher:Notifications:DatabaseRole"];
var notificationsEnabled = workspaceEnabled && !string.IsNullOrWhiteSpace(notificationRole);
if (notificationsEnabled)
{
    builder.Services.AddNotificationServer(workspaceConnection!, notificationRole!);
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
var eventRole = builder.Configuration["Dispatcher:Events:DatabaseRole"];
var alarmRole = builder.Configuration["Dispatcher:Alarm:DatabaseRole"];
var eventMaxPageSize = builder.Configuration.GetValue<int?>("Dispatcher:Events:MaxPageSize");
var eventRetainedProjectionChanges = builder.Configuration.GetValue<int?>(
    "Dispatcher:Events:RetainedProjectionChanges");
var eventMaxFeedChanges = builder.Configuration.GetValue<int?>("Dispatcher:Events:MaxFeedChanges");
var eventEnabled = !string.IsNullOrWhiteSpace(workspaceConnection) &&
                   !string.IsNullOrWhiteSpace(eventRole) &&
                   eventMaxPageSize > 0 &&
                   eventRetainedProjectionChanges > 0 &&
                   eventMaxFeedChanges > 0;
if (eventEnabled)
{
    builder.Services.AddEventServer(
        workspaceConnection!,
        eventRole!,
        new Dispatcher.Events.EventDispatcherLimits(
            eventMaxPageSize!.Value,
            eventRetainedProjectionChanges!.Value,
            eventMaxFeedChanges!.Value));
    if (!string.IsNullOrWhiteSpace(alarmRole))
    {
        builder.Services.AddAlarmActionsServer(workspaceConnection!, alarmRole!);
    }
}
var dashboardRole = builder.Configuration["Dispatcher:Dashboards:DatabaseRole"];
var dashboardMaxVisibleWindows = builder.Configuration.GetValue<int?>(
    "Dispatcher:Dashboards:MaxVisibleWindows");
var dashboardMaxBindings = builder.Configuration.GetValue<int?>("Dispatcher:Dashboards:MaxBindings");
var mimicMaxSvgBytes = builder.Configuration.GetValue<int?>("Dispatcher:Mimics:MaxSvgBytes");
var mimicMaxElements = builder.Configuration.GetValue<int?>("Dispatcher:Mimics:MaxElements");
var mimicMaxAttributesPerElement = builder.Configuration.GetValue<int?>(
    "Dispatcher:Mimics:MaxAttributesPerElement");
var mimicMaxAttributeLength = builder.Configuration.GetValue<int?>("Dispatcher:Mimics:MaxAttributeLength");
var dashboardEnabled = !string.IsNullOrWhiteSpace(workspaceConnection) &&
                       !string.IsNullOrWhiteSpace(dashboardRole) &&
                       dashboardMaxVisibleWindows > 0 &&
                       dashboardMaxBindings > 0 &&
                       mimicMaxSvgBytes > 0 &&
                       mimicMaxElements > 0 &&
                       mimicMaxAttributesPerElement > 0 &&
                       mimicMaxAttributeLength > 0;
if (dashboardEnabled)
{
    builder.Services.AddDashboardServer(
        workspaceConnection!,
        dashboardRole!,
        new DashboardRuntimeLimits(dashboardMaxVisibleWindows!.Value, dashboardMaxBindings!.Value),
        new Dispatcher.Dashboards.SvgIntakeLimits(
            mimicMaxSvgBytes!.Value,
            mimicMaxElements!.Value,
            mimicMaxAttributesPerElement!.Value,
            mimicMaxAttributeLength!.Value));
}

var app = builder.Build();
app.MapDispatcherServer();
if (workspaceEnabled)
{
    app.MapWorkspaceServer();
}
if (notificationsEnabled)
{
    app.MapNotificationServer();
}
if (registryEnabled)
{
    app.MapRegistryServer();
}
if (historyEnabled)
{
    app.MapHistoryServer();
}
if (eventEnabled)
{
    app.MapEventServer();
    if (!string.IsNullOrWhiteSpace(alarmRole))
    {
        app.MapAlarmActionsServer();
    }
}
if (dashboardEnabled)
{
    app.MapDashboardServer();
    app.MapDashboardAuthoringServer();
}
app.Run();
