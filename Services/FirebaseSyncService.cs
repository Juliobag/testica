using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Testica.Services;

public sealed class FirebaseSyncService(DatabaseService database)
{
	private static readonly EntityTable[] EntityTables =
	[
		new("theme", "themes", ["id", "name", "description", "status", "updated_at", "revision_id"], "id"),
		new("cycle", "cycles", ["id", "name", "description", "order_index", "status", "updated_at", "revision_id"], "id"),
		new("questionnaire", "questionnaires", ["id", "title", "description", "theme_id", "cycle_id", "status", "question_order_mode", "question_limit", "revision_id"], "id"),
		new("question", "questions", ["id", "theme_id", "cycle_id", "statement", "context", "image_path", "difficulty", "status", "updated_at", "revision_id"], "id"),
		new("answer", "answers", ["id", "question_id", "label", "answer_text", "is_correct", "order_index", "status"], "id"),
		new("questionnaire_question", "questionnaire_questions", ["questionnaire_id", "question_id", "order_index"], null)
	];
	private const string DeletedHash = "__testica_deleted__";

	public async Task<FirebaseSyncResult> SynchronizeAsync(string email, string password,
		IProgress<string>? progress = null, CancellationToken cancellationToken = default)
	{
		using var client = new FirebaseRestClient();
		progress?.Report("Connexion à Firebase Authentication…");
		await client.SignInAsync(email, password, cancellationToken);

		progress?.Report("Lecture de la collection d’édition…");
		var remote = await client.ListAuthoringDocumentsAsync(cancellationToken);
		var local = ReadLocalRecords();
		var baseline = ReadBaseline();
		var keys = local.Keys.Concat(remote.Keys).Concat(baseline.Keys).Distinct(StringComparer.Ordinal).ToArray();
		var toPull = new List<(string Key, RemoteSyncDocument? Document, string State)>();
		var toPush = new List<(string Key, LocalSyncRecord? Record, RemoteSyncDocument? ExpectedRemote, string State)>();
		var conflicts = new List<string>();
		var alreadySynchronized = new List<(string Key, string State)>();

		foreach (var key in keys)
		{
			local.TryGetValue(key, out var localRecord);
			remote.TryGetValue(key, out var remoteDocument);
			baseline.TryGetValue(key, out var baseState);
			var localState = localRecord?.ContentHash ?? DeletedHash;
			var remoteState = remoteDocument is { IsDeleted: false, ContentHash: not null }
				? remoteDocument.ContentHash
				: remoteDocument is null && baseState is null ? null : DeletedHash;

			if (baseState is null)
			{
				if (localState == DeletedHash && remoteState is null) continue;
				if (localRecord is not null && remoteDocument is null)
				{
					toPush.Add((key, localRecord, null, localState));
				}
				else if (localRecord is null && remoteDocument is not null)
				{
					toPull.Add((key, remoteDocument, remoteState ?? DeletedHash));
				}
				else if (localState == remoteState)
				{
					alreadySynchronized.Add((key, localState));
				}
				else conflicts.Add(key);
				continue;
			}

			if (localState == baseState && remoteState == baseState) continue;
			if (localState != baseState && remoteState == baseState)
			{
				toPush.Add((key, localRecord, remoteDocument, localState));
			}
			else if (remoteState != baseState && localState == baseState)
			{
				toPull.Add((key, remoteDocument, remoteState ?? DeletedHash));
			}
			else if (localState == remoteState)
			{
				alreadySynchronized.Add((key, localState));
			}
			else conflicts.Add(key);
		}

		progress?.Report($"Application des changements : {toPull.Count} à importer, {toPush.Count} à envoyer…");
		var appliedPulls = new List<(string Key, string State)>();
		foreach (var item in toPull.OrderBy(item => ApplyOrder(item.Document)))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (item.Document is { IsDeleted: false, PayloadJson: not null } document)
			{
				ApplyRemoteRecord(document.EntityType, document.RecordId, document.PayloadJson);
			}
			else if (item.Document is not null)
			{
				DeleteLocalRecord(item.Document.EntityType, item.Document.RecordId);
			}
			appliedPulls.Add((item.Key, item.State));
		}

		var appliedPushes = new List<(string Key, string State)>();
		foreach (var item in toPush)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (entityType, recordId) = SplitSyncKey(item.Key);
			await client.WriteAuthoringDocumentAsync(entityType, recordId, item.Record?.PayloadJson,
				item.Record is null, item.ExpectedRemote, cancellationToken);
			appliedPushes.Add((item.Key, item.State));
		}

		var completed = alreadySynchronized.Concat(appliedPulls).Concat(appliedPushes).ToArray();
		SaveBaseline(completed);
		return new FirebaseSyncResult(appliedPulls.Count, appliedPushes.Count, conflicts.Count);
	}

	public static string HashPayload(string payloadJson)
	{
		var token = JToken.Parse(payloadJson);
		var canonical = Canonicalize(token).ToString(Formatting.None);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
	}

	private Dictionary<string, LocalSyncRecord> ReadLocalRecords()
	{
		var result = new Dictionary<string, LocalSyncRecord>(StringComparer.Ordinal);
		using var connection = database.OpenConnection();
		using var snapshot = connection.BeginTransaction();
		foreach (var entity in EntityTables)
		{
			using var command = connection.CreateCommand();
			command.Transaction = snapshot;
			command.CommandText = $"SELECT {string.Join(", ", entity.Columns)} FROM {entity.Table}";
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				var row = new JObject();
				for (var index = 0; index < entity.Columns.Length; index++)
				{
					var column = entity.Columns[index];
					var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
					if (column == "is_correct" && value is not null) value = Convert.ToInt32(value) != 0;
					row[column] = value is null ? JValue.CreateNull() : JToken.FromObject(value);
				}
				var recordId = entity.PrimaryKey is null
					? $"{(string?)row["questionnaire_id"]}:{(string?)row["question_id"]}"
					: (string?)row[entity.PrimaryKey] ?? string.Empty;
				var payload = Canonicalize(row).ToString(Formatting.None);
				var key = SyncKey(entity.Type, recordId);
				result[key] = new LocalSyncRecord(entity.Type, recordId, payload, HashPayload(payload));
			}
		}
		snapshot.Commit();
		return result;
	}

	private Dictionary<string, string> ReadBaseline()
	{
		using var connection = database.OpenConnection();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT sync_key, synced_hash, is_deleted FROM firebase_sync_state";
		using var reader = command.ExecuteReader();
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(2) != 0 ? DeletedHash : reader.GetString(1);
		return result;
	}

	private void SaveBaseline(IEnumerable<(string Key, string State)> states)
	{
		database.ExecuteWrite((connection, transaction) =>
		{
			foreach (var (key, state) in states)
			{
				using var command = connection.CreateCommand();
				command.Transaction = transaction;
				command.CommandText = "INSERT INTO firebase_sync_state (sync_key, synced_hash, is_deleted) VALUES (@key, @hash, @deleted) ON CONFLICT(sync_key) DO UPDATE SET synced_hash = excluded.synced_hash, is_deleted = excluded.is_deleted";
				command.Parameters.AddWithValue("@key", key);
				command.Parameters.AddWithValue("@hash", state == DeletedHash ? DBNull.Value : state);
				command.Parameters.AddWithValue("@deleted", state == DeletedHash ? 1 : 0);
				command.ExecuteNonQuery();
			}
			return 0;
		});
	}

	private void ApplyRemoteRecord(string entityType, string recordId, string payloadJson)
	{
		var entity = FindEntity(entityType);
		var row = JObject.Parse(payloadJson);
		var columns = entity.Columns;
		var updateColumns = columns.Where(column => column != entity.PrimaryKey && column != "revision_id").ToArray();
		var placeholders = columns.Select((_, index) => "@p" + index).ToArray();
		var updates = updateColumns.Select(column => $"{column} = excluded.{column}").ToArray();
		database.ExecuteWrite((connection, transaction) =>
		{
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = $"INSERT INTO {entity.Table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)}) ON CONFLICT({entity.PrimaryKey ?? "questionnaire_id, question_id"}) DO UPDATE SET {string.Join(", ", updates)}";
			for (var index = 0; index < columns.Length; index++)
			{
				var column = columns[index];
				var value = row[column]?.Type is JTokenType.Null or null ? DBNull.Value : ((JValue)row[column]!).Value ?? DBNull.Value;
				if (column == "is_correct" && value is bool boolean) value = boolean ? 1 : 0;
				command.Parameters.AddWithValue(placeholders[index], value);
			}
			return command.ExecuteNonQuery();
		});
	}

	private void DeleteLocalRecord(string entityType, string recordId)
	{
		var entity = FindEntity(entityType);
		database.ExecuteWrite((connection, transaction) =>
		{
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			if (entity.PrimaryKey is null)
			{
				var ids = recordId.Split(':', 2);
				if (ids.Length != 2) throw new InvalidDataException($"Identifiant de lien invalide : {recordId}.");
				command.CommandText = "DELETE FROM questionnaire_questions WHERE questionnaire_id = @first AND question_id = @second";
				command.Parameters.AddWithValue("@first", ids[0]);
				command.Parameters.AddWithValue("@second", ids[1]);
			}
			else
			{
				command.CommandText = $"DELETE FROM {entity.Table} WHERE {entity.PrimaryKey} = @id";
				command.Parameters.AddWithValue("@id", recordId);
			}
			return command.ExecuteNonQuery();
		});
	}

	private static EntityTable FindEntity(string entityType) => EntityTables.SingleOrDefault(entity => entity.Type == entityType)
		?? throw new InvalidDataException($"Type d’entité Firestore non pris en charge : {entityType}.");

	private static int ApplyOrder(RemoteSyncDocument? document)
	{
		if (document is null) return 0;
		if (!document.IsDeleted) return Array.IndexOf(EntityTables, FindEntity(document.EntityType));
		return document.EntityType switch
		{
			"questionnaire_question" => 0,
			"answer" => 1,
			"question" => 2,
			"questionnaire" => 3,
			"cycle" => 4,
			"theme" => 5,
			_ => 6
		};
	}

	private static string SyncKey(string entityType, string recordId) => entityType + ":" + recordId;
	private static (string EntityType, string RecordId) SplitSyncKey(string key)
	{
		var separator = key.IndexOf(':');
		if (separator <= 0) throw new InvalidDataException($"Clé de synchronisation invalide : {key}.");
		return (key[..separator], key[(separator + 1)..]);
	}

	private static JToken Canonicalize(JToken token) => token switch
	{
		JObject obj => new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
			.Select(property => new JProperty(property.Name, Canonicalize(property.Value)))),
		JArray array => new JArray(array.Select(Canonicalize)),
		_ => token.DeepClone()
	};

	private sealed record EntityTable(string Type, string Table, string[] Columns, string? PrimaryKey);
	private sealed record LocalSyncRecord(string EntityType, string RecordId, string PayloadJson, string ContentHash);
}

public sealed record FirebaseSyncResult(int PulledCount, int PushedCount, int ConflictCount);
