// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>One measured value of the performance check as a row: name, value, the limit it was judged by and a status that has an icon and a word.</summary>
public sealed class PerformanceMetricRow(PerformanceMetric metric, LocalizationService text)
{
    public string Id => metric.Id;
    public string Name => text["V2Pc_" + metric.Id];
    public bool Available => metric.Status != MetricStatus.Unavailable;
    public string ValueText => !Available ? text["V2PcUnavailable"] : metric.Id switch
    {
        MetricIds.Boot => text.Format("V2PcBootValue", Math.Round(metric.Value!.Value, 0)),
        MetricIds.Startup => text.Format("V2PcStartupValue", (int)metric.Value!.Value),
        MetricIds.Memory => text.Format("V2PcMemoryValue", Math.Round(metric.Value!.Value, 0)),
        MetricIds.Disk => text.Format("V2PcDiskValue", Math.Round(metric.Value!.Value, 0)),
        _ => text.Format("V2PcCpuValue", Math.Round(metric.Value!.Value, 0)),
    };
    public string Limit => Available ? text["V2PcLimit_" + metric.Id] : text["V2PcUnavailableHelp_" + metric.Id];
    public string StatusText => text[metric.Status switch { MetricStatus.Good => "V2PcGood", MetricStatus.Fair => "V2PcFair", MetricStatus.Poor => "V2PcPoor", _ => "V2PcUnavailable" }];
    public Tone Tone => metric.Status switch { MetricStatus.Good => Tone.Success, MetricStatus.Fair => Tone.Caution, MetricStatus.Poor => Tone.Danger, _ => Tone.Neutral };
    public string AutomationName => text.Format("V2PcRowName", Name, ValueText, StatusText);
}

/// <summary>
/// The performance check of the Overview page (decision DEC-IW-231): five values measured on this PC, each judged by a fixed limit, and one score from them. It runs only when the
/// user presses Measure, never on a timer, and it reads only. The score is not a benchmark and the page says so.
/// </summary>
public sealed class PerformanceCheckViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly PerformanceService service;
    private readonly Func<CancellationToken, Task<int?>> startupCount;
    private CancellationTokenSource? run;
    private PerformanceReport? report;
    private bool measuring;
    private string stage = "";
    private int token;

    public PerformanceCheckViewModel(LocalizationService text, PerformanceService service, Func<CancellationToken, Task<int?>> startupCount)
    {
        this.text = text; this.service = service; this.startupCount = startupCount;
        MeasureCommand = new(MeasureAsync, () => !measuring);
        CancelCommand = new(() => run?.Cancel(), () => measuring);
        text.PropertyChanged += (_, _) => Rebuild();
    }

    /// <summary>Registers the measurement in the Action center, where it is stopped. The shell sets the service.</summary>
    public RunSource Runs { get; } = new("Overview");
    public AsyncRelayCommand MeasureCommand { get; }
    public RelayCommand CancelCommand { get; }
    public ObservableCollection<PerformanceMetricRow> Rows { get; } = [];

    public bool IsMeasuring { get => measuring; private set { if (SetProperty(ref measuring, value)) { MeasureCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(MeasureLabel)); OnPropertyChanged(nameof(ShowIdle)); } } }
    public string StageText { get => stage; private set => SetProperty(ref stage, value); }
    public PerformanceReport? Report => report;
    public bool HasReport => report is not null;
    public bool HasScore => report?.Score is not null;
    public bool ShowIdle => !measuring;
    public int AnimationToken => token;
    public double Value => report?.Score ?? 0;
    public string Title => text["V2PcTitle"];
    public string Intro => text["V2PcIntro"];
    public string Of100 => text["V2OvOf100"];
    public string MeasureLabel => text[report is null ? "V2PcMeasure" : "V2PcMeasureAgain"];
    public string MeasureHelp => text["V2PcMeasureHelp"];
    public string CancelLabel => text["V2PcCancel"];
    public string Note => text["V2PcNote"];
    public string MeasuredText => report is null ? "" : text.Format("V2PcMeasuredAt", text.When(report.At.ToLocalTime(), "t"));
    public string BandLabel => text[Band switch { ScoreBand.Good => "V2OvBandGood", ScoreBand.Fair => "V2OvBandFair", _ => "V2OvBandLow" }];
    public Tone BandTone => Band switch { ScoreBand.Good => Tone.Success, ScoreBand.Fair => Tone.Caution, _ => Tone.Danger };
    public ScoreBand Band => report?.Score switch { >= 80 => ScoreBand.Good, >= 50 => ScoreBand.Fair, _ => ScoreBand.Low };
    public string Headline => !HasReport ? text["V2PcHeadNone"] : !HasScore ? text["V2PcHeadNoScore"] : text[Band switch { ScoreBand.Good => "V2PcHeadGood", ScoreBand.Fair => "V2PcHeadFair", _ => "V2PcHeadLow" }];
    public string RingName => HasScore ? text.Format("V2PcRingName", report!.Score!.Value) : text["V2PcHeadNoScore"];
    public string AutomationName => HasScore ? $"{Title}: {RingName}, {BandLabel}. {MeasuredText}" : $"{Title}: {Headline}";

    /// <summary>Measures now. A second press while it runs does nothing; Cancel stops it and keeps the last result.</summary>
    public async Task MeasureAsync()
    {
        if (measuring) return;
        using var activity = Runs.Begin(text["ACRunPerformance"], false, () => run?.Cancel());
        IsMeasuring = true; StageText = text["V2PcStage_startup"];
        run = new CancellationTokenSource();
        try
        {
            var progress = new SyncProgress(id => StageText = text["V2PcStage_" + id]);
            var startup = await startupCount(run.Token);
            var next = await service.MeasureAsync(startup, progress, run.Token);
            report = next; token++;
            Rebuild();
            activity.Finish(RunResult.Ok, text["ACRunPerformanceDone"]);
        }
        catch (OperationCanceledException) { StageText = ""; activity.Finish(RunResult.Stopped); }
        catch (Exception e) when (e is not OutOfMemoryException) { activity.Finish(RunResult.Error, text.Format("ACRunFailedText", text["ACRunPerformance"])); throw; }
        finally { run.Dispose(); run = null; IsMeasuring = false; StageText = ""; }
    }

    private void Rebuild()
    {
        Rows.Clear();
        if (report is not null) foreach (var m in report.Metrics) Rows.Add(new(m, text));
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Reports on the calling thread: the probe runs on the UI context here, so the stage text needs no marshalling.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
