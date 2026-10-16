using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Testica.Services;

namespace Testica.ViewModels;

public partial class ExportViewModel : ObservableObject
{
	private readonly ExportService _exportService;
	private readonly ImportService _importService;
	private readonly FirebaseSyncService _firebaseSyncService;
	private string _message = "Le paquet contient un manifeste versionné et les médias associés aux questions publiées.";
	private string? _lastExportPath;
	private string _firebaseEmail = string.Empty;
	private string _firebaseSyncMessage = "Connectez-vous pour synchroniser avec testica_authoring.";
	private bool _isFirebaseSyncing;

	public ExportViewModel(ExportService exportService, ImportService importService, FirebaseSyncService firebaseSyncService)
	{
		_exportService = exportService;
		_importService = importService;
		_firebaseSyncService = firebaseSyncService;
	}

	[RelayCommand]
	private void ImportJson()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Importer les données JSON",
			Filter = "Manifeste Testica JSON|*.json",
			CheckFileExists = true
		};
		if (dialog.ShowDialog() != true) return;
		if (System.Windows.MessageBox.Show(
			"Les enregistrements ayant le même identifiant seront remplacés. Voulez-vous continuer ?",
			"Confirmer l’import JSON", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
			!= System.Windows.MessageBoxResult.Yes) return;
		try
		{
			var result = _importService.ImportPublicationJson(dialog.FileName);
			Message = $"Import terminé : {result.ImportedCount} enregistrement(s) chargé(s).";
		}
		catch (Exception exception)
		{
			Message = $"Échec de l’import : {exception.Message}";
		}
	}

	public string Message { get => _message; private set => SetProperty(ref _message, value); }
	public string? LastExportPath { get => _lastExportPath; private set => SetProperty(ref _lastExportPath, value); }
	public string FirebaseEmail { get => _firebaseEmail; set => SetProperty(ref _firebaseEmail, value); }
	public string FirebaseSyncMessage { get => _firebaseSyncMessage; private set => SetProperty(ref _firebaseSyncMessage, value); }
	public bool IsFirebaseSyncing
	{
		get => _isFirebaseSyncing;
		private set
		{
			if (SetProperty(ref _isFirebaseSyncing, value)) OnPropertyChanged(nameof(IsFirebaseSyncEnabled));
		}
	}
	public bool IsFirebaseSyncEnabled => !IsFirebaseSyncing;

	public async Task SynchronizeFirebaseAsync(string email, string password)
	{
		if (IsFirebaseSyncing) return;
		IsFirebaseSyncing = true;
		try
		{
			var progress = new Progress<string>(message => FirebaseSyncMessage = message);
			var result = await _firebaseSyncService.SynchronizeAsync(email.Trim(), password, progress);
			FirebaseSyncMessage = $"Synchronisation terminée : {result.PulledCount} importé(s), {result.PushedCount} envoyé(s), {result.ConflictCount} conflit(s).";
			if (result.ConflictCount > 0)
				FirebaseSyncMessage += " Les conflits sont laissés intacts ; modifiez un côté puis relancez.";
		}
		catch (Exception exception)
		{
			FirebaseSyncMessage = $"Échec de synchronisation : {exception.Message}";
		}
		finally
		{
			IsFirebaseSyncing = false;
		}
	}

	[RelayCommand]
	private void CreatePackage()
	{
		var dialog = new SaveFileDialog
		{
			Title = "Créer un paquet de publication",
			Filter = "Paquet ZIP|*.zip",
			FileName = $"testica-publication-{DateTime.Now:yyyyMMdd-HHmm}.zip",
			DefaultExt = ".zip",
			AddExtension = true
		};

		if (dialog.ShowDialog() != true) return;

		try
		{
			var result = _exportService.CreatePublicationPackage(dialog.FileName);
			LastExportPath = result.Path;
			Message = $"Paquet {result.PublicationId[..8]} prêt · {result.ContentCount} éléments · {result.MediaCount} médias.";
		}
		catch (Exception exception)
		{
			Message = $"Échec de l’export : {exception.Message}";
		}
	}
}
