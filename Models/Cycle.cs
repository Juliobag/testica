namespace Testica.Models;

public sealed class Cycle
{
	public string Id { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public int OrderIndex { get; set; }
	public string Status { get; set; } = "active";
	public DateTime? UpdatedAt { get; set; }
	public string RevisionId { get; set; } = string.Empty;
}