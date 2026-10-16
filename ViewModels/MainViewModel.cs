using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Media;
using Testica.Data.Repositories;
using Testica.Services;

namespace Testica.ViewModels;

public partial class MainViewModel : ObservableObject
{
	public MainViewModel()
	{
		var database = new DatabaseService();
		DatabasePath = database.DatabasePath;
		var themes = new ThemeRepository(database);
		var cycles = new CycleRepository(database);
		var questions = new QuestionRepository(database);
		Themes = new ThemesViewModel(themes, cycles);
		Questions = new QuestionsViewModel(questions, Themes.Themes, Themes.Cycles);
		Questionnaires = new QuestionnairesViewModel(new QuestionnaireRepository(database), Questions.Questions, Themes.Themes, Themes.Cycles);
		Export = new ExportViewModel(new ExportService(database), new ImportService(database), new FirebaseSyncService(database));
	}

	public string DatabasePath { get; }
	private bool _isDarkMode;
	public bool IsDarkMode
	{
		get => _isDarkMode;
		private set
		{
			if (SetProperty(ref _isDarkMode, value)) OnPropertyChanged(nameof(ThemeToggleLabel));
		}
	}
	public string ThemeToggleLabel => IsDarkMode ? "☀ Mode clair" : "☾ Mode sombre";

	[RelayCommand]
	private void ToggleTheme()
	{
		IsDarkMode = !IsDarkMode;
		var palette = IsDarkMode
			? new Dictionary<string, string>
			{
				["CanvasBrush"] = "#111815", ["PanelBrush"] = "#18221D", ["SurfaceBrush"] = "#202C26",
				["InputBrush"] = "#18221D", ["PrimaryTextBrush"] = "#E5EEE9", ["BodyTextBrush"] = "#CBD8D1",
				["MutedTextBrush"] = "#9EAEA5", ["BorderBrush"] = "#33433A", ["AccentBrush"] = "#78B49A",
				["AccentTextBrush"] = "#102019", ["AccentSoftBrush"] = "#2B4035", ["SuccessBrush"] = "#65C395",
				["SuccessTextBrush"] = "#9BD5B5", ["WarningTextBrush"] = "#E0A276", ["DangerTextBrush"] = "#F08C7D",
				["BrandGoldBrush"] = "#F5C96B"
			}
			: new Dictionary<string, string>
			{
				["CanvasBrush"] = "#F4F7F5", ["PanelBrush"] = "#EEF3F0", ["SurfaceBrush"] = "#FFFFFF",
				["InputBrush"] = "#FFFFFF", ["PrimaryTextBrush"] = "#183C35", ["BodyTextBrush"] = "#53635E",
				["MutedTextBrush"] = "#687870", ["BorderBrush"] = "#DCE4DE", ["AccentBrush"] = "#183C35",
				["AccentTextBrush"] = "#FFFFFF", ["AccentSoftBrush"] = "#E5EFEA", ["SuccessBrush"] = "#56A884",
				["SuccessTextBrush"] = "#315C4B", ["WarningTextBrush"] = "#A45B3D", ["DangerTextBrush"] = "#A44837",
				["BrandGoldBrush"] = "#F5C96B"
			};

		foreach (var (key, color) in palette)
		{
			Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
		}
	}

	public ThemesViewModel Themes { get; }
	public QuestionsViewModel Questions { get; }
	public QuestionnairesViewModel Questionnaires { get; }
	public ExportViewModel Export { get; }

	private int _selectedSectionIndex;

	public int SelectedSectionIndex
	{
		get => _selectedSectionIndex;
		set
		{
			if (SetProperty(ref _selectedSectionIndex, value))
			{
				OnPropertyChanged(nameof(IsThemesSelected));
				OnPropertyChanged(nameof(IsQuestionsSelected));
				OnPropertyChanged(nameof(IsQuestionnairesSelected));
				OnPropertyChanged(nameof(IsExportSelected));
			}
		}
	}

	public bool IsThemesSelected { get => SelectedSectionIndex == 0; set { if (value) SelectedSectionIndex = 0; } }
	public bool IsQuestionsSelected { get => SelectedSectionIndex == 1; set { if (value) SelectedSectionIndex = 1; } }
	public bool IsQuestionnairesSelected { get => SelectedSectionIndex == 2; set { if (value) SelectedSectionIndex = 2; } }
	public bool IsExportSelected { get => SelectedSectionIndex == 3; set { if (value) SelectedSectionIndex = 3; } }

	[RelayCommand]
	private void AddTheme()
	{
		SelectedSectionIndex = 0;
		Themes.NewThemeCommand.Execute(null);
	}

	[RelayCommand]
	private void AddCycle()
	{
		SelectedSectionIndex = 0;
		Themes.NewCycleCommand.Execute(null);
	}

	[RelayCommand]
	private void AddQuestion()
	{
		SelectedSectionIndex = 1;
		Questions.NewQuestionCommand.Execute(null);
	}

	[RelayCommand]
	private void AddQuestionnaire()
	{
		SelectedSectionIndex = 2;
		Questionnaires.NewQuestionnaireCommand.Execute(null);
	}
}
