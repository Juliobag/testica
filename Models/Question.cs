namespace Testica.Models;

public sealed class Question
{
	public string Id { get; set; } = string.Empty;
	public string ThemeId { get; set; } = string.Empty;
	public string CycleId { get; set; } = string.Empty;
	public string Statement { get; set; } = string.Empty;
	public string? Context { get; set; }
	public string? ImagePath { get; set; }
	public int Difficulty { get; set; }
	public string Status { get; set; } = "draft";
	public DateTime? UpdatedAt { get; set; }
	public string RevisionId { get; set; } = string.Empty;
}