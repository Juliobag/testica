namespace Testica.Models;

public sealed class Answer
{
	public string Id { get; set; } = string.Empty;
	public string QuestionId { get; set; } = string.Empty;
	public string Label { get; set; } = string.Empty;
	public string AnswerText { get; set; } = string.Empty;
	public bool IsCorrect { get; set; }
	public int OrderIndex { get; set; }
	public string Status { get; set; } = "active";
}