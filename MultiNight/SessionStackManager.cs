using NINA.Core.Utility;
using NINA.Plugin.Livestack.Image;
using System;
using System.IO;

namespace NINA.Plugin.Livestack.MultiNight {

    /// <summary>
    /// Manages per-session stack file paths and resume logic.
    /// Session FITS files are dated (e.g. "M101-Ha-2026-04-24.fits") and serve as
    /// per-night checkpoints alongside the cumulative multi-night stack.
    /// </summary>
    public class SessionStackManager {

        public class SessionResumeResult {
            public float[] Stack { get; set; }
            public int ImageCount { get; set; }
        }

        public static string GetSessionDateSuffix() {
            return DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        public static string GetSessionFilePath(string target, string filter, string dateSuffix) {
            var destinationFolder = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "stacks");
            return Path.Combine(destinationFolder, CoreUtil.ReplaceAllInvalidFilenameChars($"{target}-{filter}-{dateSuffix}.fits"));
        }

        /// <summary>
        /// Try to load an existing session FITS for today. Used to recover from a mid-session restart.
        /// Returns null if no compatible file exists.
        /// </summary>
        public static SessionResumeResult TryResume(string target, string filter, int width, int height, string dateSuffix) {
            var fitsPath = GetSessionFilePath(target, filter, dateSuffix);
            if (!File.Exists(fitsPath)) {
                return null;
            }

            try {
                using (var reader = new CFitsioFITSReader(fitsPath)) {
                    if (reader.Width != width || reader.Height != height) {
                        Logger.Warning($"[SessionStack] Dimension mismatch on resume: stored {reader.Width}x{reader.Height}, current {width}x{height} — starting fresh");
                        return null;
                    }

                    var imgCount = reader.TryReadLongHeader("IMGCOUNT");
                    if (!imgCount.HasValue || imgCount.Value <= 0) {
                        Logger.Warning($"[SessionStack] IMGCOUNT missing or zero in {fitsPath} — starting fresh");
                        return null;
                    }

                    var stack = reader.ReadAllPixelsAsFloat();
                    Logger.Info($"[SessionStack] Resumed today's session for {target}-{filter}: {imgCount.Value} frames");

                    return new SessionResumeResult {
                        Stack = stack,
                        ImageCount = (int)imgCount.Value
                    };
                }
            } catch (Exception ex) {
                Logger.Error($"[SessionStack] Failed to resume session from {fitsPath}: {ex.Message}");
                return null;
            }
        }
    }
}
