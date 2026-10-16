using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Testica.Data.Repositories;
using Testica.Models;

namespace Testica.ViewModels;

public partial class QuestionsViewModel : ObservableObject
{
	private readonly QuestionRepository _questionRepository;
	private Question? _selectedQuestion;
	private Answer? _selectedAnswer;
	private string _searchText = string.Empty;
	private string _selectedStatusFilter = "Tous";
	private string _statement = string.Empty;
	private string? _context;
	private string? _imagePath;
	private string? _selectedThemeId;
	private string? _selectedCycleId;
	private int _difficulty = 1;
	private string _questionStatus = "draft";
	private string _validationMessage = string.Empty;

	public QuestionsViewModel(
		QuestionRepository questionRepository,
		ObservableCollection<Theme> themes,
		ObservableCollection<Cycle> cycles)
	{
		_questionRepository = questionRepository;
		Themes = themes;
		Cycles = cycles;
		foreach (var question in _questionRepository.GetAll()) Questions.Add(question);

		FilteredQuestions = CollectionViewSource.GetDefaultView(Questions);
		FilteredQuestions.Filter = MatchesQuestion;
	}

	public ObservableCollection<Question> Questions { get; } = new();
	public ObservableCollection<Answer> Answers { get; } = new();
	public ObservableCollection<Theme> Themes { get; }
	public ObservableCollection<Cycle> Cycles { get; }
	public IReadOnlyList<int> DifficultyLevels { get; } = [1, 2, 3, 4, 5];
	public IReadOnlyList<string> StatusFilters { get; } = ["Tous", "Brouillons", "Actives", "Inactives"];
	public ICollectionView FilteredQuestions { get; }

	public Question? SelectedQuestion
	{
		get => _selectedQuestion;
		set
		{
			if (SetProperty(ref _selectedQuestion, value) && value is not null)
			{
				Statement = value.Statement;
				Context = value.Context;
				ImagePath = value.ImagePath;
				SelectedThemeId = value.ThemeId;
				SelectedCycleId = value.CycleId;
				Difficulty = value.Difficulty;
				QuestionStatus = value.Status;
				Answers.Clear();
				foreach (var answer in _questionRepository.GetAnswers(value.Id)) Answers.Add(answer);
				ValidationMessage = string.Empty;
			}
		}
	}

	public Answer? SelectedAnswer { get => _selectedAnswer; set => SetProperty(ref _selectedAnswer, value); }

	public string SearchText
	{
		get => _searchText;
		set
		{
			if (SetProperty(ref _searchText, value)) FilteredQuestions.Refresh();
		}
	}

	public string SelectedStatusFilter
	{
		get => _selectedStatusFilter;
		set
		{
			if (SetProperty(ref _selectedStatusFilter, value)) FilteredQuestions.Refresh();
		}
	}

	public string Statement { get => _statement; set => SetProperty(ref _statement, value); }
	public string? Context { get => _context; set => SetProperty(ref _context, value); }
	public string? ImagePath { get => _imagePath; set => SetProperty(ref _imagePath, value); }
	public string? SelectedThemeId { get => _selectedThemeId; set => SetProperty(ref _selectedThemeId, value); }
	public string? SelectedCycleId { get => _selectedCycleId; set => SetProperty(ref _selectedCycleId, value); }
	public int Difficulty { get => _difficulty; set => SetProperty(ref _difficulty, value); }
	public string QuestionStatus { get => _questionStatus; set => SetProperty(ref _questionStatus, value); }
	public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

	[RelayCommand]
	private void NewQuestion()
	{
		SelectedQuestion = null;
		Statement = string.Empty;
		Context = string.Empty;
		ImagePath = string.Empty;
		SelectedThemeId = null;
		SelectedCycleId = null;
		Difficulty = 1;
		QuestionStatus = "draft";
		ValidationMessage = string.Empty;
		Answers.Clear();
		Answers.Add(new Answer { Label = "A", OrderIndex = 0 });
		Answers.Add(new Answer { Label = "B", OrderIndex = 1 });
	}

	[RelayCommand]
	private void AddAnswer()
	{
		Answers.Add(new Answer
		{
			Label = ((char)('A' + Answers.Count)).ToString(),
			OrderIndex = Answers.Count
		});
	}

	[RelayCommand]
	private void RemoveAnswer(Answer? answer)
	{
		if (answer is not null) Answers.Remove(answer);
		for (var index = 0; index < Answers.Count; index++) Answers[index].OrderIndex = index;
	}

	[RelayCommand]
	private void ChooseImage()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Choisir une illustration",
			Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Tous les fichiers|*.*"
		};

		if (dialog.ShowDialog() == true) ImagePath = dialog.FileName;
	}

	[RelayCommand]
	private void SaveQuestion()
	{
		if (string.IsNullOrWhiteSpace(Statement) || string.IsNullOrWhiteSpace(SelectedThemeId)
			|| string.IsNullOrWhiteSpace(SelectedCycleId) || Answers.Count < 2
			|| Answers.Any(answer => string.IsNullOrWhiteSpace(answer.AnswerText))
			|| !Answers.Any(answer => answer.IsCorrect))
		{
			ValidationMessage = "Renseignez l’énoncé, le thème, le cycle, deux réponses et au moins une bonne réponse.";
			return;
		}

		var question = SelectedQuestion ?? new Question();
		question.Statement = Statement.Trim();
		question.Context = string.IsNullOrWhiteSpace(Context) ? null : Context.Trim();
		question.ImagePath = string.IsNullOrWhiteSpace(ImagePath) ? null : ImagePath.Trim();
		question.ThemeId = SelectedThemeId;
		question.CycleId = SelectedCycleId;
		question.Difficulty = Difficulty;
		question.Status = QuestionStatus;
		_questionRepository.Save(question, Answers.ToArray());

		if (!Questions.Contains(question)) Questions.Insert(0, question);
		SelectedQuestion = question;
		FilteredQuestions.Refresh();
		ValidationMessage = "Question enregistrée.";
	}

	private bool MatchesQuestion(object? item)
	{
		if (item is not Question question) return false;
		var matchesStatus = SelectedStatusFilter switch
		{
			"Brouillons" => question.Status == "draft",
			"Actives" => question.Status == "active",
			"Inactives" => question.Status == "inactive",
			_ => true
		};

		return matchesStatus && (string.IsNullOrWhiteSpace(SearchText)
			|| question.Statement.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
	}
}