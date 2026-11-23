using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyASR.Asr;
using MyASR.Audio;
using MyASR.Models;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("AsrEval manifest.tsv report.json [model-id]");
    return 2;
}

var manifest = Path.GetFullPath(args[0]);
var model = args.Length == 3
    ? ModelCatalog.All.Single(m => m.Id == args[2])
    : ModelCatalog.Default;
if (model.Kind != AsrEngineKind.RnnT)
    throw new ArgumentException("Select a GigaAM RNN-T model.");
using var engine = GigaAmRnnTEngine.Create(new ModelPaths(ModelRoot.Resolve(), model));
var rows = new List<Clip>();
foreach (var line in File.ReadLines(manifest, Encoding.UTF8))
{
    var fields = line.Split('\t');
    if (fields.Length != 2) throw new FormatException($"Invalid manifest row {rows.Count + 1}");
    var path = Path.GetFullPath(fields[0], Path.GetDirectoryName(manifest)!);
    var pcm = WavReader.ReadMono16kFloat(path);
    var duration = TimeSpan.FromSeconds(pcm.Length / 16000d);
    var timer = Stopwatch.StartNew();
    var hypothesis = engine.Transcribe(pcm, duration).Text;
    timer.Stop();
    var referenceWords = Words(fields[1]);
    var actualWords = Words(hypothesis);
    if (referenceWords.Length == 0) throw new FormatException($"Empty reference for {path}");
    var referenceChars = string.Concat(referenceWords).ToCharArray();
    var actualChars = string.Concat(actualWords).ToCharArray();
    rows.Add(new(Path.GetFileName(path), fields[1], hypothesis, duration.TotalSeconds,
        timer.Elapsed.TotalSeconds, referenceWords.Length, Distance(referenceWords, actualWords),
        referenceChars.Length, Distance(referenceChars, actualChars)));
}

if (rows.Count == 0) throw new FormatException("Empty manifest.");
var summary = new {
    model = model.Id, count = rows.Count,
    words = rows.Sum(r => r.Words), wordErrors = rows.Sum(r => r.WordErrors),
    wer = rows.Sum(r => r.WordErrors) / (double)rows.Sum(r => r.Words),
    cer = rows.Sum(r => r.CharErrors) / (double)rows.Sum(r => r.Chars),
    audioSeconds = rows.Sum(r => r.AudioSeconds),
    recognitionSeconds = rows.Sum(r => r.RecognitionSeconds)
};
var options = new JsonSerializerOptions { WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
File.WriteAllText(args[1], JsonSerializer.Serialize(new { summary, clips = rows }, options));
Console.WriteLine(JsonSerializer.Serialize(summary, options));
return 0;

static string[] Words(string text) => Regex.Matches(
    text.Normalize().ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{Nd}]+")
    .Select(m => m.Value).ToArray();

static int Distance<T>(T[] reference, T[] actual) where T : IEquatable<T>
{
    var row = Enumerable.Range(0, actual.Length + 1).ToArray();
    for (var i = 0; i < reference.Length; i++)
    {
        var diagonal = row[0];
        row[0] = i + 1;
        for (var j = 0; j < actual.Length; j++)
        {
            var previous = row[j + 1];
            row[j + 1] = Math.Min(Math.Min(row[j] + 1, previous + 1),
                diagonal + (reference[i].Equals(actual[j]) ? 0 : 1));
            diagonal = previous;
        }
    }
    return row[^1];
}

record Clip(string File, string Reference, string Hypothesis, double AudioSeconds,
    double RecognitionSeconds, int Words, int WordErrors, int Chars, int CharErrors);
