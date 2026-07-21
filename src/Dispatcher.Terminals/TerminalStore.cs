using System.Security.Cryptography;
using System.Text;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Terminals;

public sealed class TerminalStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly TerminalEnrollmentPolicy policy;

    public TerminalStore(
        NpgsqlDataSource dataSource, string databaseRole, IWallClock clock, TerminalEnrollmentPolicy policy)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        this.databaseRole = databaseRole;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<Result<TerminalEnrollmentChallenge>> InitiateEnrollmentAsync(
        InitiateTerminalEnrollment request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Length > 200)
            return Failure<TerminalEnrollmentChallenge>("terminal.label_invalid", "Terminal label is invalid.");
        var now = UtcNow();
        var challenge = RandomToken(16);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var terminal = new NpgsqlCommand(
                $"""
                INSERT INTO {TerminalMigrations.Schema}.terminal
                    (terminal_id, label, state, profile_id, version, last_seen_at, created_at, updated_at)
                VALUES (@terminal, @label, 1, NULL, 1, NULL, @now, @now);
                """, connection, transaction))
            {
                terminal.Parameters.AddWithValue("terminal", request.TerminalId.Value);
                terminal.Parameters.AddWithValue("label", request.Label.Trim());
                terminal.Parameters.AddWithValue("now", now);
                await terminal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var enrollment = new NpgsqlCommand(
                $"""
                INSERT INTO {TerminalMigrations.Schema}.enrollment
                    (enrollment_id, terminal_id, challenge_hash, state, expires_at,
                     approved_session_id, approved_subject_id, approved_at, consumed_at, created_at)
                VALUES (@enrollment, @terminal, @hash, 1, @expires, NULL, NULL, NULL, NULL, @now);
                """, connection, transaction))
            {
                enrollment.Parameters.AddWithValue("enrollment", request.EnrollmentId.Value);
                enrollment.Parameters.AddWithValue("terminal", request.TerminalId.Value);
                enrollment.Parameters.AddWithValue("hash", Hash(challenge));
                enrollment.Parameters.AddWithValue("expires", now.Add(policy.ChallengeLifetime));
                enrollment.Parameters.AddWithValue("now", now);
                await enrollment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Failure<TerminalEnrollmentChallenge>(
                "terminal.enrollment_conflict", "Terminal or enrollment identity already exists.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new TerminalEnrollmentChallenge(
            request.EnrollmentId, request.TerminalId, challenge, now.Add(policy.ChallengeLifetime)));
    }

    public async Task<Result<TerminalEnrollmentState>> ApproveEnrollmentAsync(
        AuthorizedMutation authorization, TerminalEnrollmentId enrollmentId,
        CancellationToken cancellationToken = default)
    {
        if (authorization.Permission != TerminalPermissions.ApproveEnrollment)
            return Failure<TerminalEnrollmentState>("terminal.permission_contract", "Enrollment approval authorization is invalid.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var enrollment = await ReadEnrollmentForUpdateAsync(connection, transaction, enrollmentId, cancellationToken).ConfigureAwait(false);
        if (enrollment is null)
            return Failure<TerminalEnrollmentState>("terminal.enrollment_not_found", "Enrollment was not found.");
        if (enrollment.ExpiresAt <= now)
            return Failure<TerminalEnrollmentState>("terminal.enrollment_expired", "Enrollment challenge has expired.");
        if (enrollment.State == (int)TerminalEnrollmentState.Consumed)
            return Failure<TerminalEnrollmentState>("terminal.enrollment_replay", "Enrollment challenge was already consumed.");
        if (enrollment.State == (int)TerminalEnrollmentState.Approved)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(TerminalEnrollmentState.Approved);
        }
        await using (var command = new NpgsqlCommand(
            $"""
            UPDATE {TerminalMigrations.Schema}.enrollment
            SET state = 2, approved_session_id = @session, approved_subject_id = @subject, approved_at = @now
            WHERE enrollment_id = @id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", enrollmentId.Value);
            command.Parameters.AddWithValue("session", authorization.Session.Id.Value);
            command.Parameters.AddWithValue("subject", authorization.Session.SubjectId.Value);
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteAuditAsync(connection, transaction, authorization, enrollment.TerminalId, null,
            "approve-enrollment", 1, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(TerminalEnrollmentState.Approved);
    }

    public async Task<Result<TerminalCredentialIssue>> ExchangeChallengeAsync(
        TerminalEnrollmentId enrollmentId, string challenge, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var enrollment = await ReadEnrollmentForUpdateAsync(connection, transaction, enrollmentId, cancellationToken).ConfigureAwait(false);
        if (enrollment is null)
            return Failure<TerminalCredentialIssue>("terminal.enrollment_not_found", "Enrollment was not found.");
        if (enrollment.ExpiresAt <= now)
            return Failure<TerminalCredentialIssue>("terminal.enrollment_expired", "Enrollment challenge has expired.");
        if (enrollment.State == (int)TerminalEnrollmentState.Consumed)
            return Failure<TerminalCredentialIssue>("terminal.enrollment_replay", "Enrollment challenge was already consumed.");
        if (enrollment.State != (int)TerminalEnrollmentState.Approved)
            return Failure<TerminalCredentialIssue>("terminal.enrollment_not_approved", "Enrollment is not approved.");
        if (!CryptographicOperations.FixedTimeEquals(enrollment.ChallengeHash, Hash(challenge)))
            return Failure<TerminalCredentialIssue>("terminal.challenge_invalid", "Enrollment challenge is invalid.");

        var identityId = TerminalDeviceIdentityId.New();
        var credential = $"{identityId.Value:N}.{RandomToken(32)}";
        var expiresAt = now.Add(policy.CredentialLifetime);
        await using (var identity = new NpgsqlCommand(
            $"""
            INSERT INTO {TerminalMigrations.Schema}.device_identity
                (device_identity_id, terminal_id, credential_hash, issued_at, expires_at, revoked_at)
            VALUES (@identity, @terminal, @hash, @now, @expires, NULL);
            """, connection, transaction))
        {
            identity.Parameters.AddWithValue("identity", identityId.Value);
            identity.Parameters.AddWithValue("terminal", enrollment.TerminalId);
            identity.Parameters.AddWithValue("hash", Hash(credential));
            identity.Parameters.AddWithValue("now", now);
            identity.Parameters.AddWithValue("expires", expiresAt);
            await identity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var update = new NpgsqlCommand(
            $"""
            UPDATE {TerminalMigrations.Schema}.enrollment SET state = 3, consumed_at = @now WHERE enrollment_id = @id;
            UPDATE {TerminalMigrations.Schema}.terminal SET state = 2, version = 2, updated_at = @now WHERE terminal_id = @terminal;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", enrollmentId.Value);
            update.Parameters.AddWithValue("terminal", enrollment.TerminalId);
            update.Parameters.AddWithValue("now", now);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteAuditAsync(connection, transaction,
            new AuditActor(enrollment.ApprovedSessionId!.Value, enrollment.ApprovedSubjectId!.Value),
            enrollment.TerminalId, null, "activate-device-identity", 2, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new TerminalCredentialIssue(
            identityId, TerminalId.From(enrollment.TerminalId), credential, expiresAt));
    }

    public async Task<Result<TerminalProfileSnapshot>> CreateProfileAsync(
        AuthorizedMutation authorization, CreateTerminalProfile request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (authorization.Permission != TerminalPermissions.Manage)
            return Failure<TerminalProfileSnapshot>("terminal.permission_contract", "Fleet management authorization is invalid.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            return Failure<TerminalProfileSnapshot>("terminal.profile_invalid", "Terminal profile is invalid.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {TerminalMigrations.Schema}.profile
                (profile_id, name, content_kind, content_id, version, created_at, updated_at)
            VALUES (@id, @name, NULL, NULL, 1, @now, @now);
            """, connection, transaction);
        command.Parameters.AddWithValue("id", request.ProfileId.Value);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("now", now);
        try { await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Failure<TerminalProfileSnapshot>("terminal.profile_conflict", "Terminal profile already exists."); }
        await WriteAuditAsync(connection, transaction, authorization, null, request.ProfileId.Value,
            "create-profile", 1, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new TerminalProfileSnapshot(request.ProfileId, request.Name.Trim(), null, StateVersion.Initial, now));
    }

    public async Task<Result<TerminalProfileSnapshot>> AssignContentAsync(
        AuthorizedMutation authorization, AssignTerminalContent request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (authorization.Permission != TerminalPermissions.Manage || !Enum.IsDefined(request.Content.Kind))
            return Failure<TerminalProfileSnapshot>("terminal.permission_contract", "Content assignment contract is invalid.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var profile = await ReadProfileAsync(connection, transaction, request.ProfileId.Value, true, cancellationToken).ConfigureAwait(false);
        if (profile is null || profile.Version != request.ExpectedVersion.Value)
            return Failure<TerminalProfileSnapshot>(profile is null ? "terminal.profile_not_found" : "terminal.version_conflict",
                profile is null ? "Terminal profile was not found." : "Terminal profile version changed.");
        var next = profile with { ContentKind = (int)request.Content.Kind, ContentId = request.Content.ContentId,
            Version = request.ExpectedVersion.Next().Value, UpdatedAt = now };
        await using (var command = new NpgsqlCommand(
            $"UPDATE {TerminalMigrations.Schema}.profile SET content_kind=@kind, content_id=@content, version=@version, updated_at=@now WHERE profile_id=@id;",
            connection, transaction))
        {
            command.Parameters.AddWithValue("id", request.ProfileId.Value);
            command.Parameters.AddWithValue("kind", checked((short)next.ContentKind!.Value));
            command.Parameters.AddWithValue("content", next.ContentId!.Value);
            command.Parameters.AddWithValue("version", checked((long)next.Version));
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteAuditAsync(connection, transaction, authorization, null, request.ProfileId.Value,
            "assign-content", next.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(next.ToModel());
    }

    public async Task<Result<TerminalSnapshot>> AssignProfileAsync(
        AuthorizedMutation authorization, AssignTerminalProfile request,
        CancellationToken cancellationToken = default) =>
        await MutateTerminalAsync(authorization, request.TerminalId, request.ExpectedVersion,
            "assign-profile", null, request.ProfileId, cancellationToken).ConfigureAwait(false);

    public async Task<Result<TerminalSnapshot>> BlockAsync(
        AuthorizedMutation authorization, ChangeTerminalState request,
        CancellationToken cancellationToken = default) =>
        await MutateTerminalAsync(authorization, request.TerminalId, request.ExpectedVersion,
            "block-terminal", TerminalState.Blocked, null, cancellationToken).ConfigureAwait(false);

    public async Task<Result<TerminalSnapshot>> RevokeAsync(
        AuthorizedMutation authorization, ChangeTerminalState request,
        CancellationToken cancellationToken = default) =>
        await MutateTerminalAsync(authorization, request.TerminalId, request.ExpectedVersion,
            "revoke-terminal", TerminalState.Revoked, null, cancellationToken).ConfigureAwait(false);

    public async Task<Result<AuthenticatedTerminal>> AuthenticateAsync(
        TerminalCredentialPresentation? presentation, CancellationToken cancellationToken = default)
    {
        if (presentation is null || !string.Equals(
                presentation.Scheme, TerminalCredentialPresentation.RequiredScheme, StringComparison.Ordinal) ||
            !TryReadIdentity(presentation.Credential, out var identityId))
            return Failure<AuthenticatedTerminal>("terminal.authentication_required", "A terminal authorization header is required.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var identity = await ReadIdentityAsync(connection, transaction, identityId, cancellationToken).ConfigureAwait(false);
        if (identity is null || identity.RevokedAt is not null || identity.ExpiresAt <= now ||
            !CryptographicOperations.FixedTimeEquals(identity.CredentialHash, Hash(presentation.Credential)))
            return Failure<AuthenticatedTerminal>(identity?.ExpiresAt <= now ? "terminal.credential_expired" : "terminal.credential_invalid",
                identity?.ExpiresAt <= now ? "Terminal credential has expired." : "Terminal credential is invalid or revoked.");
        var terminal = await ReadTerminalAsync(connection, transaction, identity.TerminalId, false, cancellationToken).ConfigureAwait(false);
        if (terminal is null || terminal.State is not (int)TerminalState.Active)
            return Failure<AuthenticatedTerminal>("terminal.not_active", "Terminal is blocked, revoked, or not active.");
        ProfileDto? profile = terminal.ProfileId is null ? null :
            await ReadProfileAsync(connection, transaction, terminal.ProfileId.Value, false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new AuthenticatedTerminal(
            TerminalDeviceIdentityId.From(identity.DeviceIdentityId), terminal.ToModel(), profile?.ToModel(), profile?.Content));
    }

    public async Task<Result<DateTimeOffset>> RecordPresenceAsync(
        AuthenticatedTerminal context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {TerminalMigrations.Schema}.terminal AS terminal
            SET last_seen_at = @now
            FROM {TerminalMigrations.Schema}.device_identity AS identity
            WHERE terminal.terminal_id = @terminal AND terminal.state = 2
              AND identity.device_identity_id = @identity AND identity.terminal_id = terminal.terminal_id
              AND identity.revoked_at IS NULL AND identity.expires_at > @now;
            """, connection, transaction);
        command.Parameters.AddWithValue("terminal", context.Terminal.TerminalId.Value);
        command.Parameters.AddWithValue("identity", context.DeviceIdentityId.Value);
        command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            return Failure<DateTimeOffset>("terminal.not_active", "Terminal presence is no longer authorized.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(now);
    }

    public async Task<Result<IReadOnlyList<TerminalSnapshot>>> ReadFleetAsync(
        AuthorizedAccess authorization, CancellationToken cancellationToken = default)
    {
        if (authorization.Permission != TerminalPermissions.Read)
            return Failure<IReadOnlyList<TerminalSnapshot>>("terminal.permission_contract", "Fleet read authorization is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT terminal_id,label,state,profile_id,version,last_seen_at,created_at,updated_at FROM {TerminalMigrations.Schema}.terminal ORDER BY label,terminal_id;",
            connection, transaction);
        var result = new List<TerminalSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadTerminal(reader).ToModel());
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<TerminalSnapshot>>(result);
    }

    private async Task<Result<TerminalSnapshot>> MutateTerminalAsync(
        AuthorizedMutation authorization, TerminalId terminalId, StateVersion expectedVersion,
        string action, TerminalState? state, TerminalProfileId? profileId, CancellationToken cancellationToken)
    {
        if (authorization.Permission != TerminalPermissions.Manage)
            return Failure<TerminalSnapshot>("terminal.permission_contract", "Fleet management authorization is invalid.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var terminal = await ReadTerminalAsync(connection, transaction, terminalId.Value, true, cancellationToken).ConfigureAwait(false);
        if (terminal is null || terminal.Version != expectedVersion.Value)
            return Failure<TerminalSnapshot>(terminal is null ? "terminal.not_found" : "terminal.version_conflict",
                terminal is null ? "Terminal was not found." : "Terminal version changed.");
        if (terminal.State == (int)TerminalState.Revoked || state == TerminalState.Blocked && terminal.State != (int)TerminalState.Active)
            return Failure<TerminalSnapshot>("terminal.state_invalid", "Terminal state transition is invalid.");
        if (profileId is not null && await ReadProfileAsync(connection, transaction, profileId.Value.Value, false, cancellationToken).ConfigureAwait(false) is null)
            return Failure<TerminalSnapshot>("terminal.profile_not_found", "Terminal profile was not found.");
        var next = terminal with { State = state is null ? terminal.State : (int)state.Value,
            ProfileId = profileId?.Value ?? terminal.ProfileId, Version = expectedVersion.Next().Value, UpdatedAt = now };
        await using (var command = new NpgsqlCommand(
            $"UPDATE {TerminalMigrations.Schema}.terminal SET state=@state, profile_id=@profile, version=@version, updated_at=@now WHERE terminal_id=@id;",
            connection, transaction))
        {
            command.Parameters.AddWithValue("id", terminalId.Value);
            command.Parameters.AddWithValue("state", checked((short)next.State));
            command.Parameters.Add(new NpgsqlParameter("profile", NpgsqlDbType.Uuid) { Value = next.ProfileId ?? (object)DBNull.Value });
            command.Parameters.AddWithValue("version", checked((long)next.Version));
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (state == TerminalState.Revoked)
        {
            await using var revoke = new NpgsqlCommand(
                $"UPDATE {TerminalMigrations.Schema}.device_identity SET revoked_at=@now WHERE terminal_id=@id AND revoked_at IS NULL;",
                connection, transaction);
            revoke.Parameters.AddWithValue("id", terminalId.Value);
            revoke.Parameters.AddWithValue("now", now);
            await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteAuditAsync(connection, transaction, authorization, terminalId.Value, profileId?.Value,
            action, next.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(next.ToModel());
    }

    private static async Task<EnrollmentDto?> ReadEnrollmentForUpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TerminalEnrollmentId id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT terminal_id,challenge_hash,state,expires_at,approved_session_id,approved_subject_id FROM {TerminalMigrations.Schema}.enrollment WHERE enrollment_id=@id FOR UPDATE;",
            connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new EnrollmentDto(
            reader.GetGuid(0), reader.GetFieldValue<byte[]>(1), reader.GetInt16(2), reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetGuid(5)) : null;
    }

    private static async Task<TerminalDto?> ReadTerminalAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, bool forUpdate, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT terminal_id,label,state,profile_id,version,last_seen_at,created_at,updated_at FROM {TerminalMigrations.Schema}.terminal WHERE terminal_id=@id{(forUpdate ? " FOR UPDATE" : string.Empty)};",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadTerminal(reader) : null;
    }

    private static TerminalDto ReadTerminal(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
        checked((ulong)reader.GetInt64(4)), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7));

    private static async Task<ProfileDto?> ReadProfileAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, bool forUpdate, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT profile_id,name,content_kind,content_id,version,updated_at FROM {TerminalMigrations.Schema}.profile WHERE profile_id=@id{(forUpdate ? " FOR UPDATE" : string.Empty)};",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new ProfileDto(
            reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt16(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3), checked((ulong)reader.GetInt64(4)),
            reader.GetFieldValue<DateTimeOffset>(5)) : null;
    }

    private static async Task<IdentityDto?> ReadIdentityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT device_identity_id,terminal_id,credential_hash,expires_at,revoked_at FROM {TerminalMigrations.Schema}.device_identity WHERE device_identity_id=@id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new IdentityDto(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)) : null;
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AuthorizedMutation actor,
        Guid? terminalId, Guid? profileId, string action, ulong version, DateTimeOffset now, CancellationToken token) =>
        await WriteAuditAsync(connection, transaction,
            new AuditActor(actor.Session.Id.Value, actor.Session.SubjectId.Value), terminalId, profileId,
            action, version, now, token).ConfigureAwait(false);

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor actor,
        Guid? terminalId, Guid? profileId, string action, ulong version, DateTimeOffset now, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {TerminalMigrations.Schema}.mutation_audit (audit_id,terminal_id,profile_id,session_id,subject_id,action,resulting_version,changed_at) VALUES (@audit,@terminal,@profile,@session,@subject,@action,@version,@now);",
            connection, transaction);
        command.Parameters.AddWithValue("audit", Guid.NewGuid());
        command.Parameters.Add(new NpgsqlParameter("terminal", NpgsqlDbType.Uuid) { Value = terminalId ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("profile", NpgsqlDbType.Uuid) { Value = profileId ?? (object)DBNull.Value });
        command.Parameters.AddWithValue("session", actor.SessionId);
        command.Parameters.AddWithValue("subject", actor.SubjectId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", checked((long)version));
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow();
        return now.Offset == TimeSpan.Zero ? now : throw new InvalidOperationException("Terminal store requires UTC.");
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryReadIdentity(string credential, out Guid id)
    {
        id = default;
        var separator = credential.IndexOf('.');
        return separator == 32 && Guid.TryParseExact(credential[..separator], "N", out id) && separator < credential.Length - 1;
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record EnrollmentDto(Guid TerminalId, byte[] ChallengeHash, int State, DateTimeOffset ExpiresAt,
        Guid? ApprovedSessionId, Guid? ApprovedSubjectId);
    private sealed record IdentityDto(Guid DeviceIdentityId, Guid TerminalId, byte[] CredentialHash,
        DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
    private sealed record AuditActor(Guid SessionId, Guid SubjectId);
    private sealed record TerminalDto(Guid TerminalId, string Label, int State, Guid? ProfileId, ulong Version,
        DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public TerminalSnapshot ToModel() => new(Dispatcher.Terminals.TerminalId.From(TerminalId), Label,
            (TerminalState)State, ProfileId is null ? null : TerminalProfileId.From(ProfileId.Value),
            StateVersion.From(Version), LastSeenAt, CreatedAt, UpdatedAt);
    }
    private sealed record ProfileDto(Guid ProfileId, string Name, int? ContentKind, Guid? ContentId,
        ulong Version, DateTimeOffset UpdatedAt)
    {
        public TerminalContentAssignment? Content => ContentKind is null ? null : new((TerminalContentKind)ContentKind, ContentId!.Value);
        public TerminalProfileSnapshot ToModel() => new(TerminalProfileId.From(ProfileId), Name, Content,
            StateVersion.From(Version), UpdatedAt);
    }
}
