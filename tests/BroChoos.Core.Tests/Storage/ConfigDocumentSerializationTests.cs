using System.Text.Json;
using BroChoos.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Storage;

// Envelope, round-trip and surface-area checks for the 0A-5 storage DTOs.
// Serialization uses reflection (no source-gen) with camelCase field names,
// matching the locked schema (schemaVersion, schemaRevision, browsers, ...).
public sealed class ConfigDocumentSerializationTests
{
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	private static readonly DateTimeOffset VerifiedUtc = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

	// D-064: every root document carries schemaVersion = 1 and schemaRevision = 0.
	[Fact]
	public void Envelope_AppDocument_CarriesExpectedSchema()
	{
		AssertEnvelope(SerializeRoot(new AppDocument()));
	}

	[Fact]
	public void Envelope_SettingsDocument_CarriesExpectedSchema()
	{
		AssertEnvelope(SerializeRoot(new SettingsDocument()));
	}

	[Fact]
	public void Envelope_BrowsersDocument_CarriesExpectedSchema()
	{
		AssertEnvelope(SerializeRoot(new BrowsersDocument([CreateBrowser()])));
	}

	[Fact]
	public void RoundTrip_AppDocument_PreservesEnvelope()
	{
		AppDocument original = new();

		string json = JsonSerializer.Serialize(original, Options);
		AppDocument roundTripped = JsonSerializer.Deserialize<AppDocument>(json, Options)!;

		AssertHasCamelCaseKeys(json, "schemaVersion", "schemaRevision");
		AssertHasCamelCaseKeys(json, "generation");
		AssertDoesNotContainKey(json, "SchemaVersion");
		AssertDoesNotContainKey(json, "SchemaRevision");
		AssertDoesNotContainKey(json, "Generation");
		_ = roundTripped.SchemaVersion.Should().Be(1);
		_ = roundTripped.SchemaRevision.Should().Be(0);
		_ = roundTripped.Generation.Should().Be(0);
		_ = roundTripped.Should().BeEquivalentTo(original);
	}

	[Fact]
	public void RoundTrip_SettingsDocument_PreservesEnvelope()
	{
		SettingsDocument original = new();

		string json = JsonSerializer.Serialize(original, Options);
		SettingsDocument roundTripped = JsonSerializer.Deserialize<SettingsDocument>(json, Options)!;

		AssertHasCamelCaseKeys(json, "schemaVersion", "schemaRevision", "generation");
		_ = roundTripped.Generation.Should().Be(0);
		_ = roundTripped.Should().BeEquivalentTo(original);
	}

	// W8.1: SettingsDocument is no longer envelope-only. W8.1's minimal behavior requires a
	// safe preference to AUTO-SAVE through the shared mutation protocol, so exactly one
	// additive member - the stable, non-localized ControlDepth id - is now persisted. The
	// value is stored as the enum NAME, never localized UI text (D-070), and carries an
	// explicit default so an envelope-only 0A-5 file still binds instead of quarantining.
	[Fact]
	public void RoundTrip_SettingsDocument_PreservesPersistedControlDepth()
	{
		SettingsDocument original = new() { ControlDepth = SettingsControlDepth.Expert };

		string json = JsonSerializer.Serialize(original, Options);
		SettingsDocument roundTripped = JsonSerializer.Deserialize<SettingsDocument>(json, Options)!;

		AssertHasCamelCaseKeys(json, "controlDepth");
		_ = roundTripped.ControlDepth.Should().Be(SettingsControlDepth.Expert);
		_ = roundTripped.Should().BeEquivalentTo(original);
	}

	// The additive-member rule: a settings.json written before ControlDepth existed (the
	// envelope-only 0A-5 shape) must still bind to the documented DEFAULT, not fail the
	// body parse, because BodyUnparseable licenses quarantine-then-overwrite of intact data.
	[Fact]
	public void SettingsDocument_EnvelopeOnlyJson_BindsToExplicitDefaultControlDepth()
	{
		string envelopeOnly = "{\"schemaVersion\":1,\"schemaRevision\":0,\"generation\":3}";

		SettingsDocument bound = JsonSerializer.Deserialize<SettingsDocument>(envelopeOnly, Options)!;

		_ = bound.ControlDepth.Should().Be(SettingsControlDepth.Standard);
		_ = bound.Generation.Should().Be(3);
	}

	[Fact]
	public void RoundTrip_BrowsersDocument_PreservesEnvelopeAndSingleBrowser()
	{
		BrowserDefinition browser = CreateBrowser();
		BrowsersDocument original = new([browser]);

		string json = JsonSerializer.Serialize(original, Options);
		BrowsersDocument roundTripped = JsonSerializer.Deserialize<BrowsersDocument>(json, Options)!;

		AssertHasCamelCaseKeys(json, "schemaVersion", "schemaRevision", "generation", "browsers");
		_ = roundTripped.SchemaVersion.Should().Be(1);
		_ = roundTripped.SchemaRevision.Should().Be(0);
		_ = roundTripped.Generation.Should().Be(0);
		_ = roundTripped.Browsers.Should().ContainSingle();
		_ = roundTripped.Browsers[0].Should().BeEquivalentTo(browser);
	}

	[Fact]
	public void BrowserDefinition_SerializesOnlyItsDeclaredFields_CamelCase()
	{
		string json = JsonSerializer.Serialize(CreateBrowser(), Options);

		AssertHasCamelCaseKeys(
			json,
			"browserId",
			"displayName",
			"vendor",
			"channel",
			"exePath",
			"detectionSource",
			"lastVerifiedAt");
	}

	// A browser that has not been verified yet persists a null lastVerifiedAt,
	// which must round-trip back to null (not a default/today instant).
	[Fact]
	public void RoundTrip_BrowserDefinition_NullLastVerifiedAt_StaysNull()
	{
		BrowserDefinition original = new(
			"chrome-stable",
			"Google Chrome",
			"Google",
			"stable",
			@"C:\Program Files\Google\Chrome\Application\chrome.exe",
			"registry",
			null);

		string json = JsonSerializer.Serialize(original, Options);
		BrowserDefinition roundTripped = JsonSerializer.Deserialize<BrowserDefinition>(json, Options)!;

		_ = roundTripped.LastVerifiedAt.Should().BeNull();
		_ = roundTripped.Should().BeEquivalentTo(original);
	}

	[Fact]
	public void BrowserDefinition_LastVerifiedAt_IsIso8601Utc()
	{
		string json = JsonSerializer.Serialize(CreateBrowser(), Options);

		using JsonDocument doc = JsonDocument.Parse(json);
		string raw = doc.RootElement.GetProperty("lastVerifiedAt").GetString()!;
		DateTimeOffset parsed = DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

		_ = parsed.Should().Be(VerifiedUtc);
		_ = parsed.Offset.Should().Be(TimeSpan.Zero);
	}

	[Fact]
	public void Deserialize_MalformedJson_ThrowsJsonExceptionNotCrash()
	{
		string malformedJson = "{" + " not valid";
		Action act = () => _ = JsonSerializer.Deserialize<AppDocument>(malformedJson, Options);

		_ = act.Should().Throw<JsonException>();
	}

	[Fact]
	public void Deserialize_TruncatedJson_ThrowsJsonExceptionNotCrash()
	{
		string truncatedJson = "{" + "\"schemaVersion\":1,\"schemaRevision\":0,\"browsers\":[";
		Action act = () => _ = JsonSerializer.Deserialize<BrowsersDocument>(truncatedJson, Options);

		_ = act.Should().Throw<JsonException>();
	}

	// D-064/D-070/D-064-additive: each config document exposes EXACTLY its declared schema
	// surface, nothing more. AppDocument carries ONLY the envelope; SettingsDocument carries
	// the envelope plus the single W8.1-additive `controlDepth` preference (a stable,
	// non-localized id). A member added anywhere else still fails, so this stays a real
	// surface lock rather than a rubber stamp.
	[Fact]
	public void Documents_ExposeOnlyTheDeclaredSchemaSurface()
	{
		AssertOnlyKeys(JsonSerializer.Serialize(new AppDocument(), Options), "schemaVersion", "schemaRevision", "generation");
		// pickerLayout: the second additive preference (Settings > Appearance), a stable enum name.
		AssertOnlyKeys(JsonSerializer.Serialize(new SettingsDocument(), Options), "schemaVersion", "schemaRevision", "generation", "controlDepth", "pickerLayout");
		AssertOnlyKeys(
			JsonSerializer.Serialize(new ActivationHistoryDocument(new ActivationHistorySettingsContract(), []), Options),
			"schemaVersion",
			"schemaRevision",
			"generation",
			"settings",
			"entries");
		AssertOnlyKeys(
			JsonSerializer.Serialize(new ActivationHistorySettingsContract(), Options),
			"enabled",
			"skipNextLink");
		AssertOnlyKeys(
			JsonSerializer.Serialize(new Core.Privacy.ActivationHistoryRecord(DateTimeOffset.UnixEpoch, "example.com", "edge-stable", null, null, null), Options),
			"timestampUtc",
			"mainDomain",
			"browserId",
			"profileId",
			"confirmedSourceApp",
			"matchedRuleId");
		AssertOnlyKeys(
			JsonSerializer.Serialize(new BrowsersDocument([CreateBrowser()]), Options),
			"schemaVersion",
			"schemaRevision",
			"generation",
			"browsers");
		AssertOnlyKeys(
			JsonSerializer.Serialize(CreateBrowser(), Options),
			"browserId",
			"displayName",
			"vendor",
			"channel",
			"exePath",
			"detectionSource",
			"lastVerifiedAt",
			"profileIds",
			"hasUnresolvedProfiles",
			"isCustomBrowser",
			"isPortableBrowser",
			// Additive, defaulted: visible profile names for the picker tiles.
			"profileNames");
	}

	// An older settings.json without pickerLayout, and an older browsers.json entry without
	// profileNames, must still bind (to the defaults) rather than be treated as corrupt.
	[Fact]
	public void OlderFiles_WithoutTheNewMembers_BindToTheirDefaults()
	{
		// The read path's own options (ConfigFileObserver): enums as names, unknown members skipped.
		JsonSerializerOptions readOptions = new(Options)
		{
			Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
		};

		SettingsDocument settings = JsonSerializer.Deserialize<SettingsDocument>(
			/*lang=json,strict*/ """{"schemaVersion":1,"schemaRevision":0,"generation":3,"controlDepth":"Expert"}""", readOptions)!;
		_ = settings.PickerLayout.Should().Be(SettingsPickerLayout.Horizontal);
		_ = settings.ControlDepth.Should().Be(SettingsControlDepth.Expert);

		SettingsDocument vertical = JsonSerializer.Deserialize<SettingsDocument>(
			JsonSerializer.Serialize(new SettingsDocument { PickerLayout = SettingsPickerLayout.Vertical }, readOptions), readOptions)!;
		_ = vertical.PickerLayout.Should().Be(SettingsPickerLayout.Vertical);

		BrowserDefinition browser = JsonSerializer.Deserialize<BrowserDefinition>(
			/*lang=json,strict*/ """{"browserId":"edge-stable","displayName":"Edge","vendor":"microsoft","channel":"stable","exePath":"C:\\e.exe","detectionSource":"known-path","profileIds":["Default"]}""", readOptions)!;
		_ = browser.ProfileNames.Should().BeNull();
		_ = browser.ProfileIds.Should().Equal("Default");
	}

	private static BrowserDefinition CreateBrowser()
	{
		return new(
			"edge-stable",
			"Microsoft Edge",
			"Microsoft",
			"stable",
			@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
			"known-path",
			VerifiedUtc);
	}

	private static JsonElement SerializeRoot(ConfigDocument document)
	{
		string json = JsonSerializer.Serialize(document, document.GetType(), Options);

		using JsonDocument parsed = JsonDocument.Parse(json);
		return parsed.RootElement.Clone();
	}

	private static void AssertEnvelope(JsonElement root)
	{
		_ = root.GetProperty("schemaVersion").GetInt32().Should().Be(SchemaVersioning.Version);
		_ = root.GetProperty("schemaRevision").GetInt32().Should().Be(SchemaVersioning.Revision);
		_ = root.GetProperty("generation").GetInt64().Should().Be(0);
	}

	private static void AssertHasCamelCaseKeys(string json, params string[] expectedKeys)
	{
		using JsonDocument doc = JsonDocument.Parse(json);

		foreach (string key in expectedKeys)
		{
			_ = doc.RootElement.TryGetProperty(key, out _)
				.Should().BeTrue($"expected camelCase property '{key}' in: {json}");
		}
	}

	private static void AssertDoesNotContainKey(string json, string key)
	{
		using JsonDocument doc = JsonDocument.Parse(json);

		_ = doc.RootElement.TryGetProperty(key, out _)
			.Should().BeFalse($"did not expect property '{key}' in: {json}");
	}

	private static void AssertOnlyKeys(string json, params string[] expectedKeys)
	{
		using JsonDocument doc = JsonDocument.Parse(json);

		string[] actualKeys = [.. doc.RootElement.EnumerateObject()
			.Select(property => property.Name)
			.OrderBy(name => name, StringComparer.Ordinal)];

		_ = actualKeys.Should().Equal(expectedKeys.OrderBy(name => name, StringComparer.Ordinal));
	}
}
