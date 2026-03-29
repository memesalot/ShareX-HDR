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
using ShareX.Properties;
using System;
using System.IO;
using System.Windows.Forms;

namespace ShareX
{
    public class ImageData : IDisposable
    {
        public MemoryStream ImageStream { get; set; }
        public EImageFormat ImageFormat { get; set; }

        /// <summary>
        /// When set, the prepared image is backed by a temp HDR output file instead of ImageStream.
        /// Write() and OpenReadStream() will read from this file until Dispose() cleans it up.
        /// </summary>
        public string HDRFilePath { get; set; }

        public Stream OpenReadStream()
        {
            if (!string.IsNullOrEmpty(HDRFilePath) && File.Exists(HDRFilePath))
            {
                return new FileStream(HDRFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            if (ImageStream != null)
            {
                if (ImageStream.CanSeek)
                {
                    ImageStream.Position = 0;
                }

                return new MemoryStream(ImageStream.ToArray(), writable: false);
            }

            return null;
        }

        public bool Write(string filePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(HDRFilePath) && File.Exists(HDRFilePath))
                {
                    File.Copy(HDRFilePath, filePath, true);
                    return true;
                }

                if (ImageStream != null && !string.IsNullOrEmpty(filePath))
                {
                    return ImageStream.WriteToFile(filePath);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);

                string message = $"{Resources.ImageData_Write_Error_Message}\r\n\"{filePath}\"";

                if (e is UnauthorizedAccessException || e is FileNotFoundException)
                {
                    message += "\r\n\r\n" + Resources.YourAntiVirusSoftwareOrTheControlledFolderAccessFeatureInWindowsCouldBeBlockingShareX;
                }

                MessageBox.Show(message, "ShareX - " + Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return false;
        }

        public void Dispose()
        {
            ImageStream?.Dispose();

            if (!string.IsNullOrEmpty(HDRFilePath) && File.Exists(HDRFilePath))
            {
                try
                {
                    File.Delete(HDRFilePath);
                }
                catch { }
            }
        }
    }
}
