using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.DocumentExtraction.PdfPig;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig;

namespace Jellyfin.SemanticDiscovery.Extraction.Pdf;

public sealed record PdfTextPage(int PageNumber, string Text);

public sealed record RasterizedPdfPage(
    int PageNumber,
    string MediaType,
    byte[] Content);

public interface IPdfDocumentInspector
{
    ValueTask<int> GetPageCountAsync(
        string sourcePath,
        CancellationToken cancellationToken);
}

public interface IPdfTextLayerExtractor
{
    ValueTask<IReadOnlyList<PdfTextPage>> ExtractAsync(
        string sourcePath,
        int pageCount,
        CancellationToken cancellationToken);
}

public interface IPdfRasterizer
{
    ValueTask<RasterizedPdfPage> RasterizeAsync(
        string sourcePath,
        int pageNumber,
        int dpi,
        string workspace,
        CancellationToken cancellationToken);
}

public sealed class PdfPigTextLayerExtractor :
    IPdfDocumentInspector,
    IPdfTextLayerExtractor
{
    public ValueTask<int> GetPageCountAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using PdfDocument document = PdfDocument.Open(sourcePath);
        return ValueTask.FromResult(document.NumberOfPages);
    }

    public async ValueTask<IReadOnlyList<PdfTextPage>> ExtractAsync(
        string sourcePath,
        int pageCount,
        CancellationToken cancellationToken)
    {
        using PdfPigDocumentExtractionClient client = new();
        await using FileStream stream = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ExtractionDocument document = await client.ExtractAsync(
            stream,
            Path.GetFileName(sourcePath),
            "application/pdf",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<int, StringBuilder> textByPage = [];
        foreach (ExtractionDocumentTextElement element in
                 document.EnumerateContent()
                     .OfType<ExtractionDocumentTextElement>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Content is not TextContent text ||
                string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            int? sourcePage = element.Sources
                .Select(reference => reference.Page.PageNumber)
                .FirstOrDefault(number => number > 0);
            if (sourcePage is not int pageNumber ||
                pageNumber > pageCount)
            {
                continue;
            }

            if (!textByPage.TryGetValue(
                    pageNumber,
                    out StringBuilder? builder))
            {
                builder = new StringBuilder();
                textByPage.Add(pageNumber, builder);
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(text.Text);
        }

        return Enumerable.Range(1, pageCount)
            .Select(pageNumber => new PdfTextPage(
                pageNumber,
                textByPage.TryGetValue(pageNumber, out StringBuilder? text)
                    ? NormalizeText(text.ToString())
                    : string.Empty))
            .ToArray();
    }

    private static string NormalizeText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
}

public sealed class PopplerPdfRasterizer(string pdfRasterExecutable) :
    IPdfRasterizer
{
    public async ValueTask<RasterizedPdfPage> RasterizeAsync(
        string sourcePath,
        int pageNumber,
        int dpi,
        string workspace,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace);
        string outputPrefix = Path.Combine(
            workspace,
            $"page-{pageNumber.ToString("D6", CultureInfo.InvariantCulture)}");
        string outputPath = $"{outputPrefix}.png";
        try
        {
            await RunAsync(
                pdfRasterExecutable,
                [
                    "-f",
                    pageNumber.ToString(CultureInfo.InvariantCulture),
                    "-l",
                    pageNumber.ToString(CultureInfo.InvariantCulture),
                    "-singlefile",
                    "-png",
                    "-r",
                    dpi.ToString(CultureInfo.InvariantCulture),
                    sourcePath,
                    outputPrefix,
                ],
                cancellationToken).ConfigureAwait(false);
            byte[] content = await File.ReadAllBytesAsync(
                outputPath,
                cancellationToken).ConfigureAwait(false);
            return new RasterizedPdfPage(pageNumber, "image/png", content);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static async ValueTask<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Unable to start '{executable}'.");
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
                InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The configured PDF tool '{executable}' could not be started.",
                exception);
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }

        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"PDF tool '{executable}' exited with code {process.ExitCode}: {error.Trim()}");
        }

        return new ProcessResult(output, error);
    }

    private sealed record ProcessResult(
        string StandardOutput,
        string StandardError);
}
