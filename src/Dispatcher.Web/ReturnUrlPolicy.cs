namespace Dispatcher.Web;

public static class ReturnUrlPolicy
{
    private const string DefaultRoute = "/home";

    public static string Normalize(
        string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(
                returnUrl))
        {
            return DefaultRoute;
        }

        var candidate = returnUrl.Trim();
        if (!candidate.StartsWith('/') ||
            candidate.StartsWith(
                "//",
                StringComparison.Ordinal) ||
            candidate.Contains('\\') ||
            Uri.TryCreate(
                candidate,
                UriKind.Absolute,
                out _))
        {
            return DefaultRoute;
        }

        var path = candidate
            .Split('#', 2)[0]
            .Split('?', 2)[0]
            .TrimEnd('/');
        return string.Equals(
                path,
                "/login",
                StringComparison.OrdinalIgnoreCase)
            ? DefaultRoute
            : candidate;
    }
}
