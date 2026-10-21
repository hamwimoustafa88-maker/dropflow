using DropFlow.Engines.Common;

namespace DropFlow.AI.Tests;

/// <summary>
/// Makes real speech for tests with the text-to-speech voices built into Windows (SAPI), run through Windows PowerShell so no
/// package is needed. Output is exactly what Whisper reads: 16 kHz, mono, 16-bit WAV.
/// </summary>
public static class SpokenAudio
{
    private const string Script = """
        param([string]$Path, [string]$Text)
        Add-Type -AssemblyName System.Speech
        $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
        if ($synth.GetInstalledVoices().Count -eq 0) { Write-Error 'No text-to-speech voice is installed'; exit 3 }
        $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
        $synth.SetOutputToWaveFile($Path, $format)
        $synth.Speak($Text)
        $synth.Dispose()
        """;

    public static async Task<string> GenerateAsync(string wavPath, string text, string scratchDirectory)
    {
        var script = Path.Combine(scratchDirectory, "speak.ps1");
        await File.WriteAllTextAsync(script, Script);
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var run = await ProcessRunner.RunAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, wavPath, text],
            new ProcessOptions { Timeout = TimeSpan.FromMinutes(2) });
        if (!run.Success || !File.Exists(wavPath))
            throw new InvalidOperationException("Windows speech synthesis is not available here: " + run.LastMessage);
        return wavPath;
    }
}
