using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.QualityGate;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;
using Nito.AsyncEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using NINA.Plugin.Livestack.MultiNight;
using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.Livestack.LivestackDockables {

    [Export(typeof(IDockableVM))]
    public partial class LivestackDockable : DockableVM, ISubscriber {
        private const int MinimumAffineStarCount = 3;

        public override bool IsTool { get; } = true;

        [ImportingConstructor]
        public LivestackDockable(IProfileService profileService,
                                 IApplicationStatusMediator applicationStatusMediator,
                                 IImageSaveMediator imageSaveMediator,
                                 IImageDataFactory imageDataFactory,
                                 IWindowServiceFactory windowServiceFactory,
                                 ICameraMediator cameraMediator,
                                 IMessageBroker messageBroker) : base(profileService) {
            this.Title = "Live Stack";
            var dict = new ResourceDictionary();
            dict.Source = new Uri("NINA.Plugin.Livestack;component/Options.xaml", UriKind.RelativeOrAbsolute);
            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["Livestack_StackSVG"];
            ImageGeometry.Freeze();

            this.applicationStatusMediator = applicationStatusMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.imageDataFactory = imageDataFactory;
            this.windowServiceFactory = windowServiceFactory;
            this.cameraMediator = cameraMediator;
            this.messageBroker = messageBroker;
            profileService.ActiveProfile.PropertyChanged += ActiveProfile_PropertyChanged;
            InitializeQualityGates();
            tabs = new AsyncObservableCollection<IStackTab>();
            IsExpanded = true;
            LivestackMediator.RegisterLivestackDockable(this);

            messageBroker.Subscribe("Livestack_LivestackDockable_StartLiveStack", this);
            messageBroker.Subscribe("Livestack_LivestackDockable_StopLiveStack", this);
        }

        private void ActiveProfile_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
            foreach (var q in QualityGates) {
                q.PropertyChanged -= QualityGate_PropertyChanged;
            }
            InitializeQualityGates();
        }

        private void InitializeQualityGates() {
            QualityGates = new AsyncObservableCollection<IQualityGate>(LivestackMediator.PluginSettings.GetValueString(nameof(QualityGates), "").FromStringToList<IQualityGate>());
            foreach (var q in QualityGates) {
                q.PropertyChanged += QualityGate_PropertyChanged;
            }
        }

        [ObservableProperty]
        private bool isExpanded;

        [ObservableProperty]
        private AsyncObservableCollection<IQualityGate> qualityGates;

        [ObservableProperty]
        private AsyncObservableCollection<IStackTab> tabs;

        [ObservableProperty]
        private IStackTab selectedTab;

        private int queueEntries;
        public int QueueEntries { get => queueEntries; }

        private Channel<LiveStackItem> channel;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageDataFactory imageDataFactory;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly ICameraMediator cameraMediator;
        private readonly IMessageBroker messageBroker;
        private Guid? stackSessionId = null;

        // Multi-night: tracks active sidecars keyed by "Target-Filter"
        private readonly ConcurrentDictionary<string, (StackSidecar sidecar, string path)> activeSidecars = new();
        // Multi-night: tracks which (Target-Filter) have passed Layer 1 validation
        private readonly ConcurrentDictionary<string, bool> layer1Validated = new();
        // Multi-night: tracks plate solve retry counts per (Target-Filter)
        private readonly ConcurrentDictionary<string, int> layer1RetryCount = new();

        [RelayCommand(IncludeCancelCommand = true)]
        private Task StartLiveStack(CancellationToken token) {
            return Task.Run(async () => {
                try {
                    IsExpanded = false;
                    ResetQueueEntries();
                    channel = Channel.CreateBounded<LiveStackItem>(1000);
                    var localQueue = channel;
                    this.imageSaveMediator.BeforeFinalizeImageSaved += ImageSaveMediator_BeforeFinalizeImageSaved;
                    this.stackSessionId = Guid.NewGuid();

                    // Log multi-night configuration
                    if (LivestackMediator.Plugin.MultiNightMode) {
                        Logger.Info($"[MultiNight] Session starting — MultiNightMode=ON, PlatesolveThreshold={LivestackMediator.Plugin.PlatesolveThresholdArcmin}', AffineThreshold={LivestackMediator.Plugin.AffineResidualThresholdPixels}px, RetryFrames={LivestackMediator.Plugin.PlatesolveRetryFrames}, SaveStackedLights={LivestackMediator.Plugin.SaveStackedLights}, WorkingDir={LivestackMediator.Plugin.WorkingDirectory}");
                    } else {
                        Logger.Info("[MultiNight] Session starting — MultiNightMode=OFF");
                    }

                    _ = messageBroker.Publish(new LiveStackStatusBroadcast(LiveStackStatus.Running, this.stackSessionId.Value));
                    applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Waiting for first frame" });

                    try {
                        await foreach (var item in channel.Reader.ReadAllAsync(token)) {
                            try {
                                StatusUpdate("Received new frame", item);
                                DecrementQueueEntries();

                                try {
                                    if (item.StarList.Count < 8) {
                                        Logger.Info($"Skipping frame as not enough stars have been detected ({item.StarList.Count})");
                                        continue;
                                    }

                                    if (!ItemPassesQuality(item)) {
                                        continue;
                                    }

                                    await StackItem(item, token);
                                } finally {
                                    try {
                                        File.Delete(item.Path);
                                    } finally {
                                        LiveStackMemoryPressure.CollectIfNeeded("frame completed");
                                    }
                                }

                            } catch (OperationCanceledException) {
                            } catch (Exception ex) {
                                Logger.Error(ex);
                            } finally {
                                applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Waiting for next frame" });
                            }
                        }
                    } catch (OperationCanceledException) { }

                    if (localQueue != null) {
                        try {
                            localQueue.Writer.TryComplete();
                            await foreach (var item in channel.Reader.ReadAllAsync()) {
                                StatusUpdate("Flushing queue", item);
                                File.Delete(item.Path);
                            }
                        } catch { }
                    }
                } finally {
                    applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "" });
                    this.imageSaveMediator.BeforeFinalizeImageSaved -= ImageSaveMediator_BeforeFinalizeImageSaved;
                    _ = messageBroker.Publish(new LiveStackStatusBroadcast(LiveStackStatus.Stopped, this.stackSessionId.Value));
                    this.stackSessionId = null;
                    IsExpanded = true;
                    ResetQueueEntries();
                    LiveStackMemoryPressure.CompactAfterReleasingLargeBuffers("live stack stopped");
                }
            });
        }

        [RelayCommand]
        private async Task RemoveTab(IStackTab tab) {
            while (tab.Locked) {
                await Task.Delay(10);
            }

            var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == tab.Target).FirstOrDefault() as ColorCombinationTab;
            if (tab.Filter == LiveStackBag.RED_OSC || tab.Filter == LiveStackBag.GREEN_OSC || tab.Filter == LiveStackBag.BLUE_OSC) {
                var red = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.RED_OSC && x.Target == tab.Target);
                var green = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.GREEN_OSC && x.Target == tab.Target);
                var blue = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.BLUE_OSC && x.Target == tab.Target);
                Tabs.Remove(red);
                Tabs.Remove(green);
                Tabs.Remove(blue);
            } else {
                Tabs.Remove(tab);
            }
            await Task.Run(() => LiveStackMemoryPressure.CompactAfterReleasingLargeBuffers("stack tab removed"));
        }

        [RelayCommand]
        private async Task ResetStack(IStackTab tab) {
            if (tab == null) return;
            while (tab.Locked) {
                await Task.Delay(10);
            }

            var target = tab.Target;
            var filter = tab.Filter;
            var key = $"{target}-{filter}";

            // Archive the FITS file and sidecar
            var fitsPath = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "stacks",
                NINA.Core.Utility.CoreUtil.ReplaceAllInvalidFilenameChars($"{target}-{filter}.fits"));
            var sidecarPath = MultiNight.StackSidecar.GetSidecarPath(fitsPath);

            if (File.Exists(fitsPath)) {
                var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var dir = Path.GetDirectoryName(fitsPath);
                var archiveFits = Path.Combine(dir, NINA.Core.Utility.CoreUtil.ReplaceAllInvalidFilenameChars($"{target}-{filter}-archived-{date}.fits"));
                archiveFits = NINA.Core.Utility.CoreUtil.GetUniqueFilePath(archiveFits, "{0}_{1}");
                try {
                    File.Move(fitsPath, archiveFits);
                    Logger.Info($"[MultiNight] Stack reset: archived to {Path.GetFileName(archiveFits)}");
                    if (File.Exists(sidecarPath)) {
                        File.Move(sidecarPath, Path.ChangeExtension(archiveFits, ".json"));
                    }
                } catch (Exception ex) {
                    Logger.Error($"[MultiNight] Failed to archive stack: {ex.Message}");
                }
            }

            // Clear sidecar and validation tracking
            activeSidecars.TryRemove(key, out _);
            layer1Validated.TryRemove(key, out _);
            layer1RetryCount.TryRemove(key, out _);

            // Remove the tab — next frame will create a fresh one
            await RemoveTab(tab);

            Notification.ShowInformation($"[MultiNight] Stack reset for {target}-{filter}");
        }

        [RelayCommand]
        private void DeleteQualityGate(IQualityGate obj) {
            obj.PropertyChanged -= QualityGate_PropertyChanged;
            QualityGates.Remove(obj);
            LivestackMediator.PluginSettings.SetValueString(nameof(QualityGates), QualityGates.FromListToString());
        }

        [RelayCommand]
        private async Task<bool> AddQualityGate() {
            var service = windowServiceFactory.Create();
            var prompt = new QualityGatePrompt();
            await service.ShowDialog(prompt, "Quality Gate Addition", System.Windows.ResizeMode.NoResize, System.Windows.WindowStyle.ToolWindow);

            if (prompt.Continue && prompt.SelectedGate != null) {
                prompt.SelectedGate.PropertyChanged += QualityGate_PropertyChanged;
                QualityGates.Add(prompt.SelectedGate);

                LivestackMediator.PluginSettings.SetValueString(nameof(QualityGates), QualityGates.FromListToString());
            }
            return prompt.Continue;
        }

        [RelayCommand]
        private async Task AddColorCombination(CancellationToken token) {
            var service = windowServiceFactory.Create();
            var prompt = new ColorCombinationPrompt(Tabs);
            await service.ShowDialog(prompt, "Color Combination Wizard", System.Windows.ResizeMode.NoResize, System.Windows.WindowStyle.ToolWindow);

            if (prompt.Continue) {
                if (!string.IsNullOrEmpty(prompt.Target)) {
                    var colorTab = new ColorCombinationTab(profileService, prompt.RedChannel, prompt.GreenChannel, prompt.BlueChannel);
                    Tabs.Add(colorTab);
                    await colorTab.Refresh(token);
                }
            }
        }

        private void QualityGate_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
            LivestackMediator.PluginSettings.SetValueString(nameof(QualityGates), QualityGates.FromListToString());
        }

        partial void OnSelectedTabChanged(IStackTab value) {
            if (value == null) {
                return;
            }

            _ = RefreshSelectedTabAsync(value);
        }

        private async Task ImageSaveMediator_BeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs e) {
            if (e.Image.RawImageData.MetaData.Image.ImageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.LIGHT || e.Image.RawImageData.MetaData.Image.ImageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.SNAPSHOT) {
                _ = Task.Run(async () => {
                    try {
                        var statistics = await e.Image.RawImageData.Statistics;
                        var starDetectionAnalysis = e.Image.RawImageData.StarDetectionAnalysis;
                        if (NeedsStarDetection(starDetectionAnalysis)) {
                            var render = e.Image.RawImageData.RenderImage();
                            render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                            render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, default, default);
                            starDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
                        }

                        // Only retrieve the filename part of the pattern
                        var pattern = Path.GetFileName(profileService.ActiveProfile.ImageFileSettings.GetFilePattern(e.Image.RawImageData.MetaData.Image.ImageType));

                        var path = await e.Image.RawImageData.SaveToDisk(
                            new NINA.Image.FileFormat.FileSaveInfo() {
                                FilePath = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "temp"),
                                FilePattern = pattern,
                                FileType = Core.Enum.FileTypeEnum.FITS
                            },
                            default, true, e.Patterns
                        );
                        await channel.Writer.WriteAsync(new LiveStackItem(path: path,
                                                                   target: e.Image.RawImageData.MetaData.Target.Name,
                                                                   filter: e.Image.RawImageData.MetaData.FilterWheel.Filter,
                                                                   exposureTime: e.Image.RawImageData.MetaData.Image.ExposureTime,
                                                                   gain: e.Image.RawImageData.MetaData.Camera.Gain,
                                                                   offset: e.Image.RawImageData.MetaData.Camera.Offset,
                                                                   width: e.Image.RawImageData.Properties.Width,
                                                                   height: e.Image.RawImageData.Properties.Height,
                                                                   bitDepth: (int)profileService.ActiveProfile.CameraSettings.BitDepth,
                                                                   isBayered: e.Image.RawImageData.Properties.IsBayered,
                                                                   analysis: starDetectionAnalysis,
                                                                   metaData: e.Image.RawImageData.MetaData));

                        IncrementQueueEntries();
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }
                });
            }
        }

        private void ResetQueueEntries() {
            Interlocked.Exchange(ref queueEntries, 0);
            NotifyQueueEntriesChanged();
        }

        private void IncrementQueueEntries() {
            Interlocked.Increment(ref queueEntries);
            NotifyQueueEntriesChanged();
        }

        private void DecrementQueueEntries() {
            int updated = Interlocked.Decrement(ref queueEntries);
            if (updated < 0) {
                Interlocked.Exchange(ref queueEntries, 0);
            }
            NotifyQueueEntriesChanged();
        }

        private void NotifyQueueEntriesChanged() {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted) {
                _ = dispatcher.BeginInvoke(new Action(() => RaisePropertyChanged(nameof(QueueEntries))));
                return;
            }

            RaisePropertyChanged(nameof(QueueEntries));
        }

        private static bool NeedsStarDetection(IStarDetectionAnalysis analysis) {
            return analysis is null || analysis.DetectedStars <= 0;
        }

        private async Task RefreshSelectedTabAsync(IStackTab tab) {
            try {
                while (ReferenceEquals(SelectedTab, tab) && tab.Locked) {
                    await Task.Delay(25);
                }

                if (!ReferenceEquals(SelectedTab, tab)) {
                    return;
                }

                if (tab is ColorCombinationTab colorTab) {
                    if (colorTab.NeedsRefresh || colorTab.StackImage == null) {
                        await colorTab.Refresh(CancellationToken.None);
                    }
                } else if (tab is LiveStackTab liveTab && liveTab.StackImage == null) {
                    await liveTab.Refresh(CancellationToken.None);
                }
            } catch {
            }
        }

        private bool ItemPassesQuality(LiveStackItem item) {
            var failedGates = QualityGates.Where(x => !x.Passes(item));
            if (failedGates.Any()) {
                var failedGatesInfo = "Live Stack - Image ignored as it does not meet quality gate critera." + Environment.NewLine + string.Join(Environment.NewLine, failedGates.Select(x => $"{x.Name}: {x.Value}"));
                Logger.Warning(failedGatesInfo);
                Notification.ShowWarning(failedGatesInfo);

                // Multi-night: record rejection in sidecar
                var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
                var filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
                if (item.IsBayered) { filter = LiveStackBag.RED_OSC; }
                foreach (var gate in failedGates) {
                    RecordRejectedFrame(target, filter, $"quality_gate_{gate.Name}");
                    Logger.Info($"[MultiNight] Quality gate rejection recorded: {gate.Name} — {target}-{filter}");
                }

                return false;
            }
            return true;
        }

        private LiveStackTab GetOrCreateStackBag(LiveStackItem item) {
            var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
            var filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
            if (item.IsBayered) { filter = LiveStackBag.RED_OSC; }

            var tab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == filter && x.Target == target);
            if (tab == null) {
                List<Accord.Point> stars = null;
                var bag = new LiveStackBag(target, filter, new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), item.MetaData, stars);

                // Multi-night: attempt to resume from existing stack
                var resumeResult = MultiNightManager.TryResume(target, filter, item.Width, item.Height,
                    (int)(profileService.ActiveProfile.CameraSettings.BinningX ?? 1));
                if (resumeResult != null) {
                    bag.ResumeFrom(resumeResult.Stack, resumeResult.ImageCount, resumeResult.ReferenceStars);
                    var sidecarKey = $"{target}-{filter}";
                    activeSidecars[sidecarKey] = (resumeResult.Sidecar, resumeResult.SidecarPath);
                    Notification.ShowInformation($"[MultiNight] Resumed {target}-{filter}: {resumeResult.ImageCount} frames");
                } else if (LivestackMediator.Plugin.MultiNightMode) {
                    // Multi-night enabled but no existing stack — create fresh sidecar
                    var (sidecar, sidecarPath) = MultiNightManager.CreateFreshSidecar(target, filter, item.Width, item.Height,
                        (int)(profileService.ActiveProfile.CameraSettings.BinningX ?? 1));
                    var sidecarKey = $"{target}-{filter}";
                    activeSidecars[sidecarKey] = (sidecar, sidecarPath);

                    // Save reference stars to sidecar
                    if (stars != null) {
                        MultiNightManager.SaveReferenceStars(sidecar, stars);
                        Logger.Info($"[MultiNight] Saved {stars.Count} reference stars to sidecar for {target}-{filter}");
                    }
                }

                tab = new LiveStackTab(profileService, bag);
                Tabs.Add(tab);
                return tab as LiveStackTab;
            }
            return tab as LiveStackTab;
        }

        private async Task StackMono(float[] theImageArray, LiveStackItem item, LiveStackTab tab, Guid correlation, CancellationToken token) {
            if (tab.StackCount == 0 || !HasEnoughAlignmentStars(tab.ReferenceStars)) {
                var stars = LivestackMediator.GetImageTransformer().GetStars(item.StarList, item.Width, item.Height);
                if (!HasEnoughAlignmentStars(stars)) {
                    LogSkippedForInsufficientAlignmentStars("mono reference", item, GetRawStarCount(item), stars.Count, tab.ReferenceStars?.Count);
                    return;
                }

                if (tab.ReferenceStars != null && !HasEnoughAlignmentStars(tab.ReferenceStars)) {
                    Logger.Warning($"Live Stack replacing invalid mono reference. Old reference stars={tab.ReferenceStars.Count}; New reference stars={stars.Count}; Required={MinimumAffineStarCount}; Target=\"{item.Target}\"; Filter=\"{item.Filter}\"; Frame=\"{item.Path}\"");
                }

                tab.ForcePushReference(new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), stars, theImageArray);
                LogReferenceAccepted("mono", item, GetRawStarCount(item), stars.Count);
            } else {
                StatusUpdate("Aligning frame", item);
                var stars = LivestackMediator.GetImageTransformer().GetStars(item.StarList, item.Width, item.Height);
                if (!HasEnoughAlignmentStars(stars) || !HasEnoughAlignmentStars(tab.ReferenceStars)) {
                    LogSkippedForInsufficientAlignmentStars("mono frame", item, GetRawStarCount(item), stars.Count, tab.ReferenceStars?.Count);
                    return;
                }

                ImageTransformer.AffineResult affineResult;
                try {
                    affineResult = LivestackMediator.GetImageTransformer().ComputeAffineTransformationWithResidual(stars, tab.ReferenceStars);
                } catch (Exception ex) {
                    var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
                    Logger.Warning($"[MultiNight] Alignment failed: {ex.GetType().Name}: {ex.Message}. Target stars={stars.Count}; Reference stars={tab.ReferenceStars?.Count}; Target=\"{target}\"; Filter=\"{tab.Filter}\"; Frame=\"{item.Path}\"");
                    RecordRejectedFrame(target, tab.Filter, "alignment_failed");
                    return;
                }
                var affineTransformationMatrix = affineResult.Matrix;
                var flipped = LivestackMediator.GetImageTransformer().IsFlippedImage(affineTransformationMatrix);
                if (flipped) {
                    // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                    stars = LivestackMediator.GetImageMath().Flip(stars, item.Width, item.Height);
                    try {
                        affineResult = LivestackMediator.GetImageTransformer().ComputeAffineTransformationWithResidual(stars, tab.ReferenceStars);
                    } catch (Exception ex) {
                        var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
                        Logger.Warning($"[MultiNight] Flipped alignment failed: {ex.GetType().Name}: {ex.Message}. Target=\"{target}\"; Filter=\"{tab.Filter}\"");
                        RecordRejectedFrame(target, tab.Filter, "alignment_failed");
                        return;
                    }
                    affineTransformationMatrix = affineResult.Matrix;
                }

                // Layer 2: check affine residual before stacking
                var residual = affineResult.ResidualPixels;
                var threshold = LivestackMediator.Plugin.AffineResidualThresholdPixels;
                if (residual > threshold) {
                    var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
                    Logger.Warning($"[MultiNight] Frame rejected: affine residual {residual:F1}px ({affineResult.MatchedStarCount} matched stars) exceeds threshold {threshold:F1}px — {target}-{tab.Filter}");
                    Notification.ShowWarning($"Live Stack - Frame rejected: alignment residual {residual:F1}px exceeds {threshold:F1}px threshold");
                    RecordRejectedFrame(target, tab.Filter, "layer2_affine_residual");
                    return;
                }
                Logger.Info($"[MultiNight] Frame accepted: affine residual {residual:F1}px ({affineResult.MatchedStarCount} matched stars, threshold {threshold:F1}px) — {tab.Target}-{tab.Filter}");

                tab.AddTransformedImage(theImageArray, affineTransformationMatrix, flipped);

                StatusUpdate("Updating stack", item);
            }

            // Multi-night: record accepted frame in sidecar
            RecordAcceptedFrame(tab.Target, tab.Filter, item.ExposureTime);

            StatusUpdate("Rendering stack", item);
            await tab.Refresh(token);
            Logger.Info($"[MultiNight] Stack updated: {tab.Target}-{tab.Filter} now {tab.StackCount} frames");
            if (LivestackMediator.Plugin.SaveStackedLights) {
                StatusUpdate("Saving stack", item);
                tab.SaveToDisk();
                SaveSidecar(tab.Target, tab.Filter);
            }

            var (totalExposure, sessionCount) = GetSidecarStats(tab.Target, tab.Filter);
            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(tab.StackCount, tab.Filter, tab.Target, tab.StackImage, totalExposure, sessionCount), correlation));
        }

        private async Task StackOSC(float[] theImageArray, LiveStackItem item, LiveStackTab redTab, Guid correlation, CancellationToken token) {
            var meta = new ImageMetaData(); // Set bare minimum for star detection resize factor
            meta.Camera.PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize;
            meta.Telescope.FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength;
            var theImageArrayData = imageDataFactory.CreateBaseImageData(theImageArray.ToUShortArray(), item.Width, item.Height, 16, false, meta);
            var image = theImageArrayData.RenderBitmapSource();
            StatusUpdate("Debayering", item);

            var bayerPattern = SensorType.RGGB;
            if (profileService.ActiveProfile.CameraSettings.BayerPattern != BayerPatternEnum.Auto) {
                bayerPattern = (SensorType)profileService.ActiveProfile.CameraSettings.BayerPattern;
            } else if (!cameraMediator.GetInfo().Connected) {
                bayerPattern = cameraMediator.GetInfo().SensorType;
            }
            var debayeredImage = ImageUtility.Debayer(image, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale, true, false, bayerPattern);

            StatusUpdate("Aligning frame - red channel", item);
            var redChannelData = imageDataFactory.CreateBaseImageData(debayeredImage.Data.Red, item.Width, item.Height, redTab.Properties.BitDepth, false, meta);
            // We only need to detect the stars in one channel for OSC. The others should match.
            var channelStatistics = await redChannelData.Statistics;
            var channelRender = redChannelData.RenderImage();
            if (NeedsStarDetection(redChannelData.StarDetectionAnalysis)) {
                var render = channelRender.RawImageData.RenderImage();
                render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, token, default);
                redChannelData.StarDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
            }

            var redChannelStarList = redChannelData.StarDetectionAnalysis?.StarList;
            int rawRedChannelStarCount = redChannelStarList?.Count ?? 0;
            var stars = LivestackMediator.GetImageTransformer().GetStars(redChannelStarList, item.Width, item.Height);

            double[,] affineTransformationMatrix = null;
            bool flipped = false;
            bool pushedReference = false;
            var imageProperties = new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset);

            if (!HasEnoughAlignmentStars(redTab.ReferenceStars)) {
                if (!HasEnoughAlignmentStars(stars)) {
                    LogSkippedForInsufficientAlignmentStars("OSC red-channel reference", item, rawRedChannelStarCount, stars.Count, redTab.ReferenceStars?.Count);
                    return;
                }

                if (redTab.ReferenceStars != null) {
                    Logger.Warning($"Live Stack replacing invalid OSC red-channel reference. Old reference stars={redTab.ReferenceStars.Count}; New reference stars={stars.Count}; Required={MinimumAffineStarCount}; Target=\"{item.Target}\"; Frame=\"{item.Path}\"");
                }

                redTab.ForcePushReference(imageProperties, stars, redChannelData.Data.FlatArray.ToFloatArray());
                LogReferenceAccepted("OSC red channel", item, rawRedChannelStarCount, stars.Count);
                pushedReference = true;
            } else {
                if (!HasEnoughAlignmentStars(stars)) {
                    LogSkippedForInsufficientAlignmentStars("OSC red channel", item, rawRedChannelStarCount, stars.Count, redTab.ReferenceStars?.Count);
                    return;
                }

                // We only need to compute the transformation in one channel. The others should match.
                ImageTransformer.AffineResult affineResult;
                try {
                    affineResult = LivestackMediator.GetImageTransformer().ComputeAffineTransformationWithResidual(stars, redTab.ReferenceStars);
                } catch (Exception ex) {
                    Logger.Warning($"[MultiNight] OSC alignment failed: {ex.GetType().Name}: {ex.Message}. Target stars={stars.Count}; Reference stars={redTab.ReferenceStars?.Count}; Target=\"{item.Target}\"; Frame=\"{item.Path}\"");
                    RecordRejectedFrame(item.Target, LiveStackBag.RED_OSC, "alignment_failed");
                    RecordRejectedFrame(item.Target, LiveStackBag.GREEN_OSC, "alignment_failed");
                    RecordRejectedFrame(item.Target, LiveStackBag.BLUE_OSC, "alignment_failed");
                    return;
                }
                affineTransformationMatrix = affineResult.Matrix;
                flipped = LivestackMediator.GetImageTransformer().IsFlippedImage(affineTransformationMatrix);
                if (flipped) {
                    // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                    stars = LivestackMediator.GetImageMath().Flip(stars, item.Width, item.Height);
                    try {
                        affineResult = LivestackMediator.GetImageTransformer().ComputeAffineTransformationWithResidual(stars, redTab.ReferenceStars);
                    } catch (Exception ex) {
                        Logger.Warning($"[MultiNight] OSC flipped alignment failed: {ex.GetType().Name}: {ex.Message}. Target=\"{item.Target}\"");
                        RecordRejectedFrame(item.Target, LiveStackBag.RED_OSC, "alignment_failed");
                        RecordRejectedFrame(item.Target, LiveStackBag.GREEN_OSC, "alignment_failed");
                        RecordRejectedFrame(item.Target, LiveStackBag.BLUE_OSC, "alignment_failed");
                        return;
                    }
                    affineTransformationMatrix = affineResult.Matrix;
                }

                // Layer 2: check affine residual before stacking (check on red channel only)
                var residual = affineResult.ResidualPixels;
                var threshold = LivestackMediator.Plugin.AffineResidualThresholdPixels;
                if (residual > threshold) {
                    Logger.Warning($"[MultiNight] OSC frame rejected: affine residual {residual:F1}px ({affineResult.MatchedStarCount} matched stars) exceeds threshold {threshold:F1}px — {item.Target}");
                    Notification.ShowWarning($"Live Stack - OSC frame rejected: alignment residual {residual:F1}px exceeds {threshold:F1}px threshold");
                    RecordRejectedFrame(item.Target, LiveStackBag.RED_OSC, "layer2_affine_residual");
                    RecordRejectedFrame(item.Target, LiveStackBag.GREEN_OSC, "layer2_affine_residual");
                    RecordRejectedFrame(item.Target, LiveStackBag.BLUE_OSC, "layer2_affine_residual");
                    return;
                }
                Logger.Info($"[MultiNight] OSC frame accepted: affine residual {residual:F1}px ({affineResult.MatchedStarCount} matched stars, threshold {threshold:F1}px) — {item.Target}");

                redTab.AddTransformedImage(debayeredImage.Data.Red, affineTransformationMatrix, flipped);
            }

            StatusUpdate("Aligning frame - green channel", item);
            var greenTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.GREEN_OSC && x.Target == item.Target) as LiveStackTab;
            if (greenTab == null) {
                var bag = new LiveStackBag(item.Target, LiveStackBag.GREEN_OSC, imageProperties, item.MetaData, stars);
                bag.Add(debayeredImage.Data.Green.ToFloatArray());
                greenTab = new LiveStackTab(profileService, bag);
                Tabs.Add(greenTab);
            } else if (pushedReference) {
                greenTab.ForcePushReference(imageProperties, stars, debayeredImage.Data.Green.ToFloatArray());
            } else {
                greenTab.AddTransformedImage(debayeredImage.Data.Green, affineTransformationMatrix, flipped);
            }

            StatusUpdate("Aligning frame - blue channel", item);
            var blueTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.BLUE_OSC && x.Target == item.Target) as LiveStackTab;
            if (blueTab == null) {
                var bag = new LiveStackBag(item.Target, LiveStackBag.BLUE_OSC, imageProperties, item.MetaData, stars);
                bag.Add(debayeredImage.Data.Blue.ToFloatArray());
                blueTab = new LiveStackTab(profileService, bag);
                Tabs.Add(blueTab);
            } else if (pushedReference) {
                blueTab.ForcePushReference(imageProperties, stars, debayeredImage.Data.Blue.ToFloatArray());
            } else {
                blueTab.AddTransformedImage(debayeredImage.Data.Blue, affineTransformationMatrix, flipped);
            }

            await redTab.Refresh(token);
            await greenTab.Refresh(token);
            await blueTab.Refresh(token);

            var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == item.Target).FirstOrDefault() as ColorCombinationTab;
            if (colorTab == null) {
                colorTab = new ColorCombinationTab(profileService, redTab, greenTab, blueTab, channelsAlreadyAligned: true);
                Tabs.Add(colorTab);
            }
            // Multi-night: record accepted frame for all OSC channels
            RecordAcceptedFrame(item.Target, LiveStackBag.RED_OSC, item.ExposureTime);
            RecordAcceptedFrame(item.Target, LiveStackBag.GREEN_OSC, item.ExposureTime);
            RecordAcceptedFrame(item.Target, LiveStackBag.BLUE_OSC, item.ExposureTime);

            if (LivestackMediator.Plugin.SaveStackedLights) {
                StatusUpdate("Saving stacks", item);
                redTab.SaveToDisk();
                greenTab.SaveToDisk();
                blueTab.SaveToDisk();
                SaveSidecar(item.Target, LiveStackBag.RED_OSC);
                SaveSidecar(item.Target, LiveStackBag.GREEN_OSC);
                SaveSidecar(item.Target, LiveStackBag.BLUE_OSC);
            }

            var (redExposure, redSessions) = GetSidecarStats(item.Target, LiveStackBag.RED_OSC);
            var (greenExposure, greenSessions) = GetSidecarStats(item.Target, LiveStackBag.GREEN_OSC);
            var (blueExposure, blueSessions) = GetSidecarStats(item.Target, LiveStackBag.BLUE_OSC);
            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(redTab.StackCount, redTab.Filter, redTab.Target, redTab.StackImage, redExposure, redSessions), correlation));
            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(greenTab.StackCount, greenTab.Filter, greenTab.Target, greenTab.StackImage, greenExposure, greenSessions), correlation));
            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(blueTab.StackCount, blueTab.Filter, blueTab.Target, blueTab.StackImage, blueExposure, blueSessions), correlation));
        }

        private async Task StackItem(LiveStackItem item, CancellationToken token) {
            var tab = GetOrCreateStackBag(item);
            tab.Locked = true;
            try {
                if (SelectedTab == null) {
                    SelectedTab = tab;
                }

                // Layer 1: plate solve validation (runs on first frame of resumed session)
                var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
                var filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
                if (item.IsBayered) { filter = LiveStackBag.RED_OSC; }
                if (!CheckLayer1PlatesolveValidation(item, target, filter)) {
                    return; // Layer 1 failed — resume aborted, will start fresh on next frame
                }

                // Store WCS in sidecar on first frame if not already present
                var sidecarKey = $"{target}-{filter}";
                if (activeSidecars.TryGetValue(sidecarKey, out var sidecarEntry) && sidecarEntry.sidecar.ReferenceWcs == null) {
                    TryStoreWcsFromFrame(item, sidecarEntry.sidecar, sidecarEntry.path, sidecarKey);
                }

                var calibratedFrame = CalibrateFrame(item);

                SaveCalibratedFrameIfNeeded(calibratedFrame, item);

                RemoveHotpixelsIfNeeded(calibratedFrame, item);

                Guid correlation = this.stackSessionId.Value;
                if (item.IsBayered) {
                    await StackOSC(calibratedFrame, item, tab, correlation, token);
                } else {
                    await StackMono(calibratedFrame, item, tab, correlation, token);
                }

                var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == tab.Target).FirstOrDefault() as ColorCombinationTab;
                if (colorTab != null) {
                    colorTab.MarkDirty();
                    if (ShouldRefreshColorTab(colorTab)) {
                        StatusUpdate("Refreshing color combined stack", item);
                        await colorTab.Refresh(token);

                        if (LivestackMediator.Plugin.SaveStackedLights) {
                            StatusUpdate("Saving color combined stack", item);
                            colorTab.AutoSaveToDisk();
                        }

                        _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Color(colorTab.StackCountRed, colorTab.StackCountGreen, colorTab.StackCountBlue, colorTab.Filter, colorTab.Target, colorTab.StackImage), correlation));
                    }
                }
            } finally {
                tab.Locked = false;
            }
        }

        private bool ShouldRefreshColorTab(ColorCombinationTab colorTab) {
            return ReferenceEquals(SelectedTab, colorTab)
                || colorTab.StackImage == null
                || LivestackMediator.Plugin.SaveStackedLights;
        }

        private float[] CalibrateFrame(LiveStackItem item) {
            StatusUpdate("Calibrating frame", item);
            using var calibrationManager = LivestackMediator.CreateCalibrationManager();
            RegisterCalibrationMasters(calibrationManager);
            float[] theImageArray;
            using (CFitsioFITSReader reader = new CFitsioFITSReader(item.Path)) {
                theImageArray = calibrationManager.ApplyLightFrameCalibrationInPlace(reader, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered);
            }
            return theImageArray;
        }

        private void SaveCalibratedFrameIfNeeded(float[] theImageArray, LiveStackItem item) {
            if (LivestackMediator.Plugin.SaveCalibratedLights) {
                var fileName = Path.GetFileNameWithoutExtension(item.Path) + "_c" + ".fits";

                var destinationFolder = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "calibrated", "light", item.Target, item.Filter);
                if (!Directory.Exists(destinationFolder)) {
                    Directory.CreateDirectory(destinationFolder);
                }
                var destinationFile = CoreUtil.GetUniqueFilePath(Path.Combine(destinationFolder, fileName), "{0}_{1}");

                StatusUpdate($"Saving calibrated light frame at {destinationFile}", item);
                var writer = new CFitsioFITSExtendedWriter(destinationFile, theImageArray, item.Width, item.Height);
                writer.PopulateHeaderCards(item.MetaData);
                writer.Close();
            }
        }

        private void RemoveHotpixelsIfNeeded(float[] theImageArray, LiveStackItem item) {
            if (LivestackMediator.Plugin.HotpixelRemoval) {
                StatusUpdate("Removing hot pixels in frame", item);
                LivestackMediator.GetImageMath().RemoveHotPixelOutliers(theImageArray, item.Width, item.Height);
            }
        }

        private void StatusUpdate(string status, LiveStackItem item) {
            if (!string.IsNullOrEmpty(status)) {
                Logger.Info($"{status} - {item.Path}");
            }
            applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = status });
        }

        private static int GetRawStarCount(LiveStackItem item) {
            return item.StarList?.Count ?? 0;
        }

        private static bool HasEnoughAlignmentStars(List<Accord.Point> stars) {
            return stars?.Count >= MinimumAffineStarCount;
        }

        private static void LogReferenceAccepted(string context, LiveStackItem item, int rawDetectedStars, int filteredReferenceStars) {
            Logger.Info($"Live Stack reference accepted ({context}). Raw detector stars={rawDetectedStars}; Filtered reference stars={filteredReferenceStars}; Required={MinimumAffineStarCount}; Target=\"{item.Target}\"; Filter=\"{item.Filter}\"; Frame=\"{item.Path}\"");
        }

        private static void LogSkippedForInsufficientAlignmentStars(string context, LiveStackItem item, int rawDetectedStars, int filteredAlignmentStars, int? referenceAlignmentStars) {
            string referenceStarMessage = referenceAlignmentStars.HasValue
                ? $"; Filtered reference stars={referenceAlignmentStars.Value}"
                : "; Reference stars=not set";
            Logger.Warning($"Live Stack skipping frame ({context}) because affine alignment needs at least {MinimumAffineStarCount} filtered stars on both sides. Raw detector stars={rawDetectedStars}; Filtered current-frame stars={filteredAlignmentStars}{referenceStarMessage}; Target=\"{item.Target}\"; Filter=\"{item.Filter}\"; Frame=\"{item.Path}\"");
        }

        private void RegisterCalibrationMasters(ICalibrationManager calibrationManager) {
            foreach (var meta in LivestackMediator.CalibrationVM.BiasLibrary) {
                calibrationManager.RegisterBiasMaster(meta);
            }
            foreach (var meta in LivestackMediator.CalibrationVM.DarkLibrary) {
                calibrationManager.RegisterDarkMaster(meta);
            }
            foreach (var meta in LivestackMediator.CalibrationVM.FlatLibrary) {
                calibrationManager.RegisterFlatMaster(meta);
            }
            foreach (var meta in LivestackMediator.CalibrationVM.SessionFlatLibrary) {
                calibrationManager.RegisterFlatMaster(meta);
            }
        }

        // Multi-night Layer 1: plate solve validation (per-session, on first frame of resume)

        /// <summary>
        /// Check Layer 1 plate solve validation for a resumed stack.
        /// Returns true if the frame passes or Layer 1 is not applicable.
        /// Returns false if the frame should be rejected (pointing too far off).
        /// </summary>
        private bool CheckLayer1PlatesolveValidation(LiveStackItem item, string target, string filter) {
            var key = $"{target}-{filter}";

            // Already validated for this session, skip
            if (layer1Validated.ContainsKey(key)) {
                return true;
            }

            // Not a resumed stack — no Layer 1 check needed
            if (!activeSidecars.TryGetValue(key, out var entry)) {
                layer1Validated[key] = true;
                return true;
            }

            var sidecar = entry.sidecar;

            // No reference WCS in sidecar — this is the first session ever, just store it
            if (sidecar.ReferenceWcs == null) {
                TryStoreWcsFromFrame(item, sidecar, entry.path, key);
                layer1Validated[key] = true;
                return true;
            }

            // Check if frame has WCS
            var wcs = item.MetaData.WorldCoordinateSystem;
            if (wcs == null) {
                // No WCS on frame — track retries
                var retries = layer1RetryCount.AddOrUpdate(key, 1, (_, count) => count + 1);
                var maxRetries = LivestackMediator.Plugin.PlatesolveRetryFrames;
                if (retries >= maxRetries) {
                    Logger.Warning($"[MultiNight] Layer 1 skipped: no WCS available after {retries} frames for {target}-{filter}");
                    layer1Validated[key] = true; // Skip Layer 1, proceed with Layer 2 only
                } else {
                    Logger.Info($"[MultiNight] Layer 1: no WCS on frame {retries}/{maxRetries}, will retry on next frame");
                }
                return true; // Don't reject the frame — just haven't validated yet
            }

            // Compute angular separation
            var frameRa = wcs.Coordinates.RADegrees;
            var frameDec = wcs.Coordinates.Dec;
            var refRa = sidecar.ReferenceWcs.Ra;
            var refDec = sidecar.ReferenceWcs.Dec;
            var separationArcmin = ComputeAngularSeparationArcmin(frameRa, frameDec, refRa, refDec);
            var threshold = LivestackMediator.Plugin.PlatesolveThresholdArcmin;

            if (separationArcmin > threshold) {
                Logger.Warning($"[MultiNight] Layer 1 failed: pointing offset {separationArcmin:F1} arcmin exceeds threshold {threshold:F1} arcmin for {target}-{filter} — starting fresh");
                Notification.ShowWarning($"Live Stack - Multi-night resume aborted: pointing offset {separationArcmin:F1}' exceeds {threshold:F1}' threshold");

                // Abort resume: archive existing stack and clear the tab
                // The next call to GetOrCreateStackBag will create a fresh one
                activeSidecars.TryRemove(key, out _);
                layer1Validated[key] = true;
                return false;
            }

            Logger.Info($"[MultiNight] Layer 1 passed: pointing offset {separationArcmin:F1} arcmin (threshold {threshold:F1}) for {target}-{filter}");
            layer1Validated[key] = true;
            return true;
        }

        private void TryStoreWcsFromFrame(LiveStackItem item, StackSidecar sidecar, string sidecarPath, string key) {
            var wcs = item.MetaData.WorldCoordinateSystem;
            if (wcs != null) {
                sidecar.ReferenceWcs = new WcsSolution {
                    Ra = wcs.Coordinates.RADegrees,
                    Dec = wcs.Coordinates.Dec,
                    Rotation = wcs.Rotation,
                    PixelScale = wcs.PixelScaleX,
                    SolvedAt = DateTime.UtcNow
                };
                sidecar.Save(sidecarPath);
                Logger.Info($"[MultiNight] Stored reference WCS: RA={wcs.Coordinates.RADegrees:F4} Dec={wcs.Coordinates.Dec:F4} Rot={wcs.Rotation:F1}");
            }
        }

        private static double ComputeAngularSeparationArcmin(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
            var ra1 = ra1Deg * Math.PI / 180.0;
            var dec1 = dec1Deg * Math.PI / 180.0;
            var ra2 = ra2Deg * Math.PI / 180.0;
            var dec2 = dec2Deg * Math.PI / 180.0;
            var cosD = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
            cosD = Math.Max(-1.0, Math.Min(1.0, cosD)); // Clamp for floating point safety
            var dRad = Math.Acos(cosD);
            return dRad * (180.0 / Math.PI) * 60.0; // Convert to arcminutes
        }

        // Multi-night sidecar helpers

        private void RecordAcceptedFrame(string target, string filter, double exposureSeconds) {
            var key = $"{target}-{filter}";
            if (activeSidecars.TryGetValue(key, out var entry)) {
                entry.sidecar.RecordAcceptedFrame(exposureSeconds);
            }
        }

        private void RecordRejectedFrame(string target, string filter, string reason) {
            var key = $"{target}-{filter}";
            if (activeSidecars.TryGetValue(key, out var entry)) {
                entry.sidecar.RecordRejectedFrame(reason);
            }
        }

        private (double? totalExposure, int? sessionCount) GetSidecarStats(string target, string filter) {
            var key = $"{target}-{filter}";
            if (activeSidecars.TryGetValue(key, out var entry)) {
                return (entry.sidecar.TotalExposureSeconds, entry.sidecar.Sessions.Count);
            }
            return (null, null);
        }

        private void SaveSidecar(string target, string filter) {
            var key = $"{target}-{filter}";
            if (activeSidecars.TryGetValue(key, out var entry)) {
                entry.sidecar.Save(entry.path);
                Logger.Debug($"[MultiNight] Sidecar saved: {target}-{filter}, {entry.sidecar.TotalFrames} frames, {entry.sidecar.TotalExposureSeconds:F0}s total");
            }
        }

        public void Dispose() {
        }

        public async Task OnMessageReceived(IMessage message) {
            if (message.Topic == $"Livestack_LivestackDockable_StartLiveStack") {
                if (LivestackMediator.LiveStackDockable.StartLiveStackCommand.IsRunning) {
                    return;
                }
                await Application.Current.Dispatcher.BeginInvoke(() => StartLiveStackCommand.ExecuteAsync(null));
            } else if (message.Topic == $"Livestack_LivestackDockable_StopLiveStack") {
                if (LivestackMediator.LiveStackDockable.StartLiveStackCommand.IsRunning) {
                    await Application.Current.Dispatcher.BeginInvoke(() => LivestackMediator.LiveStackDockable.StartLiveStackCancelCommand.Execute(null));
                }
            }
        }
    }

    public class LivestackBroadcast : IMessage {

        public LivestackBroadcast(object content, Guid correlation) {
            Content = content;
            CorrelationId = correlation;
        }

        public Guid SenderId => Guid.Parse(LivestackMediator.Plugin.Identifier);

        public string Sender => "Livestack";

        public DateTimeOffset SentAt => DateTimeOffset.UtcNow;

        public Guid MessageId => Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId { get; }

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        public string Topic => "Livestack_LivestackDockable_StackUpdateBroadcast";

        public object Content { get; }
    }

    public class LiveStackBroadcastContent {

        private LiveStackBroadcastContent(bool isMonochrome, int? stackCount, int? redStackCount, int? greenStackCount, int? blueStackCount, string filter, string target, BitmapSource image, double? totalExposureSeconds = null, int? sessionCount = null) {
            IsMonochrome = isMonochrome;
            StackCount = stackCount;
            RedStackCount = redStackCount;
            GreenStackCount = greenStackCount;
            BlueStackCount = blueStackCount;
            Filter = filter;
            Target = target;
            Image = image;
            TotalExposureSeconds = totalExposureSeconds;
            SessionCount = sessionCount;
        }

        public static LiveStackBroadcastContent Monochrome(int stackCount, string filter, string target, BitmapSource image, double? totalExposureSeconds = null, int? sessionCount = null) {
            return new LiveStackBroadcastContent(
                true,
                stackCount,
                null,
                null,
                null,
                filter,
                target,
                image,
                totalExposureSeconds,
                sessionCount
            );
        }

        public static LiveStackBroadcastContent Color(int redStackCount, int greenStackCount, int blueStackCount, string filter, string target, BitmapSource image) {
            return new LiveStackBroadcastContent(
                false,
                null,
                redStackCount,
                greenStackCount,
                blueStackCount,
                filter,
                target,
                image
            );
        }

        public bool IsMonochrome { get; }
        public int? StackCount { get; } // Only used for monochrome

        // Only used for color
        public int? RedStackCount { get; }

        public int? GreenStackCount { get; }
        public int? BlueStackCount { get; }

        public string Filter { get; }
        public string Target { get; }
        public BitmapSource Image { get; }

        // Multi-night: cumulative stats from sidecar (null if single-session)
        public double? TotalExposureSeconds { get; }
        public int? SessionCount { get; }
    }

    public class LiveStackStatusBroadcast : IMessage {

        public LiveStackStatusBroadcast(LiveStackStatus status, Guid correlation) {
            Content = status.ToString();
            CorrelationId = correlation;
        }

        public Guid SenderId => Guid.Parse(LivestackMediator.Plugin.Identifier);

        public string Sender => "Livestack";

        public DateTimeOffset SentAt => DateTimeOffset.UtcNow;

        public Guid MessageId => Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId { get; }

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        public string Topic => "Livestack_LivestackDockable_StatusBroadcast";

        public object Content { get; }
    }

    public enum LiveStackStatus {

        [Description("running")]
        Running,

        [Description("stopped")]
        Stopped
    }
}
