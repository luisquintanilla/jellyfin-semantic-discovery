// Builds the demo library's scanned book: an image-only PDF with no text layer, so
// PdfPig finds nothing and the semantic service falls back to the vision model.
//
//   dotnet run scripts/New-ScannedBook.cs
//
// The text below is original writing for this sample (CC BY 4.0). The script lays it out
// with PdfPig, renders every page to a JPEG with PDFtoImage, and writes a new PDF that holds
// only those JPEGs, like a copier scan. Page 1 also becomes the cover (<slug>.jpg).
#:package PdfPig@0.1.11
#:package PDFtoImage@5.4.0
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:property TreatWarningsAsErrors=false
#:property NoWarn=$(NoWarn);CA1416

using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

const string Slug = "retrieval-field-notes";
string folder = Path.Combine("assets", "demo", "library", "Books", "Retrieval Field Notes (2026)");
Directory.CreateDirectory(folder);

(string Heading, string[] Paragraphs)[] chapters =
[
    ("Why retrieval needs field notes", [
        "Search demos are easy to love and hard to trust. A query returns five results, the first one looks right, and everybody nods. These notes are about the slower habit of writing down what a retrieval system actually did, one query at a time, so that a change can be judged against evidence instead of a feeling.",
        "Each note records the query, the results in order, the score of each result, and a one-line verdict from a person who knows the library. Thirty notes are enough to see patterns; three hundred are enough to catch regressions.",
    ]),
    ("The lighthouse keeper rule", [
        "A good reranker behaves like a lighthouse keeper: it ignores the waves and watches the ships. Waves are the words that appear everywhere, such as titles, boilerplate, and page furniture. Ships are the rare, specific phrases a reader actually remembers.",
        "When a result ranks highly only because it shares common words with the query, write it down as a wave. When it ranks highly because it shares the one unusual phrase, write it down as a ship. A healthy system finds ships even when the waves are loud.",
    ]),
    ("Reading scanned pages", [
        "Some documents arrive without a text layer. A copier or a phone camera produced pictures of pages, and a text extractor sees nothing but images. The fix is not a better text extractor; it is a different reader. A vision language model can look at the page image and write down what it sees, including headings and tables.",
        "Vision reading is slower and costs more, so use it only when the cheap path fails. Count the characters the text extractor returned. If there are almost none, the document is probably a scan, and only then is it worth sending each page to a vision model.",
    ]),
    ("Keeping score", [
        "Scores from cosine similarity are not probabilities. A score of 0.6 does not mean sixty percent relevant; it means the vectors point in roughly the same direction. Pick a cut-off by reading notes, not by intuition, and revisit it whenever the embedding model changes.",
        "Finally, keep the notes next to the code. A threshold without the notes that justified it is just a magic number waiting to be changed by someone who never saw the evidence.",
    ]),
];

PdfDocumentBuilder text = new();
PdfDocumentBuilder.AddedFont regular = text.AddStandard14Font(Standard14Font.Helvetica);
PdfDocumentBuilder.AddedFont bold = text.AddStandard14Font(Standard14Font.HelveticaBold);
PdfRectangle letter = new(0, 0, 612, 792);

PdfPageBuilder title = text.AddPage(letter.Width, letter.Height);
title.SetTextAndFillColor(22, 78, 99);
title.DrawRectangle(new PdfPoint(0, 400), 612, 392, lineWidth: 0, fill: true);
title.SetTextAndFillColor(255, 255, 255);
title.AddText("Retrieval Field Notes", 34, new PdfPoint(72, 520), bold);
title.AddText("Writing down what search actually did", 16, new PdfPoint(72, 484), regular);
title.SetTextAndFillColor(40, 40, 40);
title.AddText("Jellyfin Semantic Discovery PDF Corpus - 2026 - Scanned edition", 11, new PdfPoint(72, 120), regular);

for (int c = 0; c < chapters.Length; c++)
{
    PdfPageBuilder page = text.AddPage(letter.Width, letter.Height);
    double y = 700;
    page.AddText($"{c + 1}. {chapters[c].Heading}", 18, new PdfPoint(72, y), bold);
    y -= 36;
    foreach (string paragraph in chapters[c].Paragraphs)
    {
        foreach (string line in Wrap(paragraph, 86))
        {
            page.AddText(line, 11, new PdfPoint(72, y), regular);
            y -= 16;
        }

        y -= 12;
    }

    page.AddText($"{c + 2}", 9, new PdfPoint(300, 40), regular);
}

byte[] textPdf = text.Build();

// The scan: every page becomes a JPEG, and the new PDF contains nothing else.
PdfDocumentBuilder scanned = new();
for (int i = 0; i < chapters.Length + 1; i++)
{
    using SKBitmap bitmap = PDFtoImage.Conversion.ToImage(textPdf, i, options: new(Dpi: 150));
    using SKData jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
    scanned.AddPage(letter.Width, letter.Height).AddJpeg(jpeg.ToArray(), letter);
}

File.WriteAllBytes(Path.Combine(folder, $"{Slug}.pdf"), scanned.Build());

using (SKBitmap cover = PDFtoImage.Conversion.ToImage(textPdf, 0, options: new(Width: 600, WithAspectRatio: true)))
using (SKData jpeg = cover.Encode(SKEncodedImageFormat.Jpeg, 85))
{
    File.WriteAllBytes(Path.Combine(folder, $"{Slug}.jpg"), jpeg.ToArray());
}

File.WriteAllText(Path.Combine(folder, "metadata.opf"), $"""
    <?xml version="1.0" encoding="utf-8"?>
    <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
      <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
        <dc:identifier>jellyfin-semantic-discovery-{Slug}</dc:identifier>
        <dc:title>Retrieval Field Notes</dc:title>
        <dc:creator>Jellyfin Semantic Discovery PDF Corpus</dc:creator>
        <dc:date>2026</dc:date>
        <dc:language>en</dc:language>
        <dc:rights>Creative Commons Attribution 4.0 International (CC BY 4.0)</dc:rights>
      </metadata>
    </package>

    """.ReplaceLineEndings("\n"));

Console.WriteLine($"Wrote {folder}");

static IEnumerable<string> Wrap(string text, int width)
{
    string line = "";
    foreach (string word in text.Split(' '))
    {
        if (line.Length + word.Length + 1 > width)
        {
            yield return line;
            line = word;
        }
        else
        {
            line = line.Length == 0 ? word : $"{line} {word}";
        }
    }

    if (line.Length > 0)
    {
        yield return line;
    }
}
