using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Testica.Data.Repositories;
using Testica.Models;

namespace Testica.ViewModels;

public partial class QuestionnairesViewModel : ObservableObject
{
	private readonly QuestionnaireRepository _questionnaireRepository;
	private Questionnaire? _selectedQuestionnaire;
	private string _title = string.Empty;
	private string? _description;
	private string? _selectedThemeId;
	private string? _selectedCycleId;
	private string _status = "draft";
	private string _orderMode = "fixed";
	private int _questionLimit = 1;
	private string _feedback = string.Empty;

	public QuestionnairesViewModel(
		QuestionnaireRepository questionnaireRepository,
		ObservableCollection<Question> questions,
		ObservableCollection<Theme> themes,
		ObservableCollection<Cycle> cycles)
	{
		_questionnaireRepository = questionnaireRepository;
		Themes = themes;
		Cycles = cycles;
		foreach (var question in questions) QuestionOptions.Add(new QuestionSelectionViewModel(question));
		questions.CollectionChanged += OnQuestionsChanged;
		foreach (var questionnaire in _questionnaireRepository.GetAll()) Questionnaires.Add(questionnaire);
	}

	public ObservableCollection<Questionnaire> Questionnaires { get; } = new();
	public ObservableCollection<QuestionSelectionViewModel> QuestionOptions { get; } = new();
	public ObservableCollection<Theme> Themes { get; }
	public ObservableCollection<Cycle> Cycles { get; }

	public Questionnaire? SelectedQuestionnaire
	{
		get => _selectedQuestionnaire;
		set
		{
			if (SetProperty(ref _selectedQuestionnaire, value) && value is not null)
			{
				Title = value.Title;
				Description = value.Description;
				SelectedThemeId = value.ThemeId;
				SelectedCycleId = value.CycleId;
				Status = value.Status;
				OrderMode = value.QuestionOrderMode;
				QuestionLimit = value.QuestionLimit;
				var orderedIds = _questionnaireRepository.GetQuestionIds(value.Id);
				var orderLookup = orderedIds.Select((id, index) => (id, index))
					.ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
				for (var index = 0; index < QuestionOptions.Count; index++)
				{
					var option = QuestionOptions[index];
					option.IsSelected = orderLookup.TryGetValue(option.Question.Id, out var orderIndex);
					option.OrderIndex = option.IsSelected ? orderIndex : index;
				}
				Feedback = string.Empty;
			}
		}
	}

	public string Title { get => _title; set => SetProperty(ref _title, value); }
	public string? Description { get => _description; set => SetProperty(ref _description, value); }
	public string? SelectedThemeId { get => _selectedThemeId; set => SetProperty(ref _selectedThemeId, value); }
	public string? SelectedCycleId { get => _selectedCycleId; set => SetProperty(ref _selectedCycleId, value); }
	public string Status { get => _status; set => SetProperty(ref _status, value); }
	public string OrderMode { get => _orderMode; set => SetProperty(ref _orderMode, value); }
	public int QuestionLimit { get => _questionLimit; set => SetProperty(ref _questionLimit, value); }
	public string Feedback { get => _feedback; private set => SetProperty(ref _feedback, value); }

	[RelayCommand]
	private void NewQuestionnaire()
	{
		SelectedQuestionnaire = null;
		Title = string.Empty;
		Description = string.Empty;
		SelectedThemeId = null;
		SelectedCycleId = null;
		Status = "draft";
		OrderMode = "fixed";
		QuestionLimit = 1;
		Feedback = string.Empty;
		foreach (var option in QuestionOptions) option.IsSelected = false;
	}

	[RelayCommand]
	private void SaveQuestionnaire()
	{
		var selectedOptions = QuestionOptions.Where(option => option.IsSelected)
			.OrderBy(option => option.OrderIndex)
			.ToArray();

		if (string.IsNullOrWhiteSpace(Title) || selectedOptions.Length == 0
			|| QuestionLimit < 1 || QuestionLimit > selectedOptions.Length)
		{
			Feedback = "Indiquez un titre, sélectionnez des questions et choisissez une limite valide.";
			return;
		}

		var questionnaire = SelectedQuestionnaire ?? new Questionnaire();
		questionnaire.Title = Title.Trim();
		questionnaire.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
		questionnaire.ThemeId = SelectedThemeId;
		questionnaire.CycleId = SelectedCycleId;
		questionnaire.Status = Status;
		questionnaire.QuestionOrderMode = OrderMode;
		questionnaire.QuestionLimit = QuestionLimit;
		_questionnaireRepository.Save(questionnaire, selectedOptions.Select(option => option.Question.Id).ToArray());

		if (!Questionnaires.Contains(questionnaire)) Questionnaires.Add(questionnaire);
		SelectedQuestionnaire = questionnaire;
		Feedback = "Questionnaire enregistré.";
	}

	private void OnQuestionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (e.NewItems is null) return;
		foreach (Question question in e.NewItems)
		{
			if (QuestionOptions.All(option => option.Question.Id != question.Id))
			{
				QuestionOptions.Add(new QuestionSelectionViewModel(question));
			}
		}
	}
}

public partial class QuestionSelectionViewModel(Question question) : ObservableObject
{
	private bool _isSelected;
	private int _orderIndex;

	public Question Question { get; } = question;
	public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
	public int OrderIndex { get => _orderIndex; set => SetProperty(ref _orderIndex, value); }
}