// "Ask every time" for the iPhone ringtone's part (#1291): which presets and files ask, and the preset as it runs once
// the part is chosen in the Cut a clip window.

using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class AskedRingtoneTests
{
    private static Preset Ringtone() => BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.RingtoneId);

    [Fact]
    public void The_built_in_ringtone_preset_asks_for_the_part_and_a_plain_m4r_preset_does_not()
    {
        var preset = Ringtone();
        Assert.True(AskedRingtone.Asks(preset));
        preset.Media.AskRingtonePart = false;
        Assert.False(AskedRingtone.Asks(preset));
        Assert.False(AskedRingtone.Asks(new Preset { Id = "x", TargetFormat = "mp3", Media = { AskRingtonePart = true } }));
    }

    [Fact]
    public void Only_sounds_are_asked_about()
    {
        Assert.True(AskedRingtone.IsSound("song.mp3"));
        Assert.True(AskedRingtone.IsSound("song.flac"));
        Assert.False(AskedRingtone.IsSound("clip.mp4"));
        Assert.False(AskedRingtone.IsSound("photo.png"));
    }

    [Fact]
    public void The_chosen_part_is_a_copy_within_the_limits_that_does_not_ask_again()
    {
        var preset = Ringtone();
        var run = AskedRingtone.Apply(preset, 12.5, 25);
        Assert.Equal((12.5, 25), (run.Media.RingtoneStart, run.Media.RingtoneLength));
        Assert.False(run.Media.AskRingtonePart);
        // The saved preset is untouched.
        Assert.True(preset.Media.AskRingtonePart);
        Assert.Equal(0, preset.Media.RingtoneStart);

        Assert.Equal(MediaOptions.MaxRingtoneLength, AskedRingtone.Apply(preset, 0, 90).Media.RingtoneLength);
        Assert.Equal(MediaOptions.MinRingtoneLength, AskedRingtone.Apply(preset, 0, 0.2).Media.RingtoneLength);
        Assert.Equal(0, AskedRingtone.Apply(preset, -5, 10).Media.RingtoneStart);
        Assert.Equal(MediaOptions.DefaultRingtoneLength, AskedRingtone.Apply(preset, 0, double.NaN).Media.RingtoneLength);
    }
}
