// Signed files (.p7m, #1010): the signed file comes out of the CMS envelope as it was signed and converts on from its
// own format. The envelopes are signed here with a self-signed certificate made for the test; the signature is never
// checked, so nothing about the certificate matters.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Signed;
using PdfSharp.Pdf;

namespace Filee.Engines.Tests;

public sealed class P7mTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private readonly X509Certificate2 _certificate = SelfSigned();

    private const string Invoice = """
        <?xml version="1.0" encoding="UTF-8"?>
        <p:FatturaElettronica versione="FPR12" xmlns:p="http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2">
          <FatturaElettronicaHeader>
            <DatiTrasmissione>
              <ProgressivoInvio>00042</ProgressivoInvio>
            </DatiTrasmissione>
          </FatturaElettronicaHeader>
        </p:FatturaElettronica>
        """;

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Filee test signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public void Dispose() => _certificate.Dispose();

    /// <summary>A CAdES-like attached signature: <paramref name="content"/> inside a CMS SignedData.</summary>
    private byte[] Sign(byte[] content, bool detached = false)
    {
        var cms = new SignedCms(new ContentInfo(content), detached);
        cms.ComputeSignature(new CmsSigner(_certificate) { IncludeOption = X509IncludeOption.EndCertOnly });
        return cms.Encode();
    }

    private static byte[] Pdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    private async Task<string> ErrorOf(string path, string target = "pdf")
    {
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = target });
        Assert.Equal(JobState.Failed, job.State);
        return job.Files.Single().ErrorKey!;
    }

    [Fact]
    public async Task A_signed_pdf_comes_out_as_it_was_signed_and_named_after_it()
    {
        var pdf = Pdf();
        var envelope = Write("invoice.pdf.p7m", Sign(pdf));

        var job = await fx.ConvertAsync([envelope], new Preset { TargetFormat = "pdf" });

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal(Path.Combine(Path.GetDirectoryName(envelope)!, "invoice.pdf"), output);
        Assert.Equal(pdf, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(envelope));
    }

    [Fact]
    public async Task A_signed_pdf_goes_on_to_a_picture()
    {
        var envelope = Write("invoice.pdf.p7m", Sign(Pdf()));

        var job = await fx.ConvertAsync([envelope], new Preset { TargetFormat = "png" });

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("invoice.png", Path.GetFileName(output));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], (await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken))[..4]);
    }

    [Fact]
    public async Task An_e_invoice_saved_as_base64_becomes_its_xml_and_goes_on_to_json()
    {
        var xml = Encoding.UTF8.GetBytes(Invoice);
        var base64 = Convert.ToBase64String(Sign(xml), Base64FormattingOptions.InsertLineBreaks);
        var envelope = Write("IT01234567890_FPA01.xml.p7m", Encoding.ASCII.GetBytes(base64));

        var asXml = await fx.ConvertAsync([envelope], new Preset { TargetFormat = "xml" });
        var asJson = await fx.ConvertAsync([envelope], new Preset { TargetFormat = "json" });

        AssertDone(asXml);
        Assert.Equal("IT01234567890_FPA01.xml", Path.GetFileName(asXml.Outputs.Single()));
        Assert.Equal(xml, await File.ReadAllBytesAsync(asXml.Outputs.Single(), TestContext.Current.CancellationToken));
        AssertDone(asJson);
        Assert.Contains("00042", await File.ReadAllTextAsync(asJson.Outputs.Single(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Pem_text_and_a_second_signature_around_the_first_are_opened_too()
    {
        var pdf = Pdf();
        var pem = $"-----BEGIN PKCS7-----\n{Convert.ToBase64String(Sign(pdf), Base64FormattingOptions.InsertLineBreaks)}\n-----END PKCS7-----\n";

        Assert.Equal(pdf, P7mConverter.Open(Write("a.pdf.p7m", Encoding.ASCII.GetBytes(pem))));
        Assert.Equal(pdf, P7mConverter.Open(Write("b.pdf.p7m.p7m", Sign(Sign(pdf)))));
        Assert.Equal(pdf, P7mConverter.Open(Write("c.pdf.p7m", Sign(Encoding.ASCII.GetBytes(Convert.ToBase64String(Sign(pdf)))))));
    }

    [Fact]
    public async Task Without_a_name_inside_the_first_bytes_decide()
    {
        var envelope = Write("document.p7m", Sign(Pdf()));

        var job = await fx.ConvertAsync([envelope], new Preset { TargetFormat = "pdf" });

        AssertDone(job);
        Assert.Equal("document.pdf", Path.GetFileName(job.Outputs.Single()));
    }

    [Fact]
    public void The_first_bytes_tell_office_files_xml_mail_and_text_apart()
    {
        Assert.Equal("docx", P7mConverter.Sniff(Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w:document/>"))));
        Assert.Equal("xlsx", P7mConverter.Sniff(Zip(("[Content_Types].xml", "<Types/>"), ("xl/workbook.xml", "<workbook/>"))));
        Assert.Equal("odt", P7mConverter.Sniff(Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", "<x/>"))));
        Assert.Equal("zip", P7mConverter.Sniff(Zip(("readme.txt", "hello"))));
        Assert.Equal("xml", P7mConverter.Sniff("\uFEFF  <?xml version=\"1.0\"?><a/>"u8.ToArray()));
        Assert.Equal("pdf", P7mConverter.Sniff(Pdf()));
        Assert.Equal("eml", P7mConverter.Sniff("MIME-Version: 1.0\r\nContent-Type: text/plain\r\n\r\nHi"u8.ToArray()));
        Assert.Equal("rtf", P7mConverter.Sniff(@"{\rtf1\ansi Hello}"u8.ToArray()));
        Assert.Equal("txt", P7mConverter.Sniff("Grazie per l'ordine.\nTotale: 12,50 €\n"u8.ToArray()));
        Assert.Null(P7mConverter.Sniff([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0]));
        // The name of the file inside comes first.
        Assert.Equal("xml", P7mConverter.FormatOf("invoice.xml", Pdf()));
        Assert.Equal("invoice.xml", P7mConverter.InnerName(Path.Combine("Fatture", "invoice.xml.P7M")));
        Assert.Equal("contract.pdf", P7mConverter.InnerName("contract.pdf.p7m.p7m"));
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public async Task A_detached_signature_a_damaged_file_and_an_encrypted_one_are_refused_with_the_reason()
    {
        var encrypted = new EnvelopedCms(new ContentInfo(Pdf()));
        encrypted.Encrypt(new CmsRecipient(_certificate));

        Assert.Equal("error.p7m_detached", await ErrorOf(Write("invoice.pdf.p7m", Sign(Pdf(), detached: true))));
        Assert.Equal("error.p7m_unreadable", await ErrorOf(Write("broken.pdf.p7m", [0x30, 0x82, 0x01, 0x00, 0x06, 0x09, 1, 2, 3])));
        Assert.Equal("error.p7m_unreadable", await ErrorOf(Write("plain.pdf.p7m", Pdf())));
        Assert.Equal("error.p7m_encrypted", await ErrorOf(Write("smime.p7m", encrypted.Encode())));
        Assert.Equal("error.p7m_unknown_content", await ErrorOf(Write("blob.p7m", Sign([0x00, 0xFF, 0x13, 0x37, 0x00, 0x01]))));
    }

    [Fact]
    public async Task A_file_inside_that_cant_become_the_target_says_its_own_format()
    {
        var envelope = Write("IT01234567890_FPA01.xml.p7m", Sign(Encoding.UTF8.GetBytes(Invoice)));

        var job = await fx.ConvertAsync([envelope], new Preset { TargetFormat = "mp4" });

        Assert.Equal("error.no_route", job.Files.Single().ErrorKey);
        Assert.Equal("xml → mp4", job.Files.Single().ErrorDetail);
    }
}
