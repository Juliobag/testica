using System.Collections.Concurrent;
using System.Data.SQLite;
using System.IO;
using Testica.Data;

namespace Testica.Services;

public sealed class DatabaseService
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> DatabaseLocks = new(StringComparer.OrdinalIgnoreCase);
	private readonly SQLiteDbContext _context;
	private readonly SemaphoreSlim _databaseLock;

	public DatabaseService(string databasePath = SQLiteDbContext.DefaultDatabasePath)
	{
		_context = new SQLiteDbContext(databasePath);
		_databaseLock = DatabaseLocks.GetOrAdd(_context.DatabasePath, static _ => new SemaphoreSlim(1, 1));
		InitializeDatabase();
	}

	public SQLiteConnection OpenConnection() => _context.CreateConnection();

	public string DatabasePath => _context.DatabasePath;

	public T ExecuteWrite<T>(Func<SQLiteConnection, SQLiteTransaction, T> operation)
	{
		ArgumentNullException.ThrowIfNull(operation);
		_databaseLock.Wait();

		try
		{
			using var connection = OpenConnection();
			using var transaction = connection.BeginTransaction();
			var result = operation(connection, transaction);
			transaction.Commit();
			return result;
		}
		finally
		{
			_databaseLock.Release();
		}
	}

	private void InitializeDatabase()
	{
		_databaseLock.Wait();

		try
		{
			var directory = Path.GetDirectoryName(_context.DatabasePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			using var connection = OpenConnection();
			using (var journalMode = connection.CreateCommand())
			{
				journalMode.CommandText = "PRAGMA journal_mode = WAL;";
				journalMode.ExecuteScalar();
			}

			using var transaction = connection.BeginTransaction();
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				CREATE TABLE IF NOT EXISTS themes (
					id TEXT PRIMARY KEY,
					name TEXT NOT NULL,
					description TEXT,
					status TEXT NOT NULL DEFAULT 'active',
					updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
					revision_id TEXT NOT NULL
				);

				CREATE TABLE IF NOT EXISTS cycles (
					id TEXT PRIMARY KEY,
					name TEXT NOT NULL,
					description TEXT,
					order_index INTEGER NOT NULL,
					status TEXT NOT NULL DEFAULT 'active',
					updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
					revision_id TEXT NOT NULL
				);

				CREATE TABLE IF NOT EXISTS questions (
					id TEXT PRIMARY KEY,
					theme_id TEXT NOT NULL,
					cycle_id TEXT NOT NULL,
					statement TEXT NOT NULL,
					context TEXT,
					image_path TEXT,
					difficulty INTEGER NOT NULL,
					status TEXT NOT NULL DEFAULT 'draft',
					updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
					revision_id TEXT NOT NULL,
					FOREIGN KEY (theme_id) REFERENCES themes(id),
					FOREIGN KEY (cycle_id) REFERENCES cycles(id)
				);

				CREATE TABLE IF NOT EXISTS answers (
					id TEXT PRIMARY KEY,
					question_id TEXT NOT NULL,
					label TEXT NOT NULL,
					answer_text TEXT NOT NULL,
					is_correct INTEGER NOT NULL CHECK (is_correct IN (0, 1)),
					order_index INTEGER NOT NULL,
					status TEXT NOT NULL DEFAULT 'active',
					FOREIGN KEY (question_id) REFERENCES questions(id) ON DELETE CASCADE
				);

				CREATE TABLE IF NOT EXISTS questionnaires (
					id TEXT PRIMARY KEY,
					title TEXT NOT NULL,
					description TEXT,
					theme_id TEXT,
					cycle_id TEXT,
					status TEXT NOT NULL DEFAULT 'draft',
					question_order_mode TEXT NOT NULL CHECK (question_order_mode IN ('fixed', 'random')),
					question_limit INTEGER NOT NULL,
					revision_id TEXT NOT NULL,
					FOREIGN KEY (theme_id) REFERENCES themes(id),
					FOREIGN KEY (cycle_id) REFERENCES cycles(id)
				);

				CREATE TABLE IF NOT EXISTS questionnaire_questions (
					questionnaire_id TEXT NOT NULL,
					question_id TEXT NOT NULL,
					order_index INTEGER NOT NULL,
					PRIMARY KEY (questionnaire_id, question_id),
					FOREIGN KEY (questionnaire_id) REFERENCES questionnaires(id) ON DELETE CASCADE,
					FOREIGN KEY (question_id) REFERENCES questions(id)
				);

				CREATE TABLE IF NOT EXISTS publication_changes (
					change_seq INTEGER PRIMARY KEY AUTOINCREMENT,
					entity_type TEXT NOT NULL,
					entity_id TEXT NOT NULL,
					operation TEXT NOT NULL CHECK (operation IN ('added', 'modified', 'deleted')),
					changed_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
				);

				CREATE TABLE IF NOT EXISTS publication_state (
					state_id INTEGER PRIMARY KEY CHECK (state_id = 1),
					last_change_seq INTEGER NOT NULL DEFAULT 0,
					last_publication_id TEXT,
					exported_at TEXT
				);

				CREATE TABLE IF NOT EXISTS firebase_sync_state (
					sync_key TEXT PRIMARY KEY,
					synced_hash TEXT,
					is_deleted INTEGER NOT NULL DEFAULT 0
				);

				INSERT OR IGNORE INTO publication_state (state_id, last_change_seq) VALUES (1, 0);

				CREATE TRIGGER IF NOT EXISTS publication_themes_insert AFTER INSERT ON themes BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('theme', NEW.id, 'added');
				END;
				CREATE TRIGGER IF NOT EXISTS publication_themes_update AFTER UPDATE ON themes BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation)
					VALUES ('theme', NEW.id, CASE WHEN lower(trim(NEW.status)) IN ('inactive', 'archived', 'deleted') THEN 'deleted' ELSE 'modified' END);
				END;
				CREATE TRIGGER IF NOT EXISTS publication_themes_delete AFTER DELETE ON themes BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('theme', OLD.id, 'deleted');
				END;

				CREATE TRIGGER IF NOT EXISTS publication_cycles_insert AFTER INSERT ON cycles BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('cycle', NEW.id, 'added');
				END;
				CREATE TRIGGER IF NOT EXISTS publication_cycles_update AFTER UPDATE ON cycles BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation)
					VALUES ('cycle', NEW.id, CASE WHEN lower(trim(NEW.status)) IN ('inactive', 'archived', 'deleted') THEN 'deleted' ELSE 'modified' END);
				END;
				CREATE TRIGGER IF NOT EXISTS publication_cycles_delete AFTER DELETE ON cycles BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('cycle', OLD.id, 'deleted');
				END;

				CREATE TRIGGER IF NOT EXISTS publication_questions_insert AFTER INSERT ON questions BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('question', NEW.id, 'added');
				END;
				CREATE TRIGGER IF NOT EXISTS publication_questions_update AFTER UPDATE ON questions BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation)
					VALUES ('question', NEW.id, CASE WHEN lower(trim(NEW.status)) IN ('inactive', 'archived', 'deleted') THEN 'deleted' ELSE 'modified' END);
				END;
				CREATE TRIGGER IF NOT EXISTS publication_questions_delete AFTER DELETE ON questions BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('question', OLD.id, 'deleted');
				END;

				CREATE TRIGGER IF NOT EXISTS publication_answers_insert AFTER INSERT ON answers BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('answer', NEW.id, 'added');
				END;
				CREATE TRIGGER IF NOT EXISTS publication_answers_update AFTER UPDATE ON answers BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation)
					VALUES ('answer', NEW.id, CASE WHEN lower(trim(NEW.status)) IN ('inactive', 'archived', 'deleted') THEN 'deleted' ELSE 'modified' END);
				END;
				CREATE TRIGGER IF NOT EXISTS publication_answers_delete AFTER DELETE ON answers BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('answer', OLD.id, 'deleted');
				END;

				CREATE TRIGGER IF NOT EXISTS publication_questionnaires_insert AFTER INSERT ON questionnaires BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('questionnaire', NEW.id, 'added');
				END;
				CREATE TRIGGER IF NOT EXISTS publication_questionnaires_update AFTER UPDATE ON questionnaires BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation)
					VALUES ('questionnaire', NEW.id, CASE WHEN lower(trim(NEW.status)) IN ('inactive', 'archived', 'deleted') THEN 'deleted' ELSE 'modified' END);
				END;
				CREATE TRIGGER IF NOT EXISTS publication_questionnaires_delete AFTER DELETE ON questionnaires BEGIN
					INSERT INTO publication_changes (entity_type, entity_id, operation) VALUES ('questionnaire', OLD.id, 'deleted');
				END;
				""";
			command.ExecuteNonQuery();
			transaction.Commit();
		}
		finally
		{
			_databaseLock.Release();
		}
	}
}
