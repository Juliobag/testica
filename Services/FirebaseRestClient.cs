using System.Net;
using System.Net.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Testica.Services;

internal sealed class FirebaseRestClient : IDisposable
{
	private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
	private string? _idToken;

	public async Task SignInAsync(string email, string password, CancellationToken cancellationToken)
	{
		var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={FirebaseClientOptions.ApiKey}";
		var body = new JObject
		{
			["email"] = email,
			["password"] = password,
			["returnSecureToken"] = true
		};
		using var response = await _httpClient.PostAsync(url,
			new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), cancellationToken);
		var content = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			var message = (string?)JObject.Parse(content)["error"]?["message"] ?? "Firebase Authentication a refusé la connexion.";
			throw new InvalidOperationException(TranslateAuthError(message));
		}
		_idToken = (string?)JObject.Parse(content)["idToken"]
			?? throw new InvalidDataException("Firebase Authentication n’a pas retourné de jeton de session.");
	}

	public async Task<IReadOnlyDictionary<string, RemoteSyncDocument>> ListAuthoringDocumentsAsync(CancellationToken cancellationToken)
	{
		var documents = new Dictionary<string, RemoteSyncDocument>(StringComparer.Ordinal);
		string? pageToken = null;
		do
		{
			var url = $"https://firestore.googleapis.com/v1/projects/{FirebaseClientOptions.ProjectId}/databases/(default)/documents/{FirebaseClientOptions.AuthoringCollection}?pageSize=1000";
			if (!string.IsNullOrEmpty(pageToken)) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
			using var response = await SendFirestoreAsync(HttpMethod.Get, url, null, cancellationToken);
			var content = await response.Content.ReadAsStringAsync(cancellationToken);
			if (!response.IsSuccessStatusCode) throw CreateFirestoreException(response.StatusCode, content);
			var result = JObject.Parse(content);
			foreach (var item in result["documents"]?.OfType<JObject>() ?? [])
			{
				var name = (string?)item["name"] ?? string.Empty;
				var documentId = name[(name.LastIndexOf('/') + 1)..];
				var fields = item["fields"] as JObject ?? new JObject();
				var entityType = (string?)fields["entityType"]?["stringValue"] ?? string.Empty;
				var recordId = (string?)fields["recordId"]?["stringValue"] ?? string.Empty;
				var payloadJson = (string?)fields["payloadJson"]?["stringValue"];
				var deleted = (bool?)fields["deleted"]?["booleanValue"] ?? false;
				var updateTime = (string?)item["updateTime"];
				if (string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(recordId)) continue;
				if (!deleted && string.IsNullOrWhiteSpace(payloadJson))
					throw new InvalidDataException($"Le document d’édition {documentId} n’a pas de payloadJson valide.");
				documents[SyncKey(entityType, recordId)] = new RemoteSyncDocument(
					documentId, entityType, recordId, deleted ? null : payloadJson,
					deleted ? null : FirebaseSyncService.HashPayload(payloadJson ?? "{}"), deleted, updateTime);
			}
			pageToken = (string?)result["nextPageToken"];
		} while (!string.IsNullOrEmpty(pageToken));
		return documents;
	}

	public async Task WriteAuthoringDocumentAsync(string entityType, string recordId, string? payloadJson,
		bool deleted, RemoteSyncDocument? expectedRemote, CancellationToken cancellationToken)
	{
		var documentId = expectedRemote?.DocumentId ?? DocumentId(entityType, recordId);
		var url = $"https://firestore.googleapis.com/v1/projects/{FirebaseClientOptions.ProjectId}/databases/(default)/documents/{FirebaseClientOptions.AuthoringCollection}/{documentId}";
		if (expectedRemote is null)
		{
			url += "?currentDocument.exists=false";
		}
		else if (!string.IsNullOrWhiteSpace(expectedRemote.UpdateTime))
		{
			url += "?currentDocument.updateTime=" + Uri.EscapeDataString(expectedRemote.UpdateTime);
		}

		var fields = new JObject
		{
			["entityType"] = new JObject { ["stringValue"] = entityType },
			["recordId"] = new JObject { ["stringValue"] = recordId },
			["deleted"] = new JObject { ["booleanValue"] = deleted }
		};
		if (payloadJson is not null) fields["payloadJson"] = new JObject { ["stringValue"] = payloadJson };
		var body = new JObject { ["fields"] = fields };
		using var response = await SendFirestoreAsync(HttpMethod.Patch, url,
			new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			var content = await response.Content.ReadAsStringAsync(cancellationToken);
			var status = (string?)JObject.Parse(content)["error"]?["status"];
			if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
				|| status == "FAILED_PRECONDITION")
				throw new SyncPreconditionException($"Le document {entityType}/{recordId} a été modifié ailleurs pendant la synchronisation. Relancez la synchronisation pour détecter le conflit.");
			throw CreateFirestoreException(response.StatusCode, content);
		}
	}

	private async Task<HttpResponseMessage> SendFirestoreAsync(HttpMethod method, string url, HttpContent? content,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(_idToken)) throw new InvalidOperationException("Connectez-vous à Firebase avant la synchronisation.");
		using var request = new HttpRequestMessage(method, url) { Content = content };
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _idToken);
		return await _httpClient.SendAsync(request, cancellationToken);
	}

	private static string DocumentId(string entityType, string recordId) =>
		Convert.ToBase64String(Encoding.UTF8.GetBytes(SyncKey(entityType, recordId)))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static string SyncKey(string entityType, string recordId) => entityType + ":" + recordId;

	private static Exception CreateFirestoreException(HttpStatusCode statusCode, string content)
	{
		var message = (string?)JObject.Parse(content)["error"]?["message"] ?? content;
		return new InvalidOperationException(statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden
			? $"Firestore a refusé l’accès. Vérifiez que votre UID est autorisé dans les règles de la collection d’édition. ({message})"
			: $"Erreur Firestore ({(int)statusCode}) : {message}");
	}

	private static string TranslateAuthError(string code) => code switch
	{
		"EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS" =>
			$"Firebase Auth ({code}) : vérifiez Authentication > Users dans {FirebaseClientOptions.ProjectId}. Cette fenêtre exige un compte E-mail/Mot de passe et son mot de passe Firebase ; elle ne prend pas en charge Connexion Google.",
		"OPERATION_NOT_ALLOWED" => "Activez la connexion par e-mail/mot de passe dans Firebase Authentication.",
		"USER_DISABLED" => "Ce compte Firebase est désactivé.",
		_ => $"Firebase Authentication : {code}"
	};

	public void Dispose() => _httpClient.Dispose();
}

internal sealed record RemoteSyncDocument(string DocumentId, string EntityType, string RecordId,
	string? PayloadJson, string? ContentHash, bool IsDeleted, string? UpdateTime = null);

internal sealed class SyncPreconditionException(string message) : InvalidOperationException(message);
