using System;

namespace CanardConfit.NINA.BahtiFocus.Bahtinov {
    public static class Bahtinov {
        public readonly struct Point2D {
            public Point2D(double x, double y) {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }

            public Point2D Scale(double ratioX, double ratioY) {
                return new Point2D(X * ratioX, Y * ratioY);
            }
        }

        public sealed class Ellipse {
            public Ellipse(Point2D start, int width, int height) {
                Start = start;
                Width = width;
                Height = height;
            }

            public Point2D Start { get; }
            public int Width { get; }
            public int Height { get; }

            public Ellipse Scale(double ratioX, double ratioY) {
                return new Ellipse(Start.Scale(ratioX, ratioY), (int)(Width * ratioX), (int)(Height * ratioY));
            }
        }

        public sealed class Line {
            public Line(Point2D p1, Point2D p2) {
                P1 = p1;
                P2 = p2;
            }

            public Point2D P1 { get; }
            public Point2D P2 { get; }

            public Line Scale(double ratioX, double ratioY) {
                return new Line(P1.Scale(ratioX, ratioY), P2.Scale(ratioX, ratioY));
            }
        }

        public sealed class BahtinovCalc {
            public Line LineRight { get; set; }
            public Line LineMiddle { get; set; }
            public Line LineLeft { get; set; }
            public Ellipse EllipseIntersection { get; set; }
            public Ellipse EllipseError { get; set; }
            public Line LineError { get; set; }
            public float FocusError { get; set; }
            public float MaskAngle { get; set; }
            public float Angles1 { get; set; }
            public float Angles2 { get; set; }
            public float Angles3 { get; set; }
            public float AbsoluteFocusError { get; set; }
            public float CriticalFocusThreshold { get; set; }
            public bool CriticalFocus { get; set; }
            public Ellipse[] EllipseCritFocus { get; set; }

            public void Scale(double ratioX, double ratioY) {
                LineRight = LineRight?.Scale(ratioX, ratioY);
                LineMiddle = LineMiddle?.Scale(ratioX, ratioY);
                LineLeft = LineLeft?.Scale(ratioX, ratioY);
                EllipseIntersection = EllipseIntersection?.Scale(ratioX, ratioY);
                EllipseError = EllipseError?.Scale(ratioX, ratioY);
                LineError = LineError?.Scale(ratioX, ratioY);
                if (EllipseCritFocus != null) {
                    for (int i = 0; i < EllipseCritFocus.Length; i++) {
                        EllipseCritFocus[i] = EllipseCritFocus[i].Scale(ratioX, ratioY);
                    }
                }
            }
        }

        public static BahtinovCalc CalculateLines(float[,] intensity, ref float[] bahtinovAngles, double diameter, double focalLength, double pixelSize) {
            if (intensity == null) {
                throw new ArgumentNullException(nameof(intensity));
            }

            if (bahtinovAngles == null || bahtinovAngles.Length != 3) {
                throw new ArgumentException("Bahtinov angles array must contain three elements.", nameof(bahtinovAngles));
            }

            int width = intensity.GetLength(0);
            int height = intensity.GetLength(1);

            if (width <= 1 || height <= 1) {
                throw new ArgumentException("Image must be at least 2x2 pixels.", nameof(intensity));
            }

            BahtinovCalc ret = new();

            var effectiveSize = (width < height ? 0.5f * (float)Math.Sqrt(2.0) * width : 0.5f * (float)Math.Sqrt(2.0) * height) - 8f;
            var startX = (int)(0.5 * (width - (double)effectiveSize));
            var endX = (int)(0.5 * (width + (double)effectiveSize));
            var startY = (int)(0.5 * (height - (double)effectiveSize));
            var endY = (int)(0.5 * (height + (double)effectiveSize));

            startX = Math.Max(startX, 0);
            startY = Math.Max(startY, 0);
            endX = Math.Min(endX, width);
            endY = Math.Min(endY, height);

            var stepX = 1;
            var stepY = stepX;

            var pixelIntensityArray = new float[width, height];
            for (var x = startX; x < endX; ++x) {
                for (var y = startY; y < endY; ++y) {
                    float value = intensity[x, y];
                    value = Math.Clamp(value, 0f, 1f);
                    pixelIntensityArray[x, y] = (float)Math.Sqrt(value);
                }
            }

            var centerX = (float)((width + 1.0) / 2.0);
            var centerY = (float)((height + 1.0) / 2.0);

            var bahtinovAngleArray = new float[3];
            var bahtinovPositionArray = new float[3];

            var angle0Slope = 0.0f;
            var angle0Intercept = 0.0f;

            var angle1X1 = 0.0f;
            var angle1X2 = 0.0f;
            var angle1Y1 = 0.0f;
            var angle1Y2 = 0.0f;

            var angle2Slope = 0.0f;
            var angle2Intercept = 0.0f;

            if (bahtinovAngles[0] == 0.0 && bahtinovAngles[1] == 0.0 && bahtinovAngles[2] == 0.0) {
                var angleStepCount = 180;
                var angleStep = (float)Math.PI / angleStepCount;
                var angleMaxIntensityArray = new float[angleStepCount];
                var anglePositionArray = new float[angleStepCount];
                var interpolatedIntensityArray = new float[width, height];

                for (var angleIndex = 0; angleIndex < angleStepCount; ++angleIndex) {
                    var currentAngle = angleStep * angleIndex;
                    var sinAngle = (float)Math.Sin(currentAngle);
                    var cosAngle = (float)Math.Cos(currentAngle);

                    for (var x = startX; x < endX; x += stepX) {
                        for (var y = startY; y < endY; y += stepY) {
                            var rotateDeltaX = x - centerX;
                            var rotateDeltaY = y - centerY;

                            var rotatedX = (float)(centerX + rotateDeltaX * (double)cosAngle + rotateDeltaY * (double)sinAngle);
                            var rotatedY = (float)(centerY - rotateDeltaX * (double)sinAngle + rotateDeltaY * (double)cosAngle);

                            var floorX = Clamp((int)Math.Floor(rotatedX), startX, endX - 1);
                            var ceilX = Clamp((int)Math.Ceiling(rotatedX), startX, endX - 1);
                            var floorY = Clamp((int)Math.Floor(rotatedY), startY, endY - 1);
                            var ceilY = Clamp((int)Math.Ceiling(rotatedY), startY, endY - 1);

                            var fracX = rotatedX - floorX;
                            var fracY = rotatedY - floorY;

                            interpolatedIntensityArray[x, y] = (float)(pixelIntensityArray[floorX, floorY] * (1.0 - fracX) * (1.0 - fracY) +
                                                                      pixelIntensityArray[ceilX, floorY] * (double)fracX * (1.0 - fracY) +
                                                                      pixelIntensityArray[ceilX, ceilY] * (double)fracX * fracY +
                                                                      pixelIntensityArray[floorX, ceilY] * (1.0 - fracX) * fracY);
                        }
                    }

                    var verticalSumArray = new float[height];
                    for (var y = 0; y < height; ++y)
                        verticalSumArray[y] = 0.0f;

                    for (var y = startY; y < endY; y += stepY) {
                        var sumCount = 0;
                        for (var x = startX; x < endX; x += stepX) {
                            verticalSumArray[y] += interpolatedIntensityArray[x, y];
                            ++sumCount;
                        }

                        if (sumCount > 0) {
                            verticalSumArray[y] /= sumCount;
                        }
                    }

                    var smoothedArray = new float[height];
                    for (var y = 0; y < height; ++y)
                        smoothedArray[y] = verticalSumArray[y];

                    for (var y = startY; y < endY; ++y)
                        smoothedArray[y] = verticalSumArray[y];

                    for (var y = 0; y < height; ++y)
                        verticalSumArray[y] = smoothedArray[y];

                    var maxValue = -1f;
                    var maxIndex = -1f;
                    for (var y = startY; y < endY; ++y) {
                        if (verticalSumArray[y] > (double)maxValue) {
                            maxIndex = y;
                            maxValue = verticalSumArray[y];
                        }
                    }

                    anglePositionArray[angleIndex] = maxIndex;
                    angleMaxIntensityArray[angleIndex] = maxValue;
                }

                var angleCount = 0;
                for (var i = 0; i < 3; ++i) {
                    var maxAngleValue = -1f;
                    var maxAngleIndex = -1f;
                    var maxAnglePosition = -1f;

                    for (var angleIndex = 0; angleIndex < angleStepCount; ++angleIndex) {
                        if (angleMaxIntensityArray[angleIndex] > (double)maxAngleValue) {
                            maxAngleValue = angleMaxIntensityArray[angleIndex];
                            maxAnglePosition = anglePositionArray[angleIndex];
                            maxAngleIndex = angleIndex * angleStep;
                            angleCount = angleIndex;
                        }
                    }

                    bahtinovPositionArray[i] = maxAnglePosition;
                    bahtinovAngleArray[i] = maxAngleIndex;
                    bahtinovAngles[i] = maxAngleIndex;

                    var angleStepRange = (int)(0.0872664600610733 / angleStep);
                    for (var angleIndex = angleCount - angleStepRange; angleIndex < angleCount + angleStepRange; ++angleIndex) {
                        var index = (angleIndex + angleStepCount) % angleStepCount;
                        angleMaxIntensityArray[index] = 0.0f;
                    }
                }
            } else {
                var angleCount = 3;
                var peakPositions = new float[angleCount];
                var peakIntensities = new float[angleCount];
                var interpolatedArray = new float[width, height];

                for (var i = 0; i < angleCount; ++i) {
                    var currentAngle = bahtinovAngles[i];
                    var sinAngle = (float)Math.Sin(currentAngle);
                    var cosAngle = (float)Math.Cos(currentAngle);

                    for (var x = startX; x < endX; x += stepX) {
                        for (var y = startY; y < endY; y += stepY) {
                            var rotateDeltaX = x - centerX;
                            var rotateDeltaY = y - centerY;

                            var rotatedX = (float)(centerX + rotateDeltaX * (double)cosAngle + rotateDeltaY * (double)sinAngle);
                            var rotatedY = (float)(centerY - rotateDeltaX * (double)sinAngle + rotateDeltaY * (double)cosAngle);

                            var floorX = Clamp((int)Math.Floor(rotatedX), startX, endX - 1);
                            var ceilX = Clamp((int)Math.Ceiling(rotatedX), startX, endX - 1);
                            var floorY = Clamp((int)Math.Floor(rotatedY), startY, endY - 1);
                            var ceilY = Clamp((int)Math.Ceiling(rotatedY), startY, endY - 1);

                            var fracX = rotatedX - floorX;
                            var fracY = rotatedY - floorY;

                            interpolatedArray[x, y] = (float)(pixelIntensityArray[floorX, floorY] * (1.0 - fracX) * (1.0 - fracY) +
                                                              pixelIntensityArray[ceilX, floorY] * (double)fracX * (1.0 - fracY) +
                                                              pixelIntensityArray[ceilX, ceilY] * (double)fracX * fracY +
                                                              pixelIntensityArray[floorX, ceilY] * (1.0 - fracX) * fracY);
                        }
                    }

                    var yvals = new float[height];
                    for (var y = 0; y < height; ++y)
                        yvals[y] = 0.0f;

                    for (var y = startY; y < endY; y += stepY) {
                        var sumCount = 0;
                        for (var x = startX; x < endX; x += stepX) {
                            yvals[y] += interpolatedArray[x, y];
                            ++sumCount;
                        }

                        if (sumCount > 0) {
                            yvals[y] /= sumCount;
                        }
                    }

                    var maxValue = -1f;
                    var estimatedPos = -1f;
                    for (var y = startY; y < endY; ++y) {
                        if (yvals[y] > (double)maxValue) {
                            estimatedPos = y;
                            maxValue = yvals[y];
                        }
                    }

                    var peakPosition = new LsqCalculator().PeakPosition(yvals, (int)estimatedPos, 2);
                    peakPositions[i] = peakPosition;
                    peakIntensities[i] = maxValue;
                }

                for (var i = 0; i < 3; ++i) {
                    bahtinovPositionArray[i] = peakPositions[i];
                    bahtinovAngleArray[i] = bahtinovAngles[i];
                }
            }

            for (var i = 0; i < 3; ++i) {
                for (var j = i; j < 3; ++j) {
                    if (bahtinovAngleArray[j] < (double)bahtinovAngleArray[i]) {
                        var tempAngle = bahtinovAngleArray[i];
                        bahtinovAngleArray[i] = bahtinovAngleArray[j];
                        bahtinovAngleArray[j] = tempAngle;

                        var tempPosition = bahtinovPositionArray[i];
                        bahtinovPositionArray[i] = bahtinovPositionArray[j];
                        bahtinovPositionArray[j] = tempPosition;
                    }
                }
            }

            if (bahtinovAngleArray[1] - (double)bahtinovAngleArray[0] > Math.PI / 2.0) {
                bahtinovAngleArray[1] -= (float)Math.PI;
                bahtinovAngleArray[2] -= (float)Math.PI;
                bahtinovPositionArray[1] = height - bahtinovPositionArray[1];
                bahtinovPositionArray[2] = height - bahtinovPositionArray[2];
            }

            if (bahtinovAngleArray[2] - (double)bahtinovAngleArray[1] > 1.5707963705062866) {
                bahtinovAngleArray[2] -= (float)Math.PI;
                bahtinovPositionArray[2] = height - bahtinovPositionArray[2];
            }

            for (var i = 0; i < 3; ++i) {
                for (var j = i; j < 3; ++j) {
                    if (bahtinovAngleArray[j] < (double)bahtinovAngleArray[i]) {
                        var tempAngle = bahtinovAngleArray[i];
                        bahtinovAngleArray[i] = bahtinovAngleArray[j];
                        bahtinovAngleArray[j] = tempAngle;

                        var tempPosition = bahtinovPositionArray[i];
                        bahtinovPositionArray[i] = bahtinovPositionArray[j];
                        bahtinovPositionArray[j] = tempPosition;
                    }
                }
            }

            for (var i = 0; i < 3; ++i) {
                var minDimension = Math.Min(centerX, centerY);
                var x1 = centerX + -minDimension * (float)Math.Cos(bahtinovAngleArray[i]) +
                         (bahtinovPositionArray[i] - centerY) * (float)Math.Sin(bahtinovAngleArray[i]);
                var x2 = centerX + minDimension * (float)Math.Cos(bahtinovAngleArray[i]) +
                         (bahtinovPositionArray[i] - centerY) * (float)Math.Sin(bahtinovAngleArray[i]);
                var y1 = centerY + -minDimension * (float)Math.Sin(bahtinovAngleArray[i]) +
                         (float)-(bahtinovPositionArray[i] - (double)centerY) * (float)Math.Cos(bahtinovAngleArray[i]);
                var y2 = centerY + minDimension * (float)Math.Sin(bahtinovAngleArray[i]) +
                         (float)-(bahtinovPositionArray[i] - (double)centerY) * (float)Math.Cos(bahtinovAngleArray[i]);

                if (i == 0) {
                    var slope = (float)((y2 - (double)y1) / (x2 - (double)x1));
                    var intercept = (float)(-(double)x1 * ((y2 - (double)y1) / (x2 - (double)x1))) + y1;
                    angle0Slope = slope;
                    angle0Intercept = intercept;
                } else if (i == 1) {
                    angle1X1 = x1;
                    angle1X2 = x2;
                    angle1Y1 = y1;
                    angle1Y2 = y2;
                } else if (i == 2) {
                    var slope = (float)((y2 - (double)y1) / (x2 - (double)x1));
                    var intercept = (float)(-(double)x1 * ((y2 - (double)y1) / (x2 - (double)x1))) + y1;
                    angle2Slope = slope;
                    angle2Intercept = intercept;
                }

                Point2D p1 = new(x1, height - y1);
                Point2D p2 = new(x2, height - y2);

                switch (i) {
                    case 0:
                        ret.LineRight = new Line(p1, p2);
                        break;
                    case 1:
                        ret.LineMiddle = new Line(p1, p2);
                        break;
                    case 2:
                        ret.LineLeft = new Line(p1, p2);
                        break;
                }
            }

            var intersectionX = (float)(-(angle0Intercept - (double)angle2Intercept) / (angle0Slope - (double)angle2Slope));
            var intersectionY = angle0Slope * intersectionX + angle0Intercept;

            var ellipseRadius = 8;

            ret.EllipseIntersection = new Ellipse(new Point2D(intersectionX - ellipseRadius, height - intersectionY - ellipseRadius), ellipseRadius * 2, ellipseRadius * 2);

            var projectionFactor = (float)((intersectionX - (double)angle1X1) * (angle1X2 - (double)angle1X1) +
                                            (intersectionY - (double)angle1Y1) * (angle1Y2 - (double)angle1Y1)) /
                                   (float)((angle1X2 - (double)angle1X1) * (angle1X2 - (double)angle1X1) +
                                           (angle1Y2 - (double)angle1Y1) * (angle1Y2 - (double)angle1Y1));

            var projectedX = angle1X1 + projectionFactor * (angle1X2 - angle1X1);
            var projectedY = angle1Y1 + projectionFactor * (angle1Y2 - angle1Y1);

            var errorDistance = (float)Math.Sqrt((intersectionX - (double)projectedX) * (intersectionX - (double)projectedX) +
                                                 (intersectionY - (double)projectedY) * (intersectionY - (double)projectedY));

            var deltaX = intersectionX - projectedX;
            var deltaY = intersectionY - projectedY;
            var directionX = angle1X2 - angle1X1;
            var directionY = angle1Y2 - angle1Y1;

            float errorSign = -Math.Sign((float)(deltaX * (double)directionY - deltaY * (double)directionX));

            var projectedXLong = intersectionX + (float)((projectedX - (double)intersectionX) * 20.0);
            var projectedYLong = intersectionY + (float)((projectedY - (double)intersectionY) * 20.0);

            var ellipseLongRadius = 8;

            ret.EllipseError = new Ellipse(new Point2D(projectedXLong - ellipseLongRadius, height - projectedYLong - ellipseLongRadius), ellipseLongRadius * 2, ellipseLongRadius * 2);

            ret.LineError = new Line(new Point2D(projectedXLong, height - projectedYLong), new Point2D(intersectionX, height - intersectionY));

            ret.FocusError = errorSign * errorDistance;

            var degreeFactor = 57.2957764f;
            var radianFactor = (float)Math.PI / 180f;
            var averageAngle = Math.Abs((float)((bahtinovAngleArray[2] - (double)bahtinovAngleArray[0]) / 2.0));
            var calculatedError = (float)(9.0 / 32.0 * ((float)diameter / ((float)focalLength * pixelSize)) *
                                          (1.0 + Math.Cos(45.0 * radianFactor) * (1.0 + Math.Tan(averageAngle))));

            ret.MaskAngle = averageAngle * degreeFactor;

            ret.Angles1 = degreeFactor * bahtinovAngleArray[0];
            ret.Angles2 = degreeFactor * bahtinovAngleArray[1];
            ret.Angles3 = degreeFactor * bahtinovAngleArray[2];

            ret.AbsoluteFocusError = calculatedError != 0 ? errorSign * errorDistance / calculatedError : 0f;

            ret.CriticalFocusThreshold = (float)(8.9999997499035089E-07 *
                                                 (focalLength / diameter) *
                                                 (focalLength / diameter));

            ret.CriticalFocus = Math.Abs(ret.AbsoluteFocusError * 1E-06) < Math.Abs(ret.CriticalFocusThreshold);

            if (ret.CriticalFocus) {
                Ellipse[] list = new Ellipse[3];

                for (var i = 32; i < 128; i += 32) {
                    list[(i / 32) - 1] = new Ellipse(new Point2D(intersectionX - i, height - intersectionY - i), i * 2, i * 2);
                }

                ret.EllipseCritFocus = list;
            }

            return ret;
        }

        private static int Clamp(int value, int min, int max) {
            if (value < min) {
                return min;
            }

            if (value > max) {
                return max;
            }

            return value;
        }
    }
}
