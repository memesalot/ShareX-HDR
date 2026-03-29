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
using System.IO;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Minimal OpenEXR writer for uncompressed scanline RGBA half-float images.
    /// Writes EXR version 2.0 single-part scanline format.
    /// </summary>
    public static class EXRWriter
    {
        private const int EXR_MAGIC = 20000630; // 0x01312F76
        private const int EXR_VERSION = 2;
        private const byte HALF_PIXEL_TYPE = 1; // HALF = 1
        private const byte NO_COMPRESSION = 0;

        public static void Write(string filePath, byte[] pixelData, int width, int height, int srcStride, HDRPixelFormat srcFormat,
            EXRCompression compression = EXRCompression.None)
        {
            if (compression != EXRCompression.None)
            {
                DebugHelper.WriteLine($"OpenEXR native codec is not bundled, falling back to uncompressed managed EXR output. Requested compression: {compression}.");
            }

            using FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using BinaryWriter writer = new BinaryWriter(fs);

            // Magic number and version
            writer.Write(EXR_MAGIC);
            writer.Write(EXR_VERSION); // version 2, flags = 0 (single-part scanline)

            // Header attributes
            WriteChannelList(writer, "channels");
            WriteCompressionAttribute(writer, "compression", NO_COMPRESSION);
            WriteBox2iAttribute(writer, "dataWindow", 0, 0, width - 1, height - 1);
            WriteBox2iAttribute(writer, "displayWindow", 0, 0, width - 1, height - 1);
            WriteLineOrderAttribute(writer, "lineOrder", 0); // INCREASING_Y
            WriteFloatAttribute(writer, "pixelAspectRatio", 1.0f);
            WriteV2fAttribute(writer, "screenWindowCenter", 0.0f, 0.0f);
            WriteFloatAttribute(writer, "screenWindowWidth", 1.0f);

            // End of header
            writer.Write((byte)0);

            // Scanline offset table
            int halfBytesPerPixel = 8; // RGBA x 2 bytes each (half-float)
            int scanlineDataSize = width * halfBytesPerPixel;
            // Each scanline block: 4 bytes (y coord) + 4 bytes (data size) + pixel data
            int scanlineBlockSize = 4 + 4 + scanlineDataSize;

            long offsetTablePosition = fs.Position;
            long firstScanlineOffset = offsetTablePosition + (long)height * 8; // 8 bytes per offset (int64)

            for (int y = 0; y < height; y++)
            {
                writer.Write(firstScanlineOffset + (long)y * scanlineBlockSize);
            }

            // Write scanline data
            byte[] scanlineBuffer = new byte[scanlineDataSize];

            for (int y = 0; y < height; y++)
            {
                writer.Write(y); // y coordinate
                writer.Write(scanlineDataSize); // data size

                if (srcFormat == HDRPixelFormat.R16G16B16A16_Float)
                {
                    // Data is already in RGBA half-float, but EXR stores channels separately (ABGR order by name sort)
                    // EXR channel order is alphabetical: A, B, G, R
                    int srcRowOffset = y * srcStride;

                    for (int x = 0; x < width; x++)
                    {
                        int srcPixelOffset = srcRowOffset + x * 8;

                        // Source is RGBA interleaved, EXR expects channel-separated but for simplicity
                        // we write interleaved RGBA which is valid if channels are declared in R,G,B,A order
                        // Actually, EXR requires channels sorted alphabetically and stored per-channel per scanline
                        // So we need to deinterleave: write all A values, then all B, then all G, then all R

                        // A channel
                        scanlineBuffer[x * 2] = pixelData[srcPixelOffset + 6];
                        scanlineBuffer[x * 2 + 1] = pixelData[srcPixelOffset + 7];

                        // B channel
                        scanlineBuffer[width * 2 + x * 2] = pixelData[srcPixelOffset + 4];
                        scanlineBuffer[width * 2 + x * 2 + 1] = pixelData[srcPixelOffset + 5];

                        // G channel
                        scanlineBuffer[width * 4 + x * 2] = pixelData[srcPixelOffset + 2];
                        scanlineBuffer[width * 4 + x * 2 + 1] = pixelData[srcPixelOffset + 3];

                        // R channel
                        scanlineBuffer[width * 6 + x * 2] = pixelData[srcPixelOffset];
                        scanlineBuffer[width * 6 + x * 2 + 1] = pixelData[srcPixelOffset + 1];
                    }
                }
                else // R10G10B10A2_UNorm - convert to half-float first
                {
                    int srcRowOffset = y * srcStride;

                    for (int x = 0; x < width; x++)
                    {
                        int srcPixelOffset = srcRowOffset + x * 4;
                        uint pixel = BitConverter.ToUInt32(pixelData, srcPixelOffset);

                        float r = (pixel & 0x3FF) / 1023.0f;
                        float g = ((pixel >> 10) & 0x3FF) / 1023.0f;
                        float b = ((pixel >> 20) & 0x3FF) / 1023.0f;
                        float a = ((pixel >> 30) & 0x3) / 3.0f;

                        ushort rHalf = FloatToHalf(r);
                        ushort gHalf = FloatToHalf(g);
                        ushort bHalf = FloatToHalf(b);
                        ushort aHalf = FloatToHalf(a);

                        // A channel
                        WriteUInt16ToBuffer(scanlineBuffer, x * 2, aHalf);
                        // B channel
                        WriteUInt16ToBuffer(scanlineBuffer, width * 2 + x * 2, bHalf);
                        // G channel
                        WriteUInt16ToBuffer(scanlineBuffer, width * 4 + x * 2, gHalf);
                        // R channel
                        WriteUInt16ToBuffer(scanlineBuffer, width * 6 + x * 2, rHalf);
                    }
                }

                writer.Write(scanlineBuffer);
            }
        }

        private static void WriteChannelList(BinaryWriter writer, string attrName)
        {
            WriteAttributeName(writer, attrName);
            WriteAttributeType(writer, "chlist");

            // Calculate size: each channel = name + null + pixelType(4) + pLinear(1) + reserved(3) + xSampling(4) + ySampling(4)
            // Channels: A, B, G, R (alphabetical order)
            // Size = sum of (name_len + 1 + 16) for each channel + 1 (null terminator)
            int size = (1 + 1 + 16) + (1 + 1 + 16) + (1 + 1 + 16) + (1 + 1 + 16) + 1; // A,B,G,R + null
            writer.Write(size);

            WriteChannel(writer, "A");
            WriteChannel(writer, "B");
            WriteChannel(writer, "G");
            WriteChannel(writer, "R");

            writer.Write((byte)0); // end of channel list
        }

        private static void WriteChannel(BinaryWriter writer, string name)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0); // null terminator
            writer.Write((int)HALF_PIXEL_TYPE); // pixel type: HALF
            writer.Write((byte)0); // pLinear (not perceptually linear)
            writer.Write((byte)0); // reserved
            writer.Write((byte)0); // reserved
            writer.Write((byte)0); // reserved
            writer.Write(1); // xSampling
            writer.Write(1); // ySampling
        }

        private static void WriteCompressionAttribute(BinaryWriter writer, string attrName, byte compression)
        {
            WriteAttributeName(writer, attrName);
            WriteAttributeType(writer, "compression");
            writer.Write(1); // size
            writer.Write(compression);
        }

        private static void WriteBox2iAttribute(BinaryWriter writer, string attrName, int xMin, int yMin, int xMax, int yMax)
        {
            WriteAttributeName(writer, attrName);
            WriteAttributeType(writer, "box2i");
            writer.Write(16); // size
            writer.Write(xMin);
            writer.Write(yMin);
            writer.Write(xMax);
            writer.Write(yMax);
        }

        private static void WriteLineOrderAttribute(BinaryWriter writer, string attrName, byte order)
        {
            WriteAttributeName(writer, attrName);
            WriteAttributeType(writer, "lineOrder");
            writer.Write(1); // size
            writer.Write(order);
        }

        private static void WriteFloatAttribute(BinaryWriter writer, string attrName, float value)
        {
            WriteAttributeName(writer, attrName);
            WriteAttributeType(writer, "float");
            writer.Write(4); // size
            writer.Write(value);
        }

        private static void WriteV2fAttribute(BinaryWriter writer, string attrName, float x, float y)
        {
            WriteAttributeName(writer, attrName);
            WriteAttributeType(writer, "v2f");
            writer.Write(8); // size
            writer.Write(x);
            writer.Write(y);
        }

        private static void WriteAttributeName(BinaryWriter writer, string name)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
        }

        private static void WriteAttributeType(BinaryWriter writer, string type)
        {
            writer.Write(Encoding.ASCII.GetBytes(type));
            writer.Write((byte)0);
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
    }
}
