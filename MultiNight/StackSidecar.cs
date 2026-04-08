using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Plugin.Livestack.MultiNight {

    public class StackSidecar {
        [JsonPropertyName("target")]
        public string Target { get; set; }

        [JsonPropertyName("filter")]
        public string Filter { get; set; }

        [JsonPropertyName("totalFrames")]
        public int TotalFrames { get; set; }

        [JsonPropertyName("totalExposureSeconds")]
        public double TotalExposureSeconds { get; set; }

        [JsonPropertyName("sessions")]
        public List<SessionEntry> Sessions { get; set; } = new List<SessionEntry>();

        [JsonPropertyName("referenceWcs")]
        public WcsSolution ReferenceWcs { get; set; }

        [JsonPropertyName("referenceStars")]
        public List<StarPosition> ReferenceStars { get; set; } = new List<StarPosition>();

        [JsonPropertyName("dimensions")]
        public ImageDimensions Dimensions { get; set; }

        [JsonPropertyName("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        [JsonPropertyName("lastUpdatedUtc")]
        public DateTime LastUpdatedUtc { get; set; }

        /// <summary>
        /// Get the sidecar file path for a given FITS stack file path.
        /// Replaces .fits extension with .json.
        /// </summary>
        public static string GetSidecarPath(string fitsPath) {
            return Path.ChangeExtension(fitsPath, ".json");
        }

        /// <summary>
        /// Load a sidecar from disk. Returns null if the file does not exist or is corrupt.
        /// </summary>
        public static StackSidecar Load(string sidecarPath) {
            try {
                if (!File.Exists(sidecarPath)) {
                    return null;
                }
                var json = File.ReadAllText(sidecarPath);
                return JsonSerializer.Deserialize<StackSidecar>(json, SerializerOptions);
            } catch (Exception ex) {
                Logger.Warning($"[MultiNight] Failed to load sidecar {sidecarPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save sidecar to disk atomically (temp file + rename).
        /// </summary>
        public void Save(string sidecarPath) {
            var tempPath = sidecarPath + ".tmp";
            try {
                var json = JsonSerializer.Serialize(this, SerializerOptions);

                if (File.Exists(tempPath)) {
                    File.Delete(tempPath);
                }
                File.WriteAllText(tempPath, json);

                if (File.Exists(sidecarPath)) {
                    File.Delete(sidecarPath);
                }
                File.Move(tempPath, sidecarPath);
            } catch (Exception ex) {
                Logger.Error($"[MultiNight] Failed to save sidecar {sidecarPath}: {ex.Message}");
                // Clean up temp file if it exists
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>
        /// Create a new sidecar for a fresh stack.
        /// </summary>
        public static StackSidecar CreateNew(string target, string filter, int width, int height, int binning) {
            return new StackSidecar {
                Target = target,
                Filter = filter,
                TotalFrames = 0,
                TotalExposureSeconds = 0,
                Sessions = new List<SessionEntry>(),
                ReferenceStars = new List<StarPosition>(),
                Dimensions = new ImageDimensions { Width = width, Height = height, Binning = binning },
                CreatedUtc = DateTime.UtcNow,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Get or create the session entry for today's date.
        /// </summary>
        public SessionEntry GetOrCreateTodaySession() {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var session = Sessions.Find(s => s.Date == today);
            if (session == null) {
                session = new SessionEntry { Date = today };
                Sessions.Add(session);
            }
            return session;
        }

        /// <summary>
        /// Record an accepted frame in the sidecar.
        /// </summary>
        public void RecordAcceptedFrame(double exposureSeconds) {
            TotalFrames++;
            TotalExposureSeconds += exposureSeconds;
            LastUpdatedUtc = DateTime.UtcNow;

            var session = GetOrCreateTodaySession();
            session.Frames++;
            session.ExposureSeconds += exposureSeconds;
        }

        /// <summary>
        /// Record a rejected frame in the sidecar.
        /// </summary>
        public void RecordRejectedFrame(string reason) {
            LastUpdatedUtc = DateTime.UtcNow;

            var session = GetOrCreateTodaySession();
            session.RejectedFrames++;
            if (!session.RejectionReasons.Contains(reason)) {
                session.RejectionReasons.Add(reason);
            }
        }

        /// <summary>
        /// Sync totalFrames with the authoritative IMGCOUNT from the FITS header.
        /// Logs a warning if they disagree.
        /// </summary>
        public void SyncWithFitsImgCount(int fitsImgCount) {
            if (TotalFrames != fitsImgCount) {
                Logger.Warning($"[MultiNight] Sidecar IMGCOUNT mismatch: FITS={fitsImgCount}, sidecar={TotalFrames} — trusting FITS header");
                TotalFrames = fitsImgCount;
            }
        }

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public class SessionEntry {
        [JsonPropertyName("date")]
        public string Date { get; set; }

        [JsonPropertyName("frames")]
        public int Frames { get; set; }

        [JsonPropertyName("exposureSeconds")]
        public double ExposureSeconds { get; set; }

        [JsonPropertyName("rejectedFrames")]
        public int RejectedFrames { get; set; }

        [JsonPropertyName("rejectionReasons")]
        public List<string> RejectionReasons { get; set; } = new List<string>();
    }

    public class WcsSolution {
        [JsonPropertyName("ra")]
        public double Ra { get; set; }

        [JsonPropertyName("dec")]
        public double Dec { get; set; }

        [JsonPropertyName("rotation")]
        public double Rotation { get; set; }

        [JsonPropertyName("pixelScale")]
        public double PixelScale { get; set; }

        [JsonPropertyName("solvedAt")]
        public DateTime SolvedAt { get; set; }
    }

    public class StarPosition {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }
    }

    public class ImageDimensions {
        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("binning")]
        public int Binning { get; set; }
    }
}
