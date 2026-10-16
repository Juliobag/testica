using System.Globalization;
using Testica.Models;
using Testica.Services;

namespace Testica.Data.Repositories;

public sealed class CycleRepository(DatabaseService database)
{
    public IReadOnlyList<Cycle> GetAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, order_index, status, updated_at, revision_id FROM cycles ORDER BY order_index, name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var cycles = new List<Cycle>();

        while (reader.Read())
        {
            cycles.Add(new Cycle
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                OrderIndex = reader.GetInt32(3),
                Status = reader.GetString(4),
                UpdatedAt = ReadDate(reader, 5),
                RevisionId = reader.GetString(6)
            });
        }

        return cycles;
    }

    public Cycle Save(Cycle cycle)
    {
        cycle.Id = string.IsNullOrWhiteSpace(cycle.Id) ? Guid.NewGuid().ToString("D") : cycle.Id;
        cycle.UpdatedAt = DateTime.UtcNow;

        database.ExecuteWrite((connection, transaction) =>
        {
            using (var revisionCommand = connection.CreateCommand())
            {
                revisionCommand.Transaction = transaction;
                revisionCommand.CommandText = "SELECT revision_id FROM cycles WHERE id = @id";
                revisionCommand.Parameters.AddWithValue("@id", cycle.Id);
                cycle.RevisionId = revisionCommand.ExecuteScalar() as string
                    ?? (string.IsNullOrWhiteSpace(cycle.RevisionId) ? Guid.NewGuid().ToString("D") : cycle.RevisionId);
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cycles (id, name, description, order_index, status, updated_at, revision_id)
                VALUES (@id, @name, @description, @order_index, @status, @updated_at, @revision_id)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    description = excluded.description,
                    order_index = excluded.order_index,
                    status = excluded.status,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("@id", cycle.Id);
            command.Parameters.AddWithValue("@name", cycle.Name);
            command.Parameters.AddWithValue("@description", (object?)cycle.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@order_index", cycle.OrderIndex);
            command.Parameters.AddWithValue("@status", cycle.Status);
            command.Parameters.AddWithValue("@updated_at", cycle.UpdatedAt.Value.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@revision_id", cycle.RevisionId);
            return command.ExecuteNonQuery();
        });

        return cycle;
    }

    private static DateTime? ReadDate(System.Data.IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTime.TryParse(reader.GetValue(ordinal).ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value
                : null;
}
