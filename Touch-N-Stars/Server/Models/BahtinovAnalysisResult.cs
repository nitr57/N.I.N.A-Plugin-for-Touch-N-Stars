namespace TouchNStars.Server.Models {
    public class BahtinovAnalysisResult {
        public double FocusErrorPixels { get; set; }
        public double AbsoluteFocusError { get; set; }
        public double MaskAngleDegrees { get; set; }
        public double[] LineAnglesDegrees { get; set; }
        public double CriticalFocusThreshold { get; set; }
        public bool IsWithinCriticalFocus { get; set; }
        public LineResult LineLeft { get; set; }
        public LineResult LineMiddle { get; set; }
        public LineResult LineRight { get; set; }
        public EllipseResult IntersectionMarker { get; set; }
        public EllipseResult ErrorMarker { get; set; }
        public LineResult ErrorLine { get; set; }
        public EllipseResult[] CriticalFocusRings { get; set; }
        public float[] UpdatedAngles { get; set; }
        public double? ReferenceErrorScale { get; set; }
        public int ProcessedWidth { get; set; }
        public int ProcessedHeight { get; set; }
        public double EffectiveResizeFactor { get; set; }
        public CropRegion ProcessedCrop { get; set; }
    }

    public class LineResult {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    public class EllipseResult {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
