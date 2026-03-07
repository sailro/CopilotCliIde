using System.Text.Json;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Performs structural superset comparison of JSON documents against golden snapshots.
/// Golden snapshots use TYPE-PLACEHOLDER format: "string", "number", "boolean".
/// Actual JSON must contain all golden properties (superset); extra properties are allowed.
/// </summary>
public static class JsonSchemaComparer
{
	private const string CommentProperty = "_comment";

	/// <summary>
	/// Compares an actual JSON element against a golden snapshot.
	/// Returns a list of mismatch descriptions with JSON paths.
	/// An empty list means the actual JSON is a valid superset of the golden schema.
	/// </summary>
	public static List<string> Compare(JsonElement actual, JsonElement golden, string path = "$")
	{
		var mismatches = new List<string>();
		CompareElements(actual, golden, path, mismatches);
		return mismatches;
	}

	/// <summary>
	/// Convenience overload that parses JSON strings before comparing.
	/// </summary>
	public static List<string> Compare(string actualJson, string goldenJson)
	{
		using var actualDoc = JsonDocument.Parse(actualJson);
		using var goldenDoc = JsonDocument.Parse(goldenJson);
		return Compare(actualDoc.RootElement, goldenDoc.RootElement);
	}

	private static void CompareElements(JsonElement actual, JsonElement golden, string path, List<string> mismatches)
	{
		switch (golden.ValueKind)
		{
			case JsonValueKind.Object:
				CompareObject(actual, golden, path, mismatches);
				break;

			case JsonValueKind.Array:
				CompareArray(actual, golden, path, mismatches);
				break;

			case JsonValueKind.String:
				CompareTypePlaceholder(actual, golden.GetString()!, path, mismatches);
				break;

			default:
				// Golden should only contain objects, arrays, and type-placeholder strings.
				// If we encounter something else, it's a malformed golden file.
				mismatches.Add($"{path}: unexpected golden value kind {golden.ValueKind}");
				break;
		}
	}

	private static void CompareObject(JsonElement actual, JsonElement golden, string path, List<string> mismatches)
	{
		if (actual.ValueKind != JsonValueKind.Object)
		{
			mismatches.Add($"{path}: expected object, got {FormatValueKind(actual.ValueKind)}");
			return;
		}

		foreach (var goldenProp in golden.EnumerateObject())
		{
			// Skip _comment properties in golden snapshots
			if (goldenProp.Name == CommentProperty)
				continue;

			if (!actual.TryGetProperty(goldenProp.Name, out var actualProp))
			{
				mismatches.Add($"{path}.{goldenProp.Name}: missing property");
				continue;
			}

			CompareElements(actualProp, goldenProp.Value, $"{path}.{goldenProp.Name}", mismatches);
		}
	}

	private static void CompareArray(JsonElement actual, JsonElement golden, string path, List<string> mismatches)
	{
		if (actual.ValueKind != JsonValueKind.Array)
		{
			mismatches.Add($"{path}: expected array, got {FormatValueKind(actual.ValueKind)}");
			return;
		}

		// If the golden array has at least one element, verify the actual array
		// has at least one element that matches the expected item shape.
		var goldenItems = golden.EnumerateArray().ToList();
		if (goldenItems.Count == 0)
			return;

		if (actual.GetArrayLength() == 0)
		{
			mismatches.Add($"{path}: expected array with items, got empty array");
			return;
		}

		// Compare first golden item against first actual item
		CompareElements(actual[0], goldenItems[0], $"{path}[0]", mismatches);
	}

	private static void CompareTypePlaceholder(JsonElement actual, string placeholder, string path, List<string> mismatches)
	{
		// Null is acceptable for any type placeholder (nullable fields)
		if (actual.ValueKind == JsonValueKind.Null)
			return;

		switch (placeholder)
		{
			case "string":
				if (actual.ValueKind != JsonValueKind.String)
					mismatches.Add($"{path}: expected string, got {FormatValueKind(actual.ValueKind)}");
				break;

			case "number":
				if (actual.ValueKind != JsonValueKind.Number)
					mismatches.Add($"{path}: expected number, got {FormatValueKind(actual.ValueKind)}");
				break;

			case "boolean":
				if (actual.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
					mismatches.Add($"{path}: expected boolean, got {FormatValueKind(actual.ValueKind)}");
				break;

			default:
				// Non-placeholder string in golden — treat as expecting a string type
				if (actual.ValueKind != JsonValueKind.String)
					mismatches.Add($"{path}: expected string, got {FormatValueKind(actual.ValueKind)}");
				break;
		}
	}

	private static string FormatValueKind(JsonValueKind kind) => kind switch
	{
		JsonValueKind.Object => "object",
		JsonValueKind.Array => "array",
		JsonValueKind.String => "string",
		JsonValueKind.Number => "number",
		JsonValueKind.True => "boolean",
		JsonValueKind.False => "boolean",
		JsonValueKind.Null => "null",
		JsonValueKind.Undefined => "undefined",
		_ => kind.ToString(),
	};
}
