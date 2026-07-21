using Dispatcher.Persistence;

namespace Dispatcher.MyWork;

public static class MyWorkMigrations
{
    public const string Owner = "my_work";
    public const string Schema = "my_work";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner, Schema, databaseRole,
        [new MigrationStep(1, "rebuildable owner assignment projection", $"""
        CREATE TABLE {Schema}.assignment_projection (
            source_owner text NOT NULL,
            source_kind text NOT NULL,
            source_item_id uuid NOT NULL,
            source_version bigint NOT NULL CHECK (source_version > 0),
            assigned_person_id uuid NOT NULL,
            summary text NOT NULL CHECK (length(trim(summary)) BETWEEN 1 AND 500),
            state text NOT NULL CHECK (length(trim(state)) > 0),
            route text NOT NULL CHECK (route LIKE '/%'),
            required_permissions text[] NOT NULL,
            updated_at timestamp with time zone NOT NULL,
            fingerprint character(64) NOT NULL,
            PRIMARY KEY (source_owner, source_item_id)
        );
        CREATE INDEX assignment_person_idx
            ON {Schema}.assignment_projection (assigned_person_id, updated_at DESC, source_owner, source_item_id);
        """)]);
}
