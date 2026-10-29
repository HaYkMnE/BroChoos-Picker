using System.Globalization;
using System.Text.Json;
using BroChoos.Core.Privacy;
using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Storage;

public sealed class ActivationHistorySerializationTests
{
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	[Fact]
	public void HistoryDocument_AllowlistKeysOnly_NoFullUrlPathQueryOrClipboardFields()
	{
		ActivationHistoryDocument document = new(
			new ActivationHistorySettingsContract(Enabled: true, SkipNextLink: false),
			[
				new ActivationHistoryRecord(
					DateTimeOffset.Parse("2026-01-01T10:00:00Z", CultureInfo.InvariantCulture),
					"example.com",
					"edge-stable",
					"Profile 1",
					"chrome.exe",
					"work-domains")
			]);

		string json = JsonSerializer.Serialize(document, Options);

		using JsonDocument parsed = JsonDocument.Parse(json);
		string[] rootKeys = [.. parsed.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];
		_ = rootKeys.Should().Equal(["entries", "generation", "schemaRevision", "schemaVersion", "settings"]);

		string[] settingsKeys = [.. parsed.RootElement.GetProperty("settings").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];
		_ = settingsKeys.Should().Equal(["enabled", "skipNextLink"]);

		string[] entryKeys = [.. parsed.RootElement.GetProperty("entries")[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)];
		_ = entryKeys.Should().Equal(["browserId", "confirmedSourceApp", "mainDomain", "matchedRuleId", "profileId", "timestampUtc"]);

		_ = json.ToLowerInvariant().Should().NotContain("url");
		_ = json.ToLowerInvariant().Should().NotContain("path");
		_ = json.ToLowerInvariant().Should().NotContain("query");
		_ = json.ToLowerInvariant().Should().NotContain("fragment");
		_ = json.ToLowerInvariant().Should().NotContain("clipboard");
	}
}
