using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BroChoos.App.Localization;
using BroChoos.Core.Storage;

namespace BroChoos.App.Settings;

// W8.1 — the Settings shell view model.
//
// WHAT WAS WRONG. The shell rendered a frozen 12-section sidebar of <Button>s that bound
// Content only: no Command, no CommandParameter, no Click. `SelectSection` was a plain
// method, `SelectedSection` had no setter and the type did not implement
// INotifyPropertyChanged, so even if the buttons HAD been wired the content area could not
// have reacted. The window was also never instantiated from a production path. In short:
// the information architecture existed as DATA and as STATIC TEXT, not as behaviour.
//
// WHAT THIS NOW IS. The VM is observable (INotifyPropertyChanged), the selection is a real
// observable property changed by a real ICommand (<see cref="SelectSectionCommand"/>), the
// content area reacts through bindings, the Browsers/Profiles tabs and the truthful
// indicator chips the plan names are surfaced, and the one safe preference the shell
// exposes (Control depth) auto-saves through the W3 generation/CAS mutation protocol via
// <see cref="ISettingsStore"/>.
//
// WHAT IT DELIBERATELY DOES NOT DO. No section is ever hidden (D-v3.5-269 is
// PROPOSED/NEEDS USABILITY VALIDATION, not authority): the command only changes which
// section is shown, never the section list. No general Apply button exists. No value that
// the shell cannot derive from runtime state is invented: the indicator chips read the
// project's real, already-published defaults.
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
	// The 12 sections with their texts resolved through `text`. Built twice on purpose: once here for
	// the static OrderedSections (stable ids and order, process culture), and once PER INSTANCE in
	// the instance's own culture, which is what the nav buttons, the section description, search and
	// SelectSection use.
	private static SettingsSection[] BuildSections(Func<string, string> text)
	{
		return
	[
		new(
			SectionId: "home",
			Order: 1,
			GroupLabel: text("Settings.Group.Experience"),
			DisplayLabel: text("Settings.Section.Home"),
			Description: text("Settings.Section.Home.Description"),
			IsHome: true),
		new(
			SectionId: "control-depth",
			Order: 2,
			GroupLabel: text("Settings.Group.Experience"),
			DisplayLabel: text("Settings.Section.ControlDepth"),
			Description: text("Settings.Section.ControlDepth.Description"),
			IsHome: false),
		new(
			SectionId: "browsers-profiles",
			Order: 3,
			GroupLabel: text("Settings.Group.Content"),
			DisplayLabel: text("Settings.Section.BrowsersProfiles"),
			Description: text("Settings.Section.BrowsersProfiles.Description"),
			IsHome: false),
		new(
			SectionId: "what-brochoos-opens",
			Order: 4,
			GroupLabel: text("Settings.Group.Content"),
			DisplayLabel: text("Settings.Section.WhatBroChoosOpens"),
			Description: text("Settings.Section.WhatBroChoosOpens.Description"),
			IsHome: false),
		new(
			SectionId: "rules-suggestions",
			Order: 5,
			GroupLabel: text("Settings.Group.Content"),
			DisplayLabel: text("Settings.Section.RulesSuggestions"),
			Description: text("Settings.Section.RulesSuggestions.Description"),
			IsHome: false),
		new(
			SectionId: "privacy-network",
			Order: 6,
			GroupLabel: text("Settings.Group.Protection"),
			DisplayLabel: text("Settings.Section.PrivacyNetwork"),
			Description: text("Settings.Section.PrivacyNetwork.Description"),
			IsHome: false),
		new(
			SectionId: "appearance",
			Order: 7,
			GroupLabel: text("Settings.Group.Interface"),
			DisplayLabel: text("Settings.Section.Appearance"),
			Description: text("Settings.Section.Appearance.Description"),
			IsHome: false),
		new(
			SectionId: "hotkeys",
			Order: 8,
			GroupLabel: text("Settings.Group.Interface"),
			DisplayLabel: text("Settings.Section.Hotkeys"),
			Description: text("Settings.Section.Hotkeys.Description"),
			IsHome: false),
		new(
			SectionId: "language",
			Order: 9,
			GroupLabel: text("Settings.Group.Interface"),
			DisplayLabel: text("Settings.Section.Language"),
			Description: text("Settings.Section.Language.Description"),
			IsHome: false),
		new(
			SectionId: "updates",
			Order: 10,
			GroupLabel: text("Settings.Group.System"),
			DisplayLabel: text("Settings.Section.Updates"),
			Description: text("Settings.Section.Updates.Description"),
			IsHome: false),
		new(
			SectionId: "maintenance",
			Order: 11,
			GroupLabel: text("Settings.Group.System"),
			DisplayLabel: text("Settings.Section.Maintenance"),
			Description: text("Settings.Section.Maintenance.Description"),
			IsHome: false),
		new(
			SectionId: "advanced",
			Order: 12,
			GroupLabel: text("Settings.Group.System"),
			DisplayLabel: text("Settings.Section.Advanced"),
			Description: text("Settings.Section.Advanced.Description"),
			IsHome: false),
	];
	}

	private static IReadOnlyList<SettingsSection> FrozenSections { get; } = Array.AsReadOnly(BuildSections(LocalizationService.GetString));

	public static IReadOnlyList<SettingsSection> OrderedSections => FrozenSections;

	// W8.1 — the plan's named stable section identifiers, exposed for tests and the
	// command parameter vocabulary so neither spells them independently.
	public static class SectionIds
	{
		public const string Home = "home";
		public const string ControlDepth = "control-depth";
		public const string BrowsersProfiles = "browsers-profiles";
		public const string WhatBroChoosOpens = "what-brochoos-opens";
		public const string RulesSuggestions = "rules-suggestions";
		public const string PrivacyNetwork = "privacy-network";
		public const string Appearance = "appearance";
		public const string Hotkeys = "hotkeys";
		public const string Language = "language";
		public const string Updates = "updates";
		public const string Maintenance = "maintenance";
		public const string Advanced = "advanced";
	}

	// This instance's sections, texts in this instance's culture (see BuildSections).
	private readonly System.Collections.ObjectModel.ReadOnlyCollection<SettingsSection> _sections;
	private readonly ISettingsStore? _settingsStore;

	// The expected-generation token this view model last OBSERVED. It is a token, not a
	// number, because an absent settings.json must be expected as Absent rather than
	// Match(0) (see ISettingsStore.ObserveExpectedGeneration). Re-observed after every
	// conflict so a concurrent writer cannot wedge this window's saves forever.
	private ExpectedDocumentGeneration _observedExpected = ExpectedDocumentGeneration.Absent;

	// The language of THIS view model. Fixed at construction; every text resolved later (a save
	// status, a confirmation, a search result) uses it too, so the whole window stays in one language.
	private readonly CultureInfo _culture;

	// `culture` is the language to show; null keeps the existing behaviour (the process-wide
	// LocalizationService.CurrentCulture, read once here). Asking for a language this way means a
	// caller never has to switch the process-wide culture around the construction.
	public SettingsViewModel(
		ISettingsStore? settingsStore = null,
		string? portableDataPath = null,
		string? buildHashTextOverride = null,
		string? publisherStatusTextOverride = null,
		string? sha256TextOverride = null,
		CultureInfo? culture = null)
	{
		_culture = culture ?? LocalizationService.CurrentCulture;
		_sections = Array.AsReadOnly(BuildSections(T));
		SelectedSection = _sections[0];
		_settingsStore = settingsStore;
		PortableDataPath = portableDataPath;

		ControlDepthChoiceTexts = [.. ControlDepthChoices.Select(ControlDepthChoiceText)];
		PickerLayoutChoiceTexts =
		[
			T("Settings.Appearance.PickerLayout.Horizontal"),
			T("Settings.Appearance.PickerLayout.Vertical"),
		];

		BuildHashText = buildHashTextOverride ?? T("Settings.Support.BuildHashUnavailable");
		PublisherStatusText = publisherStatusTextOverride ?? T("Settings.Support.PublisherUnsigned");
		Sha256Text = sha256TextOverride ?? T("Settings.Support.Sha256Unavailable");

		IsBuildVerificationAvailable = !string.Equals(
			BuildHashText,
			T("Settings.Support.BuildHashUnavailable"),
			StringComparison.Ordinal)
			&& !string.IsNullOrWhiteSpace(BuildHashText)
			&& !string.Equals(
				PublisherStatusText,
				T("Settings.Support.PublisherUnsigned"),
				StringComparison.Ordinal)
			&& !string.IsNullOrWhiteSpace(PublisherStatusText)
			&& !string.Equals(
				Sha256Text,
				T("Settings.Support.Sha256Unavailable"),
				StringComparison.Ordinal)
			&& !string.IsNullOrWhiteSpace(Sha256Text);

		SelectSectionCommand = new SelectSectionCommandImpl(this);
		SearchCommand = new SearchCommandImpl(this);
		RequestCheckWindowsSettingsCommand = new DelegateCommand(_ => _ = RequestConsequentialAction(ConsequentialActionIds.CheckWindowsSettings), _ => true);
		RequestExportSettingsCommand = new DelegateCommand(_ => _ = RequestConsequentialAction(ConsequentialActionIds.ExportSettings), _ => true);
		ConfirmConsequentialActionCommand = new DelegateCommand(_ => ConsequentialActionResultText = ConfirmConsequentialAction().Reason, _ => IsConsequentialConfirmationPending);
		CancelConsequentialActionCommand = new DelegateCommand(_ => CancelConsequentialAction(), _ => IsConsequentialConfirmationPending);

		LoadPersistedSettings();

		DashboardStatusText = T(
			_settingsStore is null ? "Settings.Dashboard.Status" : "Settings.Dashboard.Status.Connected");
		DashboardDepthText = ControlDepthDisplayText;
	}

	// The one live handle a test or the window needs. Kept as a property so the XAML can
	// bind a tab and a test can assert the same value.
	public string? PortableDataPath { get; }

	public event PropertyChangedEventHandler? PropertyChanged;

	// The observable selection. Raises PropertyChanged for every derived display property,
	// so the content area follows the sidebar with no general Apply step (spec 3.12).
	public SettingsSection SelectedSection
	{
		get;
		private set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsHomeSelected));
			OnPropertyChanged(nameof(IsControlDepthSelected));
			OnPropertyChanged(nameof(IsBrowsersProfilesSelected));
			OnPropertyChanged(nameof(IsPrivacyNetworkSelected));
			OnPropertyChanged(nameof(IsUpdatesSelected));
			OnPropertyChanged(nameof(IsAppearanceSelected));
			OnPropertyChanged(nameof(SelectedSectionDescription));
		}
	}

	public string SelectedSectionDescription => SelectedSection.Description;

	public bool IsHomeSelected => string.Equals(SelectedSection.SectionId, SectionIds.Home, StringComparison.Ordinal);

	public bool IsBrowsersProfilesSelected => string.Equals(SelectedSection.SectionId, SectionIds.BrowsersProfiles, StringComparison.Ordinal);

	public bool IsControlDepthSelected => string.Equals(SelectedSection.SectionId, SectionIds.ControlDepth, StringComparison.Ordinal);

	public bool IsPrivacyNetworkSelected => string.Equals(SelectedSection.SectionId, SectionIds.PrivacyNetwork, StringComparison.Ordinal);

	public bool IsUpdatesSelected => string.Equals(SelectedSection.SectionId, SectionIds.Updates, StringComparison.Ordinal);

	public bool IsAppearanceSelected => string.Equals(SelectedSection.SectionId, SectionIds.Appearance, StringComparison.Ordinal);

	// --- Commands -------------------------------------------------------------------

	// The nav command. Every sidebar button binds Command= this + CommandParameter=<id>,
	// so the section shown follows the button pressed without any code-behind.
	public ICommand SelectSectionCommand { get; }

	public ICommand RequestCheckWindowsSettingsCommand { get; }

	public ICommand RequestExportSettingsCommand { get; }

	public ICommand ConfirmConsequentialActionCommand { get; }

	public ICommand CancelConsequentialActionCommand { get; }

	public ICommand SearchCommand { get; }

	// Returns false only for an unknown id; never hides a section (D-v3.5-269 not authority).
	public bool SelectSection(string sectionId)
	{
		SettingsSection? candidate = _sections.FirstOrDefault(section => string.Equals(section.SectionId, sectionId, StringComparison.Ordinal));
		if (candidate is null)
		{
			return false;
		}

		SelectedSection = candidate;
		return true;
	}

	// --- Search (plan W8.1: "searchable, localized information architecture") --------
	//
	// The window renders a search box and a "Search examples" line, so the box has to DO
	// something: a search affordance that silently does nothing is a fake status the plan's
	// negative clause forbids. Search selects the matching section (it NEVER hides sections
	// - the frozen list is always complete) and reports the outcome truthfully through
	// SearchStatusText, including "nothing matched".
	public string? SearchQuery
	{
		get;
		set
		{
			if (string.Equals(field, value, StringComparison.Ordinal))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}

	// The truthful result of the last search. Empty until a search has run; a concrete
	// "nothing matched" sentence when it did not, so no match is never silently ignored.
	public string SearchStatusText
	{
		get;
		private set
		{
			if (string.Equals(field, value, StringComparison.Ordinal))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	} = string.Empty;

	// Runs the search: resolves the query to a section (alias, id, or label) and selects
	// it. Returns the resolved section id, or null when nothing matched. An empty query
	// clears the status and matches nothing (it does not jump anywhere).
	public string? Search(string? query)
	{
		SearchQuery = query;
		string? resolved = SettingsSearchAliases.Resolve(query, _sections);

		if (resolved is null)
		{
			SearchStatusText = string.IsNullOrWhiteSpace(query)
				? string.Empty
				: T("Settings.Search.NoMatch");
			return null;
		}

		_ = SelectSection(resolved);
		SearchStatusText = string.Empty;
		return resolved;
	}

	private sealed class SearchCommandImpl(SettingsViewModel owner) : ICommand
	{
		private readonly SettingsViewModel _owner = owner;

		public event EventHandler? CanExecuteChanged
		{
			add { }
			remove { }
		}

		public bool CanExecute(object? parameter)
		{
			return true;
		}

		public void Execute(object? parameter)
		{
			// The TextBox passes its text as the command parameter in the common case; if a
			// host invokes the command without one, the bound SearchQuery is used instead.
			string? query = parameter as string ?? _owner.SearchQuery;
			_ = _owner.Search(query);
		}
	}

	// --- Consequential-action confirmation (spec 3.12, D-v3.5-254) ------------------

	// Named consequential actions require an explicit, action-specific confirmation. This
	// is NOT a generic "Are you sure?" — each id carries its own consequence sentence, and
	// the confirmation is per-action. A generic confirmation is explicitly disallowed by
	// the plan's negative clause.
	public static bool RequiresConfirmation(string actionId)
	{
		return string.Equals(actionId, ConsequentialActionIds.CheckWindowsSettings, StringComparison.Ordinal)
			|| string.Equals(actionId, ConsequentialActionIds.ExportSettings, StringComparison.Ordinal);
	}

	public bool IsConsequentialConfirmationPending
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			((DelegateCommand)ConfirmConsequentialActionCommand).RaiseCanExecuteChanged();
			((DelegateCommand)CancelConsequentialActionCommand).RaiseCanExecuteChanged();
		}
	}

	public string? ConfirmationMessage
	{
		get;
		private set
		{
			if (string.Equals(field, value, StringComparison.Ordinal))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}

	public string? PendingConsequentialActionId { get; private set; }

	// What happened to the last CONFIRMED action, in words. Empty until one was confirmed, and
	// cleared again when a new request starts. This build composes no launcher or export writer
	// for the two actions, so the text states that the action is unavailable; without it the
	// confirmation would just disappear and the user could not tell the action did nothing.
	public string ConsequentialActionResultText
	{
		get;
		private set
		{
			if (string.Equals(field, value, StringComparison.Ordinal))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	} = string.Empty;

	public string ConfirmButtonText => T("Settings.Confirm.ConfirmButton");

	public string CancelButtonText => T("Settings.Confirm.CancelButton");

	// Requests confirmation for a named consequential action. Rejects an unknown or
	// non-consequential id so a caller cannot fake the gate.
	public bool RequestConsequentialAction(string actionId)
	{
		if (!RequiresConfirmation(actionId))
		{
			return false;
		}

		ConsequentialActionResultText = string.Empty;
		PendingConsequentialActionId = actionId;
		ConfirmationMessage = string.Equals(actionId, ConsequentialActionIds.CheckWindowsSettings, StringComparison.Ordinal)
			? T("Settings.Confirm.CheckWindowsSettings")
			: T("Settings.Confirm.ExportSettings");
		IsConsequentialConfirmationPending = true;
		OnPropertyChanged(nameof(PendingConsequentialActionId));
		return true;
	}

	// The confirmed disposition for the pending action. This build composes NO external
	// launcher or export writer for these two actions, so the HONEST outcome is that the
	// action is unavailable — never a fake "done". The gate clears and the truthful reason
	// is reported. (The plan's negative clause forbids faking status.)
	public ConsequentialActionOutcome ConfirmConsequentialAction()
	{
		if (!IsConsequentialConfirmationPending || PendingConsequentialActionId is null)
		{
			return ConsequentialActionOutcome.NothingPending;
		}

		string actionId = PendingConsequentialActionId;
		ClearConfirmation();
		return new ConsequentialActionOutcome(actionId, Performed: false, Reason: T("Settings.Consequential.Unavailable"));
	}

	public void CancelConsequentialAction()
	{
		ClearConfirmation();
	}

	private void ClearConfirmation()
	{
		PendingConsequentialActionId = null;
		ConfirmationMessage = null;
		IsConsequentialConfirmationPending = false;
		OnPropertyChanged(nameof(PendingConsequentialActionId));
	}

	public static class ConsequentialActionIds
	{
		public const string CheckWindowsSettings = "check-windows-settings";
		public const string ExportSettings = "export-settings";
	}

	// --- The one safe preference: Control depth -------------------------------------

	public SettingsControlDepth ControlDepth
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(ControlDepthDisplayText));
			DashboardDepthText = ControlDepthDisplayText;
			OnPropertyChanged(nameof(DashboardDepthText));
			// The editor binds SelectedControlDepth, which is a view of this value; a change
			// that arrives from the dashboard or from disk must move the editor too.
			OnPropertyChanged(nameof(SelectedControlDepth));
			OnPropertyChanged(nameof(SelectedControlDepthIndex));
		}
	}

	public string ControlDepthDisplayText => ControlDepth switch
	{
		SettingsControlDepth.Guided => T("Settings.Depth.Guided"),
		SettingsControlDepth.Expert => T("Settings.Depth.Expert"),
		SettingsControlDepth.Standard => T("Settings.Depth.Standard"),
		_ => T("Settings.Depth.Standard"),
	};

	// The three depths the editor offers, in the order they are shown. A plain, frozen set -
	// this is the whole vocabulary of the preference, so it is not data-driven.
	public static IReadOnlyList<SettingsControlDepth> ControlDepthChoices { get; } =
	[
		SettingsControlDepth.Standard,
		SettingsControlDepth.Guided,
		SettingsControlDepth.Expert,
	];

	// The editor's two-way selection, bound to a standard control (ComboBox) in the
	// Control-depth section. Assigning it SAVES through the real mutation path, so the
	// control is wired to the same SetControlDepth the dashboard and tests use - there is
	// no second, unsaved code path.
	//
	// The setter never blocks on a commit result: the save is immediate and its outcome is
	// surfaced honestly through ControlDepthSaveStatusText (a refused save says so rather
	// than pretending the preference took effect).
	public SettingsControlDepth SelectedControlDepth
	{
		get => ControlDepth;
		set
		{
			if (ControlDepth == value)
			{
				return;
			}

			MutationCommitResult? result = SetControlDepth(value);
			ControlDepthSaveStatusText = DescribeSaveOutcome(result);

			// Raised AFTER the save attempt, whatever its outcome. When the change was committed,
			// ControlDepth already notified and this repeats it harmlessly. When it was refused,
			// ControlDepth did not change, so nothing else would tell the ComboBox: it would keep
			// showing the choice the user made while the stored depth is the old one. These
			// notifications make it read the actual value back.
			OnPropertyChanged();
			OnPropertyChanged(nameof(SelectedControlDepthIndex));
		}
	}

	// Truthful, localized statement of what happened to the last editor save. Empty before
	// any save, and a plain "not saved" sentence when the CAS refused it.
	public string ControlDepthSaveStatusText
	{
		get;
		private set
		{
			if (string.Equals(field, value, StringComparison.Ordinal))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	} = string.Empty;

	private string DescribeSaveOutcome(MutationCommitResult? result)
	{
		// A null result means NO store was composed (a bare window): the value is shown but not
		// durable. That is a DIFFERENT state from a refusal, so it gets its own sentence -
		// telling the user "another change landed first" here would be false (F-C).
		if (result is null)
		{
			return T("Settings.Depth.NotDurable");
		}

		// Anything other than Committed is a refusal. The wording states the observable fact
		// (the change was not saved, and the stored file was left as it was) without claiming a
		// specific cause it cannot see: a stale generation and a corrupt/unreadable destination
		// both land here, and both mean the same thing to the user.
		return result.Status == MutationCommitStatus.Committed
			? T("Settings.Depth.Saved")
			: T("Settings.Depth.NotSaved");
	}

	// The editor's own label and the display text for one choice, so the ComboBox is bound
	// to view-model values rather than hard-coded literals (the IA test forbids literals).
	public string ControlDepthEditorLabelText => T("Settings.Depth.EditorLabel");

	public string ControlDepthChoiceText(SettingsControlDepth depth)
	{
		return depth switch
		{
			SettingsControlDepth.Standard => T("Settings.Depth.Standard"),
			SettingsControlDepth.Guided => T("Settings.Depth.Guided"),
			SettingsControlDepth.Expert => T("Settings.Depth.Expert"),
			_ => T("Settings.Depth.Standard"),
		};
	}

	// The localized choice captions, index-aligned with ControlDepthChoices. A standard
	// ComboBox binds ItemsSource to this and SelectedIndex to SelectedControlDepthIndex, so
	// the control needs no item template or converter and the captions stay localized.
	public IReadOnlyList<string> ControlDepthChoiceTexts { get; }

	// The ComboBox selection, index into ControlDepthChoices. Assigning it saves through the
	// real mutation path exactly as SelectedControlDepth does.
	public int SelectedControlDepthIndex
	{
		get => IndexOfControlDepth(ControlDepth);
		set
		{
			if (value < 0 || value >= ControlDepthChoices.Count)
			{
				return;
			}

			SelectedControlDepth = ControlDepthChoices[value];
		}
	}

	private static int IndexOfControlDepth(SettingsControlDepth depth)
	{
		for (int index = 0; index < ControlDepthChoices.Count; index++)
		{
			if (ControlDepthChoices[index] == depth)
			{
				return index;
			}
		}

		return 0;
	}

	// A safe ordinary preference saves immediately through the shared mutation protocol
	// (spec 3.12). Returns the mutation result so a caller (and a test) can see the CAS
	// outcome rather than assuming success.
	public MutationCommitResult? SetControlDepth(SettingsControlDepth depth)
	{
		if (_settingsStore is null)
		{
			// No store composed: the window still shows the new value, but nothing durable
			// happened. Reported honestly by a null result rather than a fabricated commit.
			ControlDepth = depth;
			return null;
		}

		MutationCommitResult result = CommitSettings(_settingsStore, current => current with { ControlDepth = depth });
		if (result.Status == MutationCommitStatus.Committed)
		{
			ControlDepth = depth;
		}

		return result;
	}

	// The shared save for every safe preference (Control depth, Picker layout): one load, one CAS
	// save, one adoption of the resulting generation, so the two preferences cannot drift apart
	// in how they handle a concurrent writer.
	private MutationCommitResult CommitSettings(ISettingsStore store, Func<SettingsDocument, SettingsDocument> change)
	{
		SettingsDocument current = store.LoadOrDefault();

		// Compare against the generation this view model last OBSERVED, not the one just
		// re-read here. The re-read is the document body to mutate; the observed token is
		// what this writer actually believes the file was at, so a concurrent writer that
		// landed since the load is refused by the coordinator's CAS rather than silently
		// overwritten (spec 2.5 shared mutation protocol).
		MutationCommitResult result = store.Save(change(current), _observedExpected);

		if (result.Status == MutationCommitStatus.Committed)
		{
			// Adopt the committed generation so the next save compares against the new one.
			_observedExpected = ExpectedDocumentGeneration.Match(
				result.CommittedGeneration ?? (current.Generation + 1));
		}
		else if (result.Status == MutationCommitStatus.Conflict)
		{
			// RECOVER, WITHOUT DEFEATING CAS. A conflict means our expectation was stale, so
			// we adopt the generation the COORDINATOR itself observed at conflict time (the
			// real on-disk value), not the value we guessed and not "whatever we wanted it to
			// be". A null observed generation means the file is ABSENT (another writer removed
			// it), which is the correct Absent token - never Match(0).
			//
			// This is what stops a single concurrent write from wedging every later save from
			// this window forever: the NEXT save CASes against reality and can succeed. It does
			// not weaken CAS - a writer that is still stale simply conflicts again, and the
			// token is refreshed from observation each time.
			_observedExpected = result.ObservedGeneration is long observedGeneration
				? ExpectedDocumentGeneration.Match(observedGeneration)
				: ExpectedDocumentGeneration.Absent;
		}

		return result;
	}

	private void LoadPersistedSettings()
	{
		if (_settingsStore is null)
		{
			return;
		}

		SettingsDocument document = _settingsStore.LoadOrDefault();
		_observedExpected = _settingsStore.ObserveExpectedGeneration();
		ControlDepth = document.ControlDepth;
		PickerLayout = document.PickerLayout == SettingsPickerLayout.Vertical ? SettingsPickerLayout.Vertical : SettingsPickerLayout.Horizontal;
	}

	// --- Appearance: Picker layout (Horizontal default / Vertical) -------------------

	public SettingsPickerLayout PickerLayout
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(SelectedPickerLayoutIndex));
		}
	} = SettingsPickerLayout.Horizontal;

	public static IReadOnlyList<SettingsPickerLayout> PickerLayoutChoices { get; } =
	[
		SettingsPickerLayout.Horizontal,
		SettingsPickerLayout.Vertical,
	];

	public string PickerLayoutEditorLabelText => T("Settings.Appearance.PickerLayout.Label");

	public IReadOnlyList<string> PickerLayoutChoiceTexts { get; }

	// The ComboBox selection. Assigning it saves at once through the shared mutation path, and
	// the status line states truthfully whether the change was saved.
	public int SelectedPickerLayoutIndex
	{
		get => PickerLayout == SettingsPickerLayout.Vertical ? 1 : 0;
		set
		{
			if (value < 0 || value >= PickerLayoutChoices.Count || PickerLayoutChoices[value] == PickerLayout)
			{
				return;
			}

			MutationCommitResult? result = SetPickerLayout(PickerLayoutChoices[value]);
			PickerLayoutSaveStatusText = DescribeSaveOutcome(result);
			OnPropertyChanged();
		}
	}

	public string PickerLayoutSaveStatusText
	{
		get;
		private set
		{
			if (string.Equals(field, value, StringComparison.Ordinal))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	} = string.Empty;

	public MutationCommitResult? SetPickerLayout(SettingsPickerLayout layout)
	{
		if (_settingsStore is null)
		{
			PickerLayout = layout;
			return null;
		}

		MutationCommitResult result = CommitSettings(_settingsStore, current => current with { PickerLayout = layout });
		if (result.Status == MutationCommitStatus.Committed)
		{
			PickerLayout = layout;
		}

		return result;
	}

	// --- Localized display (unchanged shell text) -----------------------------------

	// Every text below is resolved in this view model's own culture (see T), so a caller that asked
	// for a language gets it without the process-wide CurrentCulture being touched.
	public string WindowTitle => T("Settings.Window.Title");

	public string SearchWatermarkText => T("Settings.Search.Watermark");

	public string SearchExamplesText => T("Settings.Search.Examples");

	public string GroupExperienceText => T("Settings.Group.Experience");

	public string GroupContentText => T("Settings.Group.Content");

	public string GroupProtectionText => T("Settings.Group.Protection");

	public string GroupInterfaceText => T("Settings.Group.Interface");

	public string GroupSystemText => T("Settings.Group.System");

	public string HomeSubtitleText => T("Settings.Home.Subtitle");

	public string DashboardStatusLabelText => T("Settings.Dashboard.Status.Label");

	// OVERVIEW = ONLY WHAT THIS WINDOW CAN ACTUALLY KNOW. The old tiles were fixed sample
	// values ("Browsers: 1 detected", "BroChoos is working") whatever the machine looked like.
	//   - Status: whether a settings store is composed, i.e. whether changes here are saved.
	//   - Control depth: the loaded/saved preference (follows the editor).
	//   - Browsers / Profiles: "unknown" with the reason, until the catalog is passed in. The
	//     Settings lifetime has no browser-catalog reader composed (Program builds only the
	//     settings store for --settings), so any number here would be invented.
	//   - Health: "not checked", because this window runs no checks.
	//   - Coverage, Privacy, Updates: fixed facts of THIS build, unchanged.
	// Both texts are resolved when the state is set (construction / depth change), like every
	// other shell string here, so a later culture switch does not re-translate one tile only.
	public string DashboardStatusText { get; private set; } = string.Empty;

	public string DashboardDepthLabelText => T("Settings.Dashboard.Depth.Label");

	public string DashboardDepthText { get; private set; } = string.Empty;

	public string DashboardBrowserCountLabelText => T("Settings.Dashboard.BrowserCount.Label");

	public string DashboardBrowserCountText => T("Settings.Dashboard.BrowserCount");

	public string DashboardProfileCountLabelText => T("Settings.Dashboard.ProfileCount.Label");

	public string DashboardProfileCountText => T("Settings.Dashboard.ProfileCount");

	public string SearchAccessibleNameText => T("Settings.Search.AccessibleName");

	public string DashboardCoverageLabelText => T("Settings.Dashboard.Coverage.Label");

	public string DashboardCoverageText => T("Settings.Dashboard.Coverage");

	public string DashboardPrivacyLabelText => T("Settings.Dashboard.Privacy.Label");

	public string DashboardPrivacyText => T("Settings.Dashboard.Privacy");

	public string DashboardHealthLabelText => T("Settings.Dashboard.Health.Label");

	public string DashboardHealthText => T("Settings.Dashboard.Health");

	public string DashboardUpdateLabelText => T("Settings.Dashboard.Update.Label");

	public string DashboardUpdateText => T("Settings.Dashboard.Update");

	public string CheckWindowsSettingsButtonText => T("Settings.Actions.CheckWindowsSettings");

	public string OpenBrowsersButtonText => T("Settings.Actions.OpenBrowsers");

	public string ChangeDepthButtonText => T("Settings.Actions.ChangeDepth");

	public string ExportSettingsButtonText => T("Settings.Actions.ExportSettings");

	public string PrivacyHeadingText => T("Settings.Privacy.Heading");

	public string PrivacyTelemetryText => T("Settings.Privacy.Telemetry");

	public string PrivacyTrackingCleanupText => T("Settings.Privacy.TrackingCleanup");

	public string PrivacyShortLinkText => T("Settings.Privacy.ShortLink");

	public string PrivacyClipboardText => T("Settings.Privacy.Clipboard");

	public string PrivacyHistoryAllowlistText => T("Settings.Privacy.HistoryAllowlist");

	public string PrivacyHistoryControlsText => T("Settings.Privacy.HistoryControls");

	public string UpdatesHeadingText => T("Settings.Updates.Heading");

	public string UpdatesConsentText => T("Settings.Updates.Consent");

	public string VerifyBuildButtonText => T("Settings.Updates.VerifyAction");

	public string VerifyBuildUnavailableText => T("Settings.Updates.VerifyUnavailable");

	public string SupportRuntimeVersionLabelText => T("Settings.Support.RuntimeVersionLabel");

	public string SupportReleaseChannelLabelText => T("Settings.Support.ReleaseChannelLabel");

	public string SupportBuildHashLabelText => T("Settings.Support.BuildHashLabel");

	public string SupportPublisherLabelText => T("Settings.Support.PublisherLabel");

	public string SupportSha256LabelText => T("Settings.Support.Sha256Label");

	public string RuntimeVersionText { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

	public string ReleaseChannelText => T("Settings.Support.ReleaseChannelValue");

	public string BuildHashText { get; }

	public string PublisherStatusText { get; }

	public string Sha256Text { get; }

	public bool IsBuildVerificationAvailable { get; }

	// --- Truthful indicator chips (spec 3.12, D-v3.5-238/240/241/242/244/256) --------
	//
	// Each chip states a REAL, already-published default of THIS build and nothing more.
	// No chip claims a capability the build does not have, and no chip is decorative:
	// these are the same facts the app publishes elsewhere (history default ON with
	// retained-field allowlist, tracking cleanup and short-link expansion default OFF,
	// no telemetry, clipboard read local/transient, update check consent-required).
	public string IndicatorTelemetryText => T("Settings.Indicator.Telemetry");

	public string IndicatorTrackingCleanupText => T("Settings.Indicator.TrackingCleanup");

	public string IndicatorShortLinkText => T("Settings.Indicator.ShortLink");

	public string IndicatorClipboardText => T("Settings.Indicator.Clipboard");

	public string IndicatorHistoryText => T("Settings.Indicator.History");

	public string IndicatorUpdatesText => T("Settings.Indicator.Updates");

	// --- Browsers / Profiles tabs (spec 3.12, plan W8.1 minimal behavior) ------------
	public string BrowsersTabHeaderText => T("Settings.BrowsersProfiles.BrowsersTab");

	public string ProfilesTabHeaderText => T("Settings.BrowsersProfiles.ProfilesTab");

	public string BrowsersTabEmptyText => T("Settings.BrowsersProfiles.BrowsersEmpty");

	public string ProfilesTabEmptyText => T("Settings.BrowsersProfiles.ProfilesEmpty");

	public string DepthTabHeaderText => T("Settings.Depth.TabHeader");

	// The sidebar button labels. Each button binds Content to one of these and
	// CommandParameter to the matching stable section id (see SectionIds), so the label
	// and the command cannot drift: both derive from the same frozen section list.
	public string NavHomeText => FormatNav(_sections[0]);
	public string NavControlDepthText => FormatNav(_sections[1]);
	public string NavBrowsersProfilesText => FormatNav(_sections[2]);
	public string NavWhatBroChoosOpensText => FormatNav(_sections[3]);
	public string NavRulesSuggestionsText => FormatNav(_sections[4]);
	public string NavPrivacyNetworkText => FormatNav(_sections[5]);
	public string NavAppearanceText => FormatNav(_sections[6]);
	public string NavHotkeysText => FormatNav(_sections[7]);
	public string NavLanguageText => FormatNav(_sections[8]);
	public string NavUpdatesText => FormatNav(_sections[9]);
	public string NavMaintenanceText => FormatNav(_sections[10]);
	public string NavAdvancedText => FormatNav(_sections[11]);

	private static string FormatNav(SettingsSection section)
	{
		return $"{section.Order} {section.DisplayLabel}";
	}

	// The localized text for `key` in THIS view model's culture.
	private string T(string key)
	{
		return LocalizationService.GetStringFor(key, _culture);
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	// The nav command. Holds the VM weakly-by-construction (it is a nested private type
	// created by the VM) and delegates to SelectSection, so the XAML binds ONE command and
	// a per-button CommandParameter — no code-behind, no per-section handler.
	private sealed class SelectSectionCommandImpl(SettingsViewModel owner) : ICommand
	{
		private readonly SettingsViewModel _owner = owner;

		public event EventHandler? CanExecuteChanged
		{
			add { }
			remove { }
		}

		public bool CanExecute(object? parameter)
		{
			return parameter is string id && OrderedSections.Any(section => string.Equals(section.SectionId, id, StringComparison.Ordinal));
		}

		public void Execute(object? parameter)
		{
			if (parameter is string id)
			{
				_ = _owner.SelectSection(id);
			}
		}
	}

	// A minimal ICommand for the confirmation pair (no external toolkit dependency).
	private sealed class DelegateCommand(Action<object?> execute, Func<object?, bool> canExecute) : ICommand
	{
		private readonly Action<object?> _execute = execute;
		private readonly Func<object?, bool> _canExecute = canExecute;

		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter)
		{
			return _canExecute(parameter);
		}

		public void Execute(object? parameter)
		{
			_execute(parameter);
		}

		public void RaiseCanExecuteChanged()
		{
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
		}
	}
}

internal sealed record SettingsSection(
	string SectionId,
	int Order,
	string GroupLabel,
	string DisplayLabel,
	string Description,
	bool IsHome);

// The outcome of a confirmed consequential action. Performed is false in this build for
// the named actions because no external launcher/export writer is composed — the reason
// states that plainly instead of pretending the action ran.
internal sealed record ConsequentialActionOutcome(string ActionId, bool Performed, string Reason)
{
	public static ConsequentialActionOutcome NothingPending { get; } = new(string.Empty, false, string.Empty);
}
