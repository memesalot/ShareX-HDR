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
using ShareX.MediaLib;
using ShareX.Properties;
using ShareX.ScreenCaptureLib;
using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    public static class ScreenRecordManager
    {
        public static bool IsRecording { get; private set; }

        private static ScreenRecorder screenRecorder;
        private static ScreenRecordForm recordForm;

        public static void StartStopRecording(ScreenRecordOutput outputType, ScreenRecordStartMethod startMethod, TaskSettings taskSettings)
        {
            if (IsRecording)
            {
                if (recordForm != null && !recordForm.IsDisposed)
                {
                    recordForm.StartStopRecording();
                }
            }
            else
            {
                StartRecording(outputType, taskSettings, startMethod);
            }
        }

        public static void StopRecording()
        {
            if (IsRecording && screenRecorder != null)
            {
                screenRecorder.StopRecording();
            }
        }

        public static void PauseScreenRecording()
        {
            if (IsRecording && recordForm != null && !recordForm.IsDisposed)
            {
                recordForm.PauseResumeRecording();
            }
        }

        public static void AbortRecording()
        {
            if (IsRecording && recordForm != null && !recordForm.IsDisposed)
            {
                recordForm.AbortRecording();
            }
        }

        private static void StartRecording(ScreenRecordOutput outputType, TaskSettings taskSettings, ScreenRecordStartMethod startMethod = ScreenRecordStartMethod.Region)
        {
            if (outputType == ScreenRecordOutput.GIF)
            {
                taskSettings.CaptureSettings.FFmpegOptions.VideoCodec = FFmpegVideoCodec.gif;
            }

            bool useHDRGIFCapture = outputType == ScreenRecordOutput.GIF && taskSettings.CaptureSettings.ScreenRecordHDREnabled;
            bool useTwoPassEncoding = taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding;

            if (taskSettings.CaptureSettings.FFmpegOptions.IsAnimatedImage && !useHDRGIFCapture)
            {
                useTwoPassEncoding = true;
            }

            int fps;

            if (taskSettings.CaptureSettings.FFmpegOptions.VideoCodec == FFmpegVideoCodec.gif)
            {
                fps = taskSettings.CaptureSettings.GIFFPS;
            }
            else
            {
                fps = taskSettings.CaptureSettings.ScreenRecordFPS;
            }

            DebugHelper.WriteLine("Starting screen recording. Video encoder: \"{0}\", Audio encoder: \"{1}\", FPS: {2}",
                taskSettings.CaptureSettings.FFmpegOptions.VideoCodec.GetDescription(), taskSettings.CaptureSettings.FFmpegOptions.AudioCodec.GetDescription(), fps);

            if (!TaskHelpers.CheckFFmpeg(taskSettings))
            {
                return;
            }

            if (!taskSettings.CaptureSettings.FFmpegOptions.IsSourceSelected)
            {
                MessageBox.Show(Resources.FFmpeg_FFmpeg_video_and_audio_source_both_can_t_be__None__,
                    "ShareX - " + Resources.FFmpeg_FFmpeg_error, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (taskSettings.GeneralSettings.ToastWindowAutoHide)
            {
                NotificationForm.CloseActiveForm();
            }

            Rectangle captureRectangle = Rectangle.Empty;
            TaskMetadata metadata = new TaskMetadata();

            switch (startMethod)
            {
                case ScreenRecordStartMethod.Region:
                    if (taskSettings.CaptureSettings.ScreenRecordTransparentRegion)
                    {
                        RegionCaptureTasks.GetRectangleRegionTransparent(out captureRectangle);
                    }
                    else
                    {
                        RegionCaptureTasks.GetRectangleRegion(out captureRectangle, out WindowInfo windowInfo, taskSettings.CaptureSettings.SurfaceOptions);

                        metadata.UpdateInfo(windowInfo);
                    }
                    break;
                case ScreenRecordStartMethod.ActiveWindow:
                    if (taskSettings.CaptureSettings.CaptureClientArea)
                    {
                        captureRectangle = CaptureHelpers.GetActiveWindowClientRectangle();
                    }
                    else
                    {
                        captureRectangle = CaptureHelpers.GetActiveWindowRectangle();
                    }

                    IntPtr handle = NativeMethods.GetForegroundWindow();
                    WindowInfo activeWindowInfo = new WindowInfo(handle);
                    metadata.UpdateInfo(activeWindowInfo);
                    break;
                case ScreenRecordStartMethod.CustomRegion:
                    captureRectangle = taskSettings.CaptureSettings.CaptureCustomRegion;
                    break;
                case ScreenRecordStartMethod.LastRegion:
                    captureRectangle = Program.Settings.ScreenRecordRegion;
                    break;
            }

            Rectangle screenRectangle = CaptureHelpers.GetScreenBounds();
            captureRectangle = Rectangle.Intersect(captureRectangle, screenRectangle);

            if (taskSettings.CaptureSettings.FFmpegOptions.IsEvenSizeRequired)
            {
                captureRectangle = CaptureHelpers.EvenRectangleSize(captureRectangle);
            }

            if (IsRecording || !captureRectangle.IsValid() || screenRecorder != null)
            {
                return;
            }

            Program.Settings.ScreenRecordRegion = captureRectangle;

            IsRecording = true;

            string path = "";
            string concatPath = "";
            string tempPath = "";
            bool abortRequested = false;
            int gifSegmentIndex = 0;
            List<ScreenRecorder> gifSegments = useHDRGIFCapture ? new List<ScreenRecorder>() : null;
            List<string> gifSegmentPaths = useHDRGIFCapture ? new List<string>() : null;

            float duration = taskSettings.CaptureSettings.ScreenRecordFixedDuration ? taskSettings.CaptureSettings.ScreenRecordDuration : 0;

            recordForm = new ScreenRecordForm(captureRectangle)
            {
                ActivateWindow = startMethod == ScreenRecordStartMethod.Region,
                Duration = duration,
                AskConfirmationOnAbort = taskSettings.CaptureSettings.ScreenRecordAskConfirmationOnAbort
            };

            recordForm.StopRequested += StopRecording;
            recordForm.Show();

            Task.Run(() =>
            {
                try
                {
                    string extension;
                    if (useHDRGIFCapture)
                    {
                        extension = "gif";
                    }
                    else if (useTwoPassEncoding)
                    {
                        extension = "mp4";
                    }
                    else
                    {
                        extension = taskSettings.CaptureSettings.FFmpegOptions.Extension;
                    }
                    string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(taskSettings, metadata);
                    string fileName = TaskHelpers.GetFileName(taskSettings, extension, metadata);
                    path = TaskHelpers.HandleExistsFile(screenshotsFolder, fileName, taskSettings);

                    if (string.IsNullOrEmpty(path))
                    {
                        abortRequested = true;
                    }
                    else
                    {
                        if (!useHDRGIFCapture)
                        {
                            concatPath = FileHelpers.AppendTextToFileName(path, "-concat");
                            FileHelpers.DeleteFile(concatPath);
                            tempPath = FileHelpers.AppendTextToFileName(path, "-temp");
                            FileHelpers.DeleteFile(tempPath);
                        }
                    }

                    while (!abortRequested && (recordForm.Status == ScreenRecordingStatus.Waiting || recordForm.Status == ScreenRecordingStatus.Paused))
                    {
                        recordForm.ChangeState(ScreenRecordState.BeforeStart);

                        if (recordForm.Status == ScreenRecordingStatus.Paused || !taskSettings.CaptureSettings.ScreenRecordAutoStart)
                        {
                            recordForm.RecordResetEvent.WaitOne();
                        }
                        else
                        {
                            int delay = (int)(taskSettings.CaptureSettings.ScreenRecordStartDelay * 1000);

                            if (delay > 0)
                            {
                                recordForm.InvokeSafe(() => recordForm.StartCountdown(delay));

                                recordForm.RecordResetEvent.WaitOne(delay);
                            }
                        }

                        if (recordForm.Status == ScreenRecordingStatus.Aborted)
                        {
                            abortRequested = true;
                        }

                        if (recordForm.Status == ScreenRecordingStatus.Waiting || recordForm.Status == ScreenRecordingStatus.Paused)
                        {
                            if (!useHDRGIFCapture && recordForm.Status == ScreenRecordingStatus.Paused && File.Exists(path))
                            {
                                FileHelpers.RenameFile(path, concatPath);
                            }

                            recordForm.ChangeState(ScreenRecordState.AfterStart);

                            captureRectangle = recordForm.RecordingRegion;

                            // Sync HDR settings from TaskSettingsCapture to FFmpegOptions
                            taskSettings.CaptureSettings.FFmpegOptions.HDR = taskSettings.CaptureSettings.ScreenRecordHDREnabled;
                            if (taskSettings.CaptureSettings.ScreenRecordHDREnabled)
                            {
                                taskSettings.CaptureSettings.FFmpegOptions.HDRVideoCodec = taskSettings.CaptureSettings.ScreenRecordHDRCodec;
                                taskSettings.CaptureSettings.FFmpegOptions.HDRTransferFunction = taskSettings.CaptureSettings.ScreenRecordHDRTransferFunction;
                                taskSettings.CaptureSettings.FFmpegOptions.HDRColorSpace = taskSettings.CaptureSettings.ScreenRecordHDRColorSpace;
                                taskSettings.CaptureSettings.FFmpegOptions.HDR_Bitrate = taskSettings.CaptureSettings.ScreenRecordHDRBitrate;
                            }

                            ScreenRecordingOptions options = new ScreenRecordingOptions()
                            {
                                IsRecording = true,
                                IsLossless = useTwoPassEncoding,
                                FFmpeg = taskSettings.CaptureSettings.FFmpegOptions,
                                FPS = fps,
                                Duration = duration,
                                OutputPath = useHDRGIFCapture ? FileHelpers.AppendTextToFileName(path, $"-segment-{gifSegmentIndex++}") : path,
                                CaptureArea = captureRectangle,
                                DrawCursor = taskSettings.CaptureSettings.ScreenRecordShowCursor
                            };

                            if (useHDRGIFCapture)
                            {
                                FileHelpers.DeleteFile(options.OutputPath);
                                gifSegmentPaths.Add(options.OutputPath);
                            }

                            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
                            screenshot.CaptureCursor = taskSettings.CaptureSettings.ScreenRecordShowCursor;

                            if (useHDRGIFCapture)
                            {
                                screenshot.CaptureHDR = true;
                                screenshot.HDRToneMapAlgorithm = taskSettings.CaptureSettings.ScreenRecordHDRToneMap;
                            }

                            if (useHDRGIFCapture && screenRecorder != null)
                            {
                                gifSegments.Add(screenRecorder);
                            }
                            else
                            {
                                screenRecorder?.Dispose();
                            }

                            screenRecorder = new ScreenRecorder(useHDRGIFCapture ? ScreenRecordOutput.GIF : ScreenRecordOutput.FFmpeg, options, screenshot, captureRectangle);
                            screenRecorder.RecordingStarted += ScreenRecorder_RecordingStarted;
                            screenRecorder.EncodingProgressChanged += ScreenRecorder_EncodingProgressChanged;
                            screenRecorder.StartRecording();
                            recordForm.ChangeState(ScreenRecordState.RecordingEnd);

                            if (recordForm.Status == ScreenRecordingStatus.Aborted)
                            {
                                abortRequested = true;
                            }
                        }

                        TaskHelpers.PlayNotificationSoundAsync(NotificationSound.ActionCompleted, taskSettings);

                        if (!useHDRGIFCapture && File.Exists(concatPath))
                        {
                            using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(taskSettings.CaptureSettings.FFmpegOptions.FFmpegPath))
                            {
                                ffmpeg.ShowError = true;
                                ffmpeg.ConcatenateVideos(new string[] { concatPath, path }, tempPath, true);
                                FileHelpers.RenameFile(tempPath, path);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }

                if (useHDRGIFCapture && !abortRequested && screenRecorder != null)
                {
                    gifSegments.Add(screenRecorder);
                    screenRecorder = null;

                    if (gifSegments.Count > 0)
                    {
                        recordForm.ChangeState(ScreenRecordState.Encoding);
                        path = ProcessGIFSegments(path, gifSegments, taskSettings.ImageSettings.ImageGIFQuality, 1000 / fps);
                    }
                }
                else if (useTwoPassEncoding && !abortRequested && screenRecorder != null && File.Exists(path))
                {
                    recordForm.ChangeState(ScreenRecordState.Encoding);

                    path = ProcessTwoPassEncoding(path, metadata, taskSettings);
                }

                if (recordForm != null)
                {
                    recordForm.InvokeSafe(() =>
                    {
                        recordForm.Close();
                        recordForm.Dispose();
                        recordForm = null;
                    });
                }

                if (screenRecorder != null)
                {
                    screenRecorder.Dispose();
                    screenRecorder = null;
                }

                if (gifSegments != null)
                {
                    foreach (ScreenRecorder gifSegment in gifSegments)
                    {
                        gifSegment.Dispose();
                    }
                }

                if (abortRequested)
                {
                    FileHelpers.DeleteFile(path);
                }

                FileHelpers.DeleteFile(concatPath);
                FileHelpers.DeleteFile(tempPath);

                if (gifSegmentPaths != null)
                {
                    foreach (string gifSegmentPath in gifSegmentPaths)
                    {
                        FileHelpers.DeleteFile(gifSegmentPath);
                    }
                }
            }).ContinueInCurrentContext(() =>
            {
                if (!abortRequested && !string.IsNullOrEmpty(path) && File.Exists(path) && TaskHelpers.ShowAfterCaptureForm(taskSettings, out string customFileName, null, path))
                {
                    if (!string.IsNullOrEmpty(customFileName))
                    {
                        string currentFileName = Path.GetFileNameWithoutExtension(path);
                        string ext = Path.GetExtension(path);

                        if (!currentFileName.Equals(customFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            path = FileHelpers.RenameFile(path, customFileName + ext);
                        }
                    }

                    WorkerTask task = WorkerTask.CreateFileJobTask(path, metadata, taskSettings, customFileName);
                    TaskManager.Start(task);
                }

                abortRequested = false;
                IsRecording = false;
            });
        }

        private static void ScreenRecorder_RecordingStarted()
        {
            recordForm.ChangeState(ScreenRecordState.AfterRecordingStart);
        }

        private static void ScreenRecorder_EncodingProgressChanged(int progress)
        {
            recordForm.ChangeStateProgress(progress);
        }

        private static string ProcessTwoPassEncoding(string input, TaskMetadata metadata, TaskSettings taskSettings, bool deleteInputFile = true)
        {
            string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(taskSettings, metadata);
            string fileName = TaskHelpers.GetFileName(taskSettings, taskSettings.CaptureSettings.FFmpegOptions.Extension, metadata);
            string output = Path.Combine(screenshotsFolder, fileName);

            try
            {
                if (taskSettings.CaptureSettings.FFmpegOptions.VideoCodec == FFmpegVideoCodec.gif)
                {
                    screenRecorder.FFmpegEncodeAsGIF(input, output);
                }
                else
                {
                    screenRecorder.FFmpegEncodeVideo(input, output);
                }
            }
            finally
            {
                if (deleteInputFile && !input.Equals(output, StringComparison.OrdinalIgnoreCase) && File.Exists(input))
                {
                    File.Delete(input);
                }
            }

            return output;
        }

        private static string ProcessGIFSegments(string outputPath, List<ScreenRecorder> gifSegments, GIFQuality quality, int frameDelay)
        {
            FileHelpers.CreateDirectoryFromFilePath(outputPath);

            int totalFrames = 0;

            foreach (ScreenRecorder gifSegment in gifSegments)
            {
                totalFrames += gifSegment.CachedFrameCount;
            }

            using (AnimatedGifCreator gifEncoder = new AnimatedGifCreator(outputPath, frameDelay))
            {
                int currentFrame = 0;

                foreach (ScreenRecorder gifSegment in gifSegments)
                {
                    foreach (Image img in gifSegment.GetCachedFrames())
                    {
                        currentFrame++;

                        if (totalFrames > 0)
                        {
                            ScreenRecorder_EncodingProgressChanged((int)((float)currentFrame / totalFrames * 100));
                        }

                        using (img)
                        {
                            gifEncoder.AddFrame(img, quality);
                        }
                    }
                }
            }

            return outputPath;
        }
    }
}
