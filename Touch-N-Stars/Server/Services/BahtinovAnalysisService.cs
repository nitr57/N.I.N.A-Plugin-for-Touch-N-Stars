using System;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;
using TouchNStars.Server.Models;
using BahtinovCore = CanardConfit.NINA.BahtiFocus.Bahtinov.Bahtinov;

namespace TouchNStars.Server.Services {
    public class BahtinovAnalysisService {
        public BahtinovAnalysisResult Analyze(BahtinovAnalysisRequest request) {
            if (request == null) {
                throw new ArgumentNullException(nameof(request));
            }

            request.Validate();

            Logger.Info($"Starting Bahtinov analysis: focalLength={request.FocalLength}, pixelSize={request.PixelSize}, aperture={(request.ApertureDiameter?.ToString() ?? "<derived>")}, focalRatio={(request.FocalRatio?.ToString() ?? "n/a")}, resize={(request.ResizeFactor?.ToString() ?? "none")}, crop={DescribeCrop(request.Crop)}");

            double diameter = request.ApertureDiameter ?? request.FocalLength / request.FocalRatio!.Value;
            float[] angles = request.PreviousAngles != null ? (float[])request.PreviousAngles.Clone() : new float[3];

            ImagePayload payload = LoadImagePayload(request);
            float[,] working = payload.Intensity;
            Logger.Info($"Loaded image payload with dimensions {working.GetLength(0)}x{working.GetLength(1)}");

            double effectiveResize = 1.0;
            working = ApplyResize(working, request.ResizeFactor, ref effectiveResize);
            if (Math.Abs(effectiveResize - 1.0) > 0.0001) {
                Logger.Info($"Applied resize factor {effectiveResize:F3}, new dimensions {working.GetLength(0)}x{working.GetLength(1)}");
            }

            CropRegion appliedCrop = null;
            if (request.Crop != null) {
                working = ApplyCrop(working, request.Crop, out appliedCrop);
                Logger.Info($"Applied crop {DescribeCrop(appliedCrop)}, resulting dimensions {working.GetLength(0)}x{working.GetLength(1)}");
            }

            BahtinovCore.BahtinovCalc calc = BahtinovCore.CalculateLines(working, ref angles, diameter, request.FocalLength, request.PixelSize);

            Logger.Info($"Bahtinov calculation complete: focusError={calc.FocusError:F3}, absoluteError={calc.AbsoluteFocusError:F3}, critical={calc.CriticalFocus}");

            BahtinovAnalysisResult result = new BahtinovAnalysisResult {
                FocusErrorPixels = calc.FocusError,
                AbsoluteFocusError = calc.AbsoluteFocusError,
                MaskAngleDegrees = calc.MaskAngle,
                LineAnglesDegrees = new[] { (double)calc.Angles1, (double)calc.Angles2, (double)calc.Angles3 },
                CriticalFocusThreshold = calc.CriticalFocusThreshold,
                IsWithinCriticalFocus = calc.CriticalFocus,
                LineLeft = MapLine(calc.LineLeft),
                LineMiddle = MapLine(calc.LineMiddle),
                LineRight = MapLine(calc.LineRight),
                IntersectionMarker = MapEllipse(calc.EllipseIntersection),
                ErrorMarker = MapEllipse(calc.EllipseError),
                ErrorLine = MapLine(calc.LineError),
                CriticalFocusRings = MapEllipses(calc.EllipseCritFocus),
                UpdatedAngles = angles,
                ReferenceErrorScale = calc.AbsoluteFocusError != 0 ? calc.FocusError / calc.AbsoluteFocusError : null,
                ProcessedWidth = working.GetLength(0),
                ProcessedHeight = working.GetLength(1),
                EffectiveResizeFactor = effectiveResize,
                ProcessedCrop = appliedCrop
            };

            return result;
        }

        private static ImagePayload LoadImagePayload(BahtinovAnalysisRequest request) {
            byte[] data = null;

            if (request.ImageBytes != null && request.ImageBytes.Length > 0) {
                data = request.ImageBytes;
                Logger.Info($"Processing binary payload of {data.Length} bytes");
            } else if (!string.IsNullOrWhiteSpace(request.ImageBase64)) {
                string sanitized = request.ImageBase64.Contains(',') ? request.ImageBase64[(request.ImageBase64.IndexOf(',') + 1)..] : request.ImageBase64;
                try {
                    data = Convert.FromBase64String(sanitized);
                    Logger.Info($"Decoded base64 payload to {data.Length} bytes");
                } catch (FormatException ex) {
                    Logger.Error(ex);
                    throw new ArgumentException("Image data is not valid base64.", ex);
                }
            }

            if (data == null || data.Length == 0) {
                throw new ArgumentException("No image data provided.");
            }

            if (IsFitsData(data)) {
                SimpleFitsData fits = ReadFits(data);
                if (fits == null || fits.ImageData == null) {
                    throw new ArgumentException("FITS payload could not be decoded.");
                }

                float[,] normalized = NormalizeFitsData(fits.ImageData);
                Logger.Info($"Loaded FITS payload {fits.Width}x{fits.Height}");
                return new ImagePayload(normalized);
            }

            Logger.Info("Loaded raster payload (non-FITS)");
            return LoadRasterPayload(data);
        }

        private static string DescribeCrop(CropRegion crop) {
            if (crop == null) {
                return "none";
            }

            return $"{crop.Width}x{crop.Height}@{crop.X},{crop.Y}";
        }

        private static ImagePayload LoadRasterPayload(byte[] data) {
            using MemoryStream memory = new MemoryStream(data);
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();

            BitmapSource converted = bitmap.Format == PixelFormats.Gray8
                ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);

            if (converted.CanFreeze) {
                converted.Freeze();
            }

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = (converted.Format.BitsPerPixel * width + 7) / 8;

            byte[] pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            float[,] intensity = new float[width, height];
            for (int y = 0; y < height; y++) {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++) {
                    intensity[x, y] = pixels[rowOffset + x] / 255f;
                }
            }

            return new ImagePayload(intensity);
        }

        private static bool IsFitsData(byte[] data) {
            if (data == null || data.Length < 6) {
                return false;
            }

            try {
                string signature = Encoding.ASCII.GetString(data, 0, 6);
                return signature == "SIMPLE";
            } catch (ArgumentException) {
                return false;
            }
        }

        private sealed class SimpleFitsData {
            public int Width { get; }
            public int Height { get; }
            public float[,] ImageData { get; }

            public SimpleFitsData(int width, int height, float[,] imageData) {
                Width = width;
                Height = height;
                ImageData = imageData;
            }
        }

        private static SimpleFitsData ReadFits(byte[] data) {
            try {
                using MemoryStream stream = new MemoryStream(data);
                using BinaryReader reader = new BinaryReader(stream);

                int width = 0;
                int height = 0;
                int bitpix = 0;
                long dataStart = 0;

                while (dataStart < stream.Length) {
                    stream.Seek(dataStart, SeekOrigin.Begin);
                    byte[] block = reader.ReadBytes(2880);
                    if (block.Length == 0) {
                        break;
                    }

                    string headerText = Encoding.ASCII.GetString(block);

                    if (width == 0) {
                        width = ParseFitsKeyword(headerText, "NAXIS1");
                    }
                    if (height == 0) {
                        height = ParseFitsKeyword(headerText, "NAXIS2");
                    }
                    if (bitpix == 0) {
                        bitpix = ParseFitsKeyword(headerText, "BITPIX");
                    }

                    if (headerText.Contains("END     ")) {
                        dataStart += 2880;
                        break;
                    }

                    dataStart += 2880;
                }

                if (width <= 0 || height <= 0) {
                    Logger.Error($"Invalid FITS dimensions: {width}x{height}");
                    return null;
                }

                if (bitpix == 0) {
                    Logger.Error("BITPIX not found in FITS header");
                    return null;
                }

                const long MaxPixels = 50_000_000;
                long totalPixels = (long)width * height;
                if (totalPixels > MaxPixels) {
                    Logger.Error($"Image too large: {width}x{height}");
                    return null;
                }

                int bytesPerPixel = Math.Abs(bitpix) / 8;
                long expectedSize = totalPixels * bytesPerPixel;
                long available = stream.Length - dataStart;
                if (available < expectedSize) {
                    Logger.Error($"FITS file truncated. Expected {expectedSize} bytes but only {available} available.");
                    return null;
                }

                stream.Seek(dataStart, SeekOrigin.Begin);
                float[,] image = new float[width, height];

                if (bitpix == -32) {
                    FillFromFloat(reader, image, width, height);
                } else if (bitpix == -64) {
                    FillFromDouble(reader, image, width, height);
                } else if (bitpix == 16) {
                    FillFromInt16(reader, image, width, height);
                } else if (bitpix == 8) {
                    FillFromByte(reader, image, width, height);
                } else {
                    Logger.Error($"Unsupported BITPIX: {bitpix}");
                    return null;
                }

                return new SimpleFitsData(width, height, image);
            } catch (Exception ex) {
                Logger.Error($"Error reading FITS payload: {ex.Message}");
                return null;
            }
        }

        private static void FillFromFloat(BinaryReader reader, float[,] image, int width, int height) {
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    byte[] bytes = reader.ReadBytes(4);
                    if (bytes.Length < 4) {
                        throw new EndOfStreamException("Unexpected end of FITS data (float).");
                    }
                    if (BitConverter.IsLittleEndian) {
                        Array.Reverse(bytes);
                    }
                    image[x, y] = BitConverter.ToSingle(bytes, 0);
                }
            }
        }

        private static void FillFromDouble(BinaryReader reader, float[,] image, int width, int height) {
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    byte[] bytes = reader.ReadBytes(8);
                    if (bytes.Length < 8) {
                        throw new EndOfStreamException("Unexpected end of FITS data (double).");
                    }
                    if (BitConverter.IsLittleEndian) {
                        Array.Reverse(bytes);
                    }
                    image[x, y] = (float)BitConverter.ToDouble(bytes, 0);
                }
            }
        }

        private static void FillFromInt16(BinaryReader reader, float[,] image, int width, int height) {
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    byte[] bytes = reader.ReadBytes(2);
                    if (bytes.Length < 2) {
                        throw new EndOfStreamException("Unexpected end of FITS data (int16).");
                    }
                    if (BitConverter.IsLittleEndian) {
                        Array.Reverse(bytes);
                    }
                    image[x, y] = BitConverter.ToInt16(bytes, 0);
                }
            }
        }

        private static void FillFromByte(BinaryReader reader, float[,] image, int width, int height) {
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int value = reader.ReadByte();
                    if (value < 0) {
                        throw new EndOfStreamException("Unexpected end of FITS data (byte).");
                    }
                    image[x, y] = value;
                }
            }
        }

        private static int ParseFitsKeyword(string headerText, string keyword) {
            try {
                int lineCount = headerText.Length / 80;
                for (int i = 0; i < lineCount; i++) {
                    string line = headerText.Substring(i * 80, 80).TrimEnd('\0', ' ');
                    if (string.IsNullOrWhiteSpace(line)) {
                        continue;
                    }
                    if (!line.StartsWith(keyword, StringComparison.Ordinal)) {
                        continue;
                    }

                    int equalsIndex = line.IndexOf('=');
                    if (equalsIndex < 0) {
                        continue;
                    }

                    string valuePart = line[(equalsIndex + 1)..];
                    int commentIndex = valuePart.IndexOf('/');
                    if (commentIndex >= 0) {
                        valuePart = valuePart[..commentIndex];
                    }

                    if (int.TryParse(valuePart.Trim(), out int value)) {
                        return value;
                    }
                }
            } catch (Exception ex) {
                Logger.Debug($"Failed to parse FITS keyword {keyword}: {ex.Message}");
            }

            return 0;
        }

        private static float[,] NormalizeFitsData(float[,] imageData) {
            int width = imageData.GetLength(0);
            int height = imageData.GetLength(1);

            float min = float.MaxValue;
            float max = float.MinValue;

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    float value = imageData[x, y];
                    if (float.IsNaN(value) || float.IsInfinity(value)) {
                        continue;
                    }
                    if (value < min) {
                        min = value;
                    }
                    if (value > max) {
                        max = value;
                    }
                }
            }

            float range = max - min;
            if (range <= 0f || float.IsInfinity(range) || float.IsNaN(range)) {
                return new float[width, height];
            }

            float[,] normalized = new float[width, height];
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    float value = imageData[x, y];
                    float scaled = (value - min) / range;
                    if (float.IsNaN(scaled) || float.IsInfinity(scaled)) {
                        scaled = 0f;
                    }
                    normalized[x, y] = Math.Clamp(scaled, 0f, 1f);
                }
            }

            return normalized;
        }

        private static float[,] ApplyResize(float[,] source, double? requestedFactor, ref double effectiveFactor) {
            if (!requestedFactor.HasValue || Math.Abs(requestedFactor.Value - 1.0) < double.Epsilon) {
                return source;
            }

            double scale = requestedFactor.Value;
            if (scale <= 0) {
                throw new ArgumentException("Resize factor must be greater than zero.");
            }

            int sourceWidth = source.GetLength(0);
            int sourceHeight = source.GetLength(1);
            int targetWidth = Math.Max(1, (int)Math.Ceiling(sourceWidth * scale));
            int targetHeight = Math.Max(1, (int)Math.Ceiling(sourceHeight * scale));

            float[,] resized = new float[targetWidth, targetHeight];

            double invScale = 1.0 / scale;
            for (int y = 0; y < targetHeight; y++) {
                double srcY = (y + 0.5) * invScale - 0.5;
                int y0 = (int)Math.Floor(srcY);
                int y1 = Math.Min(y0 + 1, sourceHeight - 1);
                double yLerp = srcY - y0;

                if (y0 < 0) {
                    y0 = 0;
                    yLerp = 0;
                }

                for (int x = 0; x < targetWidth; x++) {
                    double srcX = (x + 0.5) * invScale - 0.5;
                    int x0 = (int)Math.Floor(srcX);
                    int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                    double xLerp = srcX - x0;

                    if (x0 < 0) {
                        x0 = 0;
                        xLerp = 0;
                    }

                    float topLeft = source[x0, y0];
                    float topRight = source[x1, y0];
                    float bottomLeft = source[x0, y1];
                    float bottomRight = source[x1, y1];

                    float top = (float)(topLeft + (topRight - topLeft) * xLerp);
                    float bottom = (float)(bottomLeft + (bottomRight - bottomLeft) * xLerp);
                    float value = (float)(top + (bottom - top) * yLerp);

                    resized[x, y] = Math.Clamp(value, 0f, 1f);
                }
            }

            effectiveFactor = scale;
            return resized;
        }

        private static float[,] ApplyCrop(float[,] source, CropRegion crop, out CropRegion appliedCrop) {
            int sourceWidth = source.GetLength(0);
            int sourceHeight = source.GetLength(1);

            int x = Math.Max(0, crop.X);
            int y = Math.Max(0, crop.Y);
            int width = Math.Min(crop.Width, sourceWidth - x);
            int height = Math.Min(crop.Height, sourceHeight - y);

            if (width <= 0 || height <= 0) {
                throw new ArgumentException("Crop region is outside of the image bounds.");
            }

            float[,] cropped = new float[width, height];
            for (int yy = 0; yy < height; yy++) {
                for (int xx = 0; xx < width; xx++) {
                    cropped[xx, yy] = source[x + xx, y + yy];
                }
            }

            appliedCrop = new CropRegion {
                X = x,
                Y = y,
                Width = width,
                Height = height
            };

            return cropped;
        }

        private sealed class ImagePayload {
            public float[,] Intensity { get; }

            public ImagePayload(float[,] intensity) {
                Intensity = intensity ?? throw new ArgumentNullException(nameof(intensity));
            }
        }

        private static LineResult MapLine(BahtinovCore.Line line) {
            if (line == null) {
                return null;
            }

            return new LineResult {
                X1 = line.P1.X,
                Y1 = line.P1.Y,
                X2 = line.P2.X,
                Y2 = line.P2.Y
            };
        }

        private static EllipseResult MapEllipse(BahtinovCore.Ellipse ellipse) {
            if (ellipse == null) {
                return null;
            }

            return new EllipseResult {
                X = ellipse.Start.X,
                Y = ellipse.Start.Y,
                Width = ellipse.Width,
                Height = ellipse.Height
            };
        }

        private static EllipseResult[] MapEllipses(BahtinovCore.Ellipse[] ellipses) {
            if (ellipses == null) {
                return null;
            }

            EllipseResult[] result = new EllipseResult[ellipses.Length];
            for (int i = 0; i < ellipses.Length; i++) {
                result[i] = MapEllipse(ellipses[i]);
            }

            return result;
        }
    }
}
