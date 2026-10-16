using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Testica.Data.Repositories;
using Testica.Models;

namespace Testica.ViewModels;

public partial class ThemesViewModel : ObservableObject
{
	private readonly ThemeRepository _themeRepository;
	private readonly CycleRepository _cycleRepository;
	private Theme? _selectedTheme;
	private Cycle? _selectedCycle;
	private string _themeName = string.Empty;
	private string? _themeDescription;
	private bool _themeIsActive = true;
	private string _cycleName = string.Empty;
	private string? _cycleDescription;
	private int _cycleOrderIndex;
	private bool _cycleIsActive = true;
	private string _selectedStatusFilter = "Tous";
	private int _selectedReferentialTab;

	public ThemesViewModel(ThemeRepository themeRepository, CycleRepository cycleRepository)
	{
		_themeRepository = themeRepository;
		_cycleRepository = cycleRepository;
		foreach (var theme in _themeRepository.GetAll()) Themes.Add(theme);
		foreach (var cycle in _cycleRepository.GetAll()) Cycles.Add(cycle);

		FilteredThemes = CollectionViewSource.GetDefaultView(Themes);
		FilteredThemes.Filter = item => MatchesStatus(((Theme)item!).Status);
		FilteredCycles = CollectionViewSource.GetDefaultView(Cycles);
		FilteredCycles.Filter = item => MatchesStatus(((Cycle)item!).Status);
	}

	public ObservableCollection<Theme> Themes { get; } = new();
	public ObservableCollection<Cycle> Cycles { get; } = new();
	public ICollectionView FilteredThemes { get; }
	public ICollectionView FilteredCycles { get; }
	public IReadOnlyList<string> StatusFilters { get; } = ["Tous", "Actifs", "Inactifs"];
	public int SelectedReferentialTab { get => _selectedReferentialTab; set => SetProperty(ref _selectedReferentialTab, value); }

	public string SelectedStatusFilter
	{
		get => _selectedStatusFilter;
		set
		{
			if (SetProperty(ref _selectedStatusFilter, value))
			{
				FilteredThemes.Refresh();
				FilteredCycles.Refresh();
			}
		}
	}

	public Theme? SelectedTheme
	{
		get => _selectedTheme;
		set
		{
			if (SetProperty(ref _selectedTheme, value) && value is not null)
			{
				ThemeName = value.Name;
				ThemeDescription = value.Description;
				ThemeIsActive = value.Status == "active";
			}
		}
	}

	public Cycle? SelectedCycle
	{
		get => _selectedCycle;
		set
		{
			if (SetProperty(ref _selectedCycle, value) && value is not null)
			{
				CycleName = value.Name;
				CycleDescription = value.Description;
				CycleOrderIndex = value.OrderIndex;
				CycleIsActive = value.Status == "active";
			}
		}
	}

	public string ThemeName { get => _themeName; set => SetProperty(ref _themeName, value); }
	public string? ThemeDescription { get => _themeDescription; set => SetProperty(ref _themeDescription, value); }
	public bool ThemeIsActive { get => _themeIsActive; set => SetProperty(ref _themeIsActive, value); }
	public string CycleName { get => _cycleName; set => SetProperty(ref _cycleName, value); }
	public string? CycleDescription { get => _cycleDescription; set => SetProperty(ref _cycleDescription, value); }
	public int CycleOrderIndex { get => _cycleOrderIndex; set => SetProperty(ref _cycleOrderIndex, value); }
	public bool CycleIsActive { get => _cycleIsActive; set => SetProperty(ref _cycleIsActive, value); }

	[RelayCommand]
	private void NewTheme()
	{
		SelectedReferentialTab = 0;
		SelectedTheme = null;
		ThemeName = string.Empty;
		ThemeDescription = string.Empty;
		ThemeIsActive = true;
	}

	[RelayCommand]
	private void SaveTheme()
	{
		if (string.IsNullOrWhiteSpace(ThemeName)) return;

		var theme = SelectedTheme ?? new Theme();
		theme.Name = ThemeName.Trim();
		theme.Description = string.IsNullOrWhiteSpace(ThemeDescription) ? null : ThemeDescription.Trim();
		theme.Status = ThemeIsActive ? "active" : "inactive";
		_themeRepository.Save(theme);

		if (!Themes.Contains(theme)) Themes.Add(theme);
		SelectedTheme = theme;
		FilteredThemes.Refresh();
	}

	[RelayCommand]
	private void NewCycle()
	{
		SelectedReferentialTab = 1;
		SelectedCycle = null;
		CycleName = string.Empty;
		CycleDescription = string.Empty;
		CycleOrderIndex = Cycles.Count == 0 ? 0 : Cycles.Max(cycle => cycle.OrderIndex) + 1;
		CycleIsActive = true;
	}

	[RelayCommand]
	private void SaveCycle()
	{
		if (string.IsNullOrWhiteSpace(CycleName)) return;

		var cycle = SelectedCycle ?? new Cycle();
		cycle.Name = CycleName.Trim();
		cycle.Description = string.IsNullOrWhiteSpace(CycleDescription) ? null : CycleDescription.Trim();
		cycle.OrderIndex = CycleOrderIndex;
		cycle.Status = CycleIsActive ? "active" : "inactive";
		_cycleRepository.Save(cycle);

		if (!Cycles.Contains(cycle)) Cycles.Add(cycle);
		SelectedCycle = cycle;
		FilteredCycles.Refresh();
	}

	private bool MatchesStatus(string status) => SelectedStatusFilter switch
	{
		"Actifs" => status == "active",
		"Inactifs" => status != "active",
		_ => true
	};
}