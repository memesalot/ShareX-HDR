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
    public static class RadianceHDRWriter
    {
        private const float MinNonZeroValue = 1e-32f;

        public static bool TryWrite(string filePath, HDRCaptureResult hdrData, out string errorMessage)
        {
            errorMessage = null;

            if (hdrData == null || hdrData.Width <= 0 || hdrData.Height <= 0)
            {
                errorMessage = "Radiance HDR save failed because no HDR pixel data is available.";
                return false;
            }

            try
            {
                using FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                using BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII, false);

                WriteHeader(writer, hdrData.Width, hdrData.Height);

                byte[] red = new byte[hdrData.Width];
                byte[] green = new byte[hdrData.Width];
                byte[] blue = new byte[hdrData.Width];
                byte[] exponent = new byte[hdrData.Width];

                for (int y = 0; y < hdrData.Height; y++)
                {
                    for (int x = 0; x < hdrData.Width; x++)
                    {
                        hdrData.GetPixelLinear(x, y, out float r, out float g, out float b, out float _);
                        EncodeRgBe(r, g, b, out red[x], out green[x], out blue[x], out exponent[x]);
                    }

                    if (hdrData.Width >= 8 && hdrData.Width <= 0x7fff)
                    {
                        writer.Write((byte)2);
                        writer.Write((byte)2);
                        writer.Write((byte)(hdrData.Width >> 8));
                        writer.Write((byte)(hdrData.Width & 0xFF));

                        WriteRleChannel(writer, red);
                        WriteRleChannel(writer, green);
                        WriteRleChannel(writer, blue);
                        WriteRleChannel(writer, exponent);
                    }
                    else
                    {
                        for (int x = 0; x < hdrData.Width; x++)
                        {
                            writer.Write(red[x]);
                            writer.Write(green[x]);
                            writer.Write(blue[x]);
                            writer.Write(exponent[x]);
                        }
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Radiance HDR save failed.");
                errorMessage = e.Message;
                return false;
            }
        }

        private static void WriteHeader(BinaryWriter writer, int width, int height)
        {
            writer.Write(Encoding.ASCII.GetBytes("#?RADIANCE\n"));
            writer.Write(Encoding.ASCII.GetBytes("FORMAT=32-bit_rle_rgbe\n\n"));
            writer.Write(Encoding.ASCII.GetBytes($"-Y {height} +X {width}\n"));
        }

        private static void EncodeRgBe(float r, float g, float b, out byte rgbeR, out byte rgbeG, out byte rgbeB, out byte rgbeE)
        {
            r = MathF.Max(r, 0f);
            g = MathF.Max(g, 0f);
            b = MathF.Max(b, 0f);

            float maxChannel = MathF.Max(r, MathF.Max(g, b));

            if (maxChannel < MinNonZeroValue)
            {
                rgbeR = 0;
                rgbeG = 0;
                rgbeB = 0;
                rgbeE = 0;
                return;
            }

            int exponent = (int)MathF.Floor(MathF.Log2(maxChannel)) + 1;
            float scale = 256.0f / MathF.Pow(2.0f, exponent);

            rgbeR = ToByte(r * scale);
            rgbeG = ToByte(g * scale);
            rgbeB = ToByte(b * scale);
            rgbeE = (byte)(exponent + 128);
        }

        private static byte ToByte(float value)
        {
            return (byte)Math.Clamp((int)value, 0, 255);
        }

        private static void WriteRleChannel(BinaryWriter writer, byte[] channel)
        {
            int position = 0;

            while (position < channel.Length)
            {
                int runLength = GetRunLength(channel, position);

                if (runLength >= 4)
                {
                    writer.Write((byte)(128 + runLength));
                    writer.Write(channel[position]);
                    position += runLength;
                    continue;
                }

                int literalStart = position;
                int literalLength = 0;

                while (position < channel.Length && literalLength < 128)
                {
                    runLength = GetRunLength(channel, position);

                    if (runLength >= 4)
                    {
                        break;
                    }

                    position++;
                    literalLength++;
                }

                writer.Write((byte)literalLength);
                writer.Write(channel, literalStart, literalLength);
            }
        }

        private static int GetRunLength(byte[] channel, int startIndex)
        {
            int runLength = 1;
            byte value = channel[startIndex];

            while (startIndex + runLength < channel.Length && runLength < 127 && channel[startIndex + runLength] == value)
            {
                runLength++;
            }

            return runLength;
        }
    }
}
