using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NINA.Core.Utility;

namespace TouchNStars.Utility
{
    public static class ImageConverter
    {
        public static byte[] ConvertBase64StarImageToJpg(string base64Pixels, int width, int height, int? targetSize = null)
        {
            try
            {
                // Decode base64 to byte array
                byte[] pixelData = Convert.FromBase64String(base64Pixels);
                Logger.Debug($"Decoded {pixelData.Length} bytes from base64, expected: {width * height * 2}");

                // PHD2 sends 16-bit pixel data, so we expect width * height * 2 bytes
                int expectedBytes = width * height * 2;
                if (pixelData.Length < expectedBytes)
                {
                    Logger.Warning($"Pixel data too short: got {pixelData.Length}, expected {expectedBytes}");
                    throw new ArgumentException($"Invalid pixel data length: expected {expectedBytes}, got {pixelData.Length}");
                }

                // Convert 16-bit pixel data to 8-bit for display
                ushort[] pixels16 = new ushort[width * height];
                Buffer.BlockCopy(pixelData, 0, pixels16, 0, Math.Min(pixelData.Length, pixels16.Length * 2));

                // Find min/max for scaling
                ushort minVal = pixels16.Min();
                ushort maxVal = pixels16.Max();

                // Avoid division by zero
                if (maxVal == minVal)
                {
                    maxVal = (ushort)(minVal + 1);
                }

                Logger.Debug($"Pixel range: {minVal} - {maxVal}");

                // Calculate target dimensions
                int targetWidth = width;
                int targetHeight = height;

                if (targetSize.HasValue && targetSize.Value > 0)
                {
                    // Resize to target size while maintaining aspect ratio
                    double aspectRatio = (double)width / height;
                    if (width >= height)
                    {
                        targetWidth = targetSize.Value;
                        targetHeight = (int)(targetSize.Value / aspectRatio);
                    }
                    else
                    {
                        targetHeight = targetSize.Value;
                        targetWidth = (int)(targetSize.Value * aspectRatio);
                    }
                    Logger.Debug($"Resizing from {width}x{height} to {targetWidth}x{targetHeight}");
                }

                // Create bitmap
                using (var bitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        // Set high quality scaling
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                        // Create source bitmap from 16-bit data
                        using (var sourceBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                        {
                            var sourceData = sourceBitmap.LockBits(new Rectangle(0, 0, width, height),
                                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

                            try
                            {
                                unsafe
                                {
                                    byte* ptr = (byte*)sourceData.Scan0;
                                    int stride = sourceData.Stride;

                                    for (int y = 0; y < height; y++)
                                    {
                                        for (int x = 0; x < width; x++)
                                        {
                                            int pixelIndex = y * width + x;
                                            ushort pixel16 = pixels16[pixelIndex];

                                            // Scale 16-bit to 8-bit with gamma correction for better visibility
                                            double normalized = (double)(pixel16 - minVal) / (maxVal - minVal);
                                            normalized = Math.Pow(normalized, 0.5); // Gamma correction
                                            byte intensity = (byte)(255 * Math.Max(0, Math.Min(1, normalized)));

                                            // Set RGB values (grayscale)
                                            byte* pixel = ptr + (y * stride) + (x * 3);
                                            pixel[0] = intensity; // Blue
                                            pixel[1] = intensity; // Green
                                            pixel[2] = intensity; // Red
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                sourceBitmap.UnlockBits(sourceData);
                            }

                            // Draw the source bitmap onto the target bitmap (with scaling if needed)
                            graphics.DrawImage(sourceBitmap, 0, 0, targetWidth, targetHeight);
                        }
                    }

                    // Convert to JPG bytes
                    using (var stream = new MemoryStream())
                    {
                        // Try simple ImageFormat approach first
                        try
                        {
                            bitmap.Save(stream, ImageFormat.Jpeg);
                        }
                        catch (Exception saveEx)
                        {
                            Logger.Debug($"Simple ImageFormat save failed: {saveEx.Message}, trying with encoder");
                            var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                                .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

                            if (jpegEncoder != null)
                            {
                                using (var encoderParams = new EncoderParameters(1))
                                {
                                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 95L);
                                    bitmap.Save(stream, jpegEncoder, encoderParams);
                                }
                            }
                            else
                            {
                                throw new InvalidOperationException("No JPEG encoder found on system", saveEx);
                            }
                        }

                        return stream.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to convert star image to JPG: {ex}");
                throw;
            }
        }
    }
}
