using StashKitMaker.Core;
using StashKitMaker.App;
using System.Text;
using System.Text.Json;

var tests = new (string, Action)[] {
 ("SHA-256 and exact deduplication", TestDedup), ("generated-kit deletion safety",TestGeneratedKitSafety), ("context-aware classification", TestClassification), ("loop, song, and tag screening",TestScreening),
 ("deterministic naming and safety", TestNaming), ("approval boundary and copy integrity", TestBuild), ("failed copies omitted from manifest", TestFailedCopyManifest),
 ("partial FLP event parsing and source integrity", TestFlp), ("pitched and self-cut MIDI sampler preview", TestMidiSamplerPreview), ("path resolution ambiguity", TestResolution), ("legacy final-kit state migration",TestLegacyState),
 ("FL Studio hierarchy colours and NFO sidecars",TestFlStudioMetadata), ("kit-wide IconIndex application",TestKitWideIconIndex) };
var failed = 0; foreach (var (name, test) in tests) try { test(); Console.WriteLine($"PASS {name}"); } catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed"); return failed;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static string Temp() { var p = Path.Combine(Path.GetTempPath(), "stashkit-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
static void TestDedup() { var d = Temp(); try { var a = Path.Combine(d, "Kick 04.wav"); var b = Path.Combine(d, "HARDKICK.wav"); File.WriteAllBytes(a, new byte[] { 1, 2, 3 }); File.Copy(a, b); var x = new Deduplicator().Consolidate(new[] { (a, "Beat1.flp"), (b, "Beat2.flp"), (a, "Beat3.flp") }); Check(x.Count == 1, "duplicate audio was not consolidated"); Check(x[0].SourcePaths.Count == 2 && x[0].Projects.Count == 3, "provenance lost"); } finally { Directory.Delete(d, true); } }
static void TestGeneratedKitSafety() { var d = Temp(); try { Check(!GeneratedKitSafety.IsGeneratedKit(d), "arbitrary folder accepted as generated kit"); var metadata = Directory.CreateDirectory(Path.Combine(d, "_metadata")); File.WriteAllText(Path.Combine(metadata.FullName, ".stashkitmaker"), "generated-by=StashKitMaker"); Check(GeneratedKitSafety.IsGeneratedKit(d), "valid generated kit not recognized"); } finally { Directory.Delete(d, true); } }
static void TestClassification() { var c = new SampleClassifier().Classify("Snare 04.wav", "Rim"); Check(c.Category == SampleCategory.Rim, "strong FL channel evidence should win"); Check(c.Evidence.Any(), "reasoning missing"); }
static void TestScreening() { var d = Temp(); try { var tag = Path.Combine(d, "producer tag.wav"); File.WriteAllBytes(tag, new byte[] { 1 }); Check(SampleScreening.ShouldExclude(tag, out _), "producer tag was retained"); var loop = Path.Combine(d, "melody loop 140.wav"); File.WriteAllBytes(loop, new byte[] { 1 }); Check(SampleScreening.ShouldExclude(loop, out _), "loop was retained"); var longWav = Path.Combine(d, "audio.wav"); using (var f = File.Create(longWav)) using (var w = new BinaryWriter(f)) { var dataSize = 44100 * 2 * 13; w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataSize); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16); w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(dataSize); f.SetLength(44 + dataSize); } Check(SampleScreening.ShouldExclude(longWav, out _), "long-form WAV was retained"); } finally { Directory.Delete(d, true); } }
static void TestNaming() { var c = new Classification(SampleCategory.EightOhEight, null, .9, Array.Empty<string>(), "Hard", null, "F Sharp"); var n = new SweetNameGenerator(); var a = n.Generate(new string('a', 64), c); Check(a == n.Generate(new string('a', 64), c), "name not deterministic"); Check(a.Contains("Hard 808 F Sharp") && !a.Contains("Producer"), "technical label lost"); var varied = Enumerable.Range(0, 100).Select(i => n.Generate(i.ToString("x64"), c)).Distinct(StringComparer.OrdinalIgnoreCase).Count(); Check(varied > 55, "curated naming variation is too low"); Check(WindowsNames.Sanitize("CON") == "CON Sample", "reserved filename unsafe"); }
static void TestBuild() { var d = Temp(); try { var source = Path.Combine(d, "source.wav"); File.WriteAllBytes(source, new byte[] { 9, 8, 7, 6 }); var before = Hashing.Sha256(source); var p = new PlannedFile(before, source, Path.Combine("Kicks", "Purin Kick.wav"), new[] { source }, new[] { "Beat.flp" }, new(SampleCategory.Kick, null, .9, Array.Empty<string>())); var plan = new BuildPlan("Kit", d, new[] { p }, false); var builder = new KitBuilder(); try { builder.BuildAsync(plan, false).GetAwaiter().GetResult(); throw new Exception("approval not enforced"); } catch (InvalidOperationException) { } Check(!Directory.Exists(plan.RootPath), "pre-approval write occurred"); var r = builder.BuildAsync(plan, true).GetAwaiter().GetResult(); Check(r.Copied == 1 && Hashing.Sha256(source) == before, "source changed"); Check(Hashing.Sha256(Path.Combine(plan.RootPath, p.RelativePath)) == before, "copy bytes changed"); var second = Path.Combine(d, "second.wav"); File.WriteAllBytes(second, new byte[] { 4, 5, 6 }); var hash2 = Hashing.Sha256(second); var p2 = new PlannedFile(hash2, second, Path.Combine("Snares", "Purin Snare.wav"), new[] { second }, new[] { "Beat2.flp" }, new(SampleCategory.Snare, null, .9, Array.Empty<string>())); builder.BuildAsync(new BuildPlan("Kit", d, new[] { p2 }, false, true), true).GetAwaiter().GetResult(); var manifest = File.ReadAllText(Path.Combine(plan.RootPath, "_metadata", "manifest.json")); Check(manifest.Contains(before) && manifest.Contains(hash2), "merge did not append manifest"); } finally { Directory.Delete(d, true); } }
static void TestFailedCopyManifest() { var d = Temp(); try { var missing = Path.Combine(d, "missing.wav"); var fakeHash = new string('f', 64); var file = new PlannedFile(fakeHash, missing, Path.Combine("Unknown", "Missing Unknown.wav"), new[] { missing }, Array.Empty<string>(), new(SampleCategory.Unknown, null, .1, Array.Empty<string>())); var result = new KitBuilder().BuildAsync(new BuildPlan("Kit", d, new[] { file }, false), true).GetAwaiter().GetResult(); Check(result.Copied == 0 && result.Failures.Count == 1, "copy failure not reported"); var manifest = File.ReadAllText(Path.Combine(result.RootPath, "_metadata", "manifest.json")); Check(!manifest.Contains(fakeHash), "failed copy was written to manifest"); } finally { Directory.Delete(d, true); } }
static void TestFlp()
{
    var d = Temp();
    try
    {
        var p = Path.Combine(d, "test.flp");
        using (var f = File.Create(p))
        using (var w = new BinaryWriter(f))
        {
            w.Write(Encoding.ASCII.GetBytes("FLhd")); w.Write((uint)6); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)96);
            using var data = new MemoryStream();
            using (var e = new BinaryWriter(data, Encoding.UTF8, true))
            {
                WriteEvent(e, 199, Encoding.UTF8.GetBytes("21.0\0"));
                WriteDwordEvent(e, 0x9C, 140000);
                WriteWordEvent(e, 0x40, 4);
                WriteEvent(e, 0xCB, Encoding.UTF8.GetBytes("HI HAT\0"));
                WriteEvent(e, 0xC4, Encoding.UTF8.GetBytes("Samples\\closed hat.wav\0"));
                WriteWordEvent(e, 0x41, 2);
                WriteEvent(e, 0xC1, Encoding.UTF8.GetBytes("Hat Bounce\0"));
                WriteEvent(e, 0xD0, NoteBytes(position: 24, channelId: 4, length: 48, key: 60, velocity: 101));
            }
            w.Write(Encoding.ASCII.GetBytes("FLdt")); w.Write((uint)data.Length); w.Write(data.ToArray());
        }
        var before = Hashing.Sha256(p);
        var a = new EventStreamFlpParser().ParseProject(p);
        Check(a.SampleReferences.Count == 1 && a.SampleReferences[0].ChannelName == "HI HAT" && a.SampleReferences[0].ChannelId == 4, "sampler channel was not retained");
        Check(a.Ppq == 96 && Math.Abs(a.TempoBpm - 140) < .001, "FL timing metadata was not retained");
        Check(a.MidiPatterns.Count == 1 && a.MidiPatterns[0].PatternName == "Hat Bounce" && a.MidiPatterns[0].SamplePath!.EndsWith("closed hat.wav"), "pattern MIDI was not aligned to its sampler sound");
        var note = a.MidiPatterns[0].Notes.Single();
        Check(note.Position == 24 && note.Length == 48 && note.Key == 60 && note.Velocity == 101, "FL note data changed during extraction");
        var midi = StandardMidiFile.Create(a.MidiPatterns[0], a.Ppq, a.TempoBpm);
        Check(Encoding.ASCII.GetString(midi, 0, 4) == "MThd" && Encoding.ASCII.GetString(midi, 14, 4) == "MTrk", "extracted file is not Standard MIDI");
        Check(StandardMidiFile.Fingerprint(midi, a.MidiPatterns[0].SamplePath) == StandardMidiFile.Fingerprint(StandardMidiFile.Create(a.MidiPatterns[0], a.Ppq, a.TempoBpm), a.MidiPatterns[0].SamplePath), "MIDI export is not deterministic");
        Check(Hashing.Sha256(p) == before, "FLP changed");
    }
    finally { Directory.Delete(d, true); }
}
static void WriteEvent(BinaryWriter w, byte id, byte[] b) { w.Write(id); var n = (uint)b.Length; while (n >= 128) { w.Write((byte)((n & 127) | 128)); n >>= 7; } w.Write((byte)n); w.Write(b); }
static void WriteWordEvent(BinaryWriter w, byte id, ushort value) { w.Write(id); w.Write(value); }
static void WriteDwordEvent(BinaryWriter w, byte id, uint value) { w.Write(id); w.Write(value); }
static byte[] NoteBytes(uint position, ushort channelId, uint length, ushort key, byte velocity)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(position); writer.Write((ushort)0); writer.Write(channelId); writer.Write(length); writer.Write(key); writer.Write((ushort)0);
    writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)64); writer.Write(velocity); writer.Write((byte)0); writer.Write((byte)0);
    return stream.ToArray();
}
static void TestMidiSamplerPreview()
{
    const int sampleRate = 8000;
    var source = Enumerable.Range(0, 1600).Select(index => index / 1600f).ToArray();
    var notes = new[]
    {
        new MidiNoteEvent(0, 10, 60, 127),
        new MidiNoteEvent(10, 10, 72, 127)
    };
    var provider = new MidiSamplerWaveProvider(source, 1, sampleRate, notes, ppq: 100, tempoBpm: 60);
    var rendered = new List<byte>();
    var buffer = new byte[256];
    int read;
    while ((read = provider.Read(buffer, 0, buffer.Length)) > 0) rendered.AddRange(buffer.Take(read));
    short Frame(int frame) => BitConverter.ToInt16(rendered.ToArray(), frame * 2);
    Check(provider.TotalFrames == 1600, "preview length did not account for the octave-up playback rate");
    Check(Math.Abs(Frame(400) / (double)short.MaxValue - .25) < .02, "C5 did not play at the sample's original pitch");
    Check(Math.Abs(Frame(800) / (double)short.MaxValue) < .02, "the next MIDI note did not cut and restart the previous sound");
    Check(Math.Abs(Frame(880) / (double)short.MaxValue - .10) < .02, "the octave-up MIDI note was not pitch-transposed");
}
static void TestResolution() { var d = Temp(); try { var a = Directory.CreateDirectory(Path.Combine(d, "a")).FullName; var b = Directory.CreateDirectory(Path.Combine(d, "b")).FullName; File.WriteAllText(Path.Combine(a, "x.wav"), "a"); File.WriteAllText(Path.Combine(b, "x.wav"), "b"); var r = new PathResolver(new[] { d }).Resolve("x.wav", Path.Combine(d, "song.flp")); Check(r.Status == ResolutionStatus.Ambiguous && r.Candidates.Count == 2, "ambiguity hidden"); } finally { Directory.Delete(d, true); } }
static void TestLegacyState() { var state=AppStateStore.Deserialize("{\"KitParent\":\"C:\\\\Kits\",\"KitName\":\"Old Kit\",\"History\":null,\"ProcessedSounds\":{\"ABC\":{\"Hash\":\"ABC\"}}}"); Check(state.KitEntries.Count==0&&state.KitGroups.Count==0,"new final-kit fields did not receive safe defaults");Check(state.SampleLibraryRoots.Count==0&&state.SamplePathOverrides.Count==0&&state.ActiveStack.Count==0&&state.ExtractedMidiPatterns.Count==0&&state.StackName=="My Stack","new Vault, MIDI Stack, and Rescue fields did not receive safe defaults");Check(state.UiAccentColor=="#A66F83","legacy settings did not receive the muted UI colour default");Check(AppStateStore.Deserialize("{\"UiAccentColor\":\"#6F859F\"}").UiAccentColor=="#6F859F","custom UI colour did not survive state deserialization");Check(state.History.Count==0,"null legacy history was not repaired");Check(state.ProcessedSounds.ContainsKey("abc"),"stable ID lookup is not case-insensitive after migration");var item=new FinalKitItemViewModel{StableId="sound-1",DisplayName="Old",UnicodeIcon="★",TextColor="#C04375",AudioPath="C:\\sound.wav"};item.DisplayName="New";Check(item.StableId=="sound-1"&&item.UnicodeIcon=="★"&&item.TextColor=="#C04375"&&item.AudioPath.EndsWith("sound.wav"),"renaming discarded associated final-kit state");var presentation=new KitStateFile{SoundUnicodeIcon="◆",SoundTextColor="#454142"};var reopened=JsonSerializer.Deserialize<KitStateFile>(JsonSerializer.Serialize(presentation));Check(reopened?.Version==3&&reopened.SoundUnicodeIcon=="◆"&&reopened.SoundTextColor=="#454142"&&reopened.BaseColor=="#93977F","current kit metadata state did not persist");var legacyPresentation=JsonSerializer.Deserialize<KitStateFile>("{\"Version\":1,\"Entries\":[{\"StableId\":\"old\",\"UnicodeIcon\":\"★\",\"TextColor\":\"#C04375\"}]}");Check(legacyPresentation?.Version==1&&legacyPresentation.Entries.Count==1,"version 1 kit presentation no longer loads"); }
static void TestFlStudioMetadata() { var exact=FlStudioColors.DeriveHsl(new HslColor(142,.40,.35),0);var sub=FlStudioColors.DeriveHsl(new HslColor(142,.40,.35),1);var deep=FlStudioColors.DeriveHsl(new HslColor(142,.40,.35),2);Check(Math.Abs(exact.Saturation-.40)<1e-12&&Math.Abs(sub.Saturation-.24)<1e-12&&Math.Abs(deep.Saturation-.12)<1e-12,"relative saturation must remain 40%, 24%, and 12%");var muted=FlStudioColors.HexToHsl("#93977F");var mutedPalette=FlStudioColors.CreatePalette("#93977F");Check(mutedPalette.Main.WebHex=="#93977F"&&Math.Abs(mutedPalette.Main.Hsl.Saturation-muted.Saturation)<1e-12,"muted base colour was not preserved exactly");var nearGrey=FlStudioColors.CreatePalette("#80817F");Check(nearGrey.Subfolder.Hsl.Saturation<=nearGrey.Main.Hsl.Saturation*.6000001&&nearGrey.Deeper.Hsl.Saturation<=nearGrey.Main.Hsl.Saturation*.3000001,"near-grey hierarchy became unexpectedly vivid");var conversions=new Dictionary<string,string>{{"#FF0000","$0000FF"},{"#00FF00","$00FF00"},{"#0000FF","$FF0000"},{"#FFFFFF","$FFFFFF"},{"#000000","$000000"},{"#FA30B1","$B130FA"}};foreach(var pair in conversions){Check(FlStudioColors.WebToFlBgr(pair.Key)==pair.Value,$"bad FL BGR conversion for {pair.Key}");Check(FlStudioColors.FlBgrToWeb(pair.Value)==pair.Key,$"bad reverse FL BGR conversion for {pair.Value}");}var d=Temp();try{var kit=Directory.CreateDirectory(Path.Combine(d,"My Kit")).FullName;var eights=Directory.CreateDirectory(Path.Combine(kit,"808s")).FullName;var claps=Directory.CreateDirectory(Path.Combine(kit,"Claps")).FullName;Directory.CreateDirectory(Path.Combine(kit,"_metadata"));File.WriteAllText(Path.Combine(kit,"_metadata",".stashkitmaker"),"generated-by=StashKitMaker");File.WriteAllText(Path.Combine(eights,"a.wav"),"a");File.WriteAllText(Path.Combine(claps,"b.wav"),"b");File.WriteAllText(Path.Combine(kit,"_metadata","manifest.json"),"[{\"RelativePath\":\"808s\\\\a.wav\",\"Hash\":\"a\"},{\"RelativePath\":\"Claps\\\\b.wav\",\"Hash\":\"b\"}]");var folders=new[]{new StagedKitFolder("kit-root",null,"Renamed Kit","",1014,5,8,"Root\nkit"),new StagedKitFolder("group:808","kit-root","Low End","808s",1015,5,8),new StagedKitFolder("group:clap","group:808","Claps","Claps",1016,5,8)};var applied=FlStudioKitMetadata.Apply(kit,"#93977F",folders);Check(Path.GetFileName(applied.RootPath)=="Renamed Kit"&&!Directory.Exists(kit),"kit root rename was not applied");Check(File.Exists(Path.Combine(applied.RootPath,"Low End","a.wav"))&&File.Exists(Path.Combine(applied.RootPath,"Low End","Claps","b.wav")),"folder move lost kit contents");var expectedSidecars=new[]{applied.RootPath+".nfo",Path.Combine(applied.RootPath,"Low End.nfo"),Path.Combine(applied.RootPath,"Low End","Claps.nfo")};Check(expectedSidecars.All(File.Exists)&&applied.Sidecars.Count==3&&Directory.EnumerateFiles(d,"*.nfo.txt",SearchOption.AllDirectories).Any()==false,"expected same-named NFO sidecars were not generated");var rootNfo=File.ReadAllText(expectedSidecars[0]);Check(rootNfo=="Tip=Root kit\r\nColor=$7F9793\r\nIconIndex=1014\r\nHeightOfs=5\r\nSortGroup=8\r\n"&&!rootNfo.Contains("Bitmap="),"root NFO metadata/order is incorrect");Check(File.ReadAllText(expectedSidecars[1]).Contains("Color="+mutedPalette.Subfolder.FlStudioBgr)&&File.ReadAllText(expectedSidecars[2]).Contains("Color="+mutedPalette.Deeper.FlStudioBgr),"folder NFO hierarchy colours disagree with preview derivation");var manifest=File.ReadAllText(Path.Combine(applied.RootPath,"_metadata","manifest.json"));Check(manifest.Contains("Low End\\\\a.wav")&&manifest.Contains("Low End\\\\Claps\\\\b.wav"),"manifest paths were not synchronized with folder hierarchy");}finally{Directory.Delete(d,true);} }

static void TestKitWideIconIndex()
{
    Check(FlStudioBrowserIcons.CodePointForIndex(22)==0xF116&&FlStudioBrowserIcons.CodePointForIndex(40)==0xF128,"FL Browser IconIndex was not aligned to the installed ILGlyphsEx Browser range");
    var d=Temp();
    try
    {
        var kit=Directory.CreateDirectory(Path.Combine(d,"Icon Kit")).FullName;
        var drums=Directory.CreateDirectory(Path.Combine(kit,"Drums")).FullName;
        var hats=Directory.CreateDirectory(Path.Combine(drums,"Hats")).FullName;
        var metadata=Directory.CreateDirectory(Path.Combine(kit,"_metadata")).FullName;
        File.WriteAllText(Path.Combine(metadata,".stashkitmaker"),"generated-by=StashKitMaker");
        File.WriteAllText(Path.Combine(metadata,"manifest.json"),"[]");
        FlStudioNfo.WriteBesideFolder(kit,new("$112233",1010,5,8,"root"));
        FlStudioNfo.WriteBesideFolder(drums,new("$445566",1011,5,8,"drums"));
        var folders=new[]{new StagedKitFolder("kit-root",null,"Icon Kit","",85,5,8,"root"),new StagedKitFolder("drums","kit-root","Drums","Drums",85,5,8,"drums"),new StagedKitFolder("hats","drums","Hats",Path.Combine("Drums","Hats"),85,5,8,"hats")};
        var result=FlStudioKitMetadata.ApplyIconIndexes(kit,"#93977F",folders);
        Check(result.FolderCount==3&&result.Sidecars.Count==3,"IconIndex was not written to every kit folder");
        Check(new[]{kit,drums,hats}.All(path=>FlStudioNfo.TryReadIconIndex(path,out var value)&&value==85),"IconIndex read-back was not uniform");
        Check(File.ReadAllText(FlStudioNfo.SidecarPath(kit)).Contains("Color=$112233"),"icon-only apply changed the existing root colour");
        Check(File.ReadAllText(FlStudioNfo.SidecarPath(drums)).Contains("Tip=drums"),"icon-only apply discarded existing metadata");
        var pendingDifferentIcon=folders.Select(folder=>folder with{IconIndex=593}).ToArray();
        FlStudioKitMetadata.Apply(kit,"#A0A0A0",pendingDifferentIcon,preserveExistingIconIndexes:true);
        Check(new[]{kit,drums,hats}.All(path=>FlStudioNfo.TryReadIconIndex(path,out var value)&&value==85),"colour-only apply changed the confirmed IconIndex");
    }
    finally{Directory.Delete(d,true);}
}
