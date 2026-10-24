using SkiaSharp;
using Svg.Skia;

if (args.Length != 1 || !Directory.Exists(args[0]))
    throw new ArgumentException("Pass the Icons directory as the only argument.");

string iconsDirectory = Path.GetFullPath(args[0]);
foreach (string source in Directory.GetFiles(iconsDirectory, "*.svg"))
{
    string markup = File.ReadAllText(source).Replace("currentColor", "#AFC4ED");
    using var svg = new SKSvg();
    if (svg.FromSvg(markup) is null)
        throw new InvalidDataException($"Cannot load {source}");
    string output = Path.ChangeExtension(source, ".png");
    using var stream = File.Create(output);
    svg.Save(stream, SKColors.Transparent, SKEncodedImageFormat.Png, 100, 8f / 3f, 8f / 3f);
    Console.WriteLine(Path.GetFileName(output));
}
