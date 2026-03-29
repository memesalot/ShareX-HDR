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

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Legacy settings placeholder kept only so older task-settings files can deserialize cleanly.
    /// The runtime hdrify dependency is no longer used by the HDR image pipeline.
    /// </summary>
    public class HdrifyOptions
    {
        public bool OverrideCLIPath { get; set; } = false;
        public string CLIPath { get; set; } = "";

        public string HdrifyPath
        {
            get
            {
                if (OverrideCLIPath && !string.IsNullOrEmpty(CLIPath))
                {
                    return FileHelpers.GetAbsolutePath(CLIPath);
                }

                return Path.Combine(GetDefaultHdrifyDirectory(), "hdrify.exe");
            }
        }

        public bool IsHdrifyAvailable()
        {
            return File.Exists(HdrifyPath);
        }

        private static string GetDefaultHdrifyDirectory()
        {
            if (!IsInstalledUnderProtectedProgramFiles())
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "ShareX", "Tools");
        }

        private static bool IsInstalledUnderProtectedProgramFiles()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            return baseDirectory.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) ||
                baseDirectory.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase);
        }
    }
}
