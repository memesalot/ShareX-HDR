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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ShareX.ScreenCaptureLib
{
    public class HDRScreenshot : IDisposable
    {
        private const int ACQUIRE_TIMEOUT_MS = 1000;
        private const int WARMUP_FRAME_COUNT = 2;

        private bool disposed;

        private readonly record struct OutputCaptureTarget(uint AdapterIndex, uint OutputIndex, Rectangle Bounds, bool SupportsHDR);

        /// <summary>
        /// Captures a rectangle from the screen in HDR, stitching together all overlapping outputs.
        /// </summary>
        public HDRCaptureResult CaptureRectangleHDR(Rectangle rect, bool captureCursor)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                throw new ArgumentException("Capture rectangle must have positive dimensions.", nameof(rect));
            }

            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            List<OutputCaptureTarget> captureTargets = FindOutputsForRect(factory, rect);

            if (captureTargets.Count == 0)
            {
                throw new InvalidOperationException("Could not find a display output for the specified rectangle.");
            }

            HDRCaptureResult compositeResult = HDRCaptureResult.CreateEmpty(rect.Width, rect.Height);
            bool capturedAnyPixels = false;
            bool capturedAnyHDRPixels = false;

            try
            {
                foreach (OutputCaptureTarget target in captureTargets)
                {
                    Rectangle captureRect = Rectangle.Intersect(rect, target.Bounds);

                    if (captureRect.Width <= 0 || captureRect.Height <= 0)
                    {
                        continue;
                    }

                    using HDRCaptureResult capturedRegion = target.SupportsHDR
                        ? CaptureOutputRegion(factory, target, captureRect)
                        : CaptureOutputRegionSDR(captureRect);

                    if (capturedRegion == null)
                    {
                        continue;
                    }

                    if (target.SupportsHDR && !capturedRegion.HasTrueHDRData)
                    {
                        using HDRCaptureResult sdrFallbackRegion = CaptureOutputRegionSDR(captureRect);

                        if (sdrFallbackRegion == null)
                        {
                            continue;
                        }

                        compositeResult.CopyRegionFrom(sdrFallbackRegion, new Rectangle(0, 0, sdrFallbackRegion.Width, sdrFallbackRegion.Height),
                            new Point(captureRect.X - rect.X, captureRect.Y - rect.Y));
                        capturedAnyPixels = true;
                        continue;
                    }

                    compositeResult.CopyRegionFrom(capturedRegion, new Rectangle(0, 0, capturedRegion.Width, capturedRegion.Height),
                        new Point(captureRect.X - rect.X, captureRect.Y - rect.Y));
                    capturedAnyPixels = true;
                    capturedAnyHDRPixels |= capturedRegion.HasTrueHDRData;
                }

                if (!capturedAnyPixels)
                {
                    throw new InvalidOperationException("Capture rectangle does not intersect with any readable display output.");
                }

                if (!capturedAnyHDRPixels)
                {
                    compositeResult.Dispose();
                    return null;
                }

                if (captureCursor)
                {
                    try
                    {
                        CursorData cursorData = new CursorData();
                        compositeResult.CompositeCursor(cursorData, rect);
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, "Cursor capture failed during HDR capture.");
                    }
                }

                return compositeResult;
            }
            catch
            {
                compositeResult.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Captures the full screen in HDR.
        /// </summary>
        public HDRCaptureResult CaptureFullscreenHDR()
        {
            Rectangle bounds = CaptureHelpers.GetScreenBounds();
            return CaptureRectangleHDR(bounds, false);
        }

        public static bool IsHDRAvailable(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            IDXGIFactory1 factory = null;

            try
            {
                factory = CreateDXGIFactory1<IDXGIFactory1>();
                List<OutputCaptureTarget> captureTargets = FindOutputsForRect(factory, rect);

                foreach (OutputCaptureTarget target in captureTargets)
                {
                    if (target.SupportsHDR)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                factory?.Dispose();
            }
        }

        /// <summary>
        /// Checks if the specified monitor supports HDR output.
        /// </summary>
        public static bool IsHDRAvailable(int monitorIndex = 0)
        {
            IDXGIFactory1 factory = null;

            try
            {
                factory = CreateDXGIFactory1<IDXGIFactory1>();

                int currentIndex = 0;

                for (uint adapterIdx = 0; factory.EnumAdapters1(adapterIdx, out IDXGIAdapter1 adapter).Success; adapterIdx++)
                {
                    try
                    {
                        for (uint outputIdx = 0; adapter.EnumOutputs(outputIdx, out IDXGIOutput output).Success; outputIdx++)
                        {
                            try
                            {
                                if (currentIndex == monitorIndex)
                                {
                                    IDXGIOutput6 output6 = null;

                                    try
                                    {
                                        output6 = output.QueryInterface<IDXGIOutput6>();
                                        OutputDescription1 desc1 = output6.Description1;

                                        return desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ||
                                               desc1.ColorSpace == ColorSpaceType.RgbFullG10NoneP709;
                                    }
                                    catch
                                    {
                                        return false;
                                    }
                                    finally
                                    {
                                        output6?.Dispose();
                                    }
                                }

                                currentIndex++;
                            }
                            finally
                            {
                                output.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        adapter.Dispose();
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                factory?.Dispose();
            }
        }

        /// <summary>
        /// Checks if any monitor supports HDR.
        /// </summary>
        public static bool IsAnyHDRAvailable()
        {
            IDXGIFactory1 factory = null;

            try
            {
                factory = CreateDXGIFactory1<IDXGIFactory1>();

                for (uint adapterIdx = 0; factory.EnumAdapters1(adapterIdx, out IDXGIAdapter1 adapter).Success; adapterIdx++)
                {
                    try
                    {
                        for (uint outputIdx = 0; adapter.EnumOutputs(outputIdx, out IDXGIOutput output).Success; outputIdx++)
                        {
                            try
                            {
                                using IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>();
                                OutputDescription1 desc1 = output6.Description1;

                                if (desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ||
                                    desc1.ColorSpace == ColorSpaceType.RgbFullG10NoneP709)
                                {
                                    return true;
                                }
                            }
                            catch
                            {
                            }
                            finally
                            {
                                output.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        adapter.Dispose();
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                factory?.Dispose();
            }
        }

        private HDRCaptureResult CaptureOutputRegion(IDXGIFactory1 factory, OutputCaptureTarget target, Rectangle captureRect)
        {
            IDXGIAdapter1 adapter = null;
            IDXGIOutput output = null;
            IDXGIOutput5 output5 = null;
            ID3D11Device device = null;
            ID3D11DeviceContext context = null;
            IDXGIOutputDuplication duplication = null;
            IDXGIResource frameResource = null;
            ID3D11Texture2D stagingTexture = null;
            bool frameAcquired = false;
            bool stagingMapped = false;

            try
            {
                if (!factory.EnumAdapters1(target.AdapterIndex, out adapter).Success)
                {
                    throw new InvalidOperationException("Failed to reopen the DXGI adapter for HDR capture.");
                }

                if (!adapter.EnumOutputs(target.OutputIndex, out output).Success)
                {
                    throw new InvalidOperationException("Failed to reopen the DXGI output for HDR capture.");
                }

                D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    new FeatureLevel[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                    out device,
                    out FeatureLevel _,
                    out context).CheckError();

                output5 = output.QueryInterface<IDXGIOutput5>();
                Format[] supportedFormats =
                {
                    Format.R16G16B16A16_Float,
                    Format.R10G10B10A2_UNorm,
                    Format.B8G8R8A8_UNorm,
                    Format.R8G8B8A8_UNorm
                };
                duplication = output5.DuplicateOutput1(device, supportedFormats);

                for (int i = 0; i < WARMUP_FRAME_COUNT; i++)
                {
                    try
                    {
                        duplication.AcquireNextFrame(ACQUIRE_TIMEOUT_MS, out OutduplFrameInfo _, out IDXGIResource warmupFrame);
                        warmupFrame?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        duplication.ReleaseFrame();
                    }
                    catch
                    {
                    }
                }

                duplication.AcquireNextFrame(ACQUIRE_TIMEOUT_MS, out OutduplFrameInfo _, out frameResource);
                frameAcquired = true;

                using ID3D11Texture2D frameTexture = frameResource.QueryInterface<ID3D11Texture2D>();
                Texture2DDescription frameDesc = frameTexture.Description;
                int relativeX = captureRect.X - target.Bounds.X;
                int relativeY = captureRect.Y - target.Bounds.Y;

                Texture2DDescription stagingDesc = new Texture2DDescription
                {
                    Width = (uint)captureRect.Width,
                    Height = (uint)captureRect.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = frameDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };

                stagingTexture = device.CreateTexture2D(stagingDesc);
                context.CopySubresourceRegion(
                    stagingTexture, 0, 0, 0, 0,
                    frameTexture, 0,
                    new Box(relativeX, relativeY, 0, relativeX + captureRect.Width, relativeY + captureRect.Height, 1));

                MappedSubresource mapped = context.Map(stagingTexture, 0, MapMode.Read);
                stagingMapped = true;
                return CreateCaptureResult(frameDesc.Format, captureRect.Width, captureRect.Height, mapped);
            }
            finally
            {
                if (stagingMapped)
                {
                    context.Unmap(stagingTexture, 0);
                }

                stagingTexture?.Dispose();

                if (frameAcquired)
                {
                    try
                    {
                        duplication?.ReleaseFrame();
                    }
                    catch
                    {
                    }
                }

                frameResource?.Dispose();
                duplication?.Dispose();
                output5?.Dispose();
                output?.Dispose();
                adapter?.Dispose();
                context?.Dispose();
                device?.Dispose();
            }
        }

        private static List<OutputCaptureTarget> FindOutputsForRect(IDXGIFactory1 factory, Rectangle rect)
        {
            List<OutputCaptureTarget> captureTargets = new List<OutputCaptureTarget>();

            for (uint adapterIdx = 0; factory.EnumAdapters1(adapterIdx, out IDXGIAdapter1 adapter).Success; adapterIdx++)
            {
                try
                {
                    for (uint outputIdx = 0; adapter.EnumOutputs(outputIdx, out IDXGIOutput output).Success; outputIdx++)
                    {
                        try
                        {
                            OutputDescription desc = output.Description;
                            Rectangle outputBounds = new Rectangle(
                                desc.DesktopCoordinates.Left,
                                desc.DesktopCoordinates.Top,
                                desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                                desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);

                            Rectangle overlap = Rectangle.Intersect(rect, outputBounds);

                            if (overlap.Width > 0 && overlap.Height > 0)
                            {
                                captureTargets.Add(new OutputCaptureTarget(adapterIdx, outputIdx, outputBounds, SupportsHDR(output)));
                            }
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                }
                finally
                {
                    adapter.Dispose();
                }
            }

            return captureTargets;
        }

        private static bool SupportsHDR(IDXGIOutput output)
        {
            IDXGIOutput6 output6 = null;

            try
            {
                output6 = output.QueryInterface<IDXGIOutput6>();
                OutputDescription1 desc1 = output6.Description1;

                return desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ||
                       desc1.ColorSpace == ColorSpaceType.RgbFullG10NoneP709;
            }
            catch
            {
                return false;
            }
            finally
            {
                output6?.Dispose();
            }
        }

        private static HDRCaptureResult CaptureOutputRegionSDR(Rectangle captureRect)
        {
            using Bitmap bitmap = CaptureRectangleNative(captureRect);

            if (bitmap == null)
            {
                return null;
            }

            return ConvertBitmapCaptureToFloat(bitmap);
        }

        private static HDRCaptureResult CreateCaptureResult(Format format, int width, int height, MappedSubresource mapped)
        {
            int sourceStride = (int)mapped.RowPitch;
            int bytesPerPixel = FormatBytesPerPixel(format);
            int destinationStride = width * bytesPerPixel;
            byte[] pixelData = new byte[height * destinationStride];

            unsafe
            {
                byte* sourcePointer = (byte*)mapped.DataPointer;

                for (int y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        (IntPtr)(sourcePointer + y * sourceStride),
                        pixelData,
                        y * destinationStride,
                        destinationStride);
                }
            }

            return format switch
            {
                Format.R16G16B16A16_Float => new HDRCaptureResult
                {
                    PixelData = pixelData,
                    Width = width,
                    Height = height,
                    PixelFormat = HDRPixelFormat.R16G16B16A16_Float,
                    Stride = destinationStride,
                    HasTrueHDRData = true
                },
                Format.R10G10B10A2_UNorm => new HDRCaptureResult
                {
                    PixelData = pixelData,
                    Width = width,
                    Height = height,
                    PixelFormat = HDRPixelFormat.R10G10B10A2_UNorm,
                    Stride = destinationStride,
                    HasTrueHDRData = true
                },
                Format.B8G8R8A8_UNorm => ConvertLdrCaptureToFloat(pixelData, width, height, destinationStride, true),
                _ => ConvertLdrCaptureToFloat(pixelData, width, height, destinationStride, false)
            };
        }

        private static HDRCaptureResult ConvertLdrCaptureToFloat(byte[] pixelData, int width, int height, int stride, bool isBgra)
        {
            HDRCaptureResult result = HDRCaptureResult.CreateEmpty(width, height);

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;

                for (int x = 0; x < width; x++)
                {
                    int pixelOffset = rowOffset + x * 4;
                    float r = pixelData[pixelOffset + (isBgra ? 2 : 0)] / 255.0f;
                    float g = pixelData[pixelOffset + 1] / 255.0f;
                    float b = pixelData[pixelOffset + (isBgra ? 0 : 2)] / 255.0f;
                    float a = pixelData[pixelOffset + 3] / 255.0f;
                    result.SetPixelLinear(x, y, SRGBToLinear(r), SRGBToLinear(g), SRGBToLinear(b), a);
                }
            }

            return result;
        }

        private static HDRCaptureResult ConvertBitmapCaptureToFloat(Bitmap bitmap)
        {
            Bitmap argbBitmap = EnsureArgbBitmap(bitmap, out bool disposeBitmap);

            try
            {
                Rectangle bounds = new Rectangle(0, 0, argbBitmap.Width, argbBitmap.Height);
                BitmapData bitmapData = argbBitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                try
                {
                    byte[] pixelData = new byte[argbBitmap.Width * argbBitmap.Height * 4];
                    int destinationStride = argbBitmap.Width * 4;

                    unsafe
                    {
                        byte* sourcePointer = (byte*)bitmapData.Scan0;

                        for (int y = 0; y < argbBitmap.Height; y++)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(
                                (IntPtr)(sourcePointer + y * bitmapData.Stride),
                                pixelData,
                                y * destinationStride,
                                destinationStride);
                        }
                    }

                    return ConvertLdrCaptureToFloat(pixelData, argbBitmap.Width, argbBitmap.Height, destinationStride, true);
                }
                finally
                {
                    argbBitmap.UnlockBits(bitmapData);
                }
            }
            finally
            {
                if (disposeBitmap)
                {
                    argbBitmap.Dispose();
                }
            }
        }

        private static Bitmap EnsureArgbBitmap(Bitmap bitmap, out bool disposeBitmap)
        {
            if (bitmap.PixelFormat == PixelFormat.Format32bppArgb)
            {
                disposeBitmap = false;
                return bitmap;
            }

            Bitmap argbBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);

            using (Graphics graphics = Graphics.FromImage(argbBitmap))
            {
                graphics.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
            }

            disposeBitmap = true;
            return argbBitmap;
        }

        private static Bitmap CaptureRectangleNative(Rectangle rect)
        {
            if (rect.Width == 0 || rect.Height == 0)
            {
                return null;
            }

            IntPtr handle = NativeMethods.GetDesktopWindow();
            IntPtr hdcSrc = NativeMethods.GetWindowDC(handle);
            IntPtr hdcDest = NativeMethods.CreateCompatibleDC(hdcSrc);
            IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcSrc, rect.Width, rect.Height);
            IntPtr hOld = NativeMethods.SelectObject(hdcDest, hBitmap);

            try
            {
                NativeMethods.BitBlt(hdcDest, 0, 0, rect.Width, rect.Height, hdcSrc, rect.X, rect.Y,
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                return Image.FromHbitmap(hBitmap);
            }
            finally
            {
                NativeMethods.SelectObject(hdcDest, hOld);
                NativeMethods.DeleteDC(hdcDest);
                NativeMethods.ReleaseDC(handle, hdcSrc);
                NativeMethods.DeleteObject(hBitmap);
            }
        }

        private static float SRGBToLinear(float srgb)
        {
            if (srgb <= 0.04045f)
            {
                return srgb / 12.92f;
            }

            return MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
        }

        private static int FormatBytesPerPixel(Format format)
        {
            return format switch
            {
                Format.R16G16B16A16_Float => 8,
                Format.R10G10B10A2_UNorm => 4,
                Format.B8G8R8A8_UNorm => 4,
                Format.R8G8B8A8_UNorm => 4,
                _ => 4
            };
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
            }
        }
    }
}
