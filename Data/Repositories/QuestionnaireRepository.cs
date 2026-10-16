using Testica.Models;
using Testica.Services;

namespace Testica.Data.Repositories;

public sealed class QuestionnaireRepository(DatabaseService database)
{
    public IReadOnlyList<Questionnaire> GetAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, description, theme_id, cycle_id, status, question_order_mode, question_limit, revision_id FROM questionnaires ORDER BY title COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var questionnaires = new List<Questionnaire>();

        while (reader.Read())
        {
            questionnaires.Add(new Questionnaire
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                ThemeId = reader.IsDBNull(3) ? null : reader.GetString(3),
                CycleId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status = reader.GetString(5),
                QuestionOrderMode = reader.GetString(6),
                QuestionLimit = reader.GetInt32(7),
                RevisionId = reader.GetString(8)
            });
        }

        return questionnaires;
    }

    public IReadOnlyList<string> GetQuestionIds(string questionnaireId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT question_id FROM questionnaire_questions WHERE questionnaire_id = @id ORDER BY order_index";
        command.Parameters.AddWithValue("@id", questionnaireId);
        using var reader = command.ExecuteReader();
        var questionIds = new List<string>();
        while (reader.Read()) questionIds.Add(reader.GetString(0));
        return questionIds;
    }

    public Questionnaire Save(Questionnaire questionnaire, IReadOnlyList<string> questionIds)
    {
        questionnaire.Id = string.IsNullOrWhiteSpace(questionnaire.Id) ? Guid.NewGuid().ToString("D") : questionnaire.Id;
        database.ExecuteWrite((connection, transaction) =>
        {
            using (var revisionCommand = connection.CreateCommand())
            {
                revisionCommand.Transaction = transaction;
                revisionCommand.CommandText = "SELECT revision_id FROM questionnaires WHERE id = @id";
                revisionCommand.Parameters.AddWithValue("@id", questionnaire.Id);
                questionnaire.RevisionId = revisionCommand.ExecuteScalar() as string
                    ?? (string.IsNullOrWhiteSpace(questionnaire.RevisionId) ? Guid.NewGuid().ToString("D") : questionnaire.RevisionId);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO questionnaires (id, title, description, theme_id, cycle_id, status, question_order_mode, question_limit, revision_id)
                    VALUES (@id, @title, @description, @theme_id, @cycle_id, @status, @order_mode, @question_limit, @revision_id)
                    ON CONFLICT(id) DO UPDATE SET
                        title = excluded.title,
                        description = excluded.description,
                        theme_id = excluded.theme_id,
                        cycle_id = excluded.cycle_id,
                        status = excluded.status,
                        question_order_mode = excluded.question_order_mode,
                        question_limit = excluded.question_limit;
                    """;
                command.Parameters.AddWithValue("@id", questionnaire.Id);
                command.Parameters.AddWithValue("@title", questionnaire.Title);
                command.Parameters.AddWithValue("@description", (object?)questionnaire.Description ?? DBNull.Value);
                command.Parameters.AddWithValue("@theme_id", (object?)questionnaire.ThemeId ?? DBNull.Value);
                command.Parameters.AddWithValue("@cycle_id", (object?)questionnaire.CycleId ?? DBNull.Value);
                command.Parameters.AddWithValue("@status", questionnaire.Status);
                command.Parameters.AddWithValue("@order_mode", questionnaire.QuestionOrderMode);
                command.Parameters.AddWithValue("@question_limit", questionnaire.QuestionLimit);
                command.Parameters.AddWithValue("@revision_id", questionnaire.RevisionId);
                command.ExecuteNonQuery();
            }

            using (var deleteQuestions = connection.CreateCommand())
            {
                deleteQuestions.Transaction = transaction;
                deleteQuestions.CommandText = "DELETE FROM questionnaire_questions WHERE questionnaire_id = @id";
                deleteQuestions.Parameters.AddWithValue("@id", questionnaire.Id);
                deleteQuestions.ExecuteNonQuery();
            }

            for (var index = 0; index < questionIds.Count; index++)
            {
                using var insertQuestion = connection.CreateCommand();
                insertQuestion.Transaction = transaction;
                insertQuestion.CommandText = "INSERT INTO questionnaire_questions (questionnaire_id, question_id, order_index) VALUES (@questionnaire_id, @question_id, @order_index)";
                insertQuestion.Parameters.AddWithValue("@questionnaire_id", questionnaire.Id);
                insertQuestion.Parameters.AddWithValue("@question_id", questionIds[index]);
                insertQuestion.Parameters.AddWithValue("@order_index", index);
                insertQuestion.ExecuteNonQuery();
            }

            return 0;
        });

        return questionnaire;
    }
}
