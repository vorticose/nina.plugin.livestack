using Accord;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.Plugin.Livestack.MultiNight {

    /// <summary>
    /// Manages multi-night stack resume logic.
    /// Handles loading existing FITS stacks, validating dimensions,
    /// loading/creating sidecars, and restoring reference stars.
    /// </summary>
    public class MultiNightManager {

        /// <summary>
        /// Result of a resume attempt.
        /// </summary>
        public class ResumeResult {
            public float[] Stack { get; set; }
            public int ImageCount { get; set; }
            public List<Accord.Point> ReferenceStars { get; set; }
            public StackSidecar Sidecar { get; set; }
            public string SidecarPath { get; set; }
        }

        /// <summary>
        /// Attempt to resume a stack for the given (target, filter) pair.
        /// Returns a ResumeResult if an existing stack was found and is compatible,
        /// or null if no stack exists or resume is not possible.
        /// </summary>
        public static ResumeResult TryResume(string target, string filter, int width, int height, int binning) {
            if (!LivestackMediator.Plugin.MultiNightMode) {
                return null;
            }

            var fitsPath = GetStackFilePath(target, filter);
            if (!File.Exists(fitsPath)) {
                Logger.Info($"[MultiNight] No existing stack for {target}-{filter} at {fitsPath} — starting fresh");
                return null;
            }

            var sidecarPath = StackSidecar.GetSidecarPath(fitsPath);

            try {
                // Load the FITS stack
                var fileSize = new FileInfo(fitsPath).Length;
                Logger.Info($"[MultiNight] Found existing stack at {fitsPath} ({fileSize / 1024.0 / 1024.0:F1}MB), attempting resume");

                float[] stack;
                int fitsWidth, fitsHeight;
                int imageCount;

                using (var reader = new CFitsioFITSReader(fitsPath)) {
                    fitsWidth = reader.Width;
                    fitsHeight = reader.Height;

                    // Dimension compatibility check
                    if (fitsWidth != width || fitsHeight != height) {
                        Logger.Warning($"[MultiNight] Dimension mismatch on resume: stored {fitsWidth}x{fitsHeight}, current {width}x{height} — starting fresh");
                        ArchiveStack(fitsPath, sidecarPath, target, filter);
                        return null;
                    }

                    // Read IMGCOUNT from FITS header
                    var imgCount = reader.TryReadLongHeader("IMGCOUNT");
                    imageCount = imgCount.HasValue ? (int)imgCount.Value : 0;

                    if (imageCount <= 0) {
                        Logger.Warning($"[MultiNight] IMGCOUNT is {imageCount} in {fitsPath} — cannot resume, starting fresh");
                        return null;
                    }

                    // Load the pixel data
                    stack = reader.ReadAllPixelsAsFloat();
                }

                // Load or create sidecar
                var sidecar = StackSidecar.Load(sidecarPath);
                if (sidecar == null) {
                    Logger.Info($"[MultiNight] No sidecar found, creating skeleton from FITS header");
                    sidecar = StackSidecar.CreateNew(target, filter, width, height, binning);
                    sidecar.TotalFrames = imageCount;
                }

                // Sync sidecar with authoritative FITS IMGCOUNT
                sidecar.SyncWithFitsImgCount(imageCount);

                // Load reference stars from sidecar
                List<Accord.Point> referenceStars = null;
                if (sidecar.ReferenceStars != null && sidecar.ReferenceStars.Count > 0) {
                    referenceStars = sidecar.ReferenceStars
                        .Select(s => new Accord.Point((float)s.X, (float)s.Y))
                        .ToList();
                    Logger.Info($"[MultiNight] Loaded {referenceStars.Count} reference stars from sidecar");
                }

                var totalHours = sidecar.TotalExposureSeconds / 3600.0;
                var lastDate = sidecar.LastUpdatedUtc.ToString("yyyy-MM-dd");
                Logger.Info($"[MultiNight] Resuming {target}-{filter}: {imageCount} frames, {totalHours:F1}h total (last session: {lastDate})");

                return new ResumeResult {
                    Stack = stack,
                    ImageCount = imageCount,
                    ReferenceStars = referenceStars,
                    Sidecar = sidecar,
                    SidecarPath = sidecarPath
                };
            } catch (Exception ex) {
                Logger.Error($"[MultiNight] Failed to resume stack from {fitsPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a fresh sidecar for a new stack.
        /// </summary>
        public static (StackSidecar sidecar, string path) CreateFreshSidecar(string target, string filter, int width, int height, int binning) {
            var fitsPath = GetStackFilePath(target, filter);
            var sidecarPath = StackSidecar.GetSidecarPath(fitsPath);
            var sidecar = StackSidecar.CreateNew(target, filter, width, height, binning);
            return (sidecar, sidecarPath);
        }

        /// <summary>
        /// Store reference stars in the sidecar (called after first frame sets the reference).
        /// </summary>
        public static void SaveReferenceStars(StackSidecar sidecar, List<Accord.Point> stars) {
            if (stars == null) return;
            sidecar.ReferenceStars = stars
                .Select(s => new StarPosition { X = s.X, Y = s.Y })
                .ToList();
        }

        /// <summary>
        /// Archive an existing stack file by renaming it with a date suffix.
        /// </summary>
        private static void ArchiveStack(string fitsPath, string sidecarPath, string target, string filter) {
            var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var dir = Path.GetDirectoryName(fitsPath);
            var archiveFits = Path.Combine(dir, CoreUtil.ReplaceAllInvalidFilenameChars($"{target}-{filter}-archived-{date}.fits"));
            var archiveSidecar = Path.ChangeExtension(archiveFits, ".json");

            try {
                if (File.Exists(fitsPath)) {
                    // Don't overwrite an existing archive
                    archiveFits = CoreUtil.GetUniqueFilePath(archiveFits, "{0}_{1}");
                    File.Move(fitsPath, archiveFits);
                    Logger.Info($"[MultiNight] Stack reset: archived to {Path.GetFileName(archiveFits)}");
                }
                if (File.Exists(sidecarPath)) {
                    archiveSidecar = Path.ChangeExtension(archiveFits, ".json");
                    File.Move(sidecarPath, archiveSidecar);
                }
            } catch (Exception ex) {
                Logger.Error($"[MultiNight] Failed to archive stack: {ex.Message}");
            }
        }

        private static string GetStackFilePath(string target, string filter) {
            var destinationFolder = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "stacks");
            return Path.Combine(destinationFolder, CoreUtil.ReplaceAllInvalidFilenameChars($"{target}-{filter}.fits"));
        }
    }
}
