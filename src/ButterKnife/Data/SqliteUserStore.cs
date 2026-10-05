using System.Security.Cryptography;
using ButterKnife.Services;
using Microsoft.Data.Sqlite;

namespace ButterKnife.Data;

public sealed class SqliteUserStore(SqliteDatabase db) : IUserStore
{
    public const int MinUsernameLength = 2;
    public const int MaxUsernameLength = 32;
    public const int MaxDisplayNameLength = 40;

    private const int SqliteConstraintViolation = 19;

    private const string Columns = "id, username, display_name, password_hash IS NOT NULL, is_admin, is_owner, security_stamp, created_at, last_login_at";

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM users ORDER BY is_owner DESC, display_name COLLATE NOCASE, username COLLATE NOCASE;";

        var list = new List<AppUser>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public Task<AppUser?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        QueryOneAsync("id = $value", id.ToString("D"), cancellationToken);

    public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        QueryOneAsync("username = $value COLLATE NOCASE", username.Trim(), cancellationToken);

    public async Task<AppUser> GetOwnerAsync(CancellationToken cancellationToken = default) =>
        await QueryOneAsync("is_owner = $value", 1L, cancellationToken)
        ?? throw new InvalidOperationException("The owner account is missing from the database.");

    private async Task<AppUser?> QueryOneAsync(string where, object value, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM users WHERE {where};"; // where is one of the literals above
        cmd.Parameters.AddWithValue("$value", value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<AppUser> CreateAsync(string username, string displayName, string password, bool isAdmin, CancellationToken cancellationToken = default)
    {
        var name = NormalizeUsername(username);
        var display = NormalizeDisplayName(displayName);
        if (PasswordHasher.Validate(password) is { } problem)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        var id = Guid.NewGuid();
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (id, username, display_name, password_hash, is_admin, is_owner, security_stamp, created_at, last_login_at)
            VALUES ($id, $username, $display, $hash, $admin, 0, $stamp, $now, NULL);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$username", name);
        cmd.Parameters.AddWithValue("$display", display);
        cmd.Parameters.AddWithValue("$hash", PasswordHasher.Hash(password));
        cmd.Parameters.AddWithValue("$admin", isAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$stamp", NewStamp());
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(DateTimeOffset.UtcNow));
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintViolation)
        {
            throw new InvalidOperationException($"The username \"{name}\" is taken.");
        }

        return (await GetAsync(id, cancellationToken))!;
    }

    public async Task UpdateProfileAsync(Guid id, string username, string displayName, CancellationToken cancellationToken = default)
    {
        var name = NormalizeUsername(username);
        var display = NormalizeDisplayName(displayName);

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE users SET username = $username, display_name = $display WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$username", name);
        cmd.Parameters.AddWithValue("$display", display);
        int rows;
        try
        {
            rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintViolation)
        {
            throw new InvalidOperationException($"The username \"{name}\" is taken.");
        }
        if (rows == 0)
        {
            throw new KeyNotFoundException($"User {id} does not exist.");
        }
    }

    public async Task SetPasswordAsync(Guid id, string? password, bool signOutEverywhere, CancellationToken cancellationToken = default)
    {
        if (password is not null && PasswordHasher.Validate(password) is { } problem)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        var user = await GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"User {id} does not exist.");
        if (password is null && !user.IsOwner)
        {
            throw new InvalidOperationException("Only the owner can be without a password; everyone else needs one to sign in.");
        }

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE users SET password_hash = $hash, security_stamp = $stamp WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$hash", password is null ? DBNull.Value : PasswordHasher.Hash(password));
        cmd.Parameters.AddWithValue("$stamp", signOutEverywhere ? NewStamp() : user.SecurityStamp);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetAdminAsync(Guid id, bool isAdmin, CancellationToken cancellationToken = default)
    {
        var user = await GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"User {id} does not exist.");
        if (user.IsOwner && !isAdmin)
        {
            throw new InvalidOperationException("The owner is always an administrator.");
        }

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE users SET is_admin = $admin, security_stamp = $stamp WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$admin", isAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$stamp", NewStamp());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await GetAsync(id, cancellationToken);
        if (user is null)
        {
            return;
        }
        if (user.IsOwner)
        {
            throw new InvalidOperationException("The owner account cannot be deleted.");
        }

        await using var connection = await db.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            DELETE FROM conversations WHERE user_id = $id; -- messages, images and files cascade
            DELETE FROM users WHERE id = $id AND is_owner = 0;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<AppUser?> VerifyPasswordAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        string id;
        string? hash;
        await using (var connection = await db.OpenAsync(cancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, password_hash FROM users WHERE username = $username COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$username", username.Trim());
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            id = reader.GetString(0);
            hash = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (hash is null || !PasswordHasher.Verify(password, hash))
        {
            return null;
        }
        return await GetAsync(Guid.Parse(id), cancellationToken);
    }

    public async Task TouchLoginAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE users SET last_login_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$now", SqliteDatabase.Format(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Null when the username is acceptable, otherwise what is wrong with it.</summary>
    public static string? ValidateUsername(string username) => username.Trim() switch
    {
        var u when u.Length < MinUsernameLength => $"Use at least {MinUsernameLength} characters for the username.",
        var u when u.Length > MaxUsernameLength => $"Use at most {MaxUsernameLength} characters for the username.",
        var u when !u.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') => "A username can only have letters, digits, dots, dashes and underscores.",
        _ => null,
    };

    /// <summary>Null when the display name is acceptable, otherwise what is wrong with it.</summary>
    public static string? ValidateDisplayName(string displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "Give the person a name to show in chats."
        : displayName.Trim().Length > MaxDisplayNameLength ? $"Use at most {MaxDisplayNameLength} characters for the name."
        : null;

    private static string NormalizeUsername(string username) =>
        ValidateUsername(username) is { } problem ? throw new ArgumentException(problem, nameof(username)) : username.Trim();

    private static string NormalizeDisplayName(string displayName) =>
        ValidateDisplayName(displayName) is { } problem ? throw new ArgumentException(problem, nameof(displayName)) : displayName.Trim();

    public static string NewStamp() => RandomNumberGenerator.GetHexString(16);

    private static AppUser Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        IsAdmin: reader.GetInt64(4) != 0,
        IsOwner: reader.GetInt64(5) != 0,
        HasPassword: reader.GetInt64(3) != 0,
        reader.GetString(6),
        SqliteDatabase.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : SqliteDatabase.Parse(reader.GetString(8)));
}
