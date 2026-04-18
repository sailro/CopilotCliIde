using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace CopilotCliIde;

internal readonly record struct SessionInfo(string Id, string? Summary, string? Cwd, DateTime UpdatedAtUtc, int TurnCount);

internal enum SessionStoreStatus
{
	Ok,
	NoDatabase,
	Unavailable,
}

internal readonly record struct SessionQueryResult(SessionStoreStatus Status, IReadOnlyList<SessionInfo> Sessions, string? ErrorMessage = null)
{
	public static SessionQueryResult Empty(SessionStoreStatus status, string? error = null) =>
		new(status, Array.Empty<SessionInfo>(), error);
}

// Reads the Copilot CLI's session-store.db (SQLite) to surface previous sessions
// for the current workspace. Opened read-only so it does not contend with the live
// CLI's WAL writer. Schema is internal CLI state — all errors degrade gracefully.
internal sealed class SessionStore(OutputLogger? logger)
{
	private const int MaxResults = 200;

	public static string DefaultDatabasePath =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-store.db");

	public Task<SessionQueryResult> GetSessionsForWorkspaceAsync(string workspacePath, CancellationToken ct) =>
		Task.Run(() => GetSessionsForWorkspace(workspacePath, ct), ct);

	private SessionQueryResult GetSessionsForWorkspace(string workspacePath, CancellationToken ct)
	{
		var dbPath = DefaultDatabasePath;
		if (!File.Exists(dbPath))
			return SessionQueryResult.Empty(SessionStoreStatus.NoDatabase);

		var normalizedWorkspace = NormalizePath(workspacePath);
		if (normalizedWorkspace == null)
			return SessionQueryResult.Empty(SessionStoreStatus.Ok);

		try
		{
			var connectionString = new SqliteConnectionStringBuilder
			{
				DataSource = dbPath,
				Mode = SqliteOpenMode.ReadOnly,
				Cache = SqliteCacheMode.Private,
			}.ToString();

			using var conn = new SqliteConnection(connectionString);
			conn.Open();

			ct.ThrowIfCancellationRequested();

			// Build separator-aware LIKE prefix so we don't overmatch siblings
			// (e.g. "C:\repo" should NOT match "C:\repo-old") and so the SQL LIMIT
			// can't drop valid descendants in favor of unrelated ones.
			// SQLite LIKE treats % and _ as wildcards — escape them.
			var separator = Path.DirectorySeparatorChar.ToString();
			var likePrefix = EscapeLike(normalizedWorkspace + separator) + "%";

			using var cmd = conn.CreateCommand();
			cmd.CommandText = @"
				SELECT s.id, s.summary, s.cwd, s.updated_at,
				       (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) AS turn_count
				FROM sessions s
				WHERE s.cwd = $exact OR s.cwd LIKE $prefix ESCAPE '\'
				ORDER BY s.updated_at DESC
				LIMIT $limit";
			cmd.Parameters.AddWithValue("$exact", normalizedWorkspace);
			cmd.Parameters.AddWithValue("$prefix", likePrefix);
			cmd.Parameters.AddWithValue("$limit", MaxResults);

			var results = new List<SessionInfo>();
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
			{
				ct.ThrowIfCancellationRequested();

				var id = reader.GetString(0);
				if (!SessionId.IsValid(id))
					continue; // defense-in-depth: never return malformed IDs to callers

				var summary = reader.IsDBNull(1) ? null : reader.GetString(1);
				var cwd = reader.IsDBNull(2) ? null : reader.GetString(2);
				var updatedAtRaw = reader.IsDBNull(3) ? null : reader.GetString(3);
				var turnCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

				if (!IsCwdMatch(cwd, normalizedWorkspace))
					continue;

				var updatedAt = ParseDateTime(updatedAtRaw);
				results.Add(new SessionInfo(id, summary, cwd, updatedAt, turnCount));
			}

			return new SessionQueryResult(SessionStoreStatus.Ok, results);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			logger?.Log($"SessionStore: read failed: {ex.GetType().Name}: {ex.Message}");
			return SessionQueryResult.Empty(SessionStoreStatus.Unavailable, ex.Message);
		}
	}

	private static string? NormalizePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;
		try
		{
			var full = Path.GetFullPath(path);
			return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch
		{
			return null;
		}
	}

	private static bool IsCwdMatch(string? cwd, string normalizedWorkspace)
	{
		var normCwd = NormalizePath(cwd);
		if (normCwd == null)
			return false;
		if (string.Equals(normCwd, normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
			return true;
		var prefixWithSep = normalizedWorkspace + Path.DirectorySeparatorChar;
		return normCwd.StartsWith(prefixWithSep, StringComparison.OrdinalIgnoreCase);
	}

	// SQLite LIKE treats % and _ as wildcards. Escape them with backslash (matches ESCAPE '\').
	private static string EscapeLike(string value) =>
		value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

	private static DateTime ParseDateTime(string? raw)
	{
		if (string.IsNullOrEmpty(raw))
			return DateTime.MinValue;
		// SQLite's datetime('now') format: "YYYY-MM-DD HH:MM:SS" UTC.
		if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
			out var dt))
			return dt;
		return DateTime.MinValue;
	}
}
