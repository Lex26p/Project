using Dispatcher.Persistence;

namespace Dispatcher.Dashboards;

public static class DashboardMigrations
{
    public const string Owner = "dashboards";
    public const string Schema = "dashboards";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "published dashboard revisions and personal catalog state",
                $"""
                CREATE TABLE {Schema}.dashboard (
                    dashboard_id uuid PRIMARY KEY,
                    name text NOT NULL CHECK (length(trim(name)) > 0),
                    description text NULL,
                    published_revision_id uuid NULL,
                    published_revision_number bigint NULL CHECK (published_revision_number > 0)
                );
                CREATE TABLE {Schema}.published_revision (
                    revision_id uuid PRIMARY KEY,
                    dashboard_id uuid NOT NULL REFERENCES {Schema}.dashboard(dashboard_id),
                    revision_number bigint NOT NULL CHECK (revision_number > 0),
                    manifest jsonb NOT NULL,
                    manifest_fingerprint character(64) NOT NULL,
                    dependencies jsonb NOT NULL,
                    dependency_fingerprint character(64) NOT NULL,
                    published_at timestamp with time zone NOT NULL,
                    CONSTRAINT dashboard_revision_number_key UNIQUE (dashboard_id, revision_number)
                );
                CREATE TABLE {Schema}.personal_dashboard (
                    subject_id uuid NOT NULL,
                    dashboard_id uuid NOT NULL REFERENCES {Schema}.dashboard(dashboard_id) ON DELETE CASCADE,
                    is_favorite boolean NOT NULL DEFAULT false,
                    last_opened_at timestamp with time zone NULL,
                    PRIMARY KEY (subject_id, dashboard_id)
                );
                CREATE TABLE {Schema}.personal_landing (
                    subject_id uuid PRIMARY KEY,
                    dashboard_id uuid NOT NULL REFERENCES {Schema}.dashboard(dashboard_id),
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX dashboard_name_idx ON {Schema}.dashboard (lower(name));
                CREATE INDEX personal_dashboard_order_idx
                    ON {Schema}.personal_dashboard (subject_id, is_favorite DESC, last_opened_at DESC);
                CREATE FUNCTION {Schema}.reject_published_revision_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'published Dashboard revisions are immutable';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_published_revision
                    BEFORE UPDATE OR DELETE ON {Schema}.published_revision
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_published_revision_mutation();
                """),
        ]);
}
