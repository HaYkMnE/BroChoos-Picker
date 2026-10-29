using BroChoos.Core.Privacy;

namespace BroChoos.Core.Storage;

public sealed record ActivationHistorySettingsContract(
	bool Enabled = true,
	bool SkipNextLink = false);

public sealed record ActivationHistoryDocument(
	ActivationHistorySettingsContract Settings,
	IReadOnlyList<ActivationHistoryRecord> Entries) : ConfigDocument;

public sealed record ActivationHistoryMutationResult(
	bool Applied,
	MutationCommitResult Commit,
	long? NewGeneration,
	ActivationHistoryDocument? Document);

public sealed record ActivationHistoryRecordRequest(
	ExpectedDocumentGeneration ExpectedGeneration,
	ActivationHistorySettingsContract Settings,
	ActivationHistoryRecord? CandidateRecord);

public interface IActivationHistoryStore
{
	ActivationHistoryDocument LoadOrDefault();

	// The same fail-open read, plus WHY the value is what it is (see IRulesStore.LoadWithOutcome).
	ConfigReadResult<ActivationHistoryDocument> LoadWithOutcome()
	{
		return new(LoadOrDefault(), ConfigReadOutcome.Loaded);
	}

	ActivationHistoryMutationResult Record(ActivationHistoryRecordRequest request);

	ActivationHistoryMutationResult ClearConfirmed(ExpectedDocumentGeneration expectedGeneration);
}

public interface IActivationHistoryStoreFactory
{
	IActivationHistoryStore Create(string? portableDataPath);
}
