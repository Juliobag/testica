using System.Globalization;
using Testica.Models;
using Testica.Services;

namespace Testica.Data.Repositories;

public sealed class QuestionRepository(DatabaseService database)
{
	public IReadOnlyList<Question> GetAll()
	{
		using var connection = database.OpenConnection();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT id, theme_id, cycle_id, statement, context, image_path, difficulty, status, updated_at, revision_id FROM questions ORDER BY updated_at DESC";
		using var reader = command.ExecuteReader();
		var questions = new List<Question>();

		while (reader.Read())
		{
			questions.Add(new Question
			{
				Id = reader.GetString(0),
				ThemeId = reader.GetString(1),
				CycleId = reader.GetString(2),
				Statement = reader.GetString(3),
				Context = reader.IsDBNull(4) ? null : reader.GetString(4),
				ImagePath = reader.IsDBNull(5) ? null : reader.GetString(5),
				Difficulty = reader.GetInt32(6),
				Status = reader.GetString(7),
				UpdatedAt = ReadDate(reader, 8),
				RevisionId = reader.GetString(9)
			});
		}

		return questions;
	}

	public IReadOnlyList<Answer> GetAnswers(string questionId)
	{
		using var connection = database.OpenConnection();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT id, question_id, label, answer_text, is_correct, order_index, status FROM answers WHERE question_id = @question_id ORDER BY order_index";
		command.Parameters.AddWithValue("@question_id", questionId);
		using var reader = command.ExecuteReader();
		var answers = new List<Answer>();

		while (reader.Read())
		{
			answers.Add(new Answer
			{
				Id = reader.GetString(0),
				QuestionId = reader.GetString(1),
				Label = reader.GetString(2),
				AnswerText = reader.GetString(3),
				IsCorrect = reader.GetInt32(4) != 0,
				OrderIndex = reader.GetInt32(5),
				Status = reader.GetString(6)
			});
		}

		return answers;
	}

	public Question Save(Question question, IReadOnlyCollection<Answer> answers)
	{
		question.Id = string.IsNullOrWhiteSpace(question.Id) ? Guid.NewGuid().ToString("D") : question.Id;
		question.UpdatedAt = DateTime.UtcNow;

		database.ExecuteWrite((connection, transaction) =>
		{
			using (var revisionCommand = connection.CreateCommand())
			{
				revisionCommand.Transaction = transaction;
				revisionCommand.CommandText = "SELECT revision_id FROM questions WHERE id = @id";
				revisionCommand.Parameters.AddWithValue("@id", question.Id);
				var persistedRevisionId = revisionCommand.ExecuteScalar() as string;
				question.RevisionId = persistedRevisionId
					?? (string.IsNullOrWhiteSpace(question.RevisionId) ? Guid.NewGuid().ToString("D") : question.RevisionId);
			}

			using (var command = connection.CreateCommand())
			{
				command.Transaction = transaction;
				command.CommandText = """
					INSERT INTO questions (id, theme_id, cycle_id, statement, context, image_path, difficulty, status, updated_at, revision_id)
					VALUES (@id, @theme_id, @cycle_id, @statement, @context, @image_path, @difficulty, @status, @updated_at, @revision_id)
					ON CONFLICT(id) DO UPDATE SET
						theme_id = excluded.theme_id,
						cycle_id = excluded.cycle_id,
						statement = excluded.statement,
						context = excluded.context,
						image_path = excluded.image_path,
						difficulty = excluded.difficulty,
						status = excluded.status,
						updated_at = excluded.updated_at;
					""";
				command.Parameters.AddWithValue("@id", question.Id);
				command.Parameters.AddWithValue("@theme_id", question.ThemeId);
				command.Parameters.AddWithValue("@cycle_id", question.CycleId);
				command.Parameters.AddWithValue("@statement", question.Statement);
				command.Parameters.AddWithValue("@context", (object?)question.Context ?? DBNull.Value);
				command.Parameters.AddWithValue("@image_path", (object?)question.ImagePath ?? DBNull.Value);
				command.Parameters.AddWithValue("@difficulty", question.Difficulty);
				command.Parameters.AddWithValue("@status", question.Status);
				command.Parameters.AddWithValue("@updated_at", question.UpdatedAt.Value.ToString("O", CultureInfo.InvariantCulture));
				command.Parameters.AddWithValue("@revision_id", question.RevisionId);
				command.ExecuteNonQuery();
			}

			using (var deleteAnswers = connection.CreateCommand())
			{
				deleteAnswers.Transaction = transaction;
				deleteAnswers.CommandText = "DELETE FROM answers WHERE question_id = @question_id";
				deleteAnswers.Parameters.AddWithValue("@question_id", question.Id);
				deleteAnswers.ExecuteNonQuery();
			}

			foreach (var answer in answers)
			{
				answer.Id = string.IsNullOrWhiteSpace(answer.Id) ? Guid.NewGuid().ToString("D") : answer.Id;
				answer.QuestionId = question.Id;

				using var insertAnswer = connection.CreateCommand();
				insertAnswer.Transaction = transaction;
				insertAnswer.CommandText = """
					INSERT INTO answers (id, question_id, label, answer_text, is_correct, order_index, status)
					VALUES (@id, @question_id, @label, @answer_text, @is_correct, @order_index, @status);
					""";
				insertAnswer.Parameters.AddWithValue("@id", answer.Id);
				insertAnswer.Parameters.AddWithValue("@question_id", answer.QuestionId);
				insertAnswer.Parameters.AddWithValue("@label", answer.Label);
				insertAnswer.Parameters.AddWithValue("@answer_text", answer.AnswerText);
				insertAnswer.Parameters.AddWithValue("@is_correct", answer.IsCorrect ? 1 : 0);
				insertAnswer.Parameters.AddWithValue("@order_index", answer.OrderIndex);
				insertAnswer.Parameters.AddWithValue("@status", answer.Status);
				insertAnswer.ExecuteNonQuery();
			}

			return 0;
		});

		return question;
	}

	private static DateTime? ReadDate(System.Data.IDataRecord reader, int ordinal) =>
		reader.IsDBNull(ordinal)
			? null
			: DateTime.TryParse(reader.GetValue(ordinal).ToString(), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
				? value
				: null;
}
