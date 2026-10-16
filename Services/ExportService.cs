using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Testica.Services;

public sealed class ExportService(DatabaseService database)
{
	private const string SchemaVersion = "1.0";
	private static readonly SemaphoreSlim ExportLock = new(1, 1);
	private static readonly string[] EntityTables = ["themes", "cycles", "questions", "answers", "questionnaires"];
	private static readonly Dictionary<string, string> EntityTypes = new(StringComparer.Ordinal)
	{
		["themes"] = "theme",
		["cycles"] = "cycle",
		["questions"] = "question",
		["answers"] = "answer",
		["questionnaires"] = "questionnaire"
	};
	private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
	};

	public PublicationExportResult CreatePublicationPackage(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var fullPath = Path.GetFullPath(path);
		if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Le paquet de publication doit être un fichier .zip.");
		}

		ExportLock.Wait();
		var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
		try
		{
			var snapshot = ReadSnapshot();
			var manifest = BuildManifest(snapshot, out var mediaFiles);
			var outputDirectory = Path.GetDirectoryName(fullPath);
			if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

			using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
		using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
		{
				foreach (var media in mediaFiles)
				{
					var entry = archive.CreateEntry(media.PackagePath, CompressionLevel.Optimal);
					using var source = new FileStream(media.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
					using var destination = entry.Open();
					using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
					var buffer = new byte[81920];
					int read;
					long size = 0;
					while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
					{
						hash.AppendData(buffer, 0, read);
						destination.Write(buffer, 0, read);
						size += read;
					}
					media.ManifestEntry.SizeBytes = size;
					media.ManifestEntry.Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
				}

				var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
				using var manifestStream = manifestEntry.Open();
				using var writer = new StreamWriter(manifestStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				writer.Write(JsonConvert.SerializeObject(manifest, Formatting.Indented));
			}

			File.Move(temporaryPath, fullPath, overwrite: true);
			MarkPublished(manifest.Metadata.ChangeSequence, manifest.Metadata.PublicationId, manifest.Metadata.ExportedAt);
			return new PublicationExportResult(
				manifest.Metadata.PublicationId,
				manifest.Content.Values.Sum(rows => rows.Count),
				manifest.Media.Count,
				fullPath);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
			ExportLock.Release();
		}
	}

	private PublicationSnapshot ReadSnapshot()
	{
		using var connection = database.OpenConnection();
		using var transaction = connection.BeginTransaction();
		long lastChangeSequence;
		string? lastPublicationId;
		using (var stateCommand = connection.CreateCommand())
		{
			stateCommand.Transaction = transaction;
			stateCommand.CommandText = "SELECT last_change_seq, last_publication_id FROM publication_state WHERE state_id = 1";
			using var reader = stateCommand.ExecuteReader();
			if (!reader.Read()) throw new InvalidDataException("L’état de publication SQLite est absent.");
			lastChangeSequence = reader.GetInt64(0);
			lastPublicationId = reader.IsDBNull(1) ? null : reader.GetString(1);
		}

		long changeSequence;
		using (var sequenceCommand = connection.CreateCommand())
		{
			sequenceCommand.Transaction = transaction;
			sequenceCommand.CommandText = "SELECT COALESCE(MAX(change_seq), 0) FROM publication_changes";
			changeSequence = Convert.ToInt64(sequenceCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
		}

		var tables = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
		foreach (var table in EntityTables.Append("questionnaire_questions"))
		{
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = $"SELECT * FROM {table}";
			using var reader = command.ExecuteReader();
			var rows = new List<Dictionary<string, object?>>();
			while (reader.Read())
			{
				var row = new Dictionary<string, object?>(StringComparer.Ordinal);
				for (var column = 0; column < reader.FieldCount; column++)
				{
					var value = reader.IsDBNull(column) ? null : reader.GetValue(column);
					if (reader.GetName(column) == "is_correct" && value is not null)
					{
						value = Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
					}
					row.Add(reader.GetName(column), value);
				}
				rows.Add(row);
			}
			tables.Add(table, rows);
		}

		var changes = new List<ChangeEvent>();
		using (var changesCommand = connection.CreateCommand())
		{
			changesCommand.Transaction = transaction;
			changesCommand.CommandText = "SELECT change_seq, entity_type, entity_id, operation FROM publication_changes WHERE change_seq > @last_sequence ORDER BY change_seq";
			changesCommand.Parameters.AddWithValue("@last_sequence", lastChangeSequence);
			using var reader = changesCommand.ExecuteReader();
			while (reader.Read())
			{
				changes.Add(new ChangeEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
			}
		}

		transaction.Commit();
		return new PublicationSnapshot(lastPublicationId is null, lastChangeSequence, changeSequence, tables, changes);
	}

	private PublicationManifest BuildManifest(PublicationSnapshot snapshot, out List<PreparedMedia> mediaFiles)
	{
		var histories = new Dictionary<(string EntityType, string EntityId), ChangeHistory>();
		foreach (var change in snapshot.Changes)
		{
			var key = (change.EntityType, change.EntityId);
			if (histories.TryGetValue(key, out var history))
			{
				history.LastOperation = change.Operation;
				history.LastSequence = change.Sequence;
			}
			else
			{
				histories.Add(key, new ChangeHistory(change.Operation, change.Operation, change.Sequence));
			}
		}

		var manifest = new PublicationManifest
		{
			Metadata = new PublicationMetadata
			{
				SchemaVersion = SchemaVersion,
				PublicationId = Guid.NewGuid().ToString("D"),
				SourceVersion = typeof(ExportService).Assembly.GetName().Version?.ToString() ?? "unknown",
				ExportedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
				ChangeSequence = snapshot.ChangeSequence
			}
		};
		var currentIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		var includedIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

		foreach (var table in EntityTables)
		{
			var entityType = EntityTypes[table];
			var rowsById = snapshot.Tables[table].ToDictionary(row => GetString(row, "id"), StringComparer.Ordinal);
			currentIds[entityType] = rowsById.Keys.ToHashSet(StringComparer.Ordinal);
			var included = new HashSet<string>(StringComparer.Ordinal);
			includedIds[entityType] = included;
			var contentRows = new List<Dictionary<string, object?>>();

			foreach (var row in snapshot.Tables[table])
			{
				var id = GetString(row, "id");
				histories.TryGetValue((entityType, id), out var history);
				var status = GetString(row, "status");
				var logicallyDeleted = IsLogicallyDeleted(status);
				var include = !logicallyDeleted && (IsActive(status) || (history is not null && history.LastOperation != "deleted"));

				if (include)
				{
					included.Add(id);
					contentRows.Add(new Dictionary<string, object?>(row, StringComparer.Ordinal));
				}

				if (snapshot.IsFirstPublication)
				{
					if (logicallyDeleted)
					{
						manifest.Deleted.Add(CreateChange(entityType, id, 0, row));
					}
					else if (IsActive(status))
					{
						manifest.Added.Add(CreateChange(entityType, id, 0, row));
					}
					else if (history is not null && history.LastOperation != "deleted")
					{
						var change = CreateChange(entityType, id, history.LastSequence, row);
						if (history.FirstOperation == "added") manifest.Added.Add(change);
						else manifest.Modified.Add(change);
					}
				}
				else if (history is not null)
				{
					if (logicallyDeleted || history.LastOperation == "deleted")
					{
						manifest.Deleted.Add(CreateChange(entityType, id, history.LastSequence, row));
					}
					else if (history.FirstOperation == "added")
					{
						manifest.Added.Add(CreateChange(entityType, id, history.LastSequence, row));
					}
					else
					{
						manifest.Modified.Add(CreateChange(entityType, id, history.LastSequence, row));
					}
				}
			}

			manifest.Content.Add(table, contentRows);
		}

		foreach (var (key, history) in histories)
		{
			if (!EntityTypes.Values.Contains(key.EntityType, StringComparer.Ordinal)
				|| currentIds[key.EntityType].Contains(key.EntityId)
				|| history.LastOperation != "deleted") continue;

			manifest.Deleted.Add(CreateChange(key.EntityType, key.EntityId, history.LastSequence, null));
		}

		var themes = includedIds["theme"];
		var cycles = includedIds["cycle"];
		var questions = includedIds["question"];
		var answers = manifest.Content["answers"];
		foreach (var question in manifest.Content["questions"])
		{
			var questionId = GetString(question, "id");
			var themeId = GetString(question, "theme_id");
			var cycleId = GetString(question, "cycle_id");
			if (string.IsNullOrWhiteSpace(GetString(question, "statement"))
				|| string.IsNullOrWhiteSpace(themeId) || string.IsNullOrWhiteSpace(cycleId))
			{
				throw new InvalidDataException($"La question {questionId} ne possède pas tous ses champs obligatoires.");
			}
			if (!themes.Contains(themeId) || !cycles.Contains(cycleId))
			{
				throw new InvalidDataException($"La question {questionId} référence un thème ou un cycle non publiable.");
			}
			if (!answers.Any(answer => GetString(answer, "question_id") == questionId
				&& answer.TryGetValue("is_correct", out var correct) && correct is true))
			{
				throw new InvalidDataException($"La question {questionId} ne possède aucune réponse correcte active ou modifiée.");
			}
		}

		foreach (var questionnaire in manifest.Content["questionnaires"])
		{
			var questionnaireId = GetString(questionnaire, "id");
			if (!string.IsNullOrWhiteSpace(GetString(questionnaire, "theme_id"))
				&& !themes.Contains(GetString(questionnaire, "theme_id")))
			{
				throw new InvalidDataException($"Le questionnaire {questionnaireId} référence un thème non publiable.");
			}
			if (!string.IsNullOrWhiteSpace(GetString(questionnaire, "cycle_id"))
				&& !cycles.Contains(GetString(questionnaire, "cycle_id")))
			{
				throw new InvalidDataException($"Le questionnaire {questionnaireId} référence un cycle non publiable.");
			}
		}

		var includedQuestionnaires = includedIds["questionnaire"];
		var links = snapshot.Tables["questionnaire_questions"];
		foreach (var link in links)
		{
			if (includedQuestionnaires.Contains(GetString(link, "questionnaire_id"))
				&& !questions.Contains(GetString(link, "question_id")))
			{
				throw new InvalidDataException("Un questionnaire publiable contient une question absente du paquet.");
			}
		}
		manifest.Content["questionnaire_questions"] = links
			.Where(link => includedQuestionnaires.Contains(GetString(link, "questionnaire_id"))
				&& questions.Contains(GetString(link, "question_id")))
			.Select(link => new Dictionary<string, object?>(link, StringComparer.Ordinal))
			.ToList();

		var allMediaPaths = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var question in snapshot.Tables["questions"])
		{
			var sourcePath = GetString(question, "image_path");
			if (string.IsNullOrWhiteSpace(sourcePath)) continue;
			var fullMediaPath = ResolveMediaPath(sourcePath);
			if (!File.Exists(fullMediaPath))
			{
				throw new FileNotFoundException($"Le média référencé par la question {GetString(question, "id")} est introuvable.", fullMediaPath);
			}
			if (!MediaExtensions.Contains(Path.GetExtension(fullMediaPath)))
			{
				throw new InvalidDataException($"Le fichier référencé par la question {GetString(question, "id")} n’est pas un format d’image pris en charge.");
			}
			allMediaPaths[GetString(question, "id")] = fullMediaPath;
		}

		mediaFiles = [];
		foreach (var question in manifest.Content["questions"])
		{
			var questionId = GetString(question, "id");
			if (!allMediaPaths.TryGetValue(questionId, out var fullMediaPath)) continue;
			var fileName = SanitizeFileName(Path.GetFileName(fullMediaPath));
			var directory = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(questionId))).ToLowerInvariant()[..16];
			var packagePath = $"media/{directory}/{fileName}";
			question["image_path"] = packagePath;
			var mediaManifest = new PublicationMedia
			{
				QuestionId = questionId,
				Path = packagePath,
				SizeBytes = 0,
				Sha256 = string.Empty
			};
			manifest.Media.Add(mediaManifest);
			mediaFiles.Add(new PreparedMedia(fullMediaPath, packagePath, mediaManifest));
		}

		return manifest;
	}

	private string ResolveMediaPath(string path)
	{
		if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
		var databaseDirectory = Path.GetDirectoryName(database.DatabasePath) ?? Environment.CurrentDirectory;
		return Path.GetFullPath(Path.Combine(databaseDirectory, path));
	}

	private static string SanitizeFileName(string name)
	{
		var safeName = new string(name.Select(character =>
			char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray()).Trim('.');
		return string.IsNullOrWhiteSpace(safeName) ? "media.bin" : safeName;
	}

	private static bool IsActive(string? status) => string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

	private static bool IsLogicallyDeleted(string? status) => status is not null
		&& (status.Equals("inactive", StringComparison.OrdinalIgnoreCase)
			|| status.Equals("archived", StringComparison.OrdinalIgnoreCase)
			|| status.Equals("deleted", StringComparison.OrdinalIgnoreCase));

	private static string GetString(IReadOnlyDictionary<string, object?> row, string name) =>
		row.TryGetValue(name, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

	private static PublicationChange CreateChange(string entityType, string entityId, long sequence,
		IReadOnlyDictionary<string, object?>? row)
	{
		var revisionId = row is not null && row.TryGetValue("revision_id", out var revision)
			? Convert.ToString(revision, CultureInfo.InvariantCulture)
			: null;
		return new PublicationChange(entityType, entityId, sequence, revisionId);
	}

	private void MarkPublished(long changeSequence, string publicationId, string exportedAt)
	{
		database.ExecuteWrite((connection, transaction) =>
		{
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "UPDATE publication_state SET last_change_seq = @sequence, last_publication_id = @publication_id, exported_at = @exported_at WHERE state_id = 1";
			command.Parameters.AddWithValue("@sequence", changeSequence);
			command.Parameters.AddWithValue("@publication_id", publicationId);
			command.Parameters.AddWithValue("@exported_at", exportedAt);
			return command.ExecuteNonQuery();
		});
	}

	private sealed record PublicationSnapshot(
		bool IsFirstPublication,
		long LastChangeSequence,
		long ChangeSequence,
		Dictionary<string, List<Dictionary<string, object?>>> Tables,
		List<ChangeEvent> Changes);

	private sealed record ChangeEvent(long Sequence, string EntityType, string EntityId, string Operation);

	private sealed class ChangeHistory(string firstOperation, string lastOperation, long lastSequence)
	{
		public string FirstOperation { get; } = firstOperation;
		public string LastOperation { get; set; } = lastOperation;
		public long LastSequence { get; set; } = lastSequence;
	}

	private sealed record PreparedMedia(string FullPath, string PackagePath, PublicationMedia ManifestEntry);
}

public sealed record PublicationExportResult(string PublicationId, int ContentCount, int MediaCount, string Path);

public sealed class PublicationManifest
{
	public PublicationMetadata Metadata { get; init; } = new();
	public Dictionary<string, List<Dictionary<string, object?>>> Content { get; } = new(StringComparer.Ordinal);
	public List<PublicationChange> Added { get; } = [];
	public List<PublicationChange> Modified { get; } = [];
	public List<PublicationChange> Deleted { get; } = [];
	public List<PublicationMedia> Media { get; } = [];
}

public sealed class PublicationMetadata
{
	public string SchemaVersion { get; init; } = string.Empty;
	public string PublicationId { get; init; } = string.Empty;
	public string SourceVersion { get; init; } = string.Empty;
	public string ExportedAt { get; init; } = string.Empty;
	public long ChangeSequence { get; init; }
}

public sealed record PublicationChange(string EntityType, string EntityId, long Sequence, string? RevisionId);

public sealed class PublicationMedia
{
	public string QuestionId { get; init; } = string.Empty;
	public string Path { get; init; } = string.Empty;
	public long SizeBytes { get; set; }
	public string Sha256 { get; set; } = string.Empty;
}