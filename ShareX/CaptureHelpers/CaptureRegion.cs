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
using ShareX.ScreenCaptureLib;
using System.Drawing;
using System.Windows.Forms;

namespace ShareX
{
    public class CaptureRegion : CaptureBase
    {
        protected static RegionCaptureType lastRegionCaptureType = RegionCaptureType.Default;

        public RegionCaptureType RegionCaptureType { get; protected set; }

        public CaptureRegion()
        {
        }

        public CaptureRegion(RegionCaptureType regionCaptureType)
        {
            RegionCaptureType = regionCaptureType;
        }

        protected override TaskMetadata Execute(TaskSettings taskSettings)
        {
            switch (RegionCaptureType)
            {
                default:
                case RegionCaptureType.Default:
                    return ExecuteRegionCapture(taskSettings);
                case RegionCaptureType.Light:
                    return ExecuteRegionCaptureLight(taskSettings);
                case RegionCaptureType.Transparent:
                    return ExecuteRegionCaptureTransparent(taskSettings);
            }
        }

        protected TaskMetadata ExecuteRegionCapture(TaskSettings taskSettings)
        {
            RegionCaptureMode mode;

            if (taskSettings.AdvancedSettings.RegionCaptureDisableAnnotation)
            {
                mode = RegionCaptureMode.Default;
            }
            else
            {
                mode = RegionCaptureMode.Annotation;
            }

            Bitmap canvas;
            Screenshot previewScreenshot = TaskHelpers.GetScreenshot(taskSettings);
            previewScreenshot.CaptureCursor = false;
            previewScreenshot.CaptureHDR = false;

            if (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode)
            {
                canvas = previewScreenshot.CaptureActiveMonitor();
            }
            else
            {
                canvas = previewScreenshot.CaptureFullscreen();
            }

            CursorData cursorData = null;

            if (taskSettings.CaptureSettings.ShowCursor)
            {
                cursorData = new CursorData();
            }

            using (RegionCaptureForm form = new RegionCaptureForm(mode, taskSettings.CaptureSettingsReference.SurfaceOptions, canvas))
            {
                if (cursorData != null && cursorData.IsVisible)
                {
                    form.AddCursor(cursorData.ToBitmap(), form.PointToClient(cursorData.DrawPosition));
                }

                form.ShowDialog();

                TaskMetadata metadata = null;

                if (taskSettings.ImageSettings.HDRCaptureEnabled && form.IsPlainRectangleRegionSelection)
                {
                    Rectangle captureRect = form.GetSelectedRectangle();

                    if (!captureRect.IsEmpty)
                    {
                        Screenshot regionScreenshot = TaskHelpers.GetScreenshot(taskSettings);
                        Bitmap hdrResult = regionScreenshot.CaptureRectangle(captureRect);

                        if (hdrResult != null)
                        {
                            metadata = new TaskMetadata(hdrResult);
                            TaskHelpers.TransferHDRData(regionScreenshot, metadata);
                        }
                    }
                }

                if (metadata == null)
                {
                    Bitmap result = form.GetResultImage();

                    if (result != null)
                    {
                        metadata = new TaskMetadata(result);
                        TaskHelpers.TransferHDRData(previewScreenshot, metadata, false);
                    }
                }

                if (!ReferenceEquals(metadata?.HDRData, previewScreenshot.LastHDRCaptureResult))
                {
                    previewScreenshot.LastHDRCaptureResult?.Dispose();
                }

                if (metadata != null)
                {
                    if (form.IsImageModified)
                    {
                        AllowAnnotation = false;
                    }

                    if (form.Result == RegionResult.Region)
                    {
                        WindowInfo windowInfo = form.GetWindowInfo();
                        metadata.UpdateInfo(windowInfo);
                    }

                    lastRegionCaptureType = RegionCaptureType.Default;

                    return metadata;
                }
            }

            return null;
        }

        protected TaskMetadata ExecuteRegionCaptureLight(TaskSettings taskSettings)
        {
            Bitmap canvas;
            Screenshot previewScreenshot = TaskHelpers.GetScreenshot(taskSettings);
            previewScreenshot.CaptureHDR = false;

            if (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode)
            {
                canvas = previewScreenshot.CaptureActiveMonitor();
            }
            else
            {
                canvas = previewScreenshot.CaptureFullscreen();
            }

            bool activeMonitorMode = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;

            using (RegionCaptureLightForm rectangleLight = new RegionCaptureLightForm(canvas, activeMonitorMode))
            {
                if (rectangleLight.ShowDialog() == DialogResult.OK)
                {
                    Screenshot captureScreenshot = TaskHelpers.GetScreenshot(taskSettings);
                    Bitmap result = rectangleLight.GetAreaImage(captureScreenshot);

                    if (result != null)
                    {
                        lastRegionCaptureType = RegionCaptureType.Light;

                        TaskMetadata metadata = new TaskMetadata(result);
                        TaskHelpers.TransferHDRData(captureScreenshot, metadata);
                        return metadata;
                    }
                }
            }

            return null;
        }

        protected TaskMetadata ExecuteRegionCaptureTransparent(TaskSettings taskSettings)
        {
            bool activeMonitorMode = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;

            using (RegionCaptureLightForm rectangleTransparent = new RegionCaptureLightForm(null, activeMonitorMode))
            {
                if (rectangleTransparent.ShowDialog() == DialogResult.OK)
                {
                    Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
                    Bitmap result = rectangleTransparent.GetAreaImage(screenshot);

                    if (result != null)
                    {
                        lastRegionCaptureType = RegionCaptureType.Transparent;

                        TaskMetadata metadata = new TaskMetadata(result);
                        TaskHelpers.TransferHDRData(screenshot, metadata);
                        return metadata;
                    }
                }
            }

            return null;
        }
    }
}
