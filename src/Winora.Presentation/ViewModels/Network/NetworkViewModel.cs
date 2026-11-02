// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;

namespace Winora.Desktop;

public sealed record NetworkProviderItem(string Id, string Name, string Description, bool SupportsDoh, string Source, bool IsCustom)
{
    public override string ToString() => Name;
}

public sealed record NetworkDohItem(DohMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One adapter that is up: its facts are always shown, the check box chooses it for a DNS change.</summary>
public sealed class NetworkAdapterRow : ObservableObject
{
    private readonly Action changed;
    private bool selected, included;

    public NetworkAdapterRow(NetworkAdapter adapter, string kindText, string virtualText, string addresses, string dns, string effective, string encryption, string includeLabel, string includeHelp, Action changed, Action<NetworkAdapterRow> include)
    {
        Adapter = adapter; KindText = kindText; VirtualText = virtualText; AddressesText = addresses; DnsText = dns; EffectiveText = effective; EncryptionText = encryption; this.changed = changed;
        IncludeLabel = includeLabel; IncludeHelp = includeHelp;
        selected = !adapter.IsVirtual && adapter.Dns is not null;
        IncludeCommand = new(() => include(this), () => adapter.Dns is not null);
    }

    public NetworkAdapter Adapter { get; }
    public Guid Id => Adapter.Id;
    /// <summary>The Windows name of the connection, pinned left to right when it is Latin (set by the page).</summary>
    public string Name { get; init; } = "";
    public string KindText { get; }
    public string VirtualText { get; }
    public bool IsVirtual => Adapter.IsVirtual;
    public string AddressesText { get; }
    public string DnsText { get; }
    public string EffectiveText { get; }
    public string EncryptionText { get; }
    public string IncludeLabel { get; private set; }
    public string IncludeHelp { get; }
    public RelayCommand IncludeCommand { get; }
    public bool CanSelect => Adapter.Dns is not null && (!IsVirtual || included);
    public bool Included => included;
    public bool ShowInclude => IsVirtual && Adapter.Dns is not null;
    public string AutomationName => $"{Name}, {KindText}" + (IsVirtual ? $", {VirtualText}" : "");

    public bool IsSelected
    {
        get => selected && CanSelect;
        set { if (value != selected && SetProperty(ref selected, value)) changed(); }
    }

    public void SetIncluded(bool value, string label)
    {
        included = value; IncludeLabel = label;
        selected = value;
        OnPropertyChanged(nameof(Included)); OnPropertyChanged(nameof(CanSelect)); OnPropertyChanged(nameof(IsSelected)); OnPropertyChanged(nameof(IncludeLabel));
        changed();
    }
}

public sealed class NetworkHistoryRow(DnsChangeRecord record, string description, string stateText, string when, bool canRevert, RelayCommand<NetworkHistoryRow> revert)
{
    public DnsChangeRecord Record { get; } = record;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public RelayCommand<NetworkHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

/// <summary>
/// The Network page: the adapters that are up with their addresses, DNS servers and DNS-over-HTTPS state, and a DNS provider switcher. A change shows the servers before and after for each
/// adapter, asks once, is recorded before it is made, is read back afterwards, is followed by one name lookup, and can be undone (decision DEC-IW-035).
/// </summary>
public sealed class NetworkViewModel : PageViewModel, INoticePage
{
    private int selectedTab;
    /// <summary>The tab of the page, kept while the app runs.</summary>
    public int SelectedTab { get => selectedTab; set => SetProperty(ref selectedTab, value); }

    private const int HistoryLimit = 30;
    private readonly NetworkService service;
    private readonly IReviewPrompt prompt;
    private Task work = Task.CompletedTask;
    private NetworkProviderItem? provider;
    private NetworkDohItem? doh;
    private string custom = "";
    private string? notice;
    private bool noticeIsError;
    private bool busy;
    private bool loaded;
    private string? loadError;
    private NetworkSnapshot? last;
    private Guid? offerUndo;
    private bool dohStaged, resetPickers;
    private string? baseProviderId;
    private DohMode? baseDoh;
    private int quiet;

    public NetworkViewModel(LocalizationService text, SimulationModel model, NetworkService service, IReviewPrompt prompt) : base("Network", text, model)
    {
        HistoryList = new(text, HistoryArea.Network, () => OpenHistory);
        this.service = service; this.prompt = prompt;
        RefreshCommand = new(() => work = ReloadAsync(), () => !Busy);
        ApplyCommand = new(() => work = ApplyAsync(), () => CanApply);
        DiscardCommand = new(Discard, () => PendingCount > 0 && !Busy);
        UndoLastCommand = new(() => work = UndoOfferedAsync(), () => offerUndo is not null && !Busy && IsElevated);
        RevertHistory = new(row => { if (row is not null && !Busy) work = RevertAsync(row.Record.Change.Id); }, _ => !Busy);
        BuildLists();
    }

    public ObservableCollection<NetworkAdapterRow> Adapters { get; } = [];
    public ObservableCollection<NetworkProviderItem> Providers { get; } = [];
    public ObservableCollection<NetworkDohItem> DohChoices { get; } = [];

    private NetworkChoiceRow? providerCard, dohCard;
    /// <summary>The DNS provider as a card.</summary>
    public NetworkChoiceRow ProviderCard => providerCard ??= new(Text, "network.provider", () => Text["NetProviderLabel"], () => Text["NetProviderHelp"],
        () => Providers.Select(p => new RowChoice(p.Id, p.Name)).ToList(), () => SelectedProvider is { } p ? new RowChoice(p.Id, p.Name) : null,
        c => SelectedProvider = Providers.FirstOrDefault(p => p.Id == c.Id), () => !Busy, () => ProviderDescription);
    /// <summary>Encrypted DNS as a card; it is available only for providers that support it.</summary>
    public NetworkChoiceRow DohCard => dohCard ??= new(Text, "network.doh", () => Text["NetDohLabel"], () => Text["NetDohHelp"],
        () => DohChoices.Select(d => new RowChoice(d.Mode.ToString(), d.Label)).ToList(), () => SelectedDoh is { } d ? new RowChoice(d.Mode.ToString(), d.Label) : null,
        c => SelectedDoh = DohChoices.FirstOrDefault(d => d.Mode.ToString() == c.Id), () => DohEnabled && !Busy, () => SelectedDoh?.Label ?? "");
    /// <summary>A pick changes what the cards show, not their lists: the choice lists stay the same objects so an open drop-down keeps its items.</summary>
    private void RefreshCards() { providerCard?.Refresh(); dohCard?.Refresh(); }
    private void RebuildCards() { providerCard?.Rebuild(); dohCard?.Rebuild(); }
    public ObservableCollection<NetworkHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand UndoLastCommand { get; }
    public RelayCommand<NetworkHistoryRow> RevertHistory { get; }

    public override bool IsBusy => Busy;
    private bool Busy { get => busy; set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsBusy)); Notify(); } } }

    public bool IsElevated => service.IsElevated;
    public bool ShowNeedsAdministrator => !service.IsElevated;
    public string NeedsAdministratorTitle => service.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["AdminNoticeTitle"];
    public string NeedsAdministratorText => service.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["AdminNoticeMessage"];
    public bool ShowAdminHowTo => !service.UnsafeLocation;
    public bool HasAdapters => Adapters.Count > 0;
    public bool ShowEmpty => loaded && !HasAdapters && !HasLoadError && !Busy;
    public bool ShowLoading => Busy && !loaded;
    public bool ShowContent => !ShowLoading && !HasLoadError;
    public string? LoadError => loadError;
    public bool HasLoadError => loadError is not null;
    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => noticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public string AdapterPickHelp => Text["NetPickHelp"];
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool ShowUndoOffer => offerUndo is not null;
    public bool HasHistory => History.Count > 0;

    public NetworkProviderItem? SelectedProvider
    {
        get => provider;
        set
        {
            if (!SetProperty(ref provider, value)) return;
            if (value is not null && !value.SupportsDoh) SelectedDoh = DohChoices.First(d => d.Mode == DohMode.Off);
            else if (value is { SupportsDoh: true } && doh is { Mode: DohMode.Off } && value.Id != DnsProviderCatalog.AutomaticId) SelectedDoh = DohChoices.First(d => d.Mode == DohMode.OnWithFallback);
            OnPropertyChanged(nameof(ShowCustom)); OnPropertyChanged(nameof(DohEnabled)); OnPropertyChanged(nameof(ProviderDescription)); OnPropertyChanged(nameof(ProviderSource)); OnPropertyChanged(nameof(HasProviderSource)); Notify();
        }
    }

    public NetworkDohItem? SelectedDoh { get => doh; set { if (quiet == 0) dohStaged = true; if (SetProperty(ref doh, value) || quiet == 0) Notify(); } }
    public string CustomServers { get => custom; set { if (SetProperty(ref custom, value ?? "")) Notify(); } }
    public bool ShowCustom => provider?.IsCustom == true;
    public bool DohEnabled => provider is { SupportsDoh: true };
    public string ProviderDescription => provider?.Description ?? "";
    public string ProviderSource => provider is { Source.Length: > 0 } p ? Text.Format("NetSourceLine", Text.Ltr(p.Source)) : "";
    public bool HasProviderSource => provider is { Source.Length: > 0 };

    public bool HasSelection => Adapters.Any(a => a.IsSelected);
    public bool CanApply => IsElevated && !Busy && PendingCount > 0 && HasSelection && provider is not null && doh is not null && (!ShowCustom || CustomServers.Trim().Length > 0);

    /// <summary>Staged choices: the DNS provider (with its custom servers) and encrypted DNS, once the user has picked them. Which adapters take them is shown in the list, not counted.</summary>
    public override int PendingCount => (ProviderStaged ? 1 : 0) + (DohStaged ? 1 : 0);
    // Staged means different from what the ticked adapters have now. Choosing the value they already have changes nothing, so it stages nothing.
    private bool ProviderStaged => provider is not null && (provider.Id != baseProviderId || (provider.IsCustom && custom.Trim().Length > 0));
    private bool DohStaged => DohEnabled && doh is not null && (baseDoh is null ? dohStaged : doh.Mode != baseDoh);
    public string ActionBarSummary => PendingCount == 1 ? Text["ABPendingOne"] : Text.Format("ABPendingFormat", PendingCount);
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key, NetworkChanges(),
        ActionBarSummary, Text["ABReview"], Text["NetApplyHelp"], Text["Discard"], Text["NetDiscardHelp"],
        () => ApplyCommand.CanExecute(null), () => ApplyCommand.Execute(null), () => DiscardCommand.CanExecute(null), () => DiscardCommand.Execute(null));
    private IReadOnlyList<StagedChange> NetworkChanges()
    {
        var list = new List<StagedChange>();
        void Remove() { if (!Busy) Discard(); }
        if (ProviderStaged && provider is not null)
            list.Add(new("network.provider", Text["NetProviderLabel"], Providers.FirstOrDefault(p => p.Id == baseProviderId)?.Name ?? "",
                ShowCustom && CustomServers.Trim().Length > 0 ? Text.Format("NetPendingCustom", Text.Ltr(CustomServers.Trim())) : provider.Name, null, Remove));
        if (DohStaged && doh is not null)
            list.Add(new("network.doh", Text["NetDohLabel"], DohChoices.FirstOrDefault(d => d.Mode == baseDoh)?.Label ?? "", doh.Label, null, Remove));
        return list;
    }
    public IReadOnlyList<string> PendingLines
    {
        get
        {
            var lines = new List<string>();
            if (ProviderStaged && provider is not null)
                lines.Add(Text.Format("ABLineFormat", Text["NetProviderLabel"], ShowCustom && CustomServers.Trim().Length > 0 ? Text.Format("NetPendingCustom", Text.Ltr(CustomServers.Trim())) : provider.Name));
            if (DohStaged && doh is not null) lines.Add(Text.Format("ABLineFormat", Text["NetDohLabel"], doh.Label));
            if (lines.Count > 0) lines.Add(Text.Format("NetPendingAdapters", Text.LtrList(Adapters.Where(a => a.IsSelected).Select(a => a.Name))));
            return lines;
        }
    }

    /// <summary>Puts the pickers back to what they show after a load and forgets what was staged. The adapters keep their ticks: they only say where a change goes.</summary>
    private void Discard()
    {
        quiet++;
        try
        {
            ShowBaseline();
        }
        finally { quiet--; }
        ClearStaged();
        Notice = null;
    }

    private void ClearStaged() { dohStaged = false; Notify(); }

    /// <summary>The pickers show what the ticked adapters have now: the provider whose servers they use, or Automatic. When they differ or use other servers, no provider is shown as chosen.</summary>
    private void ShowBaseline()
    {
        SelectedProvider = baseProviderId is null ? null : Providers.FirstOrDefault(p => p.Id == baseProviderId);
        SelectedDoh = DohChoices.First(d => d.Mode == (baseDoh ?? DohMode.OnWithFallback));
        CustomServers = "";
    }

    private void DetectBaseline(NetworkSnapshot snapshot)
    {
        baseProviderId = null; baseDoh = null;
        var rows = snapshot.Adapters.Where(a => !a.IsVirtual && a.Dns is not null).Select(a => a.Dns!).ToList();
        if (rows.Count == 0) return;
        if (rows.All(d => d.V4.IsAutomatic && d.V6.IsAutomatic)) { baseProviderId = DnsProviderCatalog.AutomaticId; baseDoh = DohMode.Off; return; }
        var servers = rows[0].V4.Servers.Select(x => x.Address).ToList();
        if (servers.Count == 0 || rows.Any(d => !d.V4.Servers.Select(x => x.Address).SequenceEqual(servers))) return;
        var match = service.Catalog.Providers.FirstOrDefault(p => p.Ipv4.SequenceEqual(servers));
        if (match is null) return;
        baseProviderId = match.Id;
        baseDoh = match.SupportsDoh ? rows[0].V4.Servers[0].Mode : DohMode.Off;
    }

    private void Notify()
    {
        RefreshCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); UndoLastCommand.NotifyCanExecuteChanged(); RevertHistory.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        foreach (var n in new[] { nameof(CanApply), nameof(HasSelection), nameof(ShowEmpty), nameof(ShowLoading), nameof(ShowContent), nameof(ShowUndoOffer), nameof(SelectionText), nameof(PendingCount), nameof(ActionBarSummary), nameof(PendingLines) }) OnPropertyChanged(n);
        RefreshCards();
    }

    public string SelectionText => Text.Format("NetSelectionCount", Adapters.Count(a => a.IsSelected));

    public override void Activate() { if ((!loaded || Journal.IsStale) && !Busy) work = ReloadAsync(); }
    public override Task WhenIdleAsync() => work;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;

    protected override void OnLanguageChanged()
    {
        var providerId = provider?.Id; var dohMode = doh?.Mode;
        base.OnLanguageChanged();
        BuildLists();
        RebuildCards();
        quiet++;
        try
        {
            SelectedProvider = providerId is null ? null : Providers.FirstOrDefault(p => p.Id == providerId);
            SelectedDoh = DohChoices.FirstOrDefault(d => d.Mode == dohMode) ?? DohChoices.First();
        }
        finally { quiet--; }
        if (last is not null) Fill(last);
        work = Task.WhenAll(work, RefreshHistoryAsync());
    }

    // ---- loading ----

    private void BuildLists()
    {
        Providers.Clear();
        foreach (var p in service.Catalog.Providers)
            Providers.Add(new(p.Id, p.Name, Describe(p), p.SupportsDoh, p.Sources[0], false));
        Providers.Add(new(DnsProviderCatalog.AutomaticId, Text["NetProviderAutomatic"], Text["NetProviderAutomaticDesc"], false, "", false));
        Providers.Add(new(DnsProviderCatalog.CustomId, Text["NetProviderCustom"], Text["NetProviderCustomDesc"], false, "", true));
        DohChoices.Clear();
        DohChoices.Add(new(DohMode.Off, Text["NetDohOff"]));
        DohChoices.Add(new(DohMode.OnWithFallback, Text["NetDohFallback"]));
        DohChoices.Add(new(DohMode.OnWithoutFallback, Text["NetDohStrict"]));
        doh ??= DohChoices[1];
        if (provider is not null) provider = Providers.FirstOrDefault(p => p.Id == provider.Id) ?? Providers[0];
        doh = DohChoices.First(d => d.Mode == doh.Mode);
    }

    private string Describe(DnsProvider p)
    {
        var key = "NetProv_" + p.Id.Replace('-', '_') + "_Desc";
        return Text.Has(key) ? Text[key] : p.Name;
    }

    private Task ReloadAsync() => ReloadCoreAsync();

    private async Task ReloadCoreAsync()
    {
        Journal.MarkRead();
        Busy = true;
        try
        {
            var snapshot = await Task.Run(() => service.Read()).ConfigureAwait(true);
            loadError = snapshot.Support == DnsSupport.Unavailable ? Text["NetUnavailable"] : null;
            loaded = true;
            last = snapshot;
            DetectBaseline(snapshot);
            if (resetPickers || PendingCount == 0)
            {
                resetPickers = false; dohStaged = false;
                quiet++;
                try { ShowBaseline(); } finally { quiet--; }
            }
            Fill(snapshot);
        }
        catch (Exception e) when (e is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException or IOException)
        {
            loadError = Text["NetLoadFailed"]; loaded = true;
        }
        finally { Busy = false; OnPropertyChanged(nameof(LoadError)); OnPropertyChanged(nameof(HasLoadError)); OnPropertyChanged(nameof(HasAdapters)); Notify(); }
        await RefreshHistoryAsync().ConfigureAwait(true);
    }

    private void Fill(NetworkSnapshot snapshot)
    {
        var keepOff = Adapters.Where(a => !a.IsSelected && !a.IsVirtual).Select(a => a.Id).ToHashSet();
        var keepIncluded = Adapters.Where(a => a.Included).Select(a => a.Id).ToHashSet();
        Adapters.Clear();
        foreach (var a in snapshot.Adapters.OrderBy(a => a.IsVirtual).ThenBy(a => a.Kind).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new NetworkAdapterRow(a, Text.Enum(a.Kind), a.VirtualReason is null ? "" : Text["NetVirtual_" + a.VirtualReason], AddressesText(a), DnsText(a), EffectiveText(a), EncryptionText(a),
                Text["NetInclude"], Text["NetIncludeHelp"], Notify, r => work = IncludeVirtualAsync(r)) { Name = Text.Name(a.Name) };
            Adapters.Add(row);
            if (keepOff.Contains(a.Id) && !a.IsVirtual) row.IsSelected = false;
            if (keepIncluded.Contains(a.Id)) row.SetIncluded(true, Text["NetExclude"]);
        }
        OnPropertyChanged(nameof(HasAdapters));
        Notify();
    }

    private string AddressesText(NetworkAdapter a)
    {
        var parts = new List<string>();
        if (a.Ipv4Addresses.Count > 0) parts.Add(Text.Format("NetAddrV4", Text.LtrList(a.Ipv4Addresses)));
        if (a.Ipv6Addresses.Count > 0) parts.Add(Text.Format("NetAddrV6", Text.LtrList(a.Ipv6Addresses.Take(3))));
        return parts.Count == 0 ? Text["NetNoAddress"] : string.Join(Text["ListSeparatorStrong"], parts);
    }

    private string DnsText(NetworkAdapter a)
    {
        if (a.Dns is null) return Text["NetDnsUnreadable"];
        var how = a.Dns.V4.IsAutomatic && a.Dns.V6.IsAutomatic ? Text["NetSourceAutomatic"] : Text["NetSourceStatic"];
        return Text.Format("NetDnsLine", how, Describe(a.Dns));
    }

    private string EffectiveText(NetworkAdapter a) => a.EffectiveDns.Count == 0 ? Text["NetEffectiveNone"] : Text.Format("NetEffectiveLine", Text.LtrList(a.EffectiveDns));

    private string EncryptionText(NetworkAdapter a) => a.Dns is null ? Text["NetEncUnknown"] : Text["NetEnc_" + DnsEncryption.Summarize(a.Dns)];

    public string Describe(DnsSettings s) => Text.Format("NetDescribe", DescribeFamily(s.V4), DescribeFamily(s.V6));

    private string DescribeFamily(DnsFamily f)
    {
        if (f.IsAutomatic) return Text["NetAutomaticShort"];
        var modes = f.Servers.Select(x => x.Mode).Distinct().ToList();
        var doh = modes.Count > 1 ? Text["NetDohMixed"] : modes[0] switch { DohMode.OnWithFallback => Text["NetDohTagFallback"], DohMode.OnWithoutFallback => Text["NetDohTagStrict"], _ => Text["NetDohTagOff"] };
        // The mark keeps "(DNS ..." out of the Latin address run in a right-to-left line.
        return Text.Format("NetFamilyServers", Text.LtrList(f.Servers.Select(x => x.Address)), Text.Direction == TextDirection.RightToLeft ? Bidi.RightToLeftMark + doh : doh);
    }

    // ---- virtual adapters ----

    private async Task IncludeVirtualAsync(NetworkAdapterRow row)
    {
        if (row.Included) { row.SetIncluded(false, Text["NetInclude"]); return; }
        var confirmation = new ActionConfirmation(Text["NetIncludeTitle"], Text.Format("NetIncludeBody", row.Name), [row.Name + " (" + row.VirtualText + ")"], Text["NetIncludeWarning"], Text["NetIncludePrimary"])
        {
            Changes = [new ConfirmChange(row.Name + " (" + row.VirtualText + ")")], Risk = RiskLevel.Medium, Undo = ConfirmUndo.Exact, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        row.SetIncluded(true, Text["NetExclude"]);
    }

    // ---- actions ----

    private DnsRequest? BuildRequest()
    {
        if (provider is null || doh is null) return null;
        var picked = Adapters.Where(a => a.IsSelected).ToList();
        return new(provider.Id, provider.SupportsDoh ? doh.Mode : DohMode.Off, picked.Select(a => a.Id).ToList(), picked.Where(a => a.Included).Select(a => a.Id).ToList(), provider.IsCustom ? CustomServers : null);
    }

    private async Task ApplyAsync()
    {
        if (BuildRequest() is not { } request) return;
        Notice = null; ClearOffer();
        NetworkPreview preview;
        Busy = true;
        try { preview = await Task.Run(() => service.Preview(request)).ConfigureAwait(true); }
        finally { Busy = false; }
        if (preview.IsRefused) { Fail(CodeText(preview.RefusalCode!)); return; }
        var changes = preview.Changes;
        if (changes.Count == 0)
        {
            Notice = preview.Adapters.Count > 0 && preview.Adapters.All(a => a.RefusalCode == NetworkCode.AlreadySet) ? Text["NetNothingToChange"] : string.Join(" ", preview.Adapters.Select(a => a.RefusalCode).Where(c => c is not null).Distinct().Select(c => CodeText(c!)));
            NoticeIsError = preview.Adapters.Any(a => a.RefusalCode is not null and not NetworkCode.AlreadySet); OnPropertyChanged(nameof(NoticeIsError));
            return;
        }
        var lines = new List<string>();
        foreach (var c in changes)
        {
            lines.Add(c.Adapter.Name);
            lines.Add("   " + Text.Format("NetBeforeLine", Describe(c.Before)));
            lines.Add("   " + Text.Format("NetAfterLine", Describe(c.After)));
            foreach (var note in c.Notes) lines.Add("   " + CodeText(note));
        }
        var skipped = preview.Adapters.Where(a => a.RefusalCode is not null and not NetworkCode.AlreadySet).Select(a => Text.Format("NetSkippedLine", a.Adapter.Name, CodeText(a.RefusalCode!))).ToList();
        lines.AddRange(skipped);
        var confirmation = new ActionConfirmation(Text["NetApplyTitle"], Text.Format("NetApplyBody", provider!.Name, changes.Count), lines, Text["NetApplyWarning"], Text["NetApplyPrimary"])
        {
            Changes = changes.Select(c => new ConfirmChange(c.Adapter.Name, Describe(c.Before), Describe(c.After), null, c.Notes.Count == 0 ? null : string.Join(" ", c.Notes.Select(CodeText)))).ToList(),
            Risk = RiskLevel.Medium, Undo = ConfirmUndo.WhileUnchanged, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;

        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        Busy = true;
        NetworkApplyResult? result = null;
        try { result = await service.ApplyAsync(preview).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail(Text["ActionFailedUnexpected"]); }
        finally { Busy = false; }
        if (result is not null) Report(result);
        run.Finish(result is null ? RunResult.Error : NoticeIsError ? RunResult.Warning : RunResult.Ok, result is null ? Text.Format("ACRunFailedText", Title) : Notice);
        if (result is not null && result.Results.Any(r => r.Code == NetworkCode.Ok)) { resetPickers = true; ClearStaged(); }
        await ReloadAsync().ConfigureAwait(true); // also after a failure: part of the change may be written
    }

    private async Task RevertAsync(Guid id)
    {
        if (Busy) return;
        var record = service.History(500).FirstOrDefault(r => r.Change.Id == id);
        var lines = record?.Change.Entries.Select(e => e.AdapterName + ": " + Describe(e.Before)).ToList() ?? [];
        var confirmation = new ActionConfirmation(Text["NetRevertTitle"], Text["NetRevertBody"], lines, Text["NetApplyWarning"], Text["NetRevertPrimary"])
        {
            Changes = record?.Change.Entries.Select(e => new ConfirmChange(e.AdapterName, null, Describe(e.Before))).ToList() ?? [],
            Risk = RiskLevel.Medium, Undo = ConfirmUndo.WhileUnchanged, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true) || Busy) return;
        using var run = Runs.Begin(Text.Format("ACRunRevert", Title), true);
        if (!run.Started) return;
        Busy = true; Notice = null; ClearOffer();
        NetworkApplyResult? result = null;
        resetPickers = true; // the adapters change: the pickers follow what they have afterwards
        try { result = await service.RevertAsync(id).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { Fail(Text["ActionFailedUnexpected"]); }
        finally { Busy = false; }
        if (result is not null) Report(result);
        run.Finish(result is null ? RunResult.Error : NoticeIsError ? RunResult.Warning : RunResult.Ok, result is null ? Text.Format("ACRunFailedText", Title) : Notice);
        await ReloadAsync().ConfigureAwait(true);
    }

    private Task UndoOfferedAsync() => offerUndo is { } id ? RevertAsync(id) : Task.CompletedTask;

    private void ClearOffer() { offerUndo = null; OnPropertyChanged(nameof(ShowUndoOffer)); UndoLastCommand.NotifyCanExecuteChanged(); }

    private void Fail(string message) { Notice = message; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); }

    private void Report(NetworkApplyResult result)
    {
        var applied = result.Results.Count(r => r.Code == NetworkCode.Ok);
        var problems = result.Results.Where(r => r.Code != NetworkCode.Ok).Select(r => CodeText(r.Code)).Distinct().ToList();
        if (applied == 0)
        {
            Fail(problems.Count > 0 ? string.Join(" ", problems) : CodeText(result.Code));
            return;
        }
        var text = problems.Count == 0 ? (applied == 1 ? Text["NetAppliedOne"] : Text.Format("NetApplied", applied)) : Text.Format("NetPartial", applied, string.Join(" ", problems));
        text += " " + Text["NetAppliesToNew"];
        if (result.RecordLost) text += " " + Text["RecordLostNote"];
        switch (result.Connectivity)
        {
            case ConnectivityResult.Ok: text += " " + Text["NetCheckOk"]; break;
            case ConnectivityResult.Failed:
                text += " " + Text["NetCheckFailed"];
                if (result.Change is { RevertOf: null } change) { offerUndo = change.Id; OnPropertyChanged(nameof(ShowUndoOffer)); UndoLastCommand.NotifyCanExecuteChanged(); }
                break;
        }
        Notice = text;
        NoticeIsError = problems.Count > 0 || result.Connectivity == ConnectivityResult.Failed;
        OnPropertyChanged(nameof(NoticeIsError));
    }

    public string CodeText(string code)
    {
        var key = "NetCode_" + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["NetCode_network_failed"];
    }

    // ---- history ----

    private sealed record HistoryData(IReadOnlyList<DnsChangeRecord> Records, IReadOnlyList<bool> Revertable);

    /// <summary>The journal and adapter reads of the history. No UI object is touched, so it can run on a pool thread.</summary>
    private HistoryData GatherHistory()
    {
        IReadOnlyList<DnsChangeRecord> records;
        try { records = service.History(HistoryLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { records = []; }
        return new(records, IsElevated && records.Count > 0 ? service.CanRevertAll(records) : []);
    }

    private void RefreshHistory() => ApplyHistory(GatherHistory());

    private async Task RefreshHistoryAsync() => ApplyHistory(await Task.Run(GatherHistory).ConfigureAwait(true));

    private void ApplyHistory(HistoryData data)
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        var records = data.Records;
        var revertable = data.Revertable;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            History.Add(new(r, DescribeChange(r.Change), r.State is { } s ? Text["NetState_" + s] : Text["NetInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), IsElevated && i < revertable.Count && revertable[i], RevertHistory));
            var h = History[^1];
            shown.Add(HistoryList.Row(r.Change.Id, r.BeganUtc, r.State == NetworkChangeState.Applied ? (records.Any(o => o.Change.RevertOf == r.Change.Id && o.State == NetworkChangeState.Applied) ? HistoryResult.Undone : HistoryResult.Applied) : r.State == NetworkChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed, h.Description, h.CanRevert, h.RevertCommand, h, new(Text["NetUndo"], Text["NetUndoHelp"])));
        }
        HistoryList.Set(shown);
        OnPropertyChanged(nameof(HasHistory));
    }

    private string DescribeChange(DnsChange c)
    {
        var name = c.ProviderId == DnsProviderCatalog.AutomaticId ? Text["NetProviderAutomatic"] : c.ProviderId == DnsProviderCatalog.CustomId ? Text["NetProviderCustom"] : service.Catalog.Find(c.ProviderId)?.Name ?? c.ProviderId;
        var adapters = Text.LtrList(c.Entries.Select(e => e.AdapterName));
        var undo = c.RevertOf is null ? "" : " " + Text["NetUndoSuffix"];
        return Text.Format("NetHistoryLine", name, adapters) + undo;
    }
}

/// <summary>
/// A choice of the Network page (the DNS provider, encrypted DNS) as a card of the settings page component. The page keeps the selection; the Apply button opens the review that applies it to
/// the ticked adapters, so choosing here changes nothing by itself.
/// </summary>
public sealed class NetworkChoiceRow(LocalizationService text, string id, Func<string> title, Func<string> description, Func<IReadOnlyList<RowChoice>> choices, Func<RowChoice?> get, Action<RowChoice> set,
    Func<bool> enabled, Func<string> valueText) : PlainSettingRow(text)
{
    private IReadOnlyList<RowChoice>? cached;
    public override string Id => id;
    public override string Title => title();
    public override string Description => description();
    public override string CategoryKey => "Network";
    public override string CategoryLabel => Text["Network"];
    public override string CurrentLabel => get()?.Label ?? "";
    public override string ValueText => valueText();
    public override IReadOnlyList<RowChoice> Choices => cached ??= choices();
    public override RowChoice? SelectedChoice
    {
        get => get() is { } c ? Choices.FirstOrDefault(x => x.Id == c.Id) : null;
        set { if (value is not null) set(value); else Refresh(); }
    }
    public override bool CanChoose => enabled();
    public override bool IsPending => false;
    public void Rebuild() { cached = null; Refresh(); }
}
