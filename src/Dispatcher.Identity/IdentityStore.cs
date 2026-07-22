using System.Security.Cryptography;
using System.Text;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Identity;

public sealed class IdentityStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly IdentitySecurityPolicy policy;

    public IdentityStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock, IdentitySecurityPolicy policy)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        this.databaseRole = databaseRole;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<Result<IdentityAccountSnapshot>> BootstrapAdministratorAsync(
        BootstrapLocalAdministrator request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCredentials(request.UserName, request.Password);
        if (validation.IsFailure) return Result.Failure<IdentityAccountSnapshot>(validation.Error!);
        var now = UtcNow();
        var salt = RandomNumberGenerator.GetBytes(16);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(73035);", connection, transaction))
            await gate.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {IdentityMigrations.Schema}.account;", connection, transaction))
            if ((long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0)
                return Failure<IdentityAccountSnapshot>("identity.bootstrap_closed", "Local account bootstrap is already closed.");
        await InsertRoleAsync(connection, transaction, request.AdministratorRoleId, "Dispatcher Administrators",
            [
                new(IdentityPermissions.Administer, null),
                new(IdentityPermissions.ReadDiagnostics, null),
                new(PermissionCode.From("administration.health.read"), null),
                new(PermissionCode.From("administration.data-quality.read"), null),
                new(PermissionCode.From("administration.audit.read"), null),
            ], now, token).ConfigureAwait(false);
        var account = new AccountDto(
            request.AccountId.Value, request.SubjectId.Value, request.WorkspaceAccountId?.Value, null,
            request.UserName.Trim(), Normalize(request.UserName), salt,
            PasswordHash(request.Password, salt, policy.PasswordIterations), policy.PasswordIterations,
            true, 0, null, 1, 1, now, now);
        await InsertAccountAsync(connection, transaction, account, token).ConfigureAwait(false);
        await InsertRelationAsync(connection, transaction, "account_role", "account_id", account.AccountId,
            "role_id", request.AdministratorRoleId.Value, token).ConfigureAwait(false);
        await using (var diagnostic = new NpgsqlCommand(
            $"INSERT INTO {IdentityMigrations.Schema}.integration_diagnostic (integration_kind,status,summary,secret_configured,checked_at) VALUES (1,2,'Local Dispatcher authentication configured.',true,@now);",
            connection, transaction))
        { diagnostic.Parameters.AddWithValue("now", now); await diagnostic.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await WriteAuditAsync(connection, transaction, null, null, "bootstrap-administrator", "account", account.AccountId, now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(account.ToModel());
    }

    public async Task<Result<ProductionSessionIssue>> LoginAsync(
        LocalLoginRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var account = await ReadAccountByNameAsync(connection, transaction, Normalize(request.UserName), true, token).ConfigureAwait(false);
        if (account is null || !account.Enabled || account.LockedUntil > now)
            return InvalidLogin<ProductionSessionIssue>();
        var verified = CryptographicOperations.FixedTimeEquals(
            account.PasswordHash, PasswordHash(request.Password, account.PasswordSalt, account.PasswordIterations));
        if (!verified)
        {
            var failures = account.FailedLoginCount + 1;
            var lockedUntil = failures >= policy.MaximumFailedAttempts ? now.Add(policy.LockoutDuration) : account.LockedUntil;
            await UpdateLoginFailureAsync(connection, transaction, account.AccountId,
                failures >= policy.MaximumFailedAttempts ? 0 : failures, lockedUntil, now, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return InvalidLogin<ProductionSessionIssue>();
        }
        await UpdateLoginFailureAsync(connection, transaction, account.AccountId, 0, null, now, token).ConfigureAwait(false);
        var issue = await IssueSessionAsync(connection, transaction, account, SessionId.New(), now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(issue);
    }

    public async Task<Result<ProductionSessionIssue>> RefreshAsync(
        RefreshProductionSession request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryParseToken(request.RefreshToken, out var sessionId)) return InvalidSession<ProductionSessionIssue>();
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var row = await ReadSessionAsync(connection, transaction, sessionId, true, token).ConfigureAwait(false);
        if (row is null || row.RevokedAt is not null || row.RefreshExpiresAt <= now || !row.Account.Enabled ||
            row.AuthorizationVersion != row.Account.AuthorizationVersion ||
            !CryptographicOperations.FixedTimeEquals(row.RefreshHash, Hash(request.RefreshToken)))
            return InvalidSession<ProductionSessionIssue>();
        var issue = await IssueSessionAsync(connection, transaction, row.Account, SessionId.From(sessionId), now, token, replace: true)
            .ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(issue);
    }

    public async Task<Result<SessionSnapshot>> ResolveAccessAsync(
        ProductionAccessPresentation? presentation, CancellationToken token = default)
    {
        if (presentation is null || !string.Equals(presentation.Scheme, ProductionAccessPresentation.RequiredScheme, StringComparison.Ordinal) ||
            !TryParseToken(presentation.Token, out var sessionId)) return InvalidSession<SessionSnapshot>();
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var row = await ReadSessionAsync(connection, transaction, sessionId, false, token).ConfigureAwait(false);
        if (row is null || row.RevokedAt is not null || row.ExpiresAt <= now || !row.Account.Enabled ||
            row.AuthorizationVersion != row.Account.AuthorizationVersion ||
            !CryptographicOperations.FixedTimeEquals(row.AccessHash, Hash(presentation.Token)))
            return InvalidSession<SessionSnapshot>();
        var permissions = await ReadEffectivePermissionsAsync(connection, transaction, row.Account.AccountId, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new SessionSnapshot(
            SessionId.From(row.SessionId), SubjectId.From(row.Account.SubjectId), PrincipalKind.User,
            row.IssuedAt, row.ExpiresAt, permissions));
    }

    public async Task<Result> RevokeAsync(string accessToken, CancellationToken token = default)
    {
        if (!TryParseToken(accessToken, out var sessionId)) return InvalidSession();
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"UPDATE {IdentityMigrations.Schema}.production_session SET revoked_at=@now WHERE session_id=@id AND access_token_hash=@hash AND revoked_at IS NULL;",
            connection, transaction);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("hash", Hash(accessToken));
        command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return InvalidSession();
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<IdentityScopeSnapshot>> CreateScopeAsync(
        AuthorizedMutation authorization, CreateIdentityScope request, CancellationToken token = default)
    {
        var denied = RequireAdmin<IdentityScopeSnapshot>(authorization); if (denied is not null) return denied;
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            return Failure<IdentityScopeSnapshot>("identity.scope_invalid", "Access scope is invalid.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {IdentityMigrations.Schema}.access_scope (scope_id,name,parent_scope_id) VALUES (@id,@name,@parent);", connection, transaction);
        command.Parameters.AddWithValue("id", request.ScopeId.Value); command.Parameters.AddWithValue("name", request.Name.Trim());
        AddNullableUuid(command, "parent", request.ParentScopeId?.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, authorization.Session.Id.Value, authorization.Session.SubjectId.Value,
            "create-scope", "scope", request.ScopeId.Value, now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new IdentityScopeSnapshot(request.ScopeId, request.Name.Trim(), request.ParentScopeId));
    }

    public async Task<Result<IdentityRoleSnapshot>> CreateRoleAsync(
        AuthorizedMutation authorization, CreateIdentityRole request, CancellationToken token = default)
    {
        var denied = RequireAdmin<IdentityRoleSnapshot>(authorization); if (denied is not null) return denied;
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidNamedGrants(request.Name, request.Grants)) return Failure<IdentityRoleSnapshot>("identity.role_invalid", "Role is invalid.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await InsertRoleAsync(connection, transaction, request.RoleId, request.Name.Trim(), request.Grants, now, token).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, authorization.Session.Id.Value, authorization.Session.SubjectId.Value,
            "create-role", "role", request.RoleId.Value, now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new IdentityRoleSnapshot(request.RoleId, request.Name.Trim(), request.Grants.ToArray(), StateVersion.Initial, now));
    }

    public async Task<Result<IdentityGroupSnapshot>> CreateGroupAsync(
        AuthorizedMutation authorization, CreateIdentityGroup request, CancellationToken token = default)
    {
        var denied = RequireAdmin<IdentityGroupSnapshot>(authorization); if (denied is not null) return denied;
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            return Failure<IdentityGroupSnapshot>("identity.group_invalid", "Group is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {IdentityMigrations.Schema}.identity_group (group_id,name,version) VALUES (@id,@name,1);", connection, transaction);
        command.Parameters.AddWithValue("id", request.GroupId.Value); command.Parameters.AddWithValue("name", request.Name.Trim());
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new IdentityGroupSnapshot(request.GroupId, request.Name.Trim(), StateVersion.Initial));
    }

    public async Task<Result<IdentityAccountSnapshot>> CreateAccountAsync(
        AuthorizedMutation authorization, CreateIdentityAccount request, CancellationToken token = default)
    {
        var denied = RequireAdmin<IdentityAccountSnapshot>(authorization); if (denied is not null) return denied;
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCredentials(request.UserName, request.Password);
        if (validation.IsFailure) return Result.Failure<IdentityAccountSnapshot>(validation.Error!);
        var now = UtcNow(); var salt = RandomNumberGenerator.GetBytes(16);
        var account = new AccountDto(request.AccountId.Value, request.SubjectId.Value, request.WorkspaceAccountId?.Value,
            request.PrimaryScopeId?.Value, request.UserName.Trim(), Normalize(request.UserName), salt,
            PasswordHash(request.Password, salt, policy.PasswordIterations), policy.PasswordIterations,
            true, 0, null, 1, 1, now, now);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await InsertAccountAsync(connection, transaction, account, token).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, authorization.Session.Id.Value, authorization.Session.SubjectId.Value,
            "create-account", "account", account.AccountId, now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(account.ToModel());
    }

    public Task<Result> AssignRoleToAccountAsync(
        AuthorizedMutation authorization, IdentityAccountId accountId, IdentityRoleId roleId,
        CancellationToken token = default) =>
        AddAdministrationRelationAsync(authorization, "account_role", "account_id", accountId.Value,
            "role_id", roleId.Value, [accountId.Value], "assign-account-role", token);

    public Task<Result> AssignRoleToGroupAsync(
        AuthorizedMutation authorization, IdentityGroupId groupId, IdentityRoleId roleId,
        CancellationToken token = default) =>
        AddGroupRelationAndInvalidateAsync(authorization, "group_role", "group_id", groupId.Value,
            "role_id", roleId.Value, groupId, "assign-group-role", token);

    public Task<Result> AddGroupMemberAsync(
        AuthorizedMutation authorization, IdentityGroupId groupId, IdentityAccountId accountId,
        CancellationToken token = default) =>
        AddAdministrationRelationAsync(authorization, "group_member", "group_id", groupId.Value,
            "account_id", accountId.Value, [accountId.Value], "add-group-member", token);

    public async Task<Result<RoleImpactPreview>> PreviewRolePermissionsAsync(
        AuthorizedAccess authorization, IdentityRoleId roleId, IReadOnlyCollection<IdentityPermissionGrant> grants,
        CancellationToken token = default)
    {
        if (authorization.Permission != IdentityPermissions.Administer)
            return Failure<RoleImpactPreview>("permission.denied", "Identity administration permission is required.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var current = await ReadRoleGrantsAsync(connection, transaction, roleId.Value, token).ConfigureAwait(false);
        if (current is null) return Failure<RoleImpactPreview>("identity.role_not_found", "Role was not found.");
        var accounts = await ReadAffectedAccountIdsAsync(connection, transaction, roleId.Value, token).ConfigureAwait(false);
        var active = await CountActiveSessionsAsync(connection, transaction, accounts, UtcNow(), token).ConfigureAwait(false);
        var currentCodes = current.Select(value => value.Permission).ToHashSet();
        var nextCodes = grants.Select(value => value.Permission).ToHashSet();
        var added = nextCodes.Except(currentCodes).OrderBy(value => value.Value).ToArray();
        var removed = currentCodes.Except(nextCodes).OrderBy(value => value.Value).ToArray();
        var fingerprint = PreviewFingerprint(roleId, grants, accounts);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new RoleImpactPreview(roleId, added, removed, accounts.Count, active, fingerprint));
    }

    public async Task<Result<IdentityRoleSnapshot>> UpdateRolePermissionsAsync(
        AuthorizedMutation authorization, IdentityRoleId roleId, IReadOnlyCollection<IdentityPermissionGrant> grants,
        StateVersion expectedVersion, string previewFingerprint, CancellationToken token = default)
    {
        var denied = RequireAdmin<IdentityRoleSnapshot>(authorization); if (denied is not null) return denied;
        ArgumentNullException.ThrowIfNull(grants);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var role = await ReadRoleAsync(connection, transaction, roleId.Value, true, token).ConfigureAwait(false);
        if (role is null || role.Version != expectedVersion.Value)
            return Failure<IdentityRoleSnapshot>(role is null ? "identity.role_not_found" : "identity.version_conflict",
                role is null ? "Role was not found." : "Role version changed.");
        var accounts = await ReadAffectedAccountIdsAsync(connection, transaction, roleId.Value, token).ConfigureAwait(false);
        if (!string.Equals(previewFingerprint, PreviewFingerprint(roleId, grants, accounts), StringComparison.Ordinal))
            return Failure<IdentityRoleSnapshot>("identity.impact_preview_stale", "Role impact preview is stale.");
        var current = await ReadRoleGrantsAsync(connection, transaction, roleId.Value, token).ConfigureAwait(false) ?? [];
        if (current.Any(value => value.Permission == IdentityPermissions.Administer) &&
            !grants.Any(value => value.Permission == IdentityPermissions.Administer) &&
            await CountOtherAdministratorsAsync(connection, transaction, roleId.Value, token).ConfigureAwait(false) == 0)
            return Failure<IdentityRoleSnapshot>("identity.last_administrator", "The final effective administrator cannot be removed.");
        await using (var delete = new NpgsqlCommand(
            $"DELETE FROM {IdentityMigrations.Schema}.role_permission WHERE role_id=@role;", connection, transaction))
        { delete.Parameters.AddWithValue("role", roleId.Value); await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await InsertRoleGrantsAsync(connection, transaction, roleId.Value, grants, token).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand(
            $"UPDATE {IdentityMigrations.Schema}.role SET version=version+1,updated_at=@now WHERE role_id=@role;", connection, transaction))
        { update.Parameters.AddWithValue("role", roleId.Value); update.Parameters.AddWithValue("now", now); await update.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await InvalidateAccountsAsync(connection, transaction, accounts, now, token).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, authorization.Session.Id.Value, authorization.Session.SubjectId.Value,
            "update-role-permissions", "role", roleId.Value, now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new IdentityRoleSnapshot(roleId, role.Name, grants.ToArray(), expectedVersion.Next(), now));
    }

    public async Task<Result> SetAccountEnabledAsync(
        AuthorizedMutation authorization, IdentityAccountId accountId, bool enabled, StateVersion expectedVersion,
        CancellationToken token = default)
    {
        var denied = RequireAdmin(authorization); if (denied is not null) return denied;
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var account = await ReadAccountByIdAsync(connection, transaction, accountId.Value, true, token).ConfigureAwait(false);
        if (account is null || account.Version != expectedVersion.Value)
            return Failure(account is null ? "identity.account_not_found" : "identity.version_conflict",
                account is null ? "Account was not found." : "Account version changed.");
        if (!enabled && await IsEffectiveAdministratorAsync(connection, transaction, accountId.Value, token).ConfigureAwait(false) &&
            await CountOtherEnabledAdministratorsAsync(connection, transaction, accountId.Value, token).ConfigureAwait(false) == 0)
            return Failure("identity.last_administrator", "The final effective administrator cannot be disabled.");
        await using var command = new NpgsqlCommand(
            $"UPDATE {IdentityMigrations.Schema}.account SET enabled=@enabled,authorization_version=authorization_version+1,version=version+1,updated_at=@now WHERE account_id=@id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", accountId.Value); command.Parameters.AddWithValue("enabled", enabled); command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    public Task<Result> SetGlobalSettingAsync(AuthorizedMutation authorization, string key, string value, CancellationToken token = default) =>
        SetSettingAsync(authorization, "setting_definition", null, key, value, token);
    public Task<Result> SetScopeSettingAsync(AuthorizedMutation authorization, IdentityScopeId scopeId, string key, string value, CancellationToken token = default) =>
        SetSettingAsync(authorization, "scope_setting", scopeId.Value, key, value, token);
    public Task<Result> SetGroupSettingAsync(AuthorizedMutation authorization, IdentityGroupId groupId, string key, string value, CancellationToken token = default) =>
        SetSettingAsync(authorization, "group_setting", groupId.Value, key, value, token);
    public Task<Result> SetAccountSettingAsync(AuthorizedMutation authorization, IdentityAccountId accountId, string key, string value, CancellationToken token = default) =>
        SetSettingAsync(authorization, "account_setting", accountId.Value, key, value, token);

    public async Task<Result> SetAccountPermissionOverrideAsync(
        AuthorizedMutation authorization, IdentityAccountId accountId, PermissionCode permission, bool allowed,
        CancellationToken token = default)
    {
        var denied = RequireAdmin(authorization); if (denied is not null) return denied;
        ArgumentNullException.ThrowIfNull(permission);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var account = await ReadAccountByIdAsync(connection, transaction, accountId.Value, true, token).ConfigureAwait(false);
        if (account is null) return Failure("identity.account_not_found", "Identity account was not found.");
        if (!allowed && permission == IdentityPermissions.Administer &&
            await IsEffectiveAdministratorAsync(connection, transaction, accountId.Value, token).ConfigureAwait(false) &&
            await CountOtherEnabledAdministratorsAsync(connection, transaction, accountId.Value, token).ConfigureAwait(false) == 0)
            return Failure("identity.last_administrator", "The final effective administrator cannot be denied.");
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {IdentityMigrations.Schema}.account_permission_override (account_id,permission_code,allowed)
            VALUES (@account,@permission,@allowed)
            ON CONFLICT (account_id,permission_code) DO UPDATE SET allowed=EXCLUDED.allowed;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("account", accountId.Value);
            command.Parameters.AddWithValue("permission", permission.Value);
            command.Parameters.AddWithValue("allowed", allowed);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await InvalidateAccountsAsync(connection, transaction, [accountId.Value], now, token).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, authorization.Session.Id.Value, authorization.Session.SubjectId.Value,
            allowed ? "grant-account-permission" : "deny-account-permission", "account", accountId.Value, now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<ResolvedIdentitySetting>> ResolveSettingAsync(
        SessionSnapshot session, string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var account = await ReadAccountBySubjectAsync(connection, transaction, session.SubjectId.Value, token).ConfigureAwait(false);
        if (account is null) return Failure<ResolvedIdentitySetting>("identity.account_not_found", "Identity account was not found.");
        var direct = await ReadSettingValueAsync(connection, transaction, "account_setting", "account_id", account.AccountId, key, token).ConfigureAwait(false);
        if (direct is not null) return Result.Success(new ResolvedIdentitySetting(key, direct, "Account", account.AccountId));
        var groups = await ReadGroupSettingValuesAsync(connection, transaction, account.AccountId, key, token).ConfigureAwait(false);
        if (groups.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            return Failure<ResolvedIdentitySetting>("identity.setting_conflict", "Inherited group settings conflict.");
        if (groups.Count > 0) return Result.Success(new ResolvedIdentitySetting(key, groups[0].Value, "Group", groups[0].Id));
        var scoped = account.PrimaryScopeId is null ? null : await ReadNearestScopeSettingAsync(
            connection, transaction, account.PrimaryScopeId.Value, key, token).ConfigureAwait(false);
        if (scoped is not null) return Result.Success(new ResolvedIdentitySetting(key, scoped.Value.Value, "Scope", scoped.Value.Id));
        await using var global = new NpgsqlCommand(
            $"SELECT global_value FROM {IdentityMigrations.Schema}.setting_definition WHERE setting_key=@key;", connection, transaction);
        global.Parameters.AddWithValue("key", key);
        var value = (string?)await global.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is null ? Failure<ResolvedIdentitySetting>("identity.setting_not_found", "Setting was not found.")
            : Result.Success(new ResolvedIdentitySetting(key, value, "Global", null));
    }

    public async Task<Result<IdentityIntegrationDiagnostic>> ReadIntegrationDiagnosticAsync(
        AuthorizedAccess authorization, CancellationToken token = default)
    {
        if (authorization.Permission != IdentityPermissions.ReadDiagnostics)
            return Failure<IdentityIntegrationDiagnostic>("permission.denied", "Identity diagnostics permission is required.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT integration_kind,status,summary,secret_configured,checked_at FROM {IdentityMigrations.Schema}.integration_diagnostic WHERE integration_kind=1;",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? Result.Success(new IdentityIntegrationDiagnostic((IdentityIntegrationKind)reader.GetInt16(0),
                (IdentityIntegrationStatus)reader.GetInt16(1), reader.GetString(2), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4)))
            : Failure<IdentityIntegrationDiagnostic>("identity.diagnostic_not_found", "Integration diagnostic was not found.");
    }

    private async Task<Result> AddAdministrationRelationAsync(
        AuthorizedMutation authorization, string table, string leftColumn, Guid left, string rightColumn, Guid right,
        IReadOnlyCollection<Guid> affectedAccounts, string action, CancellationToken token)
    {
        var denied = RequireAdmin(authorization); if (denied is not null) return denied;
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await InsertRelationAsync(connection, transaction, table, leftColumn, left, rightColumn, right, token).ConfigureAwait(false);
        await InvalidateAccountsAsync(connection, transaction, affectedAccounts, now, token).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, authorization.Session.Id.Value, authorization.Session.SubjectId.Value,
            action, table, left, now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> AddGroupRelationAndInvalidateAsync(
        AuthorizedMutation authorization, string table, string leftColumn, Guid left, string rightColumn, Guid right,
        IdentityGroupId groupId, string action, CancellationToken token)
    {
        var denied = RequireAdmin(authorization); if (denied is not null) return denied;
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await InsertRelationAsync(connection, transaction, table, leftColumn, left, rightColumn, right, token).ConfigureAwait(false);
        var accounts = await ReadGroupAccountIdsAsync(connection, transaction, groupId.Value, token).ConfigureAwait(false);
        await InvalidateAccountsAsync(connection, transaction, accounts, UtcNow(), token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> SetSettingAsync(
        AuthorizedMutation authorization, string table, Guid? targetId, string key, string value, CancellationToken token)
    {
        var denied = RequireAdmin(authorization); if (denied is not null) return denied;
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || value.Length > 4000)
            return Failure("identity.setting_invalid", "Setting is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        NpgsqlCommand command;
        if (table == "setting_definition")
        {
            command = new NpgsqlCommand($"INSERT INTO {IdentityMigrations.Schema}.setting_definition (setting_key,global_value) VALUES (@key,@value) ON CONFLICT (setting_key) DO UPDATE SET global_value=EXCLUDED.global_value;", connection, transaction);
        }
        else
        {
            var column = table switch { "scope_setting" => "scope_id", "group_setting" => "group_id", _ => "account_id" };
            command = new NpgsqlCommand($"INSERT INTO {IdentityMigrations.Schema}.{table} ({column},setting_key,setting_value) VALUES (@target,@key,@value) ON CONFLICT ({column},setting_key) DO UPDATE SET setting_value=EXCLUDED.setting_value;", connection, transaction);
            command.Parameters.AddWithValue("target", targetId!.Value);
        }
        await using (command)
        {
            command.Parameters.AddWithValue("key", key.Trim()); command.Parameters.AddWithValue("value", value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<ProductionSessionIssue> IssueSessionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AccountDto account, SessionId sessionId,
        DateTimeOffset now, CancellationToken token, bool replace = false)
    {
        var access = $"{sessionId.Value:N}.{RandomToken(32)}";
        var refresh = $"{sessionId.Value:N}.{RandomToken(32)}";
        var expires = now.Add(policy.AccessLifetime); var refreshExpires = now.Add(policy.RefreshLifetime);
        var permissions = await ReadEffectivePermissionsAsync(connection, transaction, account.AccountId, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(replace
            ? $"UPDATE {IdentityMigrations.Schema}.production_session SET access_token_hash=@access,refresh_token_hash=@refresh,authorization_version=@auth,issued_at=@now,expires_at=@expires,refresh_expires_at=@refresh_expires,refreshed_at=@now,revoked_at=NULL WHERE session_id=@id;"
            : $"INSERT INTO {IdentityMigrations.Schema}.production_session (session_id,account_id,access_token_hash,refresh_token_hash,authorization_version,issued_at,expires_at,refresh_expires_at,refreshed_at,revoked_at) VALUES (@id,@account,@access,@refresh,@auth,@now,@expires,@refresh_expires,NULL,NULL);",
            connection, transaction);
        command.Parameters.AddWithValue("id", sessionId.Value); if (!replace) command.Parameters.AddWithValue("account", account.AccountId);
        command.Parameters.AddWithValue("access", Hash(access)); command.Parameters.AddWithValue("refresh", Hash(refresh));
        command.Parameters.AddWithValue("auth", checked((long)account.AuthorizationVersion)); command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", expires); command.Parameters.AddWithValue("refresh_expires", refreshExpires);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return new ProductionSessionIssue(IdentityAccountId.From(account.AccountId),
            new SessionSnapshot(sessionId, SubjectId.From(account.SubjectId), PrincipalKind.User, now, expires, permissions),
            access, refresh, refreshExpires);
    }

    private static async Task<EffectivePermissions> ReadEffectivePermissionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, CancellationToken token)
    {
        var grants = new HashSet<PermissionCode>(); var denials = new HashSet<PermissionCode>();
        await using var command = new NpgsqlCommand($"""
            SELECT DISTINCT permission_code FROM {IdentityMigrations.Schema}.role_permission
            WHERE role_id IN (
                SELECT role_id FROM {IdentityMigrations.Schema}.account_role WHERE account_id=@account
                UNION
                SELECT group_role.role_id FROM {IdentityMigrations.Schema}.group_member
                JOIN {IdentityMigrations.Schema}.group_role USING (group_id) WHERE account_id=@account)
            UNION ALL
            SELECT permission_code FROM {IdentityMigrations.Schema}.account_permission_override
            WHERE account_id=@account AND allowed=true;
            """, connection, transaction);
        command.Parameters.AddWithValue("account", accountId);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) grants.Add(PermissionCode.From(reader.GetString(0)));
        await using var denied = new NpgsqlCommand(
            $"SELECT permission_code FROM {IdentityMigrations.Schema}.account_permission_override WHERE account_id=@account AND allowed=false;", connection, transaction);
        denied.Parameters.AddWithValue("account", accountId);
        await using (var reader = await denied.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) denials.Add(PermissionCode.From(reader.GetString(0)));
        return new EffectivePermissions(grants, denials);
    }

    private static async Task InsertRoleAsync(NpgsqlConnection c, NpgsqlTransaction t, IdentityRoleId id, string name,
        IReadOnlyCollection<IdentityPermissionGrant> grants, DateTimeOffset now, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand($"INSERT INTO {IdentityMigrations.Schema}.role (role_id,name,version,created_at,updated_at) VALUES (@id,@name,1,@now,@now);", c, t))
        { command.Parameters.AddWithValue("id", id.Value); command.Parameters.AddWithValue("name", name); command.Parameters.AddWithValue("now", now); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await InsertRoleGrantsAsync(c, t, id.Value, grants, token).ConfigureAwait(false);
    }

    private static async Task InsertRoleGrantsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid roleId,
        IReadOnlyCollection<IdentityPermissionGrant> grants, CancellationToken token)
    {
        foreach (var grant in grants.OrderBy(value => value.Permission.Value))
        {
            await using var command = new NpgsqlCommand($"INSERT INTO {IdentityMigrations.Schema}.role_permission (role_id,permission_code,scope_id) VALUES (@role,@permission,@scope);", c, t);
            command.Parameters.AddWithValue("role", roleId); command.Parameters.AddWithValue("permission", grant.Permission.Value); AddNullableUuid(command, "scope", grant.ScopeId?.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task InsertAccountAsync(NpgsqlConnection c, NpgsqlTransaction t, AccountDto value, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {IdentityMigrations.Schema}.account
                (account_id,subject_id,workspace_account_id,primary_scope_id,user_name,normalized_user_name,
                 password_salt,password_hash,password_iterations,enabled,failed_login_count,locked_until,
                 authorization_version,version,created_at,updated_at)
            VALUES (@id,@subject,@workspace,@scope,@name,@normalized,@salt,@hash,@iterations,true,0,NULL,1,1,@now,@now);
            """, c, t);
        command.Parameters.AddWithValue("id", value.AccountId); command.Parameters.AddWithValue("subject", value.SubjectId);
        AddNullableUuid(command, "workspace", value.WorkspaceAccountId); AddNullableUuid(command, "scope", value.PrimaryScopeId);
        command.Parameters.AddWithValue("name", value.UserName); command.Parameters.AddWithValue("normalized", value.NormalizedUserName);
        command.Parameters.AddWithValue("salt", value.PasswordSalt); command.Parameters.AddWithValue("hash", value.PasswordHash);
        command.Parameters.AddWithValue("iterations", value.PasswordIterations); command.Parameters.AddWithValue("now", value.CreatedAt);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task InsertRelationAsync(NpgsqlConnection c, NpgsqlTransaction t, string table,
        string leftColumn, Guid left, string rightColumn, Guid right, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {IdentityMigrations.Schema}.{table} ({leftColumn},{rightColumn}) VALUES (@left,@right) ON CONFLICT DO NOTHING;", c, t);
        command.Parameters.AddWithValue("left", left); command.Parameters.AddWithValue("right", right);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<AccountDto?> ReadAccountByNameAsync(NpgsqlConnection c, NpgsqlTransaction t, string name, bool update, CancellationToken token) =>
        await ReadAccountAsync(c, t, "normalized_user_name", name, update, token).ConfigureAwait(false);
    private static async Task<AccountDto?> ReadAccountByIdAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, bool update, CancellationToken token) =>
        await ReadAccountAsync(c, t, "account_id", id, update, token).ConfigureAwait(false);
    private static async Task<AccountDto?> ReadAccountBySubjectAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, CancellationToken token) =>
        await ReadAccountAsync(c, t, "subject_id", id, false, token).ConfigureAwait(false);
    private static async Task<AccountDto?> ReadAccountAsync(NpgsqlConnection c, NpgsqlTransaction t, string column, object value, bool update, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT account_id,subject_id,workspace_account_id,primary_scope_id,user_name,normalized_user_name,password_salt,password_hash,password_iterations,enabled,failed_login_count,locked_until,authorization_version,version,created_at,updated_at FROM {IdentityMigrations.Schema}.account WHERE {column}=@value{(update ? " FOR UPDATE" : string.Empty)};", c, t);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadAccount(reader) : null;
    }

    private static AccountDto ReadAccount(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetFieldValue<byte[]>(6), r.GetFieldValue<byte[]>(7), r.GetInt32(8),
        r.GetBoolean(9), r.GetInt32(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), checked((ulong)r.GetInt64(12)),
        checked((ulong)r.GetInt64(13)), r.GetFieldValue<DateTimeOffset>(14), r.GetFieldValue<DateTimeOffset>(15));

    private static async Task<SessionDto?> ReadSessionAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, bool update, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT session.session_id,session.access_token_hash,session.refresh_token_hash,session.authorization_version,session.issued_at,session.expires_at,session.refresh_expires_at,session.revoked_at,account.account_id,account.subject_id,account.workspace_account_id,account.primary_scope_id,account.user_name,account.normalized_user_name,account.password_salt,account.password_hash,account.password_iterations,account.enabled,account.failed_login_count,account.locked_until,account.authorization_version,account.version,account.created_at,account.updated_at FROM {IdentityMigrations.Schema}.production_session AS session JOIN {IdentityMigrations.Schema}.account AS account USING (account_id) WHERE session.session_id=@id{(update ? " FOR UPDATE OF session" : string.Empty)};", c, t);
        command.Parameters.AddWithValue("id", id); await using var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await r.ReadAsync(token).ConfigureAwait(false)) return null;
        var account = new AccountDto(r.GetGuid(8), r.GetGuid(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetGuid(11),
            r.GetString(12), r.GetString(13), r.GetFieldValue<byte[]>(14), r.GetFieldValue<byte[]>(15), r.GetInt32(16), r.GetBoolean(17), r.GetInt32(18),
            r.IsDBNull(19) ? null : r.GetFieldValue<DateTimeOffset>(19), checked((ulong)r.GetInt64(20)), checked((ulong)r.GetInt64(21)),
            r.GetFieldValue<DateTimeOffset>(22), r.GetFieldValue<DateTimeOffset>(23));
        return new SessionDto(r.GetGuid(0), r.GetFieldValue<byte[]>(1), r.GetFieldValue<byte[]>(2), checked((ulong)r.GetInt64(3)),
            r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6),
            r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), account);
    }

    private static async Task<RoleDto?> ReadRoleAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, bool update, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT name,version,updated_at FROM {IdentityMigrations.Schema}.role WHERE role_id=@id{(update ? " FOR UPDATE" : string.Empty)};", c, t);
        command.Parameters.AddWithValue("id", id); await using var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await r.ReadAsync(token).ConfigureAwait(false) ? new RoleDto(r.GetString(0), checked((ulong)r.GetInt64(1)), r.GetFieldValue<DateTimeOffset>(2)) : null;
    }

    private static async Task<IReadOnlyList<IdentityPermissionGrant>?> ReadRoleGrantsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, CancellationToken token)
    {
        if (await ReadRoleAsync(c, t, id, false, token).ConfigureAwait(false) is null) return null;
        await using var command = new NpgsqlCommand($"SELECT permission_code,scope_id FROM {IdentityMigrations.Schema}.role_permission WHERE role_id=@id ORDER BY permission_code;", c, t);
        command.Parameters.AddWithValue("id", id); var values = new List<IdentityPermissionGrant>();
        await using var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false)) values.Add(new(PermissionCode.From(r.GetString(0)), r.IsDBNull(1) ? null : IdentityScopeId.From(r.GetGuid(1))));
        return values;
    }

    private static async Task<List<Guid>> ReadAffectedAccountIdsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid role, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT account_id FROM {IdentityMigrations.Schema}.account_role WHERE role_id=@role UNION SELECT member.account_id FROM {IdentityMigrations.Schema}.group_role JOIN {IdentityMigrations.Schema}.group_member AS member USING (group_id) WHERE role_id=@role ORDER BY account_id;", c, t);
        command.Parameters.AddWithValue("role", role); var result = new List<Guid>(); await using var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false)) result.Add(r.GetGuid(0)); return result;
    }

    private static async Task<List<Guid>> ReadGroupAccountIdsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid group, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT account_id FROM {IdentityMigrations.Schema}.group_member WHERE group_id=@group;", c, t);
        command.Parameters.AddWithValue("group", group); var result = new List<Guid>(); await using var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false)) result.Add(r.GetGuid(0)); return result;
    }

    private static async Task<int> CountActiveSessionsAsync(NpgsqlConnection c, NpgsqlTransaction t, List<Guid> accounts, DateTimeOffset now, CancellationToken token)
    {
        if (accounts.Count == 0) return 0;
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {IdentityMigrations.Schema}.production_session WHERE account_id=ANY(@accounts) AND revoked_at IS NULL AND expires_at>@now;", c, t);
        command.Parameters.AddWithValue("accounts", accounts.ToArray()); command.Parameters.AddWithValue("now", now);
        return checked((int)(long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
    }

    private static async Task InvalidateAccountsAsync(NpgsqlConnection c, NpgsqlTransaction t, IReadOnlyCollection<Guid> accounts, DateTimeOffset now, CancellationToken token)
    {
        if (accounts.Count == 0) return;
        await using var command = new NpgsqlCommand($"UPDATE {IdentityMigrations.Schema}.account SET authorization_version=authorization_version+1,updated_at=@now WHERE account_id=ANY(@accounts);", c, t);
        command.Parameters.AddWithValue("accounts", accounts.ToArray()); command.Parameters.AddWithValue("now", now); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<int> CountOtherAdministratorsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid excludedRole, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT count(DISTINCT account.account_id) FROM {IdentityMigrations.Schema}.account AS account
            WHERE account.enabled AND (
                EXISTS (SELECT 1 FROM {IdentityMigrations.Schema}.account_permission_override AS override
                        WHERE override.account_id=account.account_id AND override.permission_code=@permission AND override.allowed)
                OR EXISTS (SELECT 1 FROM {IdentityMigrations.Schema}.role_permission AS permission
                    WHERE permission.permission_code=@permission AND permission.role_id<>@role AND permission.role_id IN (
                        SELECT role_id FROM {IdentityMigrations.Schema}.account_role WHERE account_id=account.account_id
                        UNION SELECT group_role.role_id FROM {IdentityMigrations.Schema}.group_member
                        JOIN {IdentityMigrations.Schema}.group_role USING (group_id) WHERE account_id=account.account_id)))
                AND NOT EXISTS (SELECT 1 FROM {IdentityMigrations.Schema}.account_permission_override AS denied
                    WHERE denied.account_id=account.account_id AND denied.permission_code=@permission AND NOT denied.allowed);
            """, c, t);
        command.Parameters.AddWithValue("permission", IdentityPermissions.Administer.Value); command.Parameters.AddWithValue("role", excludedRole);
        return checked((int)(long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
    }

    private static async Task<bool> IsEffectiveAdministratorAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid account, CancellationToken token) =>
        (await ReadEffectivePermissionsAsync(c, t, account, token).ConfigureAwait(false)).Allows(IdentityPermissions.Administer);
    private static async Task<int> CountOtherEnabledAdministratorsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid excluded, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT account_id FROM {IdentityMigrations.Schema}.account WHERE enabled AND account_id<>@excluded;", c, t);
        command.Parameters.AddWithValue("excluded", excluded); var ids = new List<Guid>(); await using (var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await r.ReadAsync(token).ConfigureAwait(false)) ids.Add(r.GetGuid(0));
        var count = 0; foreach (var id in ids) if (await IsEffectiveAdministratorAsync(c, t, id, token).ConfigureAwait(false)) count++; return count;
    }

    private static async Task UpdateLoginFailureAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid account, int failures, DateTimeOffset? locked, DateTimeOffset now, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"UPDATE {IdentityMigrations.Schema}.account SET failed_login_count=@failures,locked_until=@locked,updated_at=@now WHERE account_id=@id;", c, t);
        command.Parameters.AddWithValue("id", account); command.Parameters.AddWithValue("failures", failures);
        command.Parameters.Add(new NpgsqlParameter("locked", NpgsqlDbType.TimestampTz) { Value = locked ?? (object)DBNull.Value }); command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<string?> ReadSettingValueAsync(NpgsqlConnection c, NpgsqlTransaction t, string table, string idColumn, Guid id, string key, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT setting_value FROM {IdentityMigrations.Schema}.{table} WHERE {idColumn}=@id AND setting_key=@key;", c, t);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("key", key); return (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }
    private static async Task<List<(Guid Id, string Value)>> ReadGroupSettingValuesAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid account, string key, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT setting.group_id,setting.setting_value FROM {IdentityMigrations.Schema}.group_setting AS setting JOIN {IdentityMigrations.Schema}.group_member AS member USING (group_id) WHERE member.account_id=@account AND setting.setting_key=@key ORDER BY setting.group_id;", c, t);
        command.Parameters.AddWithValue("account", account); command.Parameters.AddWithValue("key", key); var result = new List<(Guid,string)>(); await using var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false)) result.Add((r.GetGuid(0), r.GetString(1))); return result;
    }
    private static async Task<(Guid Id, string Value)?> ReadNearestScopeSettingAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid scope, string key, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"WITH RECURSIVE lineage AS (SELECT scope_id,parent_scope_id,0 depth FROM {IdentityMigrations.Schema}.access_scope WHERE scope_id=@scope UNION ALL SELECT parent.scope_id,parent.parent_scope_id,lineage.depth+1 FROM {IdentityMigrations.Schema}.access_scope AS parent JOIN lineage ON parent.scope_id=lineage.parent_scope_id) SELECT setting.scope_id,setting.setting_value FROM lineage JOIN {IdentityMigrations.Schema}.scope_setting AS setting USING (scope_id) WHERE setting.setting_key=@key ORDER BY lineage.depth LIMIT 1;", c, t);
        command.Parameters.AddWithValue("scope", scope); command.Parameters.AddWithValue("key", key); await using var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await r.ReadAsync(token).ConfigureAwait(false) ? (r.GetGuid(0), r.GetString(1)) : null;
    }

    private static async Task WriteAuditAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid? session, Guid? subject,
        string action, string kind, Guid target, DateTimeOffset now, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {IdentityMigrations.Schema}.mutation_audit (audit_id,session_id,subject_id,action,target_kind,target_id,changed_at) VALUES (@audit,@session,@subject,@action,@kind,@target,@now);", c, t);
        command.Parameters.AddWithValue("audit", Guid.NewGuid()); AddNullableUuid(command, "session", session); AddNullableUuid(command, "subject", subject);
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken token)
    { await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", c, t); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
    private DateTimeOffset UtcNow() { var now = clock.GetUtcNow(); return now.Offset == TimeSpan.Zero ? now : throw new InvalidOperationException("Identity store requires UTC."); }
    private Result ValidateCredentials(string user, string password) => string.IsNullOrWhiteSpace(user) || user.Length > 200 || password.Length < policy.MinimumPasswordLength || password.Length > policy.MaximumPasswordLength
        ? Failure("identity.credentials_invalid", "Local account credentials do not satisfy policy.") : Result.Success();
    private static bool ValidNamedGrants(string name, IReadOnlyCollection<IdentityPermissionGrant> grants) => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && grants.Select(value => value.Permission).Distinct().Count() == grants.Count;
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static byte[] PasswordHash(string password, byte[] salt, int iterations) => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static bool TryParseToken(string token, out Guid id) { id=default; var dot=token.IndexOf('.'); return dot==32 && Guid.TryParseExact(token[..dot],"N",out id) && dot<token.Length-1; }
    private static string PreviewFingerprint(IdentityRoleId role, IReadOnlyCollection<IdentityPermissionGrant> grants, IReadOnlyCollection<Guid> accounts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{role.Value:N}|{string.Join(',', grants.OrderBy(v=>v.Permission.Value).Select(v=>$"{v.Permission.Value}:{v.ScopeId?.Value:N}"))}|{string.Join(',',accounts.Order())}"))).ToLowerInvariant();
    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) => command.Parameters.Add(new NpgsqlParameter(name,NpgsqlDbType.Uuid){Value=value??(object)DBNull.Value});
    private static Result<T>? RequireAdmin<T>(AuthorizedMutation authorization) => authorization.Permission == IdentityPermissions.Administer ? null : Failure<T>("permission.denied","Identity administration permission is required.");
    private static Result? RequireAdmin(AuthorizedMutation authorization) => authorization.Permission == IdentityPermissions.Administer ? null : Failure("permission.denied","Identity administration permission is required.");
    private static Result<T> InvalidLogin<T>() => Failure<T>("identity.login_invalid","Login credentials are invalid.");
    private static Result<T> InvalidSession<T>() => Failure<T>("identity.session_invalid","Production session credential is invalid or expired.");
    private static Result InvalidSession() => Failure("identity.session_invalid","Production session credential is invalid or expired.");
    private static Result<T> Failure<T>(string code,string message)=>Result.Failure<T>(new OperationError(ErrorCode.From(code),message));
    private static Result Failure(string code,string message)=>Result.Failure(new OperationError(ErrorCode.From(code),message));

    private sealed record AccountDto(Guid AccountId, Guid SubjectId, Guid? WorkspaceAccountId, Guid? PrimaryScopeId,
        string UserName, string NormalizedUserName, byte[] PasswordSalt, byte[] PasswordHash, int PasswordIterations,
        bool Enabled, int FailedLoginCount, DateTimeOffset? LockedUntil, ulong AuthorizationVersion, ulong Version,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    { public IdentityAccountSnapshot ToModel()=>new(IdentityAccountId.From(AccountId),Dispatcher.Platform.SubjectId.From(SubjectId),WorkspaceAccountId is null?null:Dispatcher.Workspace.AccountId.From(WorkspaceAccountId.Value),UserName,Enabled,PrimaryScopeId is null?null:IdentityScopeId.From(PrimaryScopeId.Value),StateVersion.From(AuthorizationVersion),StateVersion.From(Version),UpdatedAt); }
    private sealed record SessionDto(Guid SessionId,byte[] AccessHash,byte[] RefreshHash,ulong AuthorizationVersion,DateTimeOffset IssuedAt,DateTimeOffset ExpiresAt,DateTimeOffset RefreshExpiresAt,DateTimeOffset? RevokedAt,AccountDto Account);
    private sealed record RoleDto(string Name,ulong Version,DateTimeOffset UpdatedAt);
}
