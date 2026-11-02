using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ByteScape.Core.Ai;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Wpf.Controls;
using Xunit;

namespace ByteScape.Tests;

/// <summary>Local model client against a fake HTTP server (Ollama and OpenAI shapes) and the guard rails around its answers.</summary>
[Collection("config")]
public class AiTests
{
    /// <summary>Minimal HTTP server that records the last request and answers with a canned text.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Reply = "";
        public string LastPath = "";
        public JsonNode? LastBody;
        public string LastAuth = "";
        public string Prefix { get; }

        public FakeServer()
        {
            int port = 18400 + Random.Shared.Next(1000);
            Prefix = $"http://127.0.0.1:{port}/";
            _l.Prefixes.Add(Prefix);
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _l.GetContextAsync(); } catch { return; }
                    LastPath = ctx.Request.Url!.AbsolutePath;
                    LastAuth = ctx.Request.Headers["Authorization"] ?? "";
                    if (ctx.Request.HasEntityBody) { using var r = new StreamReader(ctx.Request.InputStream); LastBody = JsonNode.Parse(await r.ReadToEndAsync()); }
                    string json = LastPath switch
                    {
                        "/api/tags" => """{"models":[{"name":"qwen2.5:3b"},{"name":"llama3.2:1b"}]}""",
                        "/v1/models" => """{"data":[{"id":"local-model"}]}""",
                        "/api/chat" => new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply } }.ToJsonString(),
                        "/v1/chat/completions" => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply } }) }.ToJsonString(),
                        _ => "{}",
                    };
                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    [Fact]
    public async Task OllamaShape_ListsModels_AndCompletes()
    {
        using var srv = new FakeServer { Reply = "*.mp4;*.mkv;>1gb;access>1year" };
        var lm = new LocalModel(srv.Prefix, "qwen2.5:3b");
        Assert.False(lm.OpenAiStyle);
        var models = await lm.ListModelsAsync();
        Assert.Equal(new[] { "qwen2.5:3b", "llama3.2:1b" }, models);
        var text = await lm.CompleteAsync("sys", "user text");
        Assert.Equal("*.mp4;*.mkv;>1gb;access>1year", text);
        Assert.Equal("/api/chat", srv.LastPath);
        Assert.Equal("qwen2.5:3b", srv.LastBody!["model"]!.GetValue<string>());
        Assert.False(srv.LastBody["stream"]!.GetValue<bool>());
        Assert.Equal("user text", srv.LastBody["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("", srv.LastAuth);
    }

    [Fact]
    public async Task OpenAiShape_UsesV1Routes_AndBearerKey()
    {
        using var srv = new FakeServer { Reply = "hello" };
        var lm = new LocalModel(srv.Prefix + "v1", "local-model", apiKey: "secret");
        Assert.True(lm.OpenAiStyle);
        Assert.Equal(new[] { "local-model" }, await lm.ListModelsAsync());
        Assert.Equal("hello", await lm.CompleteAsync("s", "u"));
        Assert.Equal("/v1/chat/completions", srv.LastPath);
        Assert.Equal("Bearer secret", srv.LastAuth);
        Assert.Equal("local-model", srv.LastBody!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task FilterTranslation_IsValidatedByTheParser()
    {
        AppConfig.Replace(new AppConfig());
        using var srv = new FakeServer();
        var lm = new LocalModel(srv.Prefix, "m");
        srv.Reply = "```\n*.pdf;>100mb;modify>1year\n```";                       // fenced answer is unwrapped
        var r = await Assistant.TranslateFilterAsync(lm, "1 yıldan eski büyük pdfler", CancellationToken.None);
        Assert.Equal("*.pdf;>100mb;modify>1year", r!.Value.filter);
        Assert.Contains("ByteScape filter syntax", srv.LastBody!["messages"]![0]!["content"]!.GetValue<string>());
        srv.Reply = "Sure! Here is the filter you asked for:\n*.jpg;:red";        // prose before the filter
        Assert.Equal("*.jpg;:red", (await Assistant.TranslateFilterAsync(lm, "x", CancellationToken.None))!.Value.filter);
        srv.Reply = "NONE";
        Assert.Null(await Assistant.TranslateFilterAsync(lm, "x", CancellationToken.None));
        srv.Reply = "I cannot help with that.";
        Assert.Null(await Assistant.TranslateFilterAsync(lm, "x", CancellationToken.None));
        srv.Reply = ":class:NoSuchClass";                                        // parses as syntax but the parser rejects it
        Assert.Null(await Assistant.TranslateFilterAsync(lm, "x", CancellationToken.None));
        srv.Reply = ">12parsecs";
        Assert.Null(await Assistant.TranslateFilterAsync(lm, "x", CancellationToken.None));
    }

    [Fact]
    public async Task FolderDescription_SendsOnlyNamesAndSizes()
    {
        using var srv = new FakeServer { Reply = "Bu klasör muhtemelen bir derleme çıktısıdır." };
        var lm = new LocalModel(srv.Prefix, "m");
        var tree = SampleTree.Create();
        var users = (ContainerNode)tree.Root.FindChild("Users")!;
        var text = await Assistant.DescribeFolderAsync(lm, users, tree.Sync, "Turkish", CancellationToken.None);
        Assert.Equal("Bu klasör muhtemelen bir derleme çıktısıdır.", text);
        var user = srv.LastBody!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Path: C:\\Users", user);
        Assert.Contains("Pictures", user);
        Assert.Contains("Language: Turkish", user);
        var system = srv.LastBody["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("Never tell the user to delete anything", system);
    }

    [Fact]
    public async Task DiffSummary_IncludesHeadlineNumbers()
    {
        using var srv = new FakeServer { Reply = "Users grew because of new.iso." };
        var lm = new LocalModel(srv.Prefix, "m");
        var before = SampleTree.Create().Root;
        var after = SampleTree.Create().Root;
        var users = (ContainerNode)after.FindChild("Users")!;
        users.AddChild(new FileNode("new.iso") { LogicalSize = 1 << 28, DiskSize = 1 << 28 });
        var diff = TreeDiff.Compare(before, "b.sns", DateTime.UtcNow, after, null);
        var text = await Assistant.SummarizeDiffAsync(lm, diff, "English", CancellationToken.None);
        Assert.Equal("Users grew because of new.iso.", text);
        var user = srv.LastBody!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("+256.0MB", user);
        Assert.Contains("new.iso", user);
    }

    [Fact]
    public async Task Unreachable_Endpoint_Throws_NotHangs()
    {
        var lm = new LocalModel("http://127.0.0.1:9", "m", timeoutSeconds: 5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => lm.ListModelsAsync());
        Assert.True(sw.Elapsed.TotalSeconds < 15);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalModel("http://127.0.0.1:9", "").CompleteAsync("s", "u"));
    }

    [Fact]
    public void Config_RoundTripsAiSettings_AndDefaultsOff()
    {
        var c = new AppConfig();
        Assert.False(c.AiEnabled);
        Assert.Equal("http://localhost:11434", c.AiEndpoint);
        var path = Path.Combine(Path.GetTempPath(), "bytescape-tests", "ai-config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        c.AiEnabled = true; c.AiEndpoint = "http://box:8080/v1"; c.AiModel = "qwen"; c.AiApiKey = "k"; c.AiTimeoutSeconds = 90;
        Assert.True(AppConfig.Save(c, path));
        var back = AppConfig.FromXml(System.Xml.Linq.XDocument.Load(path).Root!);
        Assert.True(back.AiEnabled); Assert.Equal("http://box:8080/v1", back.AiEndpoint); Assert.Equal("qwen", back.AiModel); Assert.Equal("k", back.AiApiKey); Assert.Equal(90, back.AiTimeoutSeconds);
        File.Delete(path);
    }

    [Fact]
    public void Config_ApiKey_IsStoredEncryptedForThisUser()
    {
        const string key = "sk-local-0123456789-ÇĞİÖŞÜ";
        var c = new AppConfig { AiApiKey = key };
        var xml = AppConfig.ToXml(c);
        var e = xml.Element("aiApiKey")!;
        Assert.Equal(Core.Util.Secrets.Scheme, (string?)e.Attribute("protection"));
        Assert.DoesNotContain("0123456789", xml.ToString());
        Assert.DoesNotContain("sk-local", xml.ToString());
        Assert.Equal(key, AppConfig.FromXml(xml).AiApiKey);
        // the same key encrypts differently each time (DPAPI salts every blob)
        Assert.NotEqual(e.Value, AppConfig.ToXml(c).Element("aiApiKey")!.Value);
        // no key: an empty element without the attribute
        var empty = AppConfig.ToXml(new AppConfig()).Element("aiApiKey")!;
        Assert.Equal("", empty.Value);
        Assert.Null(empty.Attribute("protection"));
        Assert.Equal("", AppConfig.FromXml(AppConfig.ToXml(new AppConfig())).AiApiKey);
    }

    [Fact]
    public void Config_ApiKey_PlainTextInAnOldFile_IsEncryptedWhenLoaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "secret-migration");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ByteScapeConfig.xml");
        try
        {
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ByteScapeConfig><aiEnabled>true</aiEnabled><aiApiKey>sk-plain-4711</aiApiKey><fontSize>11</fontSize></ByteScapeConfig>");
            AppConfig.Load(path);
            Assert.Equal("sk-plain-4711", AppConfig.Current.AiApiKey);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("sk-plain-4711", text);                        // rewritten at once, encrypted
            Assert.Contains("protection=\"dpapi\"", text);
            Assert.Equal(11, AppConfig.Current.FontSize);                          // the rest of the settings kept
            Assert.True(AppConfig.Current.AiEnabled);
            var written = File.GetLastWriteTimeUtc(path);
            AppConfig.Load(path);                                                  // already encrypted: not written again
            Assert.Equal("sk-plain-4711", AppConfig.Current.AiApiKey);
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Config_ApiKey_OldPlainTextReads_UnreadableBlobIsEmpty()
    {
        // files of earlier versions hold the key as plain text: still read (and encrypted on the next save)
        var old = new System.Xml.Linq.XElement("ByteScapeConfig", new System.Xml.Linq.XElement("aiApiKey", "plain-key"));
        var c = AppConfig.FromXml(old);
        Assert.Equal("plain-key", c.AiApiKey);
        Assert.NotNull(AppConfig.ToXml(c).Element("aiApiKey")!.Attribute("protection"));
        // a blob from another user / computer (here: damaged or foreign bytes) gives an empty key and a log note, no exception
        foreach (var bad in new[] { "not base64 !!", Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) })
        {
            var foreign = new System.Xml.Linq.XElement("ByteScapeConfig",
                new System.Xml.Linq.XElement("aiApiKey", new System.Xml.Linq.XAttribute("protection", "dpapi"), bad));
            Assert.Equal("", AppConfig.FromXml(foreign).AiApiKey);
        }
        Assert.True(Core.Util.Secrets.TryUnprotect(Core.Util.Secrets.Protect("x"), out var x));
        Assert.Equal("x", x);
        Assert.True(Core.Util.Secrets.TryUnprotect("", out var none));
        Assert.Equal("", none);
    }
}
