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
using System.Reflection;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    public static class UltraHdrJpegWriter
    {
        private const string LibraryName = "uhdr";
        private static readonly Assembly CurrentAssembly = typeof(UltraHdrJpegWriter).Assembly;

        public static bool IsEncoderAvailable => TryLoadCodec(out _);

        public static string AvailabilityMessage => TryLoadCodec(out string errorMessage)
            ? "Ultra HDR codec: Available"
            : errorMessage;

        public static bool TryWrite(string filePath, HDRCaptureResult hdrData, Image sdrImage, int sdrQuality, out string errorMessage)
        {
            errorMessage = null;

            if (!TryLoadCodec(out errorMessage))
            {
                return false;
            }

            if (hdrData == null || sdrImage == null)
            {
                errorMessage = "Ultra HDR JPEG save failed because the HDR or SDR image source is missing.";
                return false;
            }

            if (hdrData.Width != sdrImage.Width || hdrData.Height != sdrImage.Height)
            {
                errorMessage = "Ultra HDR JPEG save failed because the HDR and SDR image sizes do not match.";
                return false;
            }

            byte[] hdrPixels = hdrData.CopyToPackedHalfFloatRgba();
            byte[] sdrPixels = GetPackedRgba8888(sdrImage);
            GCHandle hdrHandle = default;
            GCHandle sdrHandle = default;
            IntPtr encoder = IntPtr.Zero;

            try
            {
                hdrHandle = GCHandle.Alloc(hdrPixels, GCHandleType.Pinned);
                sdrHandle = GCHandle.Alloc(sdrPixels, GCHandleType.Pinned);

                UhdrRawImage hdrRawImage = new UhdrRawImage
                {
                    Format = UhdrImageFormat.RgbaHalfFloat,
                    ColorGamut = UhdrColorGamut.Bt2100,
                    ColorTransfer = UhdrColorTransfer.Linear,
                    ColorRange = UhdrColorRange.Full,
                    Width = (uint)hdrData.Width,
                    Height = (uint)hdrData.Height,
                    Plane0 = hdrHandle.AddrOfPinnedObject(),
                    Plane1 = IntPtr.Zero,
                    Plane2 = IntPtr.Zero,
                    Stride0 = (uint)hdrData.Width,
                    Stride1 = 0,
                    Stride2 = 0
                };

                UhdrRawImage sdrRawImage = new UhdrRawImage
                {
                    Format = UhdrImageFormat.Rgba8888,
                    ColorGamut = UhdrColorGamut.Bt709,
                    ColorTransfer = UhdrColorTransfer.Srgb,
                    ColorRange = UhdrColorRange.Full,
                    Width = (uint)hdrData.Width,
                    Height = (uint)hdrData.Height,
                    Plane0 = sdrHandle.AddrOfPinnedObject(),
                    Plane1 = IntPtr.Zero,
                    Plane2 = IntPtr.Zero,
                    Stride0 = (uint)hdrData.Width,
                    Stride1 = 0,
                    Stride2 = 0
                };

                encoder = UltraHdrNative.uhdr_create_encoder();

                if (encoder == IntPtr.Zero)
                {
                    errorMessage = GetMissingCodecMessage();
                    return false;
                }

                if (!EnsureSuccess(UltraHdrNative.uhdr_enc_set_raw_image(encoder, ref hdrRawImage, UhdrImageLabel.HdrImage), out errorMessage) ||
                    !EnsureSuccess(UltraHdrNative.uhdr_enc_set_raw_image(encoder, ref sdrRawImage, UhdrImageLabel.SdrImage), out errorMessage) ||
                    !EnsureSuccess(UltraHdrNative.uhdr_enc_set_quality(encoder, ClampQuality(sdrQuality), UhdrImageLabel.BaseImage), out errorMessage) ||
                    !EnsureSuccess(UltraHdrNative.uhdr_enc_set_quality(encoder, 95, UhdrImageLabel.GainMapImage), out errorMessage) ||
                    !EnsureSuccess(UltraHdrNative.uhdr_enc_set_preset(encoder, UhdrEncoderPreset.BestQuality), out errorMessage) ||
                    !EnsureSuccess(UltraHdrNative.uhdr_enc_set_output_format(encoder, UhdrCodec.Jpeg), out errorMessage) ||
                    !EnsureSuccess(UltraHdrNative.uhdr_encode(encoder), out errorMessage))
                {
                    return false;
                }

                IntPtr encodedImagePointer = UltraHdrNative.uhdr_get_encoded_stream(encoder);

                if (encodedImagePointer == IntPtr.Zero)
                {
                    errorMessage = "Ultra HDR JPEG save failed because the bundled uhdr codec did not return an encoded stream.";
                    return false;
                }

                UhdrCompressedImage encodedImage = Marshal.PtrToStructure<UhdrCompressedImage>(encodedImagePointer);
                int encodedLength = checked((int)encodedImage.DataSize.ToUInt64());

                if (encodedImage.Data == IntPtr.Zero || encodedLength <= 0)
                {
                    errorMessage = "Ultra HDR JPEG save failed because the bundled uhdr codec returned an empty image.";
                    return false;
                }

                byte[] outputBytes = new byte[encodedLength];
                Marshal.Copy(encodedImage.Data, outputBytes, 0, outputBytes.Length);
                File.WriteAllBytes(filePath, outputBytes);
                return true;
            }
            catch (DllNotFoundException e)
            {
                DebugHelper.WriteException(e, "Ultra HDR codec is missing.");
                errorMessage = GetMissingCodecMessage();
                return false;
            }
            catch (EntryPointNotFoundException e)
            {
                DebugHelper.WriteException(e, "Ultra HDR codec entry point is missing.");
                errorMessage = "Ultra HDR JPEG is not available because the bundled uhdr codec is incomplete or incompatible.";
                return false;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Ultra HDR JPEG save failed.");
                errorMessage = e.Message;
                return false;
            }
            finally
            {
                if (encoder != IntPtr.Zero)
                {
                    UltraHdrNative.uhdr_release_encoder(encoder);
                }

                if (hdrHandle.IsAllocated)
                {
                    hdrHandle.Free();
                }

                if (sdrHandle.IsAllocated)
                {
                    sdrHandle.Free();
                }
            }
        }

        private static bool TryLoadCodec(out string errorMessage)
        {
            if (NativeLibrary.TryLoad(LibraryName, CurrentAssembly, searchPath: null, out IntPtr handle) ||
                NativeLibrary.TryLoad(LibraryName, out handle))
            {
                NativeLibrary.Free(handle);
                errorMessage = null;
                return true;
            }

            errorMessage = GetMissingCodecMessage();
            return false;
        }

        private static bool EnsureSuccess(UhdrErrorInfo errorInfo, out string errorMessage)
        {
            if (errorInfo.ErrorCode == UhdrCodecError.Ok)
            {
                errorMessage = null;
                return true;
            }

            errorMessage = !string.IsNullOrWhiteSpace(errorInfo.Detail)
                ? errorInfo.Detail.TrimEnd('\0')
                : $"The bundled uhdr codec returned {errorInfo.ErrorCode}.";
            return false;
        }

        private static string GetMissingCodecMessage()
        {
            return $"Ultra HDR JPEG is not available because the bundled uhdr codec is missing for {RuntimeInformation.ProcessArchitecture}.";
        }

        private static int ClampQuality(int quality)
        {
            return Math.Clamp(quality, 0, 100);
        }

        private static byte[] GetPackedRgba8888(Image image)
        {
            Bitmap bitmap = EnsureArgbBitmap(image, out bool disposeBitmap);

            try
            {
                Rectangle bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                BitmapData bitmapData = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                try
                {
                    byte[] result = new byte[bitmap.Width * bitmap.Height * 4];
                    byte[] row = new byte[Math.Abs(bitmapData.Stride)];

                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), row, 0, row.Length);
                        int destinationRowOffset = y * bitmap.Width * 4;

                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            int sourceOffset = x * 4;
                            int destinationOffset = destinationRowOffset + x * 4;
                            result[destinationOffset] = row[sourceOffset + 2];
                            result[destinationOffset + 1] = row[sourceOffset + 1];
                            result[destinationOffset + 2] = row[sourceOffset];
                            result[destinationOffset + 3] = row[sourceOffset + 3];
                        }
                    }

                    return result;
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }
            }
            finally
            {
                if (disposeBitmap)
                {
                    bitmap.Dispose();
                }
            }
        }

        private static Bitmap EnsureArgbBitmap(Image image, out bool disposeBitmap)
        {
            if (image is Bitmap bitmap && bitmap.PixelFormat == PixelFormat.Format32bppArgb)
            {
                disposeBitmap = false;
                return bitmap;
            }

            Bitmap argbBitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(argbBitmap))
            {
                graphics.DrawImage(image, 0, 0, image.Width, image.Height);
            }

            disposeBitmap = true;
            return argbBitmap;
        }

        private enum UhdrImageFormat
        {
            Rgba8888 = 3,
            RgbaHalfFloat = 4
        }

        private enum UhdrColorGamut
        {
            Bt709 = 0,
            Bt2100 = 2
        }

        private enum UhdrColorTransfer
        {
            Linear = 0,
            Srgb = 3
        }

        private enum UhdrColorRange
        {
            Full = 1
        }

        private enum UhdrCodec
        {
            Jpeg = 0
        }

        private enum UhdrImageLabel
        {
            HdrImage = 0,
            SdrImage = 1,
            BaseImage = 2,
            GainMapImage = 3
        }

        private enum UhdrEncoderPreset
        {
            Realtime = 0,
            BestQuality = 1
        }

        private enum UhdrCodecError
        {
            Ok = 0
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct UhdrErrorInfo
        {
            public UhdrCodecError ErrorCode;
            public int HasDetail;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Detail;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UhdrRawImage
        {
            public UhdrImageFormat Format;
            public UhdrColorGamut ColorGamut;
            public UhdrColorTransfer ColorTransfer;
            public UhdrColorRange ColorRange;
            public uint Width;
            public uint Height;
            public IntPtr Plane0;
            public IntPtr Plane1;
            public IntPtr Plane2;
            public uint Stride0;
            public uint Stride1;
            public uint Stride2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UhdrCompressedImage
        {
            public IntPtr Data;
            public UIntPtr DataSize;
            public UIntPtr Capacity;
            public UhdrColorGamut ColorGamut;
            public UhdrColorTransfer ColorTransfer;
            public UhdrColorRange ColorRange;
        }

        private static class UltraHdrNative
        {
            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr uhdr_create_encoder();

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void uhdr_release_encoder(IntPtr encoder);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern UhdrErrorInfo uhdr_enc_set_raw_image(IntPtr encoder, ref UhdrRawImage image, UhdrImageLabel intent);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern UhdrErrorInfo uhdr_enc_set_quality(IntPtr encoder, int quality, UhdrImageLabel intent);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern UhdrErrorInfo uhdr_enc_set_preset(IntPtr encoder, UhdrEncoderPreset preset);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern UhdrErrorInfo uhdr_enc_set_output_format(IntPtr encoder, UhdrCodec codec);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern UhdrErrorInfo uhdr_encode(IntPtr encoder);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr uhdr_get_encoded_stream(IntPtr encoder);
        }
    }
}
