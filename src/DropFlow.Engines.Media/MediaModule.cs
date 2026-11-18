using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Operations;
using DropFlow.Engines.Media.Runtime;
using DropFlow.Engines.Media.Subtitles;

namespace DropFlow.Engines.Media;

/// <summary>
/// Entry point of the media engine project. <see cref="Create(IEngineProvider)"/> returns every converter this project provides:
/// <list type="bullet">
/// <item><b>Conversions</b> <c>convert:{mp3 m4a wav flac ogg opus aiff wma aac m4r ac3 mp4 mkv webm avi wmv gif mov webp apng}</c> for the audio and
/// video categories, including read-only sources such as amr, ape, dsf, ts, vob and 3gp (FFmpeg; see <see cref="Operations.ConvertOperation"/>).</item>
/// <item><b>Video tools</b> vid.compress, vid.metadata, vid.mute, vid.trim, vid.crop, vid.rotate (lossless), vid.speed, vid.snapshot, vid.split, vid.join (multi), vid.redact, vid.target-size, vid.burn-subs (FFmpeg).</item>
/// <item><b>Audio tools</b> aud.compress, aud.metadata, aud.normalize, aud.trim, aud.trim-silence (also the sound of videos), aud.channels, aud.visualize, aud.bleep, aud.merge (multi) (FFmpeg).</item>
/// <item><b>Subtitles</b> <c>convert:srt|vtt|ass|txt|lrc|sbv</c> from srt, vtt, ass, smi, lrc, ttml/dfxp, sub (MicroDVD) and sbv (pure C#, no engine).</item>
/// </list>
/// Not implemented here: <c>vid.transcribe</c> / <c>aud.transcribe</c> (AI project) and <c>vid.encoder</c> (the Encoder window, which uses
/// <see cref="Encoding.EncoderPlanner"/> and <see cref="MediaEncoder"/>).
/// <para>
/// FFmpeg and ffprobe are located through the <see cref="IEngineProvider"/> at run time, so the module can be created before they are
/// installed; a run without FFmpeg fails with a message that names it. ffprobe is found next to ffmpeg when the provider does not list it.
/// </para>
/// </summary>
public static class MediaModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) => Create(new MediaServices(new ProcessMediaToolRunner(engines)));

    internal static IReadOnlyList<IConverter> Create(MediaServices services)
    {
        MediaOperation[] operations =
        [
            new ConvertOperation(),

            new VideoCompressOperation(),
            new VideoMetadataOperation(),
            new VideoMuteOperation(),
            new VideoTrimOperation(),
            new VideoCropOperation(),
            new VideoRotateOperation(),
            new VideoSpeedOperation(),
            new VideoSnapshotOperation(),
            new VideoSplitOperation(),
            new VideoJoinOperation(),
            new VideoRedactOperation(),
            new VideoTargetSizeOperation(),
            new VideoBurnSubsOperation(),

            new AudioCompressOperation(),
            new AudioMetadataOperation(),
            new AudioNormalizeOperation(),
            new AudioTrimOperation(),
            new AudioTrimSilenceOperation(),
            new AudioChannelsOperation(),
            new AudioVisualizeOperation(),
            new AudioBleepOperation(),
            new AudioMergeOperation(),
        ];

        var converters = new List<IConverter>(operations.Select(o => (IConverter)new MediaOperationConverter(o, services)))
        {
            new SubtitleConverter(),
        };
        return converters;
    }
}
