using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Tests;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Sessions;

internal static class ColorCompatibilityTests
{
    public static async Task Run(string root, bool before)
    {
        var folder=Path.Combine(root,"logs","color-compat");
        Directory.CreateDirectory(folder);
        var palette=File.ReadAllLines(Path.Combine(folder,"desktop-palette.tsv"))
            .Select(s=>s.Split('\t')).ToArray();
        int missing=palette.Count(p=>!EmuColor.TryFromName(p[0],out _));
        if(before)
        {
            if(EmuColor.TryFromName("SkyBlue",out _)) throw new Exception("Expected old SkyBlue rejection");
            Console.WriteLine($"REPRODUCED: Android core rejects SkyBlue; missing {missing}/{palette.Length} standard desktop palette names");
            using var old=new LoaderTestHarness(Path.Combine(root,"tests","color-compat","game"));
            try
            {
                await old.InitializeAsync(); old.Console._state.process=old.Process; old.Console.RunEmueraProgram("");
            }
            catch(InvalidOperationException) { }
            string text=string.Join("\n",old.Console.DisplayLineList.Select(l=>l?.ToString()));
            File.WriteAllText(Path.Combine(folder,"android-before-output.txt"),text);
            if(!text.Contains("SkyBlue") || text.Contains("COLOR_TEST_FINISHED")) throw new Exception("Expected original SkyBlue script failure");
            Console.WriteLine("REPRODUCED: same ERB cannot finish on the old Android core");
            return;
        }
        foreach(var pair in palette)
            foreach(var name in new[]{pair[0],pair[0].ToLowerInvariant(),pair[0].ToUpperInvariant()})
                if(!EmuColor.TryFromName(name,out var color) || color.A!=255 || (color.ToArgb()&0xFFFFFF)!=int.Parse(pair[1]) || EmuColor.FromName(name)!=color)
                    throw new Exception("Color mismatch: "+name);
        if(missing!=0 || EmuColor.TryFromName("NoSuchEraColor",out _) || EmuColor.TryFromName("",out _) || !EmuColor.TryFromName("grey",out var grey) || grey!=EmuColor.Gray)
            throw new Exception("Unknown-name or legacy alias regression");
        Console.WriteLine($"PASS: {palette.Length} desktop palette names, all lower/upper variants, exact RGB and legacy grey alias");
        using var harness=new LoaderTestHarness(Path.Combine(root,"tests","color-compat","game"));
        await harness.InitializeAsync(); harness.Console._state.process=harness.Process; harness.Console.RunEmueraProgram("");
        string output=string.Join("\n",harness.Console.DisplayLineList.Select(l=>l?.ToString()));
        File.WriteAllText(Path.Combine(folder,"android-after-output.txt"),output);
        if(!output.Contains("COLOR_TEST_FINISHED") || output.Contains("COLOR_MISMATCH") || !output.Contains("COLOR_COUNT="+palette.Length) || !output.Contains("UNKNOWN_COLOR=-1"))
            throw new Exception("Android color fixture failed; see android-after-output.txt");
        Console.WriteLine("PASS: identical ERB runs through SETCOLORBYNAME, SETBGCOLORBYNAME, COLOR_FROMNAME and HTML_PRINT to final input");
        harness.Dispose();
        // Mobile sessions load function bodies lazily. Check that path too, not just eager parsing.
        using(var session=new CoreSession(new CoreSessionOptions(Path.Combine(root,"tests","color-compat","game"),new FileSystemGameDirAccessor())))
        {
            var started=await session.StartAsync();
            if(!started.Succeeded) throw new Exception("Lazy session failed: "+started.ErrorMessage);
            string snapshot=System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot);
            File.WriteAllText(Path.Combine(folder,"android-lazy-snapshot.json"),snapshot);
            if(!snapshot.Contains("COLOR_TEST_FINISHED") || snapshot.Contains("COLOR_MISMATCH") || !snapshot.Contains("#87CEEB",StringComparison.OrdinalIgnoreCase))
                throw new Exception("Lazy session did not emit completed text with SkyBlue RGB");
            await session.StopAsync();
        }
        Console.WriteLine("PASS: production lazy session finishes the same fixture and emits SkyBlue #87CEEB for display");
        var commands=File.ReadAllLines(Path.Combine(folder,"game-color-commands.txt"));
        foreach(var command in commands)
            if(!EmuColor.TryFromName(command.Split(' ',StringSplitOptions.RemoveEmptyEntries)[^1],out _))
                throw new Exception("Current game's named color is missing: "+command);
        Console.WriteLine($"PASS: all {commands.Length} named-color commands found in the current game's ERB files are recognized");
        foreach(var instruction in new[]{"SETCOLORBYNAME NoSuchEraColor", "SETBGCOLORBYNAME NoSuchEraColor", "SETCOLORBYNAME Transparent", "SETBGCOLORBYNAME Transparent", "PRINTV COLOR_FROMNAME(\"Transparent\")", "HTML_PRINT \"<font color='Transparent'>INVALID</font>\""})
            await Reject(root,folder,instruction);
        Console.WriteLine("PASS: unknown named colors and transparent foreground/background/function/HTML remain invalid as on desktop");
    }
    static async Task Reject(string root,string output,string instruction)
    {
        string game=Path.Combine(root,"tests","color-compat","invalid");
        Directory.CreateDirectory(Path.Combine(game,"CSV")); Directory.CreateDirectory(Path.Combine(game,"ERB"));
        File.WriteAllText(Path.Combine(game,"ERB","INVALID.ERB"),"@SYSTEM_TITLE\n"+instruction+"\nPRINTL BAD_COLOR_ACCEPTED\nINPUT\n");
        using var h=new LoaderTestHarness(game);
        try { await h.InitializeAsync(); h.Console._state.process=h.Process; h.Console.RunEmueraProgram(""); }
        catch(InvalidOperationException) { }
        string text=string.Join("\n",h.Console.DisplayLineList.Select(l=>l?.ToString()));
        // Eager loading can quote subsequent source lines in diagnostics; check completed display.
        if(text.Contains("BAD_COLOR_ACCEPTED") || !(text.Contains("無効な色名") || text.Contains("Transparent")))
            throw new Exception("Invalid color was not rejected: "+instruction);
        File.AppendAllText(Path.Combine(output,"android-invalid-colors.txt"),instruction+"\n"+text+"\n\n");
    }
}
