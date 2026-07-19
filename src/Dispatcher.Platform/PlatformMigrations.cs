using Dispatcher.Persistence;

namespace Dispatcher.Platform;

public static class PlatformMigrations
{
    public const string Owner = "platform_nucleus";
    public const string Schema = "platform_nucleus";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "operation admission and audit",
                $"""
                CREATE TABLE {Schema}.operation_admission (
                    operation_id uuid PRIMARY KEY,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    idempotency_key text NOT NULL,
                    request_fingerprint character(64) NOT NULL,
                    action text NOT NULL,
                    admitted_at timestamp with time zone NOT NULL,
                    CONSTRAINT operation_admission_subject_key UNIQUE (subject_id, idempotency_key)
                );
                CREATE TABLE {Schema}.audit_admission (
                    audit_id uuid PRIMARY KEY,
                    operation_id uuid NOT NULL UNIQUE REFERENCES {Schema}.operation_admission(operation_id),
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    principal_kind smallint NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    admitted_at timestamp with time zone NOT NULL
                );
                """),
            new MigrationStep(
                2,
                "durable jobs",
                $"""
                CREATE TABLE {Schema}.durable_job (
                    job_id uuid PRIMARY KEY,
                    kind text NOT NULL,
                    payload jsonb NOT NULL,
                    available_at timestamp with time zone NOT NULL,
                    claimed_by text NULL,
                    lease_until timestamp with time zone NULL,
                    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
                    completed_at timestamp with time zone NULL
                );
                CREATE INDEX durable_job_claim_idx
                    ON {Schema}.durable_job (available_at, job_id)
                    WHERE completed_at IS NULL;
                """),
        ]);
}
