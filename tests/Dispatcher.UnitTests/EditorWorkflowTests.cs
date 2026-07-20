using Dispatcher.Web;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class EditorWorkflowTests
{
    [Fact]
    public void UnsavedValidatedAndConflictStatesAreExplicit()
    {
        var state = new EditorWorkflowState();
        var saved = Revision(version: 1, validated: null);
        state.Load(saved);
        Assert.False(state.IsUnsaved);
        Assert.False(state.IsValidated);

        state.MarkChanged();
        Assert.True(state.IsUnsaved);
        Assert.False(state.IsValidated);

        var validated = Revision(version: 2, validated: DateTimeOffset.UtcNow);
        state.Saved(saved);
        state.Validated(validated);
        Assert.True(state.IsValidated);

        state.MarkChanged();
        Assert.False(state.IsValidated);
        state.MarkConflict();
        Assert.True(state.HasConflict);
    }

    [Fact]
    public void DashboardAndMimicEditorsHaveSeparateCanonicalRoutes()
    {
        var dashboardRoutes = typeof(Dispatcher.Web.Pages.DashboardEditor)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Select(attribute => attribute.Template)
            .ToArray();
        var mimicRoutes = typeof(Dispatcher.Web.Pages.MimicEditor)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Select(attribute => attribute.Template)
            .ToArray();

        Assert.Contains("/dashboard-editor/{DashboardId:guid}", dashboardRoutes);
        Assert.Contains("/mimic-editor/{MimicId:guid}", mimicRoutes);
        Assert.DoesNotContain(dashboardRoutes, mimicRoutes.Contains);
    }

    private static EditorRevisionPayload Revision(long version, DateTimeOffset? validated) => new(
        Guid.NewGuid(), 1, version, validated, null);
}
