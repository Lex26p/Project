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
var smtpHost = builder.Configuration["Dispatcher:Notifications:Smtp:Host"];
var smtpPort = builder.Configuration.GetValue<int?>("Dispatcher:Notifications:Smtp:Port");
var smtpTls = builder.Configuration.GetValue<bool?>("Dispatcher:Notifications:Smtp:Tls");
var smtpSender = builder.Configuration["Dispatcher:Notifications:Smtp:SenderAddress"];
var smtpUser = builder.Configuration["Dispatcher:Notifications:Smtp:UserName"];
var smtpCredentialReference = builder.Configuration["Dispatcher:Notifications:Smtp:CredentialReference"];
var smtpTimeoutSeconds = builder.Configuration.GetValue<int?>("Dispatcher:Notifications:Smtp:TimeoutSeconds");
var smtpMaxAttempts = builder.Configuration.GetValue<int?>("Dispatcher:Notifications:Smtp:MaxAttempts");
var smtpRetryDelays = builder.Configuration
    .GetSection("Dispatcher:Notifications:Smtp:RetryDelaySeconds")
    .Get<int[]>();
var smtpEnabled = notificationsEnabled &&
                  !string.IsNullOrWhiteSpace(smtpHost) &&
                  smtpPort is > 0 and <= 65535 &&
                  smtpTls == true &&
                  !string.IsNullOrWhiteSpace(smtpSender) &&
                  !string.IsNullOrWhiteSpace(smtpUser) &&
                  !string.IsNullOrWhiteSpace(smtpCredentialReference) &&
                  smtpTimeoutSeconds > 0 &&
                  smtpMaxAttempts > 0 &&
                  smtpRetryDelays?.Length == smtpMaxAttempts - 1;
if (smtpEnabled)
{
    builder.Services.AddSmtpNotificationProvider(
        new Dispatcher.Notifications.SmtpProviderConfiguration(
            Dispatcher.Notifications.SmtpProviderProfile.Production,
            smtpHost!,
            smtpPort!.Value,
            tls: true,
            smtpSender!,
            smtpUser!,
            Dispatcher.Notifications.NotificationSecretReference.From(smtpCredentialReference!),
            TimeSpan.FromSeconds(smtpTimeoutSeconds!.Value)),
        new Dispatcher.Notifications.NotificationDeliveryPolicy(
            smtpMaxAttempts!.Value,
            smtpRetryDelays!.Select(value => TimeSpan.FromSeconds(value)).ToArray()));
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
var terminalRole = builder.Configuration["Dispatcher:Terminals:DatabaseRole"];
var terminalChallengeSeconds = builder.Configuration.GetValue<int?>("Dispatcher:Terminals:ChallengeLifetimeSeconds");
var terminalCredentialSeconds = builder.Configuration.GetValue<int?>("Dispatcher:Terminals:CredentialLifetimeSeconds");
var terminalPinIterations = builder.Configuration.GetValue<int?>("Dispatcher:Terminals:PinIterations");
var terminalPinMinimumLength = builder.Configuration.GetValue<int?>("Dispatcher:Terminals:PinMinimumLength");
var terminalPinMaximumLength = builder.Configuration.GetValue<int?>("Dispatcher:Terminals:PinMaximumLength");
var terminalReauthenticationSeconds = builder.Configuration.GetValue<int?>("Dispatcher:Terminals:ReauthenticationLifetimeSeconds");
var terminalsEnabled = dashboardEnabled && !string.IsNullOrWhiteSpace(terminalRole) &&
                       terminalChallengeSeconds > 0 && terminalCredentialSeconds > 0 &&
                       terminalPinIterations > 0 && terminalPinMinimumLength >= 4 &&
                       terminalPinMaximumLength >= terminalPinMinimumLength && terminalReauthenticationSeconds > 0;
if (terminalsEnabled)
{
    builder.Services.AddTerminalRuntimeServer(
        workspaceConnection!, terminalRole!,
        new Dispatcher.Terminals.TerminalEnrollmentPolicy(
            TimeSpan.FromSeconds(terminalChallengeSeconds!.Value),
            TimeSpan.FromSeconds(terminalCredentialSeconds!.Value)),
        new Dispatcher.Terminals.TerminalPinPolicy(
            terminalPinIterations!.Value, terminalPinMinimumLength!.Value, terminalPinMaximumLength!.Value,
            TimeSpan.FromSeconds(terminalReauthenticationSeconds!.Value)));
}
var identityRole = builder.Configuration["Dispatcher:Identity:DatabaseRole"];
var identityPasswordIterations = builder.Configuration.GetValue<int?>("Dispatcher:Identity:PasswordIterations");
var identityPasswordMinimumLength = builder.Configuration.GetValue<int?>("Dispatcher:Identity:PasswordMinimumLength");
var identityPasswordMaximumLength = builder.Configuration.GetValue<int?>("Dispatcher:Identity:PasswordMaximumLength");
var identityMaximumFailedAttempts = builder.Configuration.GetValue<int?>("Dispatcher:Identity:MaximumFailedAttempts");
var identityLockoutSeconds = builder.Configuration.GetValue<int?>("Dispatcher:Identity:LockoutSeconds");
var identityAccessSeconds = builder.Configuration.GetValue<int?>("Dispatcher:Identity:AccessLifetimeSeconds");
var identityRefreshSeconds = builder.Configuration.GetValue<int?>("Dispatcher:Identity:RefreshLifetimeSeconds");
var identityBootstrapUserName = builder.Configuration["Dispatcher:Identity:Bootstrap:UserName"];
var identityBootstrapPassword = builder.Configuration["Dispatcher:Identity:Bootstrap:Password"];
var identityEnabled = !string.IsNullOrWhiteSpace(workspaceConnection) && !string.IsNullOrWhiteSpace(identityRole) &&
                      identityPasswordIterations > 0 && identityPasswordMinimumLength >= 8 &&
                      identityPasswordMaximumLength >= identityPasswordMinimumLength && identityMaximumFailedAttempts > 0 &&
                      identityLockoutSeconds > 0 && identityAccessSeconds > 0 && identityRefreshSeconds > identityAccessSeconds;
if (identityEnabled)
{
    builder.Services.AddIdentityServer(
        workspaceConnection!, identityRole!,
        new Dispatcher.Identity.IdentitySecurityPolicy(
            identityPasswordIterations!.Value, identityPasswordMinimumLength!.Value, identityPasswordMaximumLength!.Value,
            identityMaximumFailedAttempts!.Value, TimeSpan.FromSeconds(identityLockoutSeconds!.Value),
            TimeSpan.FromSeconds(identityAccessSeconds!.Value), TimeSpan.FromSeconds(identityRefreshSeconds!.Value)));
}
var administrationRole = builder.Configuration["Dispatcher:Administration:DatabaseRole"];
var administrationMaximumViewItems = builder.Configuration.GetValue<int?>("Dispatcher:Administration:MaximumViewItems");
var administrationMaximumAuditPageSize = builder.Configuration.GetValue<int?>("Dispatcher:Administration:MaximumAuditPageSize");
var administrationRetainedAuditTail = builder.Configuration.GetValue<int?>("Dispatcher:Administration:RetainedAuditTail");
var administrationEnabled = !string.IsNullOrWhiteSpace(workspaceConnection) &&
                            !string.IsNullOrWhiteSpace(administrationRole) &&
                            administrationMaximumViewItems > 0 && administrationMaximumAuditPageSize > 0 &&
                            administrationRetainedAuditTail > 0;
if (administrationEnabled)
{
    builder.Services.AddAdministrationServer(
        workspaceConnection!, administrationRole!,
        new Dispatcher.Administration.AdministrationQueryLimits(
            administrationMaximumViewItems!.Value,
            administrationMaximumAuditPageSize!.Value,
            administrationRetainedAuditTail!.Value));
}

var app = builder.Build();
if (identityEnabled && !string.IsNullOrWhiteSpace(identityBootstrapUserName) &&
    !string.IsNullOrWhiteSpace(identityBootstrapPassword))
{
    var bootstrap = await app.Services.GetRequiredService<Dispatcher.Identity.IdentityStore>()
        .BootstrapAdministratorAsync(new Dispatcher.Identity.BootstrapLocalAdministrator(
            Dispatcher.Identity.IdentityAccountId.New(),
            Dispatcher.Platform.SubjectId.New(),
            null,
            Dispatcher.Identity.IdentityRoleId.New(),
            identityBootstrapUserName,
            identityBootstrapPassword));
    if (bootstrap.IsFailure && bootstrap.Error?.Code.Value != "identity.bootstrap_closed")
    {
        throw new InvalidOperationException($"Identity bootstrap failed: {bootstrap.Error?.Code.Value}.");
    }
}
if (identityEnabled)
{
    app.UseProductionSessionAuthentication();
}
app.MapDispatcherServer();
if (identityEnabled)
{
    app.MapIdentityServer();
}
if (administrationEnabled)
{
    app.MapAdministrationServer();
}
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
if (terminalsEnabled)
{
    app.MapTerminalRuntimeServer();
}
app.Run();
