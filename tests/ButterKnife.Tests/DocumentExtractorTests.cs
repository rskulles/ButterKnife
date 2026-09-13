using System.Text;
using ButterKnife.Data;
using ButterKnife.Services;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ButterKnife.Tests;

public sealed class DocumentExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "butterknife-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadsTextFilesAndDropsTheBom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("line one\r\nline two\n")).ToArray();

        var file = DocumentExtractor.Extract("notes.md", "text/markdown", bytes);

        Assert.Equal("line one\nline two", file.Text);
        Assert.Equal("text/markdown", file.MediaType);
        Assert.Equal(bytes.Length, file.Size);
        Assert.Equal("notes.md", file.Name);
    }

    [Fact]
    public void KnowsCodeExtensionsWithoutAMimeType()
    {
        Assert.True(DocumentExtractor.IsSupported("Program.cs", ""));
        Assert.True(DocumentExtractor.IsSupported("report.PDF", null));
        Assert.True(DocumentExtractor.IsSupported("data.csv", "application/vnd.ms-excel"));
        Assert.False(DocumentExtractor.IsSupported("archive.zip", "application/zip"));
        Assert.Equal("text/plain", DocumentExtractor.Extract("script.py", "", "print(1)"u8.ToArray()).MediaType);
    }

    [Fact]
    public void RefusesBinaryEmptyAndUnsupportedFiles()
    {
        Assert.Contains("not a text file", Assert.Throws<InvalidDataException>(() => DocumentExtractor.Extract("blob.txt", "text/plain", [1, 0, 2, 0])).Message);
        Assert.Contains("empty", Assert.Throws<InvalidDataException>(() => DocumentExtractor.Extract("nothing.txt", "text/plain", [])).Message);
        Assert.Contains("only text files and PDFs", Assert.Throws<InvalidDataException>(() => DocumentExtractor.Extract("a.zip", "application/zip", [1, 2, 3])).Message);
        Assert.Contains("no readable text", Assert.Throws<InvalidDataException>(() => DocumentExtractor.Extract("blank.txt", "text/plain", "   \n"u8.ToArray())).Message);
        Assert.Contains("could not be read as a PDF", Assert.Throws<InvalidDataException>(() => DocumentExtractor.Extract("fake.pdf", "application/pdf", "not a pdf"u8.ToArray())).Message);
    }

    [Fact]
    public void CutsVeryLongTextWithANote()
    {
        var file = DocumentExtractor.Extract("big.txt", "text/plain", Encoding.UTF8.GetBytes(new string('x', DocumentExtractor.MaxChars + 500)));

        Assert.StartsWith(new string('x', 100), file.Text);
        Assert.Contains("[cut here: the file goes on for 500 more characters]", file.Text);
    }

    [Fact]
    public void ExtractsPdfTextPageByPage()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page1 = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page1.AddText("Hello from page one", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        var page2 = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page2.AddText("And page two", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);

        var file = DocumentExtractor.Extract("two-pages.pdf", "application/pdf", builder.Build());

        Assert.Equal("application/pdf", file.MediaType);
        Assert.Contains("Hello from page one", file.Text);
        Assert.Contains("And page two", file.Text);
        Assert.True(file.Text.IndexOf("page one", StringComparison.Ordinal) < file.Text.IndexOf("page two", StringComparison.Ordinal));
        Assert.Contains("words", DocumentExtractor.Describe(file));
    }

    [Fact]
    public void FilesRideInThePromptAsDocumentBlocks()
    {
        var message = new ChatMessage(ChatRole.User, "Summarise this", [new ChatImage("image/png", [1])])
        {
            Files = [new ChatFile("a \"quoted\".txt", "text/plain", 3, "abc"), new ChatFile("b.md", "text/markdown", 3, "def")],
        };

        var wire = message.WithFilesAsText();

        Assert.Equal("<document name=\"a 'quoted'.txt\">\nabc\n</document>\n\n<document name=\"b.md\">\ndef\n</document>\n\nSummarise this", wire.Content);
        Assert.Single(wire.Images);
        var plain = new ChatMessage(ChatRole.User, "plain");
        Assert.Same(plain, plain.WithFilesAsText());
        Assert.Equal(TokenEstimator.TokensPerMessageOverhead + TokenEstimator.Estimate("Summarise this") + TokenEstimator.TokensPerImage + 2, TokenEstimator.Estimate(message));

        var textOnly = message.WithImagesAsText();
        Assert.Equal(2, textOnly.Files.Count); // stripping images keeps the documents
    }

    [Fact]
    public async Task StoreKeepsFilesWithTheMessage()
    {
        var db = new SqliteDatabase(Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" }));
        var store = new SqliteConversationStore(db);
        var conv = await store.CreateAsync("t", Guid.NewGuid(), "m", null, CancellationToken.None);
        var file = new ChatFile("report.pdf", "application/pdf", 1234, "The quarterly numbers.");

        var id = await store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.User, "look") { Files = [file] }, CancellationToken.None);
        await store.AppendMessageAsync(conv.Id, new ChatMessage(ChatRole.Assistant, "ok"), CancellationToken.None);

        var loaded = (await store.GetAsync(conv.Id, CancellationToken.None))!.Messages;
        Assert.Equal([file], loaded[0].Files);
        Assert.Empty(loaded[1].Files);

        var branch = await store.BranchAsync(conv.Id, id, "b", CancellationToken.None);
        Assert.Equal([file], branch.Messages[0].Files);

        await store.DeleteMessageAsync(conv.Id, id, CancellationToken.None);
        Assert.DoesNotContain((await store.GetAsync(conv.Id, CancellationToken.None))!.Messages, m => m.HasFiles);
    }

    [Fact]
    public void SummaryRequestQuotesTheStartOfEachDocument()
    {
        var request = ConversationCompactor.BuildSummaryRequest(null,
        [
            new ChatMessage(ChatRole.User, "read it") { Files = [new ChatFile("long.txt", "text/plain", 9, new string('y', 3000))] },
            new ChatMessage(ChatRole.Assistant, "done"),
        ]);

        var transcript = request[1].Content;
        Assert.Contains("[document \"long.txt\" attached; it begins: " + new string('y', 1500) + "…]", transcript);
        Assert.DoesNotContain(new string('y', 1600), transcript);
    }

    [Fact]
    public void ExportListsAttachments()
    {
        var at = DateTimeOffset.Now;
        var conversation = new Conversation(Guid.NewGuid(), "T", Guid.NewGuid(), "m", null, null, null, null, null, at, at,
            [new ChatMessage(ChatRole.User, "look") { Files = [new ChatFile("report.pdf", "application/pdf", 2048, "one two three")] }]);

        var markdown = ChatExporter.ToMarkdown(conversation, "Me", null, at);

        Assert.Contains("*Attached: report.pdf (2 KB · about 3 words)*", markdown);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
