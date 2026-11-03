// "Run a program": the command line of a preset — the argument template with its placeholders expanded,
// what asks for the output path, and the hash the confirmation step of an imported preset goes by.

using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class ProgramToolTests
{
    // ───────────────────────── BuildArguments ─────────────────────────

    [Fact]
    public void An_empty_template_gives_no_arguments()
    {
        Assert.Equal([], ProgramTool.BuildArguments("", ["C:\\a.jpg"], null));
        Assert.Equal([], ProgramTool.BuildArguments(null, ["C:\\a.jpg"], null));
    }

    [Fact]
    public void Plain_words_become_one_argument_each_and_quotes_group()
    {
        Assert.Equal(["--fast", "-o", "out.txt"], ProgramTool.BuildArguments("--fast -o out.txt", [], null));
        Assert.Equal(["--title", "my file"], ProgramTool.BuildArguments("--title \"my file\"", [], null));
        // Quotes inside the grouped text are not re-split, and spaces inside quotes survive.
        Assert.Equal(["a b  c"], ProgramTool.BuildArguments("\"a b  c\"", [], null));
    }

    [Fact]
    public void A_path_with_spaces_stays_one_argument()
    {
        var args = ProgramTool.BuildArguments("{input}", ["C:\\my dir\\my file.jpg"], null);
        Assert.Equal(["C:\\my dir\\my file.jpg"], args);
    }

    [Fact]
    public void A_path_with_non_ascii_letters_stays_one_argument()
    {
        var args = ProgramTool.BuildArguments("{input}", ["C:\\资料\\照片.jpg"], null);
        Assert.Equal(["C:\\资料\\照片.jpg"], args);
    }

    [Fact]
    public void Inputs_expands_to_one_argument_per_path()
    {
        var inputs = new[] { "C:\\a.jpg", "C:\\b.jpg", "C:\\c.jpg" };
        Assert.Equal(inputs, ProgramTool.BuildArguments("{inputs}", inputs, null));
        // Mixed with other words: the placeholder expands where it stands, in order.
        Assert.Equal(["--in", "C:\\a.jpg", "C:\\b.jpg", "--out", "x.zip"],
            ProgramTool.BuildArguments("--in {inputs} --out x.zip", ["C:\\a.jpg", "C:\\b.jpg"], null));
    }

    [Fact]
    public void Input_name_ext_and_dir_expand_from_the_first_file()
    {
        var args = ProgramTool.BuildArguments("{dir} {name} {ext} {input}", ["C:\\my dir\\photo.JPG"], null);
        Assert.Equal(["C:\\my dir", "photo", "JPG", "C:\\my dir\\photo.JPG"], args);
    }

    [Fact]
    public void Placeholders_expand_inside_one_argument_without_splitting_it()
    {
        // The placeholder sits in the middle of a word: one argument comes out, never three.
        Assert.Equal(["--in=C:\\my dir\\photo.jpg"], ProgramTool.BuildArguments("--in={input}", ["C:\\my dir\\photo.jpg"], null));
        Assert.Equal(["photo_out.png"], ProgramTool.BuildArguments("{name}_out.png", ["C:\\d\\photo.jpg"], null));
    }

    [Fact]
    public void Output_and_outputDir_expand_to_the_path_and_its_folder()
    {
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "out", "photo.png");
        Assert.Equal([output], ProgramTool.BuildArguments("{output}", ["C:\\d\\photo.jpg"], output));
        Assert.Equal([System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output))],
            ProgramTool.BuildArguments("{outputDir}", ["C:\\d\\photo.jpg"], output));
        // Without an output (a program that makes its own files) they expand to empty arguments.
        Assert.Equal([""], ProgramTool.BuildArguments("{output}", ["C:\\d\\photo.jpg"], null));
        Assert.Equal([""], ProgramTool.BuildArguments("{outputDir}", ["C:\\d\\photo.jpg"], null));
    }

    [Fact]
    public void Placeholders_are_matched_without_case()
    {
        Assert.Equal(["C:\\d\\photo.jpg"], ProgramTool.BuildArguments("{INPUT}", ["C:\\d\\photo.jpg"], null));
        Assert.Equal(["C:\\d\\photo.jpg"], ProgramTool.BuildArguments("{Inputs}", ["C:\\d\\photo.jpg"], null));
    }

    // ───────────────────────── ReferencesOutput ─────────────────────────

    [Fact]
    public void ReferencesOutput_says_which_templates_ask_for_the_output()
    {
        Assert.True(ProgramTool.ReferencesOutput("-o {output}"));
        Assert.True(ProgramTool.ReferencesOutput("--out-dir {outputDir}"));
        Assert.True(ProgramTool.ReferencesOutput("{OUTPUT}")); // without case
        Assert.False(ProgramTool.ReferencesOutput("{input} {inputs} {name}"));
        Assert.False(ProgramTool.ReferencesOutput(""));
        Assert.False(ProgramTool.ReferencesOutput(null));
    }

    // ───────────────────────── CommandHash ─────────────────────────

    [Fact]
    public void CommandHash_differs_for_a_different_program_or_command()
    {
        var a = ProgramTool.CommandHash("C:\\Tools\\convert.exe", "{input}");
        var b = ProgramTool.CommandHash("C:\\Tools\\other.exe", "{input}");
        var c = ProgramTool.CommandHash("C:\\Tools\\convert.exe", "{input} --fast");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void CommandHash_is_stable_and_ignores_case_of_the_program_path_and_surrounding_spaces()
    {
        var a = ProgramTool.CommandHash("C:\\Tools\\Convert.EXE", " {input} ");
        var b = ProgramTool.CommandHash("c:\\tools\\convert.exe", "{input}");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA-256 as hex
        Assert.Equal(a, ProgramTool.CommandHash("C:\\Tools\\Convert.EXE", " {input} "));
    }
}
