#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    public enum HDRPixelFormat
    {
        R16G16B16A16_Float,
        R10G10B10A2_UNorm
    }

    public class HDRCaptureResult : IDisposable
    {
        private const int FloatBytesPerPixel = 8;
        private const int UNormBytesPerPixel = 4;
        private const float AgXMinEV = -12.47393f;
        private const float AgXMaxEV = 4.026069f;

        public byte[] PixelData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public HDRPixelFormat PixelFormat { get; set; }
        public int Stride { get; set; }

        private bool disposed;

        public static HDRCaptureResult CreateEmpty(int width, int height, HDRPixelFormat pixelFormat = HDRPixelFormat.R16G16B16A16_Float)
        {
            int stride = width * GetBytesPerPixel(pixelFormat);

            return new HDRCaptureResult
            {
                Width = width,
                Height = height,
                PixelFormat = pixelFormat,
                Stride = stride,
                PixelData = new byte[Math.Max(height, 0) * Math.Max(stride, 0)]
            };
        }

        public Bitmap ToSDRBitmap(HDRToneMapAlgorithm toneMapAlgorithm)
        {
            Bitmap bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, bmp.PixelFormat);

            try
            {
                byte[] sdrPixels = new byte[Width * Height * 4];
                ConvertToSDR(sdrPixels, toneMapAlgorithm);
                Marshal.Copy(sdrPixels, 0, bmpData.Scan0, sdrPixels.Length);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        public HDRCaptureResult Crop(Rectangle rect)
        {
            Rectangle sourceBounds = new Rectangle(0, 0, Width, Height);
            Rectangle croppedRect = Rectangle.Intersect(sourceBounds, rect);

            if (croppedRect.Width <= 0 || croppedRect.Height <= 0)
            {
                return null;
            }

            HDRCaptureResult result = CreateEmpty(croppedRect.Width, croppedRect.Height, PixelFormat);
            result.CopyRegionFrom(this, croppedRect, Point.Empty);
            return result;
        }

        public void CopyRegionFrom(HDRCaptureResult source, Rectangle sourceRect, Point destinationLocation)
        {
            if (source == null || source.PixelData == null || PixelData == null)
            {
                return;
            }

            Rectangle normalizedSourceRect = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height), sourceRect);

            if (normalizedSourceRect.Width <= 0 || normalizedSourceRect.Height <= 0)
            {
                return;
            }

            Rectangle destinationRect = new Rectangle(destinationLocation, normalizedSourceRect.Size);
            Rectangle clippedDestinationRect = Rectangle.Intersect(new Rectangle(0, 0, Width, Height), destinationRect);

            if (clippedDestinationRect.Width <= 0 || clippedDestinationRect.Height <= 0)
            {
                return;
            }

            int sourceOffsetX = clippedDestinationRect.X - destinationRect.X;
            int sourceOffsetY = clippedDestinationRect.Y - destinationRect.Y;
            Rectangle clippedSourceRect = new Rectangle(
                normalizedSourceRect.X + sourceOffsetX,
                normalizedSourceRect.Y + sourceOffsetY,
                clippedDestinationRect.Width,
                clippedDestinationRect.Height);

            if (PixelFormat == source.PixelFormat)
            {
                int bytesPerPixel = GetBytesPerPixel(PixelFormat);
                int copyLength = clippedSourceRect.Width * bytesPerPixel;

                for (int y = 0; y < clippedSourceRect.Height; y++)
                {
                    int sourceRowOffset = (clippedSourceRect.Y + y) * source.Stride + clippedSourceRect.X * bytesPerPixel;
                    int destinationRowOffset = (clippedDestinationRect.Y + y) * Stride + clippedDestinationRect.X * bytesPerPixel;
                    Buffer.BlockCopy(source.PixelData, sourceRowOffset, PixelData, destinationRowOffset, copyLength);
                }

                return;
            }

            for (int y = 0; y < clippedSourceRect.Height; y++)
            {
                for (int x = 0; x < clippedSourceRect.Width; x++)
                {
                    source.ReadPixel(clippedSourceRect.X + x, clippedSourceRect.Y + y, out float r, out float g, out float b, out float a);
                    WritePixel(clippedDestinationRect.X + x, clippedDestinationRect.Y + y, r, g, b, a);
                }
            }
        }

        public void CompositeCursor(CursorData cursorData, Rectangle captureBounds)
        {
            if (cursorData == null || !cursorData.IsVisible || PixelData == null)
            {
                return;
            }

            using Bitmap cursorBitmap = cursorData.ToBitmap();

            if (cursorBitmap == null || cursorBitmap.Width <= 0 || cursorBitmap.Height <= 0)
            {
                return;
            }

            Rectangle cursorRect = new Rectangle(
                cursorData.DrawPosition.X - captureBounds.X,
                cursorData.DrawPosition.Y - captureBounds.Y,
                cursorBitmap.Width,
                cursorBitmap.Height);

            Rectangle intersection = Rectangle.Intersect(new Rectangle(0, 0, Width, Height), cursorRect);

            if (intersection.Width <= 0 || intersection.Height <= 0)
            {
                return;
            }

            using Bitmap argbCursor = EnsureArgbBitmap(cursorBitmap);
            BitmapData cursorBits = argbCursor.LockBits(new Rectangle(0, 0, argbCursor.Width, argbCursor.Height), ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                int cursorOffsetX = intersection.X - cursorRect.X;
                int cursorOffsetY = intersection.Y - cursorRect.Y;

                unsafe
                {
                    byte* basePointer = (byte*)cursorBits.Scan0;

                    for (int y = 0; y < intersection.Height; y++)
                    {
                        byte* sourceRow = basePointer + (cursorOffsetY + y) * cursorBits.Stride;

                        for (int x = 0; x < intersection.Width; x++)
                        {
                            int sourceOffset = (cursorOffsetX + x) * 4;
                            float alpha = sourceRow[sourceOffset + 3] / 255.0f;

                            if (alpha <= 0)
                            {
                                continue;
                            }

                            float sourceB = SRGBToLinear(sourceRow[sourceOffset] / 255.0f);
                            float sourceG = SRGBToLinear(sourceRow[sourceOffset + 1] / 255.0f);
                            float sourceR = SRGBToLinear(sourceRow[sourceOffset + 2] / 255.0f);

                            ReadPixel(intersection.X + x, intersection.Y + y, out float destinationR, out float destinationG, out float destinationB, out float destinationA);

                            float inverseAlpha = 1.0f - alpha;
                            float blendedA = alpha + destinationA * inverseAlpha;
                            float blendedR = sourceR * alpha + destinationR * inverseAlpha;
                            float blendedG = sourceG * alpha + destinationG * inverseAlpha;
                            float blendedB = sourceB * alpha + destinationB * inverseAlpha;

                            WritePixel(intersection.X + x, intersection.Y + y, blendedR, blendedG, blendedB, blendedA);
                        }
                    }
                }
            }
            finally
            {
                argbCursor.UnlockBits(cursorBits);
            }
        }

        internal void SetPixelLinear(int x, int y, float r, float g, float b, float a)
        {
            WritePixel(x, y, r, g, b, a);
        }

        public void GetPixelLinear(int x, int y, out float r, out float g, out float b, out float a)
        {
            ReadPixel(x, y, out r, out g, out b, out a);
        }

        public byte[] CopyToPackedHalfFloatRgba()
        {
            int destinationStride = Width * FloatBytesPerPixel;
            byte[] result = new byte[Math.Max(Height, 0) * destinationStride];

            if (PixelFormat == HDRPixelFormat.R16G16B16A16_Float && Stride == destinationStride)
            {
                Buffer.BlockCopy(PixelData, 0, result, 0, result.Length);
                return result;
            }

            for (int y = 0; y < Height; y++)
            {
                int rowOffset = y * destinationStride;

                for (int x = 0; x < Width; x++)
                {
                    ReadPixel(x, y, out float r, out float g, out float b, out float a);
                    int offset = rowOffset + x * FloatBytesPerPixel;
                    WriteUInt16ToBuffer(result, offset, FloatToHalf(MathF.Max(r, 0f)));
                    WriteUInt16ToBuffer(result, offset + 2, FloatToHalf(MathF.Max(g, 0f)));
                    WriteUInt16ToBuffer(result, offset + 4, FloatToHalf(MathF.Max(b, 0f)));
                    WriteUInt16ToBuffer(result, offset + 6, FloatToHalf(Math.Clamp(a, 0f, 1f)));
                }
            }

            return result;
        }

        private void ConvertToSDR(byte[] output, HDRToneMapAlgorithm toneMapAlgorithm)
        {
            for (int y = 0; y < Height; y++)
            {
                int dstRowOffset = y * Width * 4;

                for (int x = 0; x < Width; x++)
                {
                    int dstOffset = dstRowOffset + x * 4;
                    ReadPixel(x, y, out float r, out float g, out float b, out float a);
                    ToneMap(ref r, ref g, ref b, toneMapAlgorithm);

                    output[dstOffset] = FloatToByte(LinearToSRGB(b));
                    output[dstOffset + 1] = FloatToByte(LinearToSRGB(g));
                    output[dstOffset + 2] = FloatToByte(LinearToSRGB(r));
                    output[dstOffset + 3] = FloatToByte(Math.Clamp(a, 0f, 1f));
                }
            }
        }

        private void ReadPixel(int x, int y, out float r, out float g, out float b, out float a)
        {
            int offset = y * Stride + x * GetBytesPerPixel(PixelFormat);

            switch (PixelFormat)
            {
                case HDRPixelFormat.R16G16B16A16_Float:
                    r = HalfToFloat(BitConverter.ToUInt16(PixelData, offset));
                    g = HalfToFloat(BitConverter.ToUInt16(PixelData, offset + 2));
                    b = HalfToFloat(BitConverter.ToUInt16(PixelData, offset + 4));
                    a = HalfToFloat(BitConverter.ToUInt16(PixelData, offset + 6));
                    return;
                default:
                    uint pixel = BitConverter.ToUInt32(PixelData, offset);
                    r = (pixel & 0x3FF) / 1023.0f;
                    g = ((pixel >> 10) & 0x3FF) / 1023.0f;
                    b = ((pixel >> 20) & 0x3FF) / 1023.0f;
                    a = ((pixel >> 30) & 0x3) / 3.0f;
                    return;
            }
        }

        private void WritePixel(int x, int y, float r, float g, float b, float a)
        {
            int offset = y * Stride + x * GetBytesPerPixel(PixelFormat);

            switch (PixelFormat)
            {
                case HDRPixelFormat.R16G16B16A16_Float:
                    WriteUInt16(offset, FloatToHalf(MathF.Max(r, 0f)));
                    WriteUInt16(offset + 2, FloatToHalf(MathF.Max(g, 0f)));
                    WriteUInt16(offset + 4, FloatToHalf(MathF.Max(b, 0f)));
                    WriteUInt16(offset + 6, FloatToHalf(Math.Clamp(a, 0f, 1f)));
                    return;
                default:
                    uint packedPixel =
                        ((uint)Math.Clamp((int)Math.Round(MathF.Max(r, 0f) * 1023.0f), 0, 1023)) |
                        ((uint)Math.Clamp((int)Math.Round(MathF.Max(g, 0f) * 1023.0f), 0, 1023) << 10) |
                        ((uint)Math.Clamp((int)Math.Round(MathF.Max(b, 0f) * 1023.0f), 0, 1023) << 20) |
                        ((uint)Math.Clamp((int)Math.Round(Math.Clamp(a, 0f, 1f) * 3.0f), 0, 3) << 30);
                    WriteUInt32(offset, packedPixel);
                    return;
            }
        }

        private void WriteUInt16(int offset, ushort value)
        {
            PixelData[offset] = (byte)(value & 0xFF);
            PixelData[offset + 1] = (byte)(value >> 8);
        }

        private void WriteUInt32(int offset, uint value)
        {
            PixelData[offset] = (byte)(value & 0xFF);
            PixelData[offset + 1] = (byte)((value >> 8) & 0xFF);
            PixelData[offset + 2] = (byte)((value >> 16) & 0xFF);
            PixelData[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public string WriteToTempEXR(EXRCompression compression = EXRCompression.None)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"sharex_hdr_{Guid.NewGuid():N}.exr");
            EXRWriter.Write(tempPath, PixelData, Width, Height, Stride, PixelFormat, compression);
            return tempPath;
        }

        private static float HalfToFloat(ushort half)
        {
            return (float)BitConverter.UInt16BitsToHalf(half);
        }

        private static ushort FloatToHalf(float value)
        {
            return BitConverter.HalfToUInt16Bits((Half)value);
        }

        private static void WriteUInt16ToBuffer(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static float LinearToSRGB(float linear)
        {
            linear = Math.Clamp(linear, 0f, 1f);

            if (linear <= 0.0031308f)
            {
                return linear * 12.92f;
            }

            return 1.055f * MathF.Pow(linear, 1.0f / 2.4f) - 0.055f;
        }

        private static float SRGBToLinear(float srgb)
        {
            srgb = Math.Clamp(srgb, 0f, 1f);

            if (srgb <= 0.04045f)
            {
                return srgb / 12.92f;
            }

            return MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
        }

        private static void ToneMap(ref float r, ref float g, ref float b, HDRToneMapAlgorithm toneMapAlgorithm)
        {
            r = MathF.Max(r, 0f);
            g = MathF.Max(g, 0f);
            b = MathF.Max(b, 0f);

            switch (toneMapAlgorithm)
            {
                case HDRToneMapAlgorithm.KhronosNeutral:
                    ToneMapKhronosNeutral(ref r, ref g, ref b);
                    break;
                case HDRToneMapAlgorithm.AgX:
                    r = ToneMapAgX(r);
                    g = ToneMapAgX(g);
                    b = ToneMapAgX(b);
                    break;
                case HDRToneMapAlgorithm.Reinhard:
                    r = ToneMapReinhard(r);
                    g = ToneMapReinhard(g);
                    b = ToneMapReinhard(b);
                    break;
                default:
                    r = ToneMapACES(r);
                    g = ToneMapACES(g);
                    b = ToneMapACES(b);
                    break;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
        }

        private static float ToneMapACES(float value)
        {
            value = MathF.Max(value, 0f);
            return (value * (2.51f * value + 0.03f)) / (value * (2.43f * value + 0.59f) + 0.14f);
        }

        private static float ToneMapReinhard(float value)
        {
            return value / (1.0f + value);
        }

        private static float ToneMapAgX(float value)
        {
            value = MathF.Max(value, 0f);
            float logEncoded = (MathF.Log2(MathF.Max(value, 1e-10f)) - AgXMinEV) / (AgXMaxEV - AgXMinEV);
            float x = Math.Clamp(logEncoded, 0f, 1f);
            float x2 = x * x;
            float x3 = x2 * x;
            float x4 = x2 * x2;
            float x5 = x4 * x;
            float x6 = x5 * x;

            return Math.Clamp(
                15.5f * x6
                - 40.14f * x5
                + 31.96f * x4
                - 6.868f * x3
                + 0.4298f * x2
                + 0.1191f * x
                - 0.00232f,
                0f,
                1f);
        }

        private static void ToneMapKhronosNeutral(ref float r, ref float g, ref float b)
        {
            const float startCompression = 0.76f;
            const float desaturation = 0.15f;

            float minChannel = MathF.Min(r, MathF.Min(g, b));
            float offset = minChannel < 0.08f ? minChannel - 6.25f * minChannel * minChannel : 0.04f;
            r -= offset;
            g -= offset;
            b -= offset;

            float peak = MathF.Max(r, MathF.Max(g, b));

            if (peak < startCompression)
            {
                return;
            }

            float d = 1.0f - startCompression;
            float newPeak = 1.0f - (d * d) / (peak + d - startCompression);
            float scale = newPeak / peak;
            float weight = 1.0f - 1.0f / (desaturation * (peak - newPeak) + 1.0f);

            r = Lerp(r * scale, newPeak, weight);
            g = Lerp(g * scale, newPeak, weight);
            b = Lerp(b * scale, newPeak, weight);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + ((b - a) * t);
        }

        private static Bitmap EnsureArgbBitmap(Bitmap source)
        {
            Bitmap bitmap = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            return bitmap;
        }

        private static int GetBytesPerPixel(HDRPixelFormat pixelFormat)
        {
            return pixelFormat switch
            {
                HDRPixelFormat.R16G16B16A16_Float => FloatBytesPerPixel,
                _ => UNormBytesPerPixel
            };
        }

        private static byte FloatToByte(float value)
        {
            return (byte)Math.Clamp((int)(value * 255.0f + 0.5f), 0, 255);
        }

        /// <summary>
        /// Fills black regions in the HDR capture with data from a GDI (BitBlt) capture.
        /// DXGI Desktop Duplication cannot capture hardware video overlays (e.g. browser
        /// hardware-accelerated video), so those areas appear as black. The GDI capture
        /// with CaptureBlt CAN capture these overlays. This method detects black pixels
        /// in the HDR data that have visible content in the GDI capture and fills them
        /// with the GDI data converted to linear space.
        /// </summary>
        public void FillFromGDICapture(Bitmap sdrBitmap)
        {
            if (sdrBitmap == null || PixelData == null || sdrBitmap.Width != Width || sdrBitmap.Height != Height)
            {
                return;
            }

            BitmapData bmpData = sdrBitmap.LockBits(
                new Rectangle(0, 0, Width, Height),
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                const float blackThreshold = 0.001f;

                unsafe
                {
                    byte* sdrBase = (byte*)bmpData.Scan0;

                    for (int y = 0; y < Height; y++)
                    {
                        byte* sdrRow = sdrBase + y * bmpData.Stride;

                        for (int x = 0; x < Width; x++)
                        {
                            ReadPixel(x, y, out float r, out float g, out float b, out float a);

                            if (r >= blackThreshold || g >= blackThreshold || b >= blackThreshold)
                            {
                                continue;
                            }

                            int sdrOffset = x * 4;
                            byte sdrB = sdrRow[sdrOffset];
                            byte sdrG = sdrRow[sdrOffset + 1];
                            byte sdrR = sdrRow[sdrOffset + 2];

                            if (sdrR > 0 || sdrG > 0 || sdrB > 0)
                            {
                                float linR = SRGBToLinear(sdrR / 255.0f);
                                float linG = SRGBToLinear(sdrG / 255.0f);
                                float linB = SRGBToLinear(sdrB / 255.0f);
                                WritePixel(x, y, linR, linG, linB, 1.0f);
                            }
                        }
                    }
                }
            }
            finally
            {
                sdrBitmap.UnlockBits(bmpData);
            }
        }

        public void Dispose()
        {
            if (!disposed)
            {
                PixelData = null;
                disposed = true;
            }
        }
    }
}
