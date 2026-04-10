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
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace ShareX.ScreenCaptureLib
{
    public class ScreenRecordingOptions
    {
        private readonly record struct DdagrabCaptureTarget(int OutputIndex, Rectangle RelativeBounds);

        public bool IsRecording { get; set; }
        public bool IsLossless { get; set; }
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public int FPS { get; set; }
        public Rectangle CaptureArea { get; set; }
        public float Duration { get; set; }
        public bool DrawCursor { get; set; }
        public bool AllowHDRAutoFallback { get; set; } = true;
        public FFmpegOptions FFmpeg { get; set; } = new FFmpegOptions();

        public string GetFFmpegCommands()
        {
            string commands;

            if (IsRecording && !string.IsNullOrEmpty(FFmpeg.VideoSource) &&
                FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.ScreenCaptureRecorder.Value, StringComparison.OrdinalIgnoreCase))
            {
                // https://github.com/rdp/screen-capture-recorder-to-video-windows-free
                string registryPath = "Software\\screen-capture-recorder";
                RegistryHelpers.CreateRegistry(registryPath, "start_x", CaptureArea.X);
                RegistryHelpers.CreateRegistry(registryPath, "start_y", CaptureArea.Y);
                RegistryHelpers.CreateRegistry(registryPath, "capture_width", CaptureArea.Width);
                RegistryHelpers.CreateRegistry(registryPath, "capture_height", CaptureArea.Height);
                RegistryHelpers.CreateRegistry(registryPath, "default_max_fps", 60);
                RegistryHelpers.CreateRegistry(registryPath, "capture_mouse_default_1", DrawCursor ? 1 : 0);
            }

            if (!IsLossless && FFmpeg.UseCustomCommands && !string.IsNullOrEmpty(FFmpeg.CustomCommands))
            {
                commands = FFmpeg.CustomCommands.
                    Replace("$fps$", FPS.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_x$", CaptureArea.X.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_y$", CaptureArea.Y.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_width$", CaptureArea.Width.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_height$", CaptureArea.Height.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$cursor$", DrawCursor ? "1" : "0", StringComparison.OrdinalIgnoreCase).
                    Replace("$duration$", Duration.ToString("0.0", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).
                    Replace("$output$", Path.ChangeExtension(OutputPath, FFmpeg.Extension), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                commands = GetFFmpegArgs();
            }

            return commands.Trim();
        }

        public string GetFFmpegArgs(bool isCustom = false)
        {
            if (IsRecording && !FFmpeg.IsVideoSourceSelected && !FFmpeg.IsAudioSourceSelected)
            {
                return null;
            }

            StringBuilder args = new StringBuilder();

            string framerate = isCustom ? "$fps$" : FPS.ToString();

            if (IsRecording)
            {
                if (FFmpeg.IsVideoSourceSelected)
                {
                    if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.GDIGrab.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        if (FFmpeg.IsAudioSourceSelected)
                        {
                            AppendInputDevice(args, "dshow", true);
                            args.Append($"-i audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                        }

                        string x = isCustom ? "$area_x$" : CaptureArea.X.ToString();
                        string y = isCustom ? "$area_y$" : CaptureArea.Y.ToString();
                        string width = isCustom ? "$area_width$" : CaptureArea.Width.ToString();
                        string height = isCustom ? "$area_height$" : CaptureArea.Height.ToString();
                        string cursor = isCustom ? "$cursor$" : DrawCursor ? "1" : "0";

                        // https://ffmpeg.org/ffmpeg-devices.html#gdigrab
                        AppendInputDevice(args, "gdigrab", false);
                        args.Append($"-framerate {framerate} ");
                        args.Append($"-offset_x {x} ");
                        args.Append($"-offset_y {y} ");
                        args.Append($"-video_size {width}x{height} ");
                        args.Append($"-draw_mouse {cursor} ");
                        args.Append("-i desktop ");
                    }
                    else if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.DDAGrab.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        if (FFmpeg.IsAudioSourceSelected)
                        {
                            AppendInputDevice(args, "dshow", true);
                            args.Append($"-i audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                        }

                        bool useHDREncoding = ShouldUseHDREncoding();
                        DdagrabCaptureTarget captureTarget = ResolveDDAGrabCaptureTarget(CaptureArea);
                        Rectangle captureArea = captureTarget.RelativeBounds;

                        if (FFmpeg.IsEvenSizeRequired)
                        {
                            captureArea = CaptureHelpers.EvenRectangleSize(captureArea);
                        }

                        if (captureArea.Width <= 0 || captureArea.Height <= 0)
                        {
                            throw new InvalidOperationException("The selected recording region is too small for the current encoder settings.");
                        }

                        // https://ffmpeg.org/ffmpeg-filters.html#ddagrab
                        AppendInputDevice(args, "lavfi", false);
                        args.Append("-i ddagrab=");
                        args.Append($"output_idx={captureTarget.OutputIndex}:"); // DXGI Output Index to capture.
                        args.Append($"draw_mouse={DrawCursor.ToString().ToLowerInvariant()}:"); // Whether to draw the mouse cursor.
                        args.Append($"framerate={framerate}:"); // Framerate at which the desktop will be captured.
                        args.Append($"offset_x={captureArea.X}:"); // Horizontal offset of the captured video.
                        args.Append($"offset_y={captureArea.Y}:"); // Vertical offset of the captured video.
                        args.Append($"video_size={captureArea.Width}x{captureArea.Height}:"); // Specify the size of the captured video.

                        if (useHDREncoding)
                        {
                            args.Append($"allow_fallback={AllowHDRAutoFallback.ToString().ToLowerInvariant()}:");
                            args.Append("output_fmt=10bit"); // Valid ddagrab HDR format.
                        }
                        else
                        {
                            args.Append("output_fmt=bgra"); // Desired filter output format.
                        }

                        if (!useHDREncoding && FFmpeg.VideoCodec != FFmpegVideoCodec.h264_nvenc && FFmpeg.VideoCodec != FFmpegVideoCodec.hevc_nvenc)
                        {
                            args.Append(",hwdownload");
                            args.Append(",format=bgra");
                        }
                        else if (useHDREncoding)
                        {
                            // Download 10-bit frames to a software format accepted by CPU and hardware encoders.
                            args.Append(",hwdownload");
                            args.Append(",format=p010le");
                        }

                        args.Append(" ");
                    }
                    else
                    {
                        // https://ffmpeg.org/ffmpeg-devices.html#dshow
                        AppendInputDevice(args, "dshow", FFmpeg.IsAudioSourceSelected);
                        args.Append($"-framerate {framerate} ");
                        args.Append($"-i video={Helpers.EscapeCLIText(FFmpeg.VideoSource)}");

                        if (FFmpeg.IsAudioSourceSelected)
                        {
                            args.Append($":audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                        }
                        else
                        {
                            args.Append(" ");
                        }
                    }
                }
                else if (FFmpeg.IsAudioSourceSelected)
                {
                    AppendInputDevice(args, "dshow", true);
                    args.Append($"-i audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                }
            }
            else
            {
                args.Append($"-i \"{InputPath}\" ");
            }

            if (!string.IsNullOrEmpty(FFmpeg.UserArgs))
            {
                args.Append(FFmpeg.UserArgs + " ");
            }

            if (FFmpeg.IsVideoSourceSelected)
            {
                bool useHDREncoding = ShouldUseHDREncoding();

                if (useHDREncoding)
                {
                    AppendHDREncodingArgs(args);
                    args.Append($"-r {framerate} "); // output FPS
                }
                else if (IsLossless || FFmpeg.VideoCodec != FFmpegVideoCodec.apng)
                {
                    string videoCodec;

                    if (IsLossless)
                    {
                        videoCodec = FFmpegVideoCodec.libx264.ToString();
                    }
                    else if (FFmpeg.VideoCodec == FFmpegVideoCodec.libvpx_vp9)
                    {
                        videoCodec = "libvpx-vp9";
                    }
                    else
                    {
                        videoCodec = FFmpeg.VideoCodec.ToString();
                    }

                    args.Append($"-c:v {videoCodec} ");
                    args.Append($"-r {framerate} "); // output FPS
                }

                if (!useHDREncoding && IsLossless)
                {
                    args.Append($"-preset {FFmpegPreset.ultrafast} ");
                    args.Append($"-tune {FFmpegTune.zerolatency} ");
                    args.Append("-qp 0 ");
                }
                else if (!useHDREncoding)
                {
                    switch (FFmpeg.VideoCodec)
                    {
                        case FFmpegVideoCodec.libx264: // https://trac.ffmpeg.org/wiki/Encode/H.264
                        case FFmpegVideoCodec.libx265: // https://trac.ffmpeg.org/wiki/Encode/H.265
                            args.Append($"-preset {FFmpeg.x264_Preset} ");
                            if (IsRecording) args.Append($"-tune {FFmpegTune.zerolatency} ");
                            if (FFmpeg.x264_Use_Bitrate)
                            {
                                args.Append($"-b:v {FFmpeg.x264_Bitrate}k ");
                            }
                            else
                            {
                                args.Append($"-crf {FFmpeg.x264_CRF} ");
                            }
                            args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise can't stream in Chrome
                            args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                            break;
                        case FFmpegVideoCodec.libvpx: // https://trac.ffmpeg.org/wiki/Encode/VP8
                        case FFmpegVideoCodec.libvpx_vp9: // https://trac.ffmpeg.org/wiki/Encode/VP9
                            if (IsRecording) args.Append("-deadline realtime ");
                            args.Append($"-b:v {FFmpeg.VPx_Bitrate}k ");
                            args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise causing issues in Chrome related to WebM transparency support
                            break;
                        case FFmpegVideoCodec.libxvid: // https://trac.ffmpeg.org/wiki/Encode/MPEG-4
                            args.Append($"-qscale:v {FFmpeg.XviD_QScale} ");
                            break;
                        case FFmpegVideoCodec.h264_nvenc: // https://trac.ffmpeg.org/wiki/HWAccelIntro#NVENC
                        case FFmpegVideoCodec.hevc_nvenc:
                            args.Append($"-preset {FFmpeg.NVENC_Preset} ");
                            args.Append($"-tune {FFmpeg.NVENC_Tune} ");
                            args.Append($"-b:v {FFmpeg.NVENC_Bitrate}k ");
                            args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                            break;
                        case FFmpegVideoCodec.h264_amf:
                        case FFmpegVideoCodec.hevc_amf:
                            args.Append($"-usage {FFmpeg.AMF_Usage} ");
                            args.Append($"-quality {FFmpeg.AMF_Quality} ");
                            args.Append($"-b:v {FFmpeg.AMF_Bitrate}k ");
                            args.Append("-pix_fmt yuv420p ");
                            break;
                        case FFmpegVideoCodec.h264_qsv: // https://trac.ffmpeg.org/wiki/Hardware/QuickSync
                        case FFmpegVideoCodec.hevc_qsv:
                            args.Append($"-preset {FFmpeg.QSV_Preset} ");
                            args.Append($"-b:v {FFmpeg.QSV_Bitrate}k ");
                            break;
                        case FFmpegVideoCodec.libwebp: // https://www.ffmpeg.org/ffmpeg-codecs.html#libwebp
                            args.Append("-lossless 0 ");
                            args.Append("-preset default ");
                            args.Append("-loop 0 ");
                            break;
                        case FFmpegVideoCodec.apng:
                            args.Append("-f apng ");
                            args.Append("-plays 0 ");
                            break;
                    }

                    switch (FFmpeg.VideoCodec)
                    {
                        case FFmpegVideoCodec.libx265:
                        case FFmpegVideoCodec.hevc_nvenc:
                        case FFmpegVideoCodec.hevc_amf:
                        case FFmpegVideoCodec.hevc_qsv:
                            args.Append("-tag:v hvc1 "); // https://trac.ffmpeg.org/wiki/Encode/H.265#FinalCutandApplestuffcompatibility
                            break;
                    }
                }

            }

            if (FFmpeg.IsAudioSourceSelected)
            {
                switch (FFmpeg.AudioCodec)
                {
                    case FFmpegAudioCodec.libvoaacenc: // http://trac.ffmpeg.org/wiki/Encode/AAC
                        args.Append($"-c:a aac -ac 2 -b:a {FFmpeg.AAC_Bitrate}k "); // -ac 2 required otherwise failing with 7.1
                        break;
                    case FFmpegAudioCodec.libopus: // https://www.ffmpeg.org/ffmpeg-codecs.html#libopus-1
                        args.Append($"-c:a libopus -b:a {FFmpeg.Opus_Bitrate}k ");
                        break;
                    case FFmpegAudioCodec.libvorbis: // http://trac.ffmpeg.org/wiki/TheoraVorbisEncodingGuide
                        args.Append($"-c:a libvorbis -qscale:a {FFmpeg.Vorbis_QScale} ");
                        break;
                    case FFmpegAudioCodec.libmp3lame: // http://trac.ffmpeg.org/wiki/Encode/MP3
                        args.Append($"-c:a libmp3lame -qscale:a {FFmpeg.MP3_QScale} ");
                        break;
                }
            }

            if (Duration > 0)
            {
                string duration = isCustom ? "$duration$" : Duration.ToString("0.0", CultureInfo.InvariantCulture);
                args.Append($"-t {duration} "); // duration limit
            }

            args.Append("-y "); // overwrite file

            string output = isCustom ? "$output$" : Path.ChangeExtension(OutputPath, IsLossless ? "mp4" : FFmpeg.Extension);
            args.Append($"\"{output}\"");

            return args.ToString();
        }

        private void AppendHDREncodingArgs(StringBuilder args)
        {
            // Get color space and transfer function values
            string colorPrimaries = FFmpeg.HDRColorSpace == HDRColorSpace.BT2020 ? "bt2020" : "smpte432";
            string colorTrc = FFmpeg.HDRTransferFunction == HDRTransferFunction.PQ ? "smpte2084" : "arib-std-b67";
            string colorspace = "bt2020nc";

            // HDR metadata (Mastering Display and Content Light Level)
            // Using typical values for a 1000-nit display
            string masteringDisplay = "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)";
            string contentLightLevel = "1000,400";

            switch (FFmpeg.HDRVideoCodec)
            {
                case HDRVideoCodec.HDR10_HEVC:
                    // libx265 HDR10 encoding
                    args.Append("-c:v libx265 ");
                    args.Append("-preset medium ");
                    args.Append($"-b:v {FFmpeg.HDR_Bitrate}k ");
                    args.Append("-pix_fmt yuv420p10le "); // 10-bit 4:2:0
                    args.Append($"-color_primaries {colorPrimaries} ");
                    args.Append($"-color_trc {colorTrc} ");
                    args.Append($"-colorspace {colorspace} ");
                    args.Append($"-x265-params \"hdr10=1:repeat-headers=1:colorprim={colorPrimaries}:transfer={colorTrc}:colormatrix={colorspace}:master-display={masteringDisplay}:max-cll={contentLightLevel}\" ");
                    args.Append("-tag:v hvc1 ");
                    args.Append("-movflags +faststart ");
                    break;

                case HDRVideoCodec.HDR10_HEVC_NVENC:
                    // NVENC HEVC HDR10 encoding
                    args.Append("-c:v hevc_nvenc ");
                    args.Append("-preset p4 ");
                    args.Append("-tune hq ");
                    args.Append("-profile:v main10 "); // 10-bit profile
                    args.Append($"-b:v {FFmpeg.HDR_Bitrate}k ");
                    args.Append("-pix_fmt p010le "); // 10-bit NVENC format
                    args.Append($"-color_primaries {colorPrimaries} ");
                    args.Append($"-color_trc {colorTrc} ");
                    args.Append($"-colorspace {colorspace} ");
                    args.Append("-tag:v hvc1 ");
                    args.Append("-movflags +faststart ");
                    break;

                case HDRVideoCodec.HDR10_HEVC_AMF:
                    // AMF HEVC HDR10 encoding
                    args.Append("-c:v hevc_amf ");
                    args.Append("-usage transcoding ");
                    args.Append("-profile main10 ");
                    args.Append("-quality balanced ");
                    args.Append($"-b:v {FFmpeg.HDR_Bitrate}k ");
                    args.Append("-pix_fmt p010le ");
                    args.Append($"-color_primaries {colorPrimaries} ");
                    args.Append($"-color_trc {colorTrc} ");
                    args.Append($"-colorspace {colorspace} ");
                    args.Append("-tag:v hvc1 ");
                    break;

                case HDRVideoCodec.HDR10_HEVC_QSV:
                    // QSV HEVC HDR10 encoding
                    args.Append("-c:v hevc_qsv ");
                    args.Append("-preset medium ");
                    args.Append($"-b:v {FFmpeg.HDR_Bitrate}k ");
                    args.Append("-pix_fmt p010le ");
                    args.Append($"-color_primaries {colorPrimaries} ");
                    args.Append($"-color_trc {colorTrc} ");
                    args.Append($"-colorspace {colorspace} ");
                    args.Append("-tag:v hvc1 ");
                    break;

                case HDRVideoCodec.HLG_HEVC:
                    // HLG (Hybrid Log-Gamma) encoding
                    args.Append("-c:v libx265 ");
                    args.Append("-preset medium ");
                    args.Append($"-b:v {FFmpeg.HDR_Bitrate}k ");
                    args.Append("-pix_fmt yuv420p10le ");
                    args.Append($"-color_primaries {colorPrimaries} ");
                    args.Append("-color_trc arib-std-b67 "); // HLG
                    args.Append($"-colorspace {colorspace} ");
                    args.Append($"-x265-params \"repeat-headers=1:colorprim={colorPrimaries}:transfer=arib-std-b67:colormatrix={colorspace}\" ");
                    args.Append("-tag:v hvc1 ");
                    args.Append("-movflags +faststart ");
                    break;

                case HDRVideoCodec.HLG_HEVC_NVENC:
                    // NVENC HEVC HLG encoding
                    args.Append("-c:v hevc_nvenc ");
                    args.Append("-preset p4 ");
                    args.Append("-tune hq ");
                    args.Append("-profile:v main10 ");
                    args.Append($"-b:v {FFmpeg.HDR_Bitrate}k ");
                    args.Append("-pix_fmt p010le ");
                    args.Append($"-color_primaries {colorPrimaries} ");
                    args.Append("-color_trc arib-std-b67 "); // HLG
                    args.Append($"-colorspace {colorspace} ");
                    args.Append("-tag:v hvc1 ");
                    args.Append("-movflags +faststart ");
                    break;
            }
        }

        private bool ShouldUseHDREncoding()
        {
            if (!FFmpeg.HDR || !FFmpeg.IsVideoSourceSelected)
            {
                return false;
            }

            if (!IsRecording || FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.DDAGrab.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!AllowHDRAutoFallback)
            {
                throw new InvalidOperationException("HDR screen recording requires the DDAGrab video source.");
            }

            DebugHelper.WriteLine("HDR recording requested with a non-DDAGrab source. Falling back to SDR recording.");
            return false;
        }

        private static DdagrabCaptureTarget ResolveDDAGrabCaptureTarget(Rectangle captureArea)
        {
            if (captureArea.Width <= 0 || captureArea.Height <= 0)
            {
                throw new InvalidOperationException("The selected recording region is empty.");
            }

            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            int globalOutputIndex = 0;
            bool intersectsAnyOutput = false;

            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success; adapterIndex++)
            {
                try
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success; outputIndex++)
                    {
                        try
                        {
                            OutputDescription description = output.Description;
                            Rectangle outputBounds = new Rectangle(
                                description.DesktopCoordinates.Left,
                                description.DesktopCoordinates.Top,
                                description.DesktopCoordinates.Right - description.DesktopCoordinates.Left,
                                description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top);

                            Rectangle overlap = Rectangle.Intersect(outputBounds, captureArea);

                            if (overlap.Width > 0 && overlap.Height > 0)
                            {
                                intersectsAnyOutput = true;
                            }

                            if (outputBounds.Contains(captureArea))
                            {
                                Rectangle relativeBounds = new Rectangle(
                                    captureArea.X - outputBounds.X,
                                    captureArea.Y - outputBounds.Y,
                                    captureArea.Width,
                                    captureArea.Height);
                                return new DdagrabCaptureTarget(globalOutputIndex, relativeBounds);
                            }

                            globalOutputIndex++;
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

            if (intersectsAnyOutput)
            {
                throw new InvalidOperationException("DDAGrab can only record a region fully contained within a single monitor.");
            }

            throw new InvalidOperationException("The selected recording region does not intersect any active display output.");
        }

        private void AppendInputDevice(StringBuilder args, string inputDevice, bool audioSource)
        {
            args.Append($"-f {inputDevice} ");
            args.Append("-thread_queue_size 1024 "); // This option sets the maximum number of queued packets when reading from the file or device.
            args.Append("-rtbufsize 256M "); // Default real time buffer size is 3041280 (3M)

            if (audioSource)
            {
                args.Append("-audio_buffer_size 80 "); // Set audio device buffer size in milliseconds (which can directly impact latency, depending on the device).
            }
        }
    }
}
