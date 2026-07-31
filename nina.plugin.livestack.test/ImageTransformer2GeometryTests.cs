using Accord;
using MathNet.Numerics.Statistics;
using NINA.Plugin.Livestack.Image;
using System;
using System.Collections.Generic;
using System.Linq;

namespace nina.plugin.livestack.test {

    /// <summary>
    /// Geometry tests for <see cref="ImageTransformer2"/> that run on a clean checkout.
    ///
    /// Deliberately separate from the <c>Tests</c> class: that one has a [SetUp] which opens FITS
    /// fixtures from nina.plugin.livestack.benchmark\BenchmarkData\, and those are not tracked in
    /// git, so every test in it fails at setup on a fresh clone. The alignment maths needs no
    /// fixtures and no plugin registration, so keeping it here means the star-matching safety rules
    /// are actually covered by a runnable test.
    /// </summary>
    public class ImageTransformer2GeometryTests {

        [Test]
        public void RejectsSpuriousIntermediateRotation() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateRandomStarField(count: 3000, width: 4200, height: 2800, seed: 151);

            // A self-consistent but physically implausible 35 degree rotation.
            double angleRad = 35.0 * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            double[,] spurious = new double[3, 3] {
                { cos, -sin, 300.0 },
                { sin, cos, -150.0 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, spurious, seed: 227, jitter: 0.22f);
            sourceStars.AddRange(CreateRandomStarField(count: 350, width: 4200, height: 2800, seed: 808));
            Shuffle(sourceStars, seed: 331);
            Shuffle(referenceStars, seed: 977);

            // Rejection may come from RANSAC finding no valid model at all, or from the final
            // plausibility guard; AffineRotationImplausibleException derives from
            // InvalidOperationException. What matters is that no transform is returned.
            Assert.That(
                () => transformer.ComputeAffineTransformation(sourceStars, referenceStars),
                Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void StillRecoversKnownTransformWithOutliers() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateSyntheticReferenceStars();

            double[,] expected = new double[3, 3] {
                { 0.9985, 0.0175, -12.75 },
                { -0.0145, 1.0015, 9.25 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 41, jitter: 0.35f);
            sourceStars.AddRange(new[] {
                new Point(75, 70),
                new Point(1490, 100),
                new Point(1510, 1120),
                new Point(210, 1050),
                new Point(820, 610),
                new Point(1280, 520)
            });
            Shuffle(sourceStars, seed: 99);

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.02, translationTol: 1.2);
            Assert.That(ComputeMedianResidual(referenceStars, ApplyAffine(referenceStars, expected), actual), Is.LessThan(1.2));
        }

        [Test]
        public void StillRecoversMeridianFlipLikeRotation() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateSyntheticReferenceStars();

            double[,] expected = new double[3, 3] {
                { -1.0, 0.0, 1600.0 },
                { 0.0, -1.0, 1200.0 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 11, jitter: 0.2f);
            sourceStars.AddRange(new[] {
                new Point(40, 100),
                new Point(1540, 180),
                new Point(1470, 1090),
                new Point(300, 1110)
            });

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.02, translationTol: 1.0);
            Assert.That(transformer.IsFlippedImage(actual), Is.True);
        }

        [Test]
        public void DenseFieldStillRecoversMeridianFlipLikeRotation() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateRandomStarField(count: 3000, width: 4200, height: 2800, seed: 151);

            double[,] expected = new double[3, 3] {
                { -1.0, 0.0, 4200.0 },
                { 0.0, -1.0, 2800.0 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 227, jitter: 0.22f);
            sourceStars.AddRange(CreateRandomStarField(count: 350, width: 4200, height: 2800, seed: 808));
            Shuffle(sourceStars, seed: 331);
            Shuffle(referenceStars, seed: 977);

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.01, translationTol: 1.0);
            Assert.That(transformer.IsFlippedImage(actual), Is.True);
        }




        [Test]
        public void RejectsMirroredModel() {
            // A reflection: positive x scale, negative y scale. Two frames of the same target can
            // differ by a rotation (0 or 180 degrees) but never by a mirror, so this must be
            // rejected. Its first row alone reads as a small rotation and |determinant| is ~1, so
            // it passed the pre-2026-07-30 checks and got stacked.
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateRandomStarField(count: 1200, width: 4200, height: 2800, seed: 604);

            double[,] mirrored = new double[3, 3] {
                { 0.9934, 0.1748, -1135.59 },
                { 0.1567, -1.0093, 4043.15 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, mirrored, seed: 71, jitter: 0.2f);
            Shuffle(sourceStars, seed: 17);
            Shuffle(referenceStars, seed: 23);

            Assert.That(
                () => transformer.ComputeAffineTransformation(sourceStars, referenceStars),
                Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void RejectsModelThatExplainsAlmostNoStars() {
            // Two unrelated star fields. There is no correct answer here, so the matcher must fail
            // loudly rather than return whatever coincidence scored best.
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateRandomStarField(count: 900, width: 4200, height: 2800, seed: 3001);
            var sourceStars = CreateRandomStarField(count: 900, width: 4200, height: 2800, seed: 9002);

            Assert.That(
                () => transformer.ComputeAffineTransformation(sourceStars, referenceStars),
                Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void AcceptsSmallDriftBetweenConsecutiveFrames() {
            // The ordinary case: a couple of pixels of drift and a fraction of a degree of rotation.
            // Guards against the plausibility rules being tightened into rejecting real frames.
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateRandomStarField(count: 1500, width: 4200, height: 2800, seed: 555);

            double angleRad = 0.4 * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            double[,] expected = new double[3, 3] {
                { cos, -sin, 3.5 },
                { sin, cos, -2.25 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 88, jitter: 0.2f);
            Shuffle(sourceStars, seed: 41);
            Shuffle(referenceStars, seed: 67);

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.02, translationTol: 1.5);
            Assert.That(transformer.IsFlippedImage(actual), Is.False);
        }

        private static List<Point> CreateSyntheticReferenceStars() {
            return new List<Point> {
                new Point(120, 140), new Point(420, 150), new Point(760, 130),
                new Point(1110, 180), new Point(1420, 160), new Point(160, 360),
                new Point(510, 390), new Point(840, 345), new Point(1190, 410),
                new Point(1450, 360), new Point(110, 650), new Point(470, 700),
                new Point(760, 660), new Point(1090, 720), new Point(1430, 690),
                new Point(180, 980), new Point(520, 1020), new Point(910, 960),
                new Point(1260, 1040), new Point(1460, 980)
            };
        }

        private static List<Point> CreateRandomStarField(int count, int width, int height, int seed) {
            Random random = new Random(seed);
            var stars = new List<Point>(count);
            for (int i = 0; i < count; i++) {
                float x = 20f + ((float)random.NextDouble() * (width - 40f));
                float y = 20f + ((float)random.NextDouble() * (height - 40f));
                stars.Add(new Point(x, y));
            }
            return stars;
        }

        private static List<Point> ApplyAffine(IEnumerable<Point> points, double[,] matrix, int seed = 0, float jitter = 0f) {
            Random random = new Random(seed);
            var transformed = new List<Point>();
            foreach (var point in points) {
                float x = (float)((matrix[0, 0] * point.X) + (matrix[0, 1] * point.Y) + matrix[0, 2]);
                float y = (float)((matrix[1, 0] * point.X) + (matrix[1, 1] * point.Y) + matrix[1, 2]);
                if (jitter > 0f) {
                    x += (((float)random.NextDouble() * 2f) - 1f) * jitter;
                    y += (((float)random.NextDouble() * 2f) - 1f) * jitter;
                }
                transformed.Add(new Point(x, y));
            }
            return transformed;
        }

        private static void Shuffle<T>(IList<T> list, int seed) {
            Random random = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--) {
                int swapIndex = random.Next(i + 1);
                (list[i], list[swapIndex]) = (list[swapIndex], list[i]);
            }
        }

        private static void AssertAffineClose(double[,] expected, double[,] actual, double matrixTol, double translationTol) {
            Assert.That(actual[0, 0], Is.EqualTo(expected[0, 0]).Within(matrixTol));
            Assert.That(actual[0, 1], Is.EqualTo(expected[0, 1]).Within(matrixTol));
            Assert.That(actual[1, 0], Is.EqualTo(expected[1, 0]).Within(matrixTol));
            Assert.That(actual[1, 1], Is.EqualTo(expected[1, 1]).Within(matrixTol));
            Assert.That(actual[0, 2], Is.EqualTo(expected[0, 2]).Within(translationTol));
            Assert.That(actual[1, 2], Is.EqualTo(expected[1, 2]).Within(translationTol));
        }

        private static double ComputeMedianResidual(IReadOnlyList<Point> referenceStars, IReadOnlyList<Point> sourceStars, double[,] matrix) {
            var residuals = new double[referenceStars.Count];
            for (int i = 0; i < referenceStars.Count; i++) {
                double projectedX = (matrix[0, 0] * referenceStars[i].X) + (matrix[0, 1] * referenceStars[i].Y) + matrix[0, 2];
                double projectedY = (matrix[1, 0] * referenceStars[i].X) + (matrix[1, 1] * referenceStars[i].Y) + matrix[1, 2];
                double dx = projectedX - sourceStars[i].X;
                double dy = projectedY - sourceStars[i].Y;
                residuals[i] = Math.Sqrt((dx * dx) + (dy * dy));
            }
            return residuals.Median();
        }
    }
}
