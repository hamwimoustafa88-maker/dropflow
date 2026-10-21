using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Runtime;

namespace DropFlow.Engines.Media.Operations;

/// <summary>Runs caller-supplied <see cref="EncoderSettings"/> (passed as the job option "settings"). Used by <see cref="MediaEncoder"/>; not registered in the module.</summary>
internal sealed class EncodeSettingsOperation(string suffix) : MediaOperation
{
    public const string OperationId = "vid.encoder";
    public const string SettingsKey = "settings";

    public override IReadOnlyCollection<string> Ids => [OperationId];

    public override bool Supports(string operation, string inputExtension) => operation == OperationId;

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        if (!ctx.Job.Opts.TryGetValue(SettingsKey, out var value) || value is not EncoderSettings settings)
            throw new MediaFailure("No encoder settings were given.");

        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var dry = EncoderPlanner.Plan(settings, info, await ctx.PlanContextAsync(null, settings).ConfigureAwait(false));
        if (!dry.Ok) throw new MediaFailure(dry.Error!);

        var output = ctx.NewFile(dry.OutputExtension, suffix);
        await ctx.EncodeAsync(settings, info, output).ConfigureAwait(false);
        return [output];
    }
}
