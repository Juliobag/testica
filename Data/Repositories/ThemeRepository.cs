using System.Globalization;
using Testica.Models;
using Testica.Services;

namespace Testica.Data.Repositories;

public sealed class ThemeRepository(DatabaseService database)
{
	public IReadOnlyList<Theme> GetAll()
	{
		using var connection = database.OpenConnection();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT id, name, description, status, updated_at, revision_id FROM themes ORDER BY name COLLATE NOCASE";
		using var reader = command.ExecuteReader();
		var themes = new List<Theme>();

		while (reader.Read())
		{
			themes.Add(new Theme
			{
				Id = reader.GetString(0),
				Name = reader.GetString(1),
				Description = reader.IsDBNull(2) ? null : reader.GetString(2),
				Status = reader.GetString(3),
				UpdatedAt = ReadDate(reader, 4),
				RevisionId = reader.GetString(5)
			});
		}

		return themes;
	}

	public Theme Save(Theme theme)
	{
		theme.Id = string.IsNullOrWhiteSpace(theme.Id) ? Guid.NewGuid().ToString("D") : theme.Id;
		theme.UpdatedAt = DateTime.UtcNow;

		database.ExecuteWrite((connection, transaction) =>
		{
			using (var revisionCommand = connection.CreateCommand())
			{
				revisionCommand.Transaction = transaction;
				revisionCommand.CommandText = "SELECT revision_id FROM themes WHERE id = @id";
				revisionCommand.Parameters.AddWithValue("@id", theme.Id);
				theme.RevisionId = revisionCommand.ExecuteScalar() as string
					?? (string.IsNullOrWhiteSpace(theme.RevisionId) ? Guid.NewGuid().ToString("D") : theme.RevisionId);
			}

			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				INSERT INTO themes (id, name, description, status, updated_at, revision_id)
				VALUES (@id, @name, @description, @status, @updated_at, @revision_id)
				ON CONFLICT(id) DO UPDATE SET
					name = excluded.name,
					description = excluded.description,
					status = excluded.status,
					updated_at = excluded.updated_at;
				""";
			command.Parameters.AddWithValue("@id", theme.Id);
			command.Parameters.AddWithValue("@name", theme.Name);
			command.Parameters.AddWithValue("@description", (object?)theme.Description ?? DBNull.Value);
			command.Parameters.AddWithValue("@status", theme.Status);
			command.Parameters.AddWithValue("@updated_at", theme.UpdatedAt.Value.ToString("O", CultureInfo.InvariantCulture));
			command.Parameters.AddWithValue("@revision_id", theme.RevisionId);
			return command.ExecuteNonQuery();
		});

		return theme;
	}

	private static DateTime? ReadDate(System.Data.IDataRecord reader, int ordinal) =>
		reader.IsDBNull(ordinal)
			? null
			: DateTime.TryParse(reader.GetValue(ordinal).ToString(), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
				? value
				: null;
}
