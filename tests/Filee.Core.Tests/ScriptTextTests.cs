// Lua, PowerShell, AutoHotkey and batch scripts, SQL and HiveQL, DBML database schemas, INI files, RouterOS exports, CSS
// style sheets, Mermaid diagrams and C, C++, C#, Dart, Java, Rust, Go, Kotlin, Swift, Scala, SCSS, Vue and TSX source
// files are plain text (issues #388, #368, #442, #607, #536, #630, #661, #660, #864, #828, #980, #997, #854, #1176,
// #1309): recognised, and learnt by existing text donuts. UDF disc images (#1338) and iPhone ringtones (#1291) are
// recognised too.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ScriptTextTests
{
    [Theory]
    [InlineData("init.lua")]
    [InlineData("site.css")]
    [InlineData("Backup.ps1")]
    [InlineData("Hotkeys.AHK")]
    [InlineData("desktop.ini")]
    [InlineData("router-export.rsc")]
    [InlineData("install.cmd")]
    [InlineData("query.sql")]
    [InlineData("main.c")]
    [InlineData("stdio.h")]
    [InlineData("Widget.CPP")]
    [InlineData("parser.cc")]
    [InlineData("parser.cxx")]
    [InlineData("vector.hpp")]
    [InlineData("vector.hh")]
    [InlineData("vector.hxx")]
    [InlineData("MyBot.cs")]
    [InlineData("main.dart")]
    [InlineData("daily_report.HQL")]
    [InlineData("shop.dbml")]
    [InlineData("HelloWorld.java")]
    [InlineData("Legacy.JAVA")]
    [InlineData("flow.mermaid")]
    [InlineData("flow.MMD")]
    [InlineData("main.rs")]
    [InlineData("server.go")]
    [InlineData("App.kt")]
    [InlineData("View.swift")]
    [InlineData("Main.scala")]
    [InlineData("theme.scss")]
    [InlineData("Home.vue")]
    [InlineData("Button.tsx")]
    public void Scripts_and_style_sheets_are_text(string name) => Assert.Equal("txt", FormatRegistry.Detect(name)?.Id);

    [Fact]
    public void Version_33_text_donuts_learn_them_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(e => e is "lua" or "css");
        profiles.Insert(0, new ToolbarProfile { Id = "my-web", Name = "Web", Extensions = ["css"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(33, presets, profiles));

        var text = profiles.Single(p => p.Id == "text").Extensions;
        Assert.Contains("lua", text);
        Assert.DoesNotContain("css", text); // the user's own donut has it
        Assert.False(SettingsMigrations.ApplyToLibrary(33, presets, profiles));
    }

    [Fact]
    public void Version_38_text_donuts_learn_powershell_scripts()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.Remove("ps1");

        Assert.True(SettingsMigrations.ApplyToLibrary(38, presets, profiles));

        Assert.Contains("ps1", profiles.Single(p => p.Id == "text").Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(39, presets, profiles));
    }

    [Fact]
    public void Version_48_text_donuts_learn_sql_ahk_ini_rsc_and_cmd_without_taking_custom_claims()
    {
        string[] added = ["sql", "ahk", "ini", "rsc", "cmd"];
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(added.Contains);
        profiles.Insert(0, new ToolbarProfile { Id = "my-router", Name = "Router", Extensions = ["rsc"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(48, presets, profiles));

        var text = profiles.Single(p => p.Id == "text").Extensions;
        Assert.All(added.Where(e => e != "rsc"), e => Assert.Contains(e, text));
        Assert.DoesNotContain("rsc", text); // the user's own donut has it
        Assert.False(SettingsMigrations.ApplyToLibrary(48, presets, profiles));
    }

    [Fact]
    public void Version_57_text_donuts_learn_c_cpp_and_cs_without_taking_custom_claims()
    {
        string[] added = ["c", "h", "cpp", "cc", "cxx", "hpp", "hh", "hxx", "cs"];
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(added.Contains);
        profiles.Insert(0, new ToolbarProfile { Id = "my-trading", Name = "cTrader", Extensions = ["cs"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(57, presets, profiles));

        var text = profiles.Single(p => p.Id == "text").Extensions;
        Assert.All(added.Where(e => e != "cs"), e => Assert.Contains(e, text));
        Assert.DoesNotContain("cs", text); // the user's own donut has it
        Assert.False(SettingsMigrations.ApplyToLibrary(57, presets, profiles));
    }

    [Fact]
    public void Version_60_donuts_learn_dart_hql_and_kra_without_taking_custom_claims()
    {
        string[] added = ["dart", "hql", "kra"];
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(added.Contains);
        profiles.Insert(0, new ToolbarProfile { Id = "my-hive", Name = "Hive", Extensions = ["hql"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(60, presets, profiles));

        var text = profiles.Single(p => p.Id == "text").Extensions;
        Assert.Contains("dart", text);
        Assert.DoesNotContain("hql", text); // the user's own donut has it
        Assert.Contains("kra", profiles.Single(p => p.Id == "images").Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(60, presets, profiles));
    }

    [Fact]
    public void Version_64_text_donuts_learn_dbml_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var text = profiles.Single(p => p.Id == "text").Extensions;
        text.Remove("dbml");

        Assert.True(SettingsMigrations.ApplyToLibrary(64, presets, profiles));
        Assert.Contains("dbml", text);
        Assert.False(SettingsMigrations.ApplyToLibrary(64, presets, profiles));

        text.Remove("dbml");
        profiles.Insert(0, new ToolbarProfile { Id = "my-schemas", Name = "Schemas", Extensions = ["dbml"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(64, presets, profiles));
        Assert.DoesNotContain("dbml", text); // the user's own donut has it
    }

    [Fact]
    public void A_java_file_uses_the_text_donut() =>
        Assert.Equal("text", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["HelloWorld.java"])?.Id);

    [Fact]
    public void Version_70_text_donuts_learn_java_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var text = profiles.Single(p => p.Id == "text").Extensions;
        text.Remove("java");

        Assert.True(SettingsMigrations.ApplyToLibrary(70, presets, profiles));
        Assert.Contains("java", text);
        Assert.False(SettingsMigrations.ApplyToLibrary(70, presets, profiles));

        text.Remove("java");
        profiles.Insert(0, new ToolbarProfile { Id = "my-java", Name = "Java", Extensions = ["JAVA"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(70, presets, profiles));
        Assert.DoesNotContain("java", text); // the user's own donut has it
    }

    [Theory]
    [InlineData("movie.udf", "udf", "archive", false)]
    [InlineData("Ringtone.M4R", "m4r", "audio", true)]
    public void Disc_images_and_ringtones_are_recognised(string name, string id, string category, bool writable)
    {
        var format = FormatRegistry.Detect(name);
        Assert.Equal(id, format?.Id);
        Assert.Equal(category, format?.Category.ToString().ToLowerInvariant());
        Assert.Equal(writable, format?.Writable);
    }

    [Fact]
    public void Version_76_donuts_learn_the_new_formats_without_taking_custom_claims()
    {
        string[] text = ["mermaid", "mmd", "rs", "go", "kt", "swift", "scala", "scss", "vue", "tsx"];
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(e => e is "udf" or "m4r" || text.Contains(e));
        profiles.Insert(0, new ToolbarProfile { Id = "my-rust", Name = "Rust", Extensions = ["rs"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(76, presets, profiles));

        var learnt = profiles.Single(p => p.Id == "text").Extensions;
        Assert.All(text.Where(e => e != "rs"), e => Assert.Contains(e, learnt));
        Assert.DoesNotContain("rs", learnt); // the user's own donut has it
        Assert.Contains("udf", profiles.Single(p => p.Id == "archives").Extensions);
        Assert.Contains("m4r", profiles.Single(p => p.Id == "audio").Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(76, presets, profiles));
    }
}
