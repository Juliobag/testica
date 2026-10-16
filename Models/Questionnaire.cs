namespace Testica.Models;

public sealed class Questionnaire
{
	public string Id { get; set; } = string.Empty;
	public string Title { get; set; } = string.Empty;
	public string? Description { get; set; }
	public string? ThemeId { get; set; }
	public string? CycleId { get; set; }
	public string Status { get; set; } = "draft";
	public string QuestionOrderMode { get; set; } = "fixed";
	public int QuestionLimit { get; set; }
	public string RevisionId { get; set; } = string.Empty;
}