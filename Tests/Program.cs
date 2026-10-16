using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Testica.Data.Repositories;
using Testica.Models;
using Testica.Services;

var root = Path.Combine(Path.GetTempPath(), "testica-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
	var database = new DatabaseService(Path.Combine(root, "ctb.sqlite"));
	var themes = new ThemeRepository(database);
	var cycles = new CycleRepository(database);
	var questions = new QuestionRepository(database);
	var theme = themes.Save(new Theme { Name = "Theme", Status = "active" });
	var cycle = cycles.Save(new Cycle { Name = "Cycle", OrderIndex = 1, Status = "active" });
	var themeId = theme.Id;
	var themeRevisionId = theme.RevisionId;
	theme.Name = "Edited theme";
	theme.RevisionId = Guid.NewGuid().ToString("D");
	themes.Save(theme);
	Require(theme.Id == themeId && theme.RevisionId == themeRevisionId, "Theme Id and RevisionId remain stable across edits.");
	var cycleId = cycle.Id;
	var cycleRevisionId = cycle.RevisionId;
	cycle.Name = "Edited cycle";
	cycle.RevisionId = Guid.NewGuid().ToString("D");
	cycles.Save(cycle);
	Require(cycle.Id == cycleId && cycle.RevisionId == cycleRevisionId, "Cycle Id and RevisionId remain stable across edits.");
	var question = questions.Save(new Question
	{
		ThemeId = theme.Id, CycleId = cycle.Id, Statement = "Initial statement", Difficulty = 1,
		Status = "active"
	}, [new Answer { Label = "A", AnswerText = "First", IsCorrect = true, OrderIndex = 0 }]);
	var originalId = question.Id;
	var originalRevisionId = question.RevisionId;
	Require(!string.IsNullOrWhiteSpace(originalId) && !string.IsNullOrWhiteSpace(originalRevisionId), "New question IDs are generated.");

	question.Statement = "Edited statement";
	question.RevisionId = Guid.NewGuid().ToString("D"); // Simulate a stale or modified editor model.
	questions.Save(question, [new Answer { Label = "B", AnswerText = "Second", IsCorrect = true, OrderIndex = 0 }]);
	var saved = questions.GetAll().Single(item => item.Id == originalId);
	Require(saved.Statement == "Edited statement", "Question edits persist to SQLite.");
	Require(saved.Id == originalId, "Question Id remains stable across edits.");
	Require(saved.RevisionId == originalRevisionId, "Question RevisionId remains stable across edits.");
	Require(questions.GetAnswers(originalId).Single().AnswerText == "Second", "Question answers are replaced atomically on edit.");
	var questionnaires = new QuestionnaireRepository(database);
	var questionnaire = questionnaires.Save(new Questionnaire
	{
		Title = "Test questionnaire", ThemeId = theme.Id, CycleId = cycle.Id,
		QuestionOrderMode = "fixed", QuestionLimit = 1, Status = "active"
	}, [originalId]);
	var questionnaireId = questionnaire.Id;
	var questionnaireRevisionId = questionnaire.RevisionId;
	questionnaire.Title = "Edited questionnaire";
	questionnaire.RevisionId = Guid.NewGuid().ToString("D");
	questionnaires.Save(questionnaire, [originalId]);
	Require(questionnaire.Id == questionnaireId && questionnaire.RevisionId == questionnaireRevisionId,
		"Questionnaire Id and RevisionId remain stable across edits.");

	var packagePath = Path.Combine(root, "publication.zip");
	var export = new ExportService(database).CreatePublicationPackage(packagePath);
	Require(File.Exists(packagePath) && export.Path == packagePath, "Publication ZIP is created.");
	using (var archive = ZipFile.OpenRead(packagePath))
	{
		var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidOperationException("manifest.json is missing.");
		using var stream = manifestEntry.Open();
		using var reader = new StreamReader(stream);
		var manifest = JObject.Parse(reader.ReadToEnd());
		Require(manifest["Content"]?["questions"]?.Any(row => (string?)row?["id"] == originalId && (string?)row?["statement"] == "Edited statement") == true,
			"Publication manifest contains the current question.");
		Require((string?)manifest["Added"]?.FirstOrDefault(item => (string?)item?["EntityType"] == "question")?["RevisionId"] == originalRevisionId,
			"Publication change record retains the question RevisionId.");
	}

	// WPF sync is restricted to the separate editor collection and UID allow-list.
	var sourceRoot = Directory.GetCurrentDirectory();
	Require(FirebaseClientOptions.AuthoringCollection == "testica_authoring",
		"Firestore sync targets only the separate authoring collection.");
	Require(FirebaseSyncService.HashPayload("{\"b\":1,\"a\":2}") == FirebaseSyncService.HashPayload("{\"a\":2,\"b\":1}"),
		"Firestore change hashes do not depend on JSON property order.");
	var firestoreRules = File.ReadAllText(Path.Combine(sourceRoot, "Firebase", "FirestoreRules.fragment"));
	Require(firestoreRules.Contains("testica_sync_editors", StringComparison.Ordinal)
		&& firestoreRules.Contains("active == true", StringComparison.Ordinal),
		"Firestore rules require an active editor allow-list entry.");
	Console.WriteLine("All regression checks passed.");
}
finally
{
	Directory.Delete(root, recursive: true);
}

static void Require(bool condition, string description)
{
	if (!condition) throw new InvalidOperationException(description);
	Console.WriteLine($"PASS {description}");
}
