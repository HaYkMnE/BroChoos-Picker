using System.Text;
using BroChoos.Platform.Windows.Browsers;
using FluentAssertions;
using Xunit;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// The App launches with the FIRST profile id. Before this fix that was simply the first
// info_cache key ("Default" on almost every machine), not the profile the user last used.
// Chromium records that profile in Local State as "profile.last_used" (pref
// kProfileLastUsed, "Directory of the last profile used", chrome/common/pref_names.h), so
// the reader now puts it first — only when it names a profile that info_cache lists.
public sealed class ChromiumProfileReaderTests
{
	[Fact]
	public void TryRead_LastUsedProfile_IsOrderedFirstAndTheRestKeepInfoCacheOrder()
	{
		ChromiumProfileReadResult result = Read(/*lang=json,strict*/ """
			{"profile":{"last_used":"Profile 2","info_cache":{"Default":{},"Profile 1":{},"Profile 2":{}}}}
			""");

		_ = result.ProfileIds.Should().Equal("Profile 2", "Default", "Profile 1");
		_ = result.HasUnresolvedProfiles.Should().BeFalse();
	}

	[Fact]
	public void TryRead_LastUsedAlreadyFirst_KeepsOrder()
	{
		ChromiumProfileReadResult result = Read(/*lang=json,strict*/ """
			{"profile":{"last_used":"Default","info_cache":{"Default":{},"Profile 1":{}}}}
			""");

		_ = result.ProfileIds.Should().Equal("Default", "Profile 1");
	}

	[Fact]
	public void TryRead_NoLastUsed_KeepsInfoCacheOrder()
	{
		ChromiumProfileReadResult result = Read(/*lang=json,strict*/ """
			{"profile":{"info_cache":{"Default":{},"Profile 1":{}}}}
			""");

		_ = result.ProfileIds.Should().Equal("Default", "Profile 1");
	}

	// last_used is a hint about an EXISTING profile, never a source of one: a deleted
	// profile's stale last_used, or a non-string value, changes nothing.
	[Theory]
	[InlineData(/*lang=json,strict*/ """{"profile":{"last_used":"Profile 9","info_cache":{"Default":{},"Profile 1":{}}}}""")]
	[InlineData(/*lang=json,strict*/ """{"profile":{"last_used":"profile 1","info_cache":{"Default":{},"Profile 1":{}}}}""")]
	[InlineData(/*lang=json,strict*/ """{"profile":{"last_used":7,"info_cache":{"Default":{},"Profile 1":{}}}}""")]
	[InlineData(/*lang=json,strict*/ """{"profile":{"last_used":"","info_cache":{"Default":{},"Profile 1":{}}}}""")]
	public void TryRead_LastUsedNotAListedProfile_IsIgnored(string localState)
	{
		ChromiumProfileReadResult result = Read(localState);

		_ = result.ProfileIds.Should().Equal("Default", "Profile 1");
		_ = result.HasUnresolvedProfiles.Should().BeFalse();
	}

	// The user-visible name (info_cache.<dir>.name) travels with its id, including through the
	// last_used reorder; a missing or non-string name is "" (the tile then shows no profile line).
	[Fact]
	public void TryRead_ProfileNames_StayAlignedWithIds()
	{
		ChromiumProfileReadResult result = Read(/*lang=json,strict*/ """
			{"profile":{"last_used":"Profile 2","info_cache":{"Default":{"name":"Anna"},"Profile 1":{"name":7},"Profile 2":{"name":"  Work  "}}}}
			""");

		_ = result.ProfileIds.Should().Equal("Profile 2", "Default", "Profile 1");
		_ = result.ProfileNames.Should().Equal("Work", "Anna", "");
	}

	[Fact]
	public void TryRead_VeryLongProfileName_IsCut()
	{
		string longName = new('x', 500);
		ChromiumProfileReadResult result = Read("{\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"" + longName + "\"}}}}");

		_ = result.ProfileNames.Single().Length.Should().Be(64);
	}

	// A Local State whose root, "profile" or "info_cache" is not a JSON object is a malformed
	// shape, not a crash: the reader reports "no profiles" without interrupting detection.
	[Theory]
	[InlineData(/*lang=json,strict*/ """[]""")]
	[InlineData("\"just a string\"")]
	[InlineData(/*lang=json,strict*/ """42""")]
	[InlineData(/*lang=json,strict*/ """null""")]
	[InlineData(/*lang=json,strict*/ """{"profile":[]}""")]
	[InlineData(/*lang=json,strict*/ """{"profile":"x"}""")]
	[InlineData(/*lang=json,strict*/ """{"profile":7}""")]
	[InlineData(/*lang=json,strict*/ """{"profile":{"info_cache":[]}}""")]
	public void TryRead_MalformedShape_ReturnsNoProfilesWithoutThrowing(string localState)
	{
		ChromiumProfileReadResult result = Read(localState);

		_ = result.ProfileIds.Should().BeEmpty();
		_ = result.HasUnresolvedProfiles.Should().BeFalse();
	}

	private static ChromiumProfileReadResult Read(string localState)
	{
		return new ChromiumProfileReader(new InMemoryFileSystem(Encoding.UTF8.GetBytes(localState)))
			.TryRead(@"C:\User Data\Local State");
	}

	private sealed class InMemoryFileSystem(byte[] payload) : IChromiumProfileFileSystem
	{
		public bool FileExists(string path)
		{
			return true;
		}

		public byte[] ReadAllBytes(string path)
		{
			return payload;
		}
	}
}
