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
            new MigrationStep(
                2,
                "dashboard and mimic editor lifecycle",
                $"""
                CREATE TABLE {Schema}.authoring_resource (
                    resource_id uuid PRIMARY KEY,
                    kind smallint NOT NULL CHECK (kind IN (1, 2)),
                    next_revision_number bigint NOT NULL CHECK (next_revision_number > 0),
                    draft_revision_id uuid NULL,
                    published_revision_id uuid NULL,
                    version bigint NOT NULL CHECK (version > 0)
                );
                CREATE TABLE {Schema}.authoring_revision (
                    revision_id uuid PRIMARY KEY,
                    resource_id uuid NOT NULL REFERENCES {Schema}.authoring_resource(resource_id),
                    kind smallint NOT NULL CHECK (kind IN (1, 2)),
                    revision_number bigint NOT NULL CHECK (revision_number > 0),
                    source_revision_id uuid NULL REFERENCES {Schema}.authoring_revision(revision_id),
                    content jsonb NOT NULL,
                    content_fingerprint character(64) NOT NULL,
                    dependencies jsonb NOT NULL,
                    dependency_fingerprint character(64) NOT NULL,
                    validated_content_fingerprint character(64) NULL,
                    validated_dependency_fingerprint character(64) NULL,
                    version bigint NOT NULL CHECK (version > 0),
                    saved_at timestamp with time zone NOT NULL,
                    validated_at timestamp with time zone NULL,
                    published_at timestamp with time zone NULL,
                    CONSTRAINT authoring_resource_number_key UNIQUE (resource_id, revision_number),
                    CONSTRAINT authoring_validation_pair CHECK (
                        (validated_content_fingerprint IS NULL) =
                        (validated_dependency_fingerprint IS NULL))
                );
                ALTER TABLE {Schema}.authoring_resource
                    ADD CONSTRAINT authoring_draft_fk
                        FOREIGN KEY (draft_revision_id) REFERENCES {Schema}.authoring_revision(revision_id);
                CREATE TABLE {Schema}.editor_audit (
                    audit_id uuid PRIMARY KEY,
                    resource_id uuid NOT NULL,
                    kind smallint NOT NULL CHECK (kind IN (1, 2)),
                    revision_id uuid NOT NULL,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    resulting_version bigint NOT NULL CHECK (resulting_version > 0),
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX editor_audit_resource_idx
                    ON {Schema}.editor_audit (resource_id, changed_at);
                CREATE FUNCTION {Schema}.protect_authoring_publication() RETURNS trigger AS $$
                BEGIN
                    IF OLD.published_at IS NOT NULL AND
                       (NEW.content IS DISTINCT FROM OLD.content OR
                        NEW.dependencies IS DISTINCT FROM OLD.dependencies OR
                        NEW.content_fingerprint IS DISTINCT FROM OLD.content_fingerprint OR
                        NEW.dependency_fingerprint IS DISTINCT FROM OLD.dependency_fingerprint) THEN
                        RAISE EXCEPTION 'published authoring content is immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_authoring_publication
                    BEFORE UPDATE ON {Schema}.authoring_revision
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.protect_authoring_publication();
                """),
        ]);
}
