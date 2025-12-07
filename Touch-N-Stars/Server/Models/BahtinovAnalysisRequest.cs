using System;
using System.Text.Json.Serialization;

namespace TouchNStars.Server.Models {
    public class BahtinovAnalysisRequest {
        public string ImageBase64 { get; set; }
        [JsonIgnore]
        public byte[] ImageBytes { get; set; }
        public double FocalLength { get; set; }
        public double? FocalRatio { get; set; }
        public double? ApertureDiameter { get; set; }
        public double PixelSize { get; set; }
        public CropRegion Crop { get; set; }
        public double? ResizeFactor { get; set; }
        public float[] PreviousAngles { get; set; }

        public bool HasValidGeometry => ApertureDiameter.HasValue || (FocalRatio.HasValue && FocalRatio.Value > 0);

        public void Validate() {
            bool hasBase64 = !string.IsNullOrWhiteSpace(ImageBase64);
            bool hasBytes = ImageBytes != null && ImageBytes.Length > 0;
            if (!hasBase64 && !hasBytes) {
                throw new ArgumentException("No image data provided.");
            }
            if (FocalLength <= 0) {
                throw new ArgumentException("Focal length must be greater than zero.");
            }
            if (!HasValidGeometry) {
                throw new ArgumentException("Provide either aperture diameter or a positive focal ratio.");
            }
            if (PixelSize <= 0) {
                throw new ArgumentException("Pixel size must be greater than zero.");
            }
            if (ResizeFactor.HasValue && ResizeFactor.Value <= 0) {
                throw new ArgumentException("Resize factor must be greater than zero.");
            }
            if (Crop != null && (Crop.Width <= 0 || Crop.Height <= 0)) {
                throw new ArgumentException("Crop width and height must be greater than zero.");
            }
            if (PreviousAngles != null && PreviousAngles.Length != 3) {
                throw new ArgumentException("Previous angles must contain exactly three entries.");
            }
        }
    }

    public class CropRegion {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public CropRegion Clone() {
            return new CropRegion {
                X = X,
                Y = Y,
                Width = Width,
                Height = Height
            };
        }
    }
}
