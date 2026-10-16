using System.Data.SQLite;
using Newtonsoft.Json.Linq;

namespace Testica.Services;

/// <summary>Imports the JSON manifest produced by Testica's publication export.</summary>
public sealed class ImportService(DatabaseService database)
{
	private static readonly (string Table, string[] Columns, string[] Required)[] Tables =
	[
		("themes", ["id", "name", "description", "status", "updated_at", "revision_id"], ["id", "name", "status", "revision_id"]),
		("cycles", ["id", "name", "description", "order_index", "status", "updated_at", "revision_id"], ["id", "name", "order_index", "status", "revision_id"]),
		("questionnaires", ["id", "title", "description", "theme_id", "cycle_id", "status", "question_order_mode", "question_limit", "revision_id"], ["id", "title", "status", "question_order_mode", "question_limit", "revision_id"]),
		("questions", ["id", "theme_id", "cycle_id", "statement", "context", "image_path", "difficulty", "status", "updated_at", "revision_id"], ["id", "theme_id", "cycle_id", "statement", "difficulty", "status", "revision_id"]),
		("answers", ["id", "question_id", "label", "answer_text", "is_correct", "order_index", "status"], ["id", "question_id", "label", "answer_text", "is_correct", "order_index", "status"]),
		("questionnaire_questions", ["questionnaire_id", "question_id", "order_index"], ["questionnaire_id", "question_id", "order_index"])
	];

	public ImportResult ImportPublicationJson(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var root = JObject.Parse(File.ReadAllText(path));
		var metadata = GetProperty(root, "metadata") as JObject;
		if ((string?)GetProperty(metadata, "schemaVersion") != "1.0")
			throw new InvalidDataException("Version du fichier JSON absente ou non prise en charge.");
		if (GetProperty(root, "content") is not JObject content)
			throw new InvalidDataException("Le fichier ne contient pas de section content valide.");

		var rows = new Dictionary<string, JArray>(StringComparer.Ordinal);
		foreach (var (table, _, required) in Tables)
		{
			if (GetProperty(content, table) is not JArray array)
				throw new InvalidDataException($"La liste « {table} » est absente ou invalide.");
			foreach (var (item, index) in array.Select((item, index) => (item, index)))
			{
				if (item is not JObject row)
					throw new InvalidDataException($"L’élément {index + 1} de « {table} » n’est pas un objet JSON.");
				foreach (var column in required)
					if (row[column] is null or { Type: JTokenType.Null })
						throw new InvalidDataException($"Le champ « {column} » manque dans « {table} » (élément {index + 1}).");
			}
			rows.Add(table, array);
		}

		var imported = 0;
		database.ExecuteWrite((connection, transaction) =>
		{
			foreach (var (table, columns, _) in Tables)
			{
				foreach (JObject row in rows[table])
				{
					using var command = connection.CreateCommand();
					command.Transaction = transaction;
					var placeholders = columns.Select((_, i) => "@p" + i).ToArray();
					var keyColumns = table == "questionnaire_questions" ? new[] { "questionnaire_id", "question_id" } : ["id"];
					var updates = columns.Where(column => !keyColumns.Contains(column, StringComparer.Ordinal))
						.Select(column => $"{column} = excluded.{column}").ToArray();
					command.CommandText = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)}) ON CONFLICT({string.Join(", ", keyColumns)}) DO UPDATE SET {string.Join(", ", updates)}";
					for (var i = 0; i < columns.Length; i++)
					{
						var token = row[columns[i]];
						object? value = token is null or { Type: JTokenType.Null } ? DBNull.Value : ((JValue)token).Value;
						if (columns[i] == "is_correct" && value is bool correct) value = correct ? 1 : 0;
						command.Parameters.AddWithValue(placeholders[i], value ?? DBNull.Value);
					}
					command.ExecuteNonQuery();
					imported++;
				}
			}
			return 0;
		});
		return new ImportResult(imported);
	}

	private static JToken? GetProperty(JObject? obj, string name) =>
		obj?.Properties().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

public sealed record ImportResult(int ImportedCount);
